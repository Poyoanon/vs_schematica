using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Schematica.Rendering;
using Schematica.Utils;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace Schematica.Profiling
{
    public sealed class SchematicaProfilingManager : ISchematicaProfilingSink, IDisposable
    {
        private const int TickIntervalMs = 50;
        private const double RadiansPerDegree = Math.PI / 180d;
        private static readonly JsonSerializerOptions JsonIndentedOptions = new JsonSerializerOptions { WriteIndented = true };
        private static readonly JsonSerializerOptions JsonCompactOptions = new JsonSerializerOptions { WriteIndented = false };

        private readonly ICoreClientAPI capi;
        private readonly SchematicaModSystem modSystem;
        private readonly SchematicRenderer renderer;

        private SchematicaProfilingConfig config;
        private bool disposed;
        private bool autoStartPending;

        private bool isRunning;
        private bool isMeasuring;
        private bool movementApplied;
        private int currentIteration;
        private int tickInIteration;
        private int renderedFrameCount;
        private int droppedEventCount;
        private int tickTraceCounter;
        private int renderTraceCounter;
        private int chunkQueueTraceCounter;
        private int layerLookupTraceCounter;
        private DateTimeOffset runStartedAtUtc;
        private string runId = string.Empty;
        private string latestSummaryPath = string.Empty;
        private float? previousYaw;
        private float? previousPitch;
        private EntityControls? previousControlsSnapshot;

        private readonly List<ProfileEventLine> events = new List<ProfileEventLine>();
        private int eventRingWriteIndex;
        private bool eventRingFilled;
        private readonly List<double> tickDurationsMs = new List<double>();
        private readonly List<double> renderDurationsMs = new List<double>();
        private readonly List<double> buildProjectionDurationsMs = new List<double>();
        private readonly List<double> chunkScanDurationsMs = new List<double>();
        private readonly List<double> chunkQueueDurationsMs = new List<double>();
        private readonly List<double> layerLookupDurationsMs = new List<double>();
        private readonly List<double> layerLookupAllocatedBytes = new List<double>();
        private readonly List<double> chunkQueueAllocatedBytes = new List<double>();
        private readonly List<double> layerLookupCounts = new List<double>();
        private readonly List<double> chunkQueueProcessedCounts = new List<double>();
        private readonly List<double> tickAllocatedBytes = new List<double>();
        private readonly List<double> renderAllocatedBytes = new List<double>();

        public SchematicaProfilingManager(ICoreClientAPI capi, SchematicaModSystem modSystem, SchematicRenderer renderer)
        {
            this.capi = capi ?? throw new ArgumentNullException(nameof(capi));
            this.modSystem = modSystem ?? throw new ArgumentNullException(nameof(modSystem));
            this.renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));

            config = LoadOrCreateConfig();
            autoStartPending = config.Enabled && config.AutoStartOnWorldLoad;
        }

        public string ConfigPath => Path.Combine(GetProfilingRootDirectory(), "schematicaplus.profiling.json");
        public string ProfilesDirectoryPath => GetProfilesDirectory();
        public string LatestSummaryPath => latestSummaryPath;
        public bool IsRunning => isRunning;
        public bool IsCapturing => isRunning && isMeasuring;

        public SchematicaProfilingConfig CurrentConfig
        {
            get
            {
                return config;
            }
        }

        public void ReloadConfig()
        {
            config = LoadOrCreateConfig();
            autoStartPending = config.Enabled && config.AutoStartOnWorldLoad && !isRunning;
        }

        public bool TryStart(out string message)
        {
            ReloadConfig();

            if (isRunning)
            {
                message = "Schematica Plus profiling is already running.";
                return false;
            }

            if (!config.Enabled)
            {
                message = $"Profiling is disabled. Enable it in: {ConfigPath}";
                return false;
            }

            if (modSystem.CurrentSchematic == null)
            {
                message = "No schematic is loaded. Load a schematic first.";
                return false;
            }

            if (!renderer.HasProjection)
            {
                message = "No projection is active. Set a render origin first.";
                return false;
            }

            var player = capi.World?.Player;
            if (player?.Entity == null)
            {
                message = "Player entity is not ready yet.";
                return false;
            }

            StartRunInternal();
            message = $"Schematica Plus profiling started. RunId={runId}";
            return true;
        }

        public bool Stop(out string message)
        {
            if (!isRunning)
            {
                message = "Schematica Plus profiling is not running.";
                return false;
            }

            CompleteRunInternal("Stopped by user command.", completed: false);
            message = string.IsNullOrWhiteSpace(latestSummaryPath)
                ? "Schematica Plus profiling stopped."
                : $"Schematica Plus profiling stopped. Summary: {latestSummaryPath}";
            return true;
        }

        public bool TryPromoteLatestSummaryToBaseline(string? baselineName, out string message)
        {
            string summaryPath = ResolveLatestSummaryPath();
            if (string.IsNullOrWhiteSpace(summaryPath) || !File.Exists(summaryPath))
            {
                message = "No summary file is available yet.";
                return false;
            }

            string safeBaselineName = SanitizeFileName(string.IsNullOrWhiteSpace(baselineName) ? config.BaselineName : baselineName.Trim());
            if (string.IsNullOrWhiteSpace(safeBaselineName))
            {
                safeBaselineName = "default";
            }

            string baselineDirectory = GetBaselinesDirectory();
            Directory.CreateDirectory(baselineDirectory);

            string baselinePath = Path.Combine(baselineDirectory, $"{safeBaselineName}.json");
            File.Copy(summaryPath, baselinePath, overwrite: true);

            message = $"Baseline updated: {baselinePath}";
            return true;
        }

        public IReadOnlyList<string> GetStatusLines()
        {
            return new List<string>
            {
                $"Profiling enabled: {config.Enabled}",
                $"Auto-start on world load: {config.AutoStartOnWorldLoad}",
                $"Running: {isRunning}",
                $"Capturing: {isMeasuring}",
                $"Scenario: {config.ScenarioName}",
                $"Capture mode: {config.CaptureMode}",
                $"Iterations: {config.Iterations}",
                $"Warmup/Measure: {config.WarmupSeconds}s/{config.MeasureSeconds}s",
                $"Config path: {ConfigPath}",
                $"Profiles directory: {ProfilesDirectoryPath}",
                string.IsNullOrWhiteSpace(latestSummaryPath) ? "Latest summary: (none)" : $"Latest summary: {latestSummaryPath}"
            };
        }

        public void OnTick(float deltaTime)
        {
            if (disposed)
            {
                return;
            }

            if (!isRunning)
            {
                TryAutoStart();
                return;
            }

            if (deltaTime < 0)
            {
                deltaTime = 0;
            }

            bool captureThisTick = isMeasuring;
            long allocBefore = captureThisTick ? GC.GetAllocatedBytesForCurrentThread() : 0;
            long startTimestamp = StopwatchTicks();

            ApplyDeterministicScenarioStep();

            if (captureThisTick)
            {
                double elapsedMs = TicksToMilliseconds(StopwatchTicks() - startTimestamp);
                long allocatedBytes = Math.Max(0, GC.GetAllocatedBytesForCurrentThread() - allocBefore);
                tickDurationsMs.Add(elapsedMs);
                tickAllocatedBytes.Add(allocatedBytes);

                if (ShouldCaptureTraceSample(ref tickTraceCounter, config.TickTraceSampleInterval))
                {
                    AppendEvent(
                        type: "tick",
                        elapsedMilliseconds: elapsedMs,
                        allocatedBytes: allocatedBytes,
                        details: new Dictionary<string, object?>
                        {
                            ["deltaTime"] = deltaTime
                        },
                        highFrequency: true);
                }
            }

            tickInIteration++;

            int warmupTicks = config.WarmupSeconds * (1000 / TickIntervalMs);
            int measurementTicks = config.MeasureSeconds * (1000 / TickIntervalMs);

            if (!isMeasuring && tickInIteration >= warmupTicks)
            {
                isMeasuring = true;
                AppendEvent(type: "phase", details: new Dictionary<string, object?> { ["state"] = "measure" });
            }

            if (tickInIteration >= warmupTicks + measurementTicks)
            {
                currentIteration++;
                if (currentIteration >= config.Iterations)
                {
                    CompleteRunInternal("Completed requested iterations.", completed: true);
                }
                else
                {
                    tickInIteration = 0;
                    isMeasuring = false;
                    AppendEvent(
                        type: "iteration",
                        details: new Dictionary<string, object?>
                        {
                            ["iteration"] = currentIteration + 1,
                            ["totalIterations"] = config.Iterations
                        });
                }
            }
        }

        public void OnRenderFrameMeasured(double elapsedMilliseconds, long allocatedBytes, int renderedChunkCount)
        {
            if (!IsCapturing)
            {
                return;
            }

            renderDurationsMs.Add(elapsedMilliseconds);
            renderAllocatedBytes.Add(Math.Max(0, allocatedBytes));
            renderedFrameCount++;

            if (ShouldCaptureTraceSample(ref renderTraceCounter, config.RenderTraceSampleInterval))
            {
                AppendEvent(
                    type: "render",
                    elapsedMilliseconds: elapsedMilliseconds,
                    allocatedBytes: allocatedBytes,
                    details: new Dictionary<string, object?>
                    {
                        ["renderedChunkCount"] = renderedChunkCount
                    },
                    highFrequency: true);
            }
        }

        public void OnBuildProjectionMeasured(double elapsedMilliseconds, int blockCount, int chunkCount, long allocatedBytes)
        {
            if (!IsCapturing)
            {
                return;
            }

            buildProjectionDurationsMs.Add(elapsedMilliseconds);
            AppendEvent(
                type: "buildProjection",
                elapsedMilliseconds: elapsedMilliseconds,
                allocatedBytes: allocatedBytes,
                details: new Dictionary<string, object?>
                {
                    ["blockCount"] = blockCount,
                    ["chunkCount"] = chunkCount
                },
                highFrequency: true);
        }

        public void OnChunkScanMeasured(double elapsedMilliseconds, int candidateChunkCount, int updatedChunkCount, long allocatedBytes)
        {
            if (!IsCapturing)
            {
                return;
            }

            chunkScanDurationsMs.Add(elapsedMilliseconds);
            AppendEvent(
                type: "chunkScan",
                elapsedMilliseconds: elapsedMilliseconds,
                allocatedBytes: allocatedBytes,
                details: new Dictionary<string, object?>
                {
                    ["candidateChunkCount"] = candidateChunkCount,
                    ["updatedChunkCount"] = updatedChunkCount
                },
                highFrequency: true);
        }

        public void OnChunkUpdateQueueMeasured(double elapsedMilliseconds, int candidateChunkCount, int processedChunkCount, int remainingChunkCount, long allocatedBytes)
        {
            if (!IsCapturing)
            {
                return;
            }

            chunkQueueDurationsMs.Add(elapsedMilliseconds);
            chunkQueueAllocatedBytes.Add(Math.Max(0, allocatedBytes));
            chunkQueueProcessedCounts.Add(processedChunkCount);
            if (candidateChunkCount >= 0)
            {
                // Keep a separate metric for queue pressure visibility.
            }

            if (ShouldCaptureTraceSample(ref chunkQueueTraceCounter, config.ChunkTraceSampleInterval))
            {
                AppendEvent(
                    type: "chunkQueue",
                    elapsedMilliseconds: elapsedMilliseconds,
                    allocatedBytes: allocatedBytes,
                    details: new Dictionary<string, object?>
                    {
                        ["candidateChunkCount"] = candidateChunkCount,
                        ["processedChunkCount"] = processedChunkCount,
                        ["remainingChunkCount"] = remainingChunkCount
                    },
                    highFrequency: true);
            }
        }

        public void OnLayerLookupMeasured(double elapsedMilliseconds, int requestedLayer, int matchedBlockCount, long allocatedBytes)
        {
            if (!IsCapturing)
            {
                return;
            }

            layerLookupDurationsMs.Add(elapsedMilliseconds);
            layerLookupAllocatedBytes.Add(Math.Max(0, allocatedBytes));
            layerLookupCounts.Add(matchedBlockCount);

            if (ShouldCaptureTraceSample(ref layerLookupTraceCounter, config.LayerTraceSampleInterval))
            {
                AppendEvent(
                    type: "layerLookup",
                    elapsedMilliseconds: elapsedMilliseconds,
                    allocatedBytes: allocatedBytes,
                    details: new Dictionary<string, object?>
                    {
                        ["layer"] = requestedLayer,
                        ["matchedBlockCount"] = matchedBlockCount
                    },
                    highFrequency: true);
            }
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            if (isRunning)
            {
                CompleteRunInternal("Profiling manager disposed.", completed: false);
            }

            disposed = true;
        }

        private void TryAutoStart()
        {
            if (!autoStartPending || !config.Enabled || !config.AutoStartOnWorldLoad)
            {
                return;
            }

            if (modSystem.CurrentSchematic == null || !renderer.HasProjection)
            {
                return;
            }

            if (capi.World?.Player?.Entity == null)
            {
                return;
            }

            autoStartPending = false;

            if (TryStart(out string message) && config.ChatNotifications)
            {
                capi.ShowChatMessage(message);
            }
        }

        private void StartRunInternal()
        {
            isRunning = true;
            isMeasuring = false;
            movementApplied = false;
            currentIteration = 0;
            tickInIteration = 0;
            renderedFrameCount = 0;
            droppedEventCount = 0;
            tickTraceCounter = 0;
            renderTraceCounter = 0;
            runStartedAtUtc = DateTimeOffset.UtcNow;
            runId = $"{runStartedAtUtc:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)[..8]}";

            events.Clear();
            eventRingWriteIndex = 0;
            eventRingFilled = false;
            tickDurationsMs.Clear();
            renderDurationsMs.Clear();
            buildProjectionDurationsMs.Clear();
            chunkScanDurationsMs.Clear();
            tickAllocatedBytes.Clear();
            renderAllocatedBytes.Clear();
            chunkQueueDurationsMs.Clear();
            chunkQueueAllocatedBytes.Clear();
            chunkQueueProcessedCounts.Clear();
            layerLookupDurationsMs.Clear();
            layerLookupAllocatedBytes.Clear();
            layerLookupCounts.Clear();

            CapturePreviousPlayerState();

            AppendEvent(
                type: "runStart",
                details: new Dictionary<string, object?>
                {
                    ["runId"] = runId,
                    ["scenario"] = config.ScenarioName,
                    ["iterations"] = config.Iterations
                });
        }

        private void CompleteRunInternal(string stopReason, bool completed)
        {
            isMeasuring = false;
            isRunning = false;

            AppendEvent(
                type: "runStop",
                details: new Dictionary<string, object?>
                {
                    ["reason"] = stopReason,
                    ["completed"] = completed
                });

            RestorePreviousPlayerState();
            PersistRunArtifacts(stopReason, completed);
        }

        private void CapturePreviousPlayerState()
        {
            previousYaw = null;
            previousPitch = null;
            previousControlsSnapshot = null;

            var player = capi.World?.Player;
            var entity = player?.Entity;
            if (player == null || entity == null)
            {
                return;
            }

            previousYaw = player.CameraYaw;
            previousPitch = player.CameraPitch;

            if (config.SimulateMovement)
            {
                previousControlsSnapshot = new EntityControls();
                previousControlsSnapshot.SetFrom(entity.Controls);
            }
        }

        private void RestorePreviousPlayerState()
        {
            var player = capi.World?.Player;
            var entity = player?.Entity;

            if (player != null)
            {
                if (previousYaw.HasValue)
                {
                    player.CameraYaw = previousYaw.Value;
                }

                if (previousPitch.HasValue)
                {
                    player.CameraPitch = previousPitch.Value;
                }
            }

            if (entity != null && previousControlsSnapshot != null)
            {
                entity.Controls.SetFrom(previousControlsSnapshot);
            }
            else if (entity != null && movementApplied)
            {
                entity.Controls.StopAllMovement();
            }

            movementApplied = false;
            previousYaw = null;
            previousPitch = null;
            previousControlsSnapshot = null;
        }

        private void ApplyDeterministicScenarioStep()
        {
            var player = capi.World?.Player;
            var entity = player?.Entity;
            if (player == null || entity == null)
            {
                return;
            }

            int baseTicksPerSecond = 1000 / TickIntervalMs;
            int measurementTicks = Math.Max(1, config.MeasureSeconds * baseTicksPerSecond);
            int scenarioTick = tickInIteration + (currentIteration * measurementTicks);
            double normalized = (scenarioTick % measurementTicks) / (double)measurementTicks;

            double sweepRadians = config.CameraSweepDegrees * RadiansPerDegree;
            double pitchRadians = config.CameraPitchDegrees * RadiansPerDegree;

            float originYaw = previousYaw ?? player.CameraYaw;
            player.CameraYaw = originYaw + (float)(Math.Sin(normalized * (2d * Math.PI)) * (sweepRadians * 0.5d));
            player.CameraPitch = (float)(Math.Sin(normalized * (4d * Math.PI)) * (pitchRadians * 0.5d));

            if (config.SimulateMovement)
            {
                movementApplied = true;
                int cycle = Math.Max(4, config.MovementCycleTicks);
                int phase = (scenarioTick / cycle) % 4;

                entity.Controls.Forward = phase == 0;
                entity.Controls.Right = phase == 1;
                entity.Controls.Backward = phase == 2;
                entity.Controls.Left = phase == 3;
                entity.Controls.Sprint = phase == 0 || phase == 1;
            }

            if (modSystem.CurrentSchematic != null && config.LayerStepIntervalTicks > 0 && scenarioTick % config.LayerStepIntervalTicks == 0)
            {
                int maxLayer = modSystem.CurrentSchematic.MaxY;
                if (maxLayer > 0)
                {
                    int layer = (scenarioTick / config.LayerStepIntervalTicks) % (maxLayer + 1);
                    renderer.SetLayer(layer);
                }
            }

            if (config.ChunkUpdateIntervalTicks > 0 && scenarioTick % config.ChunkUpdateIntervalTicks == 0)
            {
                BlockPos playerPos = SchematicaHelpers.GetEntityBlockPos(entity);
                renderer.UpdateChunksNearPlayer(playerPos, config.ChunkRadius);
            }
        }

        private void PersistRunArtifacts(string stopReason, bool completed)
        {
            try
            {
                string profilesDirectory = GetProfilesDirectory();
                Directory.CreateDirectory(profilesDirectory);

                string eventsPath = Path.Combine(profilesDirectory, $"{runId}-events.jsonl");
                string summaryPath = Path.Combine(profilesDirectory, $"{runId}-summary.json");
                string latestSummaryAliasPath = Path.Combine(profilesDirectory, "latest-summary.json");

                WriteEventsJsonl(eventsPath);

                var summary = BuildSummary(stopReason, completed);
                string summaryJson = JsonSerializer.Serialize(summary, JsonIndentedOptions);
                File.WriteAllText(summaryPath, summaryJson, Encoding.UTF8);
                File.Copy(summaryPath, latestSummaryAliasPath, overwrite: true);

                latestSummaryPath = summaryPath;

                if (config.ChatNotifications)
                {
                    capi.ShowChatMessage($"Schematica Plus profiling completed. Summary: {summaryPath}");
                }
            }
            catch (IOException ex)
            {
                capi.Logger.Warning($"[Schematica Plus] Failed to persist profiling artifacts: {ex.Message}");
            }
            catch (UnauthorizedAccessException ex)
            {
                capi.Logger.Warning($"[Schematica Plus] Failed to persist profiling artifacts: {ex.Message}");
            }
            catch (NotSupportedException ex)
            {
                capi.Logger.Warning($"[Schematica Plus] Failed to persist profiling artifacts: {ex.Message}");
            }
            catch (ArgumentException ex)
            {
                capi.Logger.Warning($"[Schematica Plus] Failed to persist profiling artifacts: {ex.Message}");
            }
            catch (JsonException ex)
            {
                capi.Logger.Warning($"[Schematica Plus] Failed to persist profiling artifacts: {ex.Message}");
            }
        }

        private ProfileSummary BuildSummary(string stopReason, bool completed)
        {
            DateTimeOffset endedAtUtc = DateTimeOffset.UtcNow;
            var summary = new ProfileSummary
            {
                RunId = runId,
                ScenarioName = config.ScenarioName,
                CaptureMode = config.CaptureMode,
                Completed = completed,
                StopReason = stopReason,
                StartedAtUtc = runStartedAtUtc,
                EndedAtUtc = endedAtUtc,
                IterationsRequested = config.Iterations,
                IterationsCompleted = currentIteration,
                WarmupSeconds = config.WarmupSeconds,
                MeasureSeconds = config.MeasureSeconds,
                TickIntervalMs = TickIntervalMs,
                RenderedFrames = renderedFrameCount,
                EventCount = events.Count,
                DroppedEventCount = droppedEventCount,
                Metrics = new ProfileMetrics
                {
                    TickMs = MetricSummary.From(tickDurationsMs),
                    RenderMs = MetricSummary.From(renderDurationsMs),
                    BuildProjectionMs = MetricSummary.From(buildProjectionDurationsMs),
                    ChunkScanMs = MetricSummary.From(chunkScanDurationsMs),
                    ChunkQueueMs = MetricSummary.From(chunkQueueDurationsMs),
                    LayerLookupMs = MetricSummary.From(layerLookupDurationsMs),
                    TickAllocatedBytes = MetricSummary.From(tickAllocatedBytes),
                    RenderAllocatedBytes = MetricSummary.From(renderAllocatedBytes),
                    ChunkQueueAllocatedBytes = MetricSummary.From(chunkQueueAllocatedBytes),
                    LayerLookupAllocatedBytes = MetricSummary.From(layerLookupAllocatedBytes),
                    ChunkQueueProcessedCount = MetricSummary.From(chunkQueueProcessedCounts),
                    LayerLookupCount = MetricSummary.From(layerLookupCounts)
                }
            };

            summary.Regression = CompareWithBaseline(summary);
            return summary;
        }

        private RegressionSummary CompareWithBaseline(ProfileSummary currentSummary)
        {
            string baselineDirectory = GetBaselinesDirectory();
            string baselineName = SanitizeFileName(config.BaselineName);
            if (string.IsNullOrWhiteSpace(baselineName))
            {
                baselineName = "default";
            }

            string baselinePath = Path.Combine(baselineDirectory, $"{baselineName}.json");
            var result = new RegressionSummary
            {
                BaselineName = baselineName,
                BaselinePath = baselinePath,
                Available = false
            };

            if (!File.Exists(baselinePath))
            {
                return result;
            }

            try
            {
                string baselineJson = File.ReadAllText(baselinePath, Encoding.UTF8);
                var baseline = JsonSerializer.Deserialize<ProfileSummary>(baselineJson, JsonCompactOptions);
                if (baseline?.Metrics == null)
                {
                    return result;
                }

                result.Available = true;

                EvaluateRegression(
                    result,
                    "render.p95.ms",
                    baseline.Metrics.RenderMs.P95,
                    currentSummary.Metrics.RenderMs.P95,
                    config.RenderP95RegressionThresholdPercent);

                EvaluateRegression(
                    result,
                    "tick.p95.ms",
                    baseline.Metrics.TickMs.P95,
                    currentSummary.Metrics.TickMs.P95,
                    config.TickP95RegressionThresholdPercent);

                EvaluateRegression(
                    result,
                    "render.alloc.avg.bytes",
                    baseline.Metrics.RenderAllocatedBytes.Average,
                    currentSummary.Metrics.RenderAllocatedBytes.Average,
                    config.RenderAllocationRegressionThresholdPercent);
            }
            catch (IOException ex)
            {
                result.Errors.Add(ex.Message);
            }
            catch (UnauthorizedAccessException ex)
            {
                result.Errors.Add(ex.Message);
            }
            catch (JsonException ex)
            {
                result.Errors.Add(ex.Message);
            }

            return result;
        }

        private static void EvaluateRegression(RegressionSummary target, string metricName, double baseline, double current, double thresholdPercent)
        {
            double deltaPercent = ComputeDeltaPercent(baseline, current);
            bool exceedsThreshold = deltaPercent > thresholdPercent;

            target.Metrics.Add(
                new RegressionMetric
                {
                    Name = metricName,
                    Baseline = baseline,
                    Current = current,
                    DeltaPercent = deltaPercent,
                    ThresholdPercent = thresholdPercent,
                    ExceedsThreshold = exceedsThreshold
                });
        }

        private static double ComputeDeltaPercent(double baseline, double current)
        {
            if (baseline <= 0)
            {
                return current > 0 ? 100d : 0d;
            }

            return ((current - baseline) / baseline) * 100d;
        }

        private void WriteEventsJsonl(string eventsPath)
        {
            using var writer = new StreamWriter(eventsPath, append: false, Encoding.UTF8);
            foreach (var evt in EnumerateEventsInOrder())
            {
                string line = JsonSerializer.Serialize(evt, JsonCompactOptions);
                writer.WriteLine(line);
            }
        }

        private IEnumerable<ProfileEventLine> EnumerateEventsInOrder()
        {
            if (!eventRingFilled)
            {
                foreach (var evt in events)
                {
                    yield return evt;
                }
                yield break;
            }

            for (int i = eventRingWriteIndex; i < events.Count; i++)
            {
                yield return events[i];
            }

            for (int i = 0; i < eventRingWriteIndex; i++)
            {
                yield return events[i];
            }
        }

        private void AppendEvent(
            string type,
            double? elapsedMilliseconds = null,
            long? allocatedBytes = null,
            IDictionary<string, object?>? details = null,
            bool highFrequency = false)
        {
            if (highFrequency && !IsTraceMode())
            {
                return;
            }

            var evt = new ProfileEventLine
            {
                TimestampUtc = DateTimeOffset.UtcNow,
                RunId = runId,
                Iteration = currentIteration + 1,
                TickIndex = tickInIteration,
                Type = type,
                ElapsedMilliseconds = elapsedMilliseconds,
                AllocatedBytes = allocatedBytes,
                Details = details == null ? null : new Dictionary<string, object?>(details)
            };

            if (events.Count < config.MaxEvents)
            {
                events.Add(evt);
                return;
            }

            droppedEventCount++;
            if (config.MaxEvents <= 0)
            {
                return;
            }

            events[eventRingWriteIndex] = evt;
            eventRingWriteIndex = (eventRingWriteIndex + 1) % config.MaxEvents;
            eventRingFilled = true;
        }

        private bool ShouldCaptureTraceSample(ref int counter, int interval)
        {
            counter++;
            int safeInterval = Math.Max(1, interval);
            return IsTraceMode() && counter % safeInterval == 0;
        }

        private bool IsTraceMode()
        {
            return string.Equals(config.CaptureMode, "trace", StringComparison.OrdinalIgnoreCase);
        }

        private SchematicaProfilingConfig LoadOrCreateConfig()
        {
            try
            {
                string path = ConfigPath;
                string root = Path.GetDirectoryName(path) ?? GetProfilingRootDirectory();
                Directory.CreateDirectory(root);

                if (!File.Exists(path))
                {
                    var defaultConfig = SchematicaProfilingConfig.CreateDefault().Normalize();
                    string defaultJson = JsonSerializer.Serialize(defaultConfig, JsonIndentedOptions);
                    File.WriteAllText(path, defaultJson, Encoding.UTF8);
                    return defaultConfig;
                }

                string json = File.ReadAllText(path, Encoding.UTF8);
                var loadedConfig = JsonSerializer.Deserialize<SchematicaProfilingConfig>(json, JsonCompactOptions);
                var normalizedConfig = (loadedConfig ?? SchematicaProfilingConfig.CreateDefault()).Normalize();

                string normalizedJson = JsonSerializer.Serialize(normalizedConfig, JsonIndentedOptions);
                File.WriteAllText(path, normalizedJson, Encoding.UTF8);
                return normalizedConfig;
            }
            catch (IOException ex)
            {
                capi.Logger.Warning($"[Schematica Plus] Failed to load profiling config. Using defaults. Reason: {ex.Message}");
                return SchematicaProfilingConfig.CreateDefault().Normalize();
            }
            catch (UnauthorizedAccessException ex)
            {
                capi.Logger.Warning($"[Schematica Plus] Failed to load profiling config. Using defaults. Reason: {ex.Message}");
                return SchematicaProfilingConfig.CreateDefault().Normalize();
            }
            catch (NotSupportedException ex)
            {
                capi.Logger.Warning($"[Schematica Plus] Failed to load profiling config. Using defaults. Reason: {ex.Message}");
                return SchematicaProfilingConfig.CreateDefault().Normalize();
            }
            catch (ArgumentException ex)
            {
                capi.Logger.Warning($"[Schematica Plus] Failed to load profiling config. Using defaults. Reason: {ex.Message}");
                return SchematicaProfilingConfig.CreateDefault().Normalize();
            }
            catch (JsonException ex)
            {
                capi.Logger.Warning($"[Schematica Plus] Failed to load profiling config. Using defaults. Reason: {ex.Message}");
                return SchematicaProfilingConfig.CreateDefault().Normalize();
            }
        }

        private string ResolveLatestSummaryPath()
        {
            if (!string.IsNullOrWhiteSpace(latestSummaryPath) && File.Exists(latestSummaryPath))
            {
                return latestSummaryPath;
            }

            string profilesDirectory = GetProfilesDirectory();
            if (!Directory.Exists(profilesDirectory))
            {
                return string.Empty;
            }

            var latestFile = new DirectoryInfo(profilesDirectory)
                .GetFiles("*-summary.json", SearchOption.TopDirectoryOnly)
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .FirstOrDefault();

            return latestFile?.FullName ?? string.Empty;
        }

        private string GetProfilingRootDirectory()
        {
            return capi.GetOrCreateDataPath("ModData/schematicaplus");
        }

        private string GetProfilesDirectory()
        {
            return Path.Combine(GetProfilingRootDirectory(), "profiles");
        }

        private string GetBaselinesDirectory()
        {
            return Path.Combine(GetProfilesDirectory(), "baselines");
        }

        private static string SanitizeFileName(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            var invalidChars = Path.GetInvalidFileNameChars();
            var builder = new StringBuilder(value.Length);
            foreach (char ch in value)
            {
                builder.Append(invalidChars.Contains(ch) ? '_' : ch);
            }

            return builder.ToString().Trim();
        }

        private static long StopwatchTicks()
        {
            return System.Diagnostics.Stopwatch.GetTimestamp();
        }

        private static double TicksToMilliseconds(long elapsedTicks)
        {
            return elapsedTicks * 1000d / System.Diagnostics.Stopwatch.Frequency;
        }

        private sealed class ProfileEventLine
        {
            public DateTimeOffset TimestampUtc { get; set; }
            public string RunId { get; set; } = string.Empty;
            public int Iteration { get; set; }
            public int TickIndex { get; set; }
            public string Type { get; set; } = string.Empty;
            public double? ElapsedMilliseconds { get; set; }
            public long? AllocatedBytes { get; set; }
            public Dictionary<string, object?>? Details { get; set; }
        }

        private sealed class ProfileSummary
        {
            public string RunId { get; set; } = string.Empty;
            public string ScenarioName { get; set; } = string.Empty;
            public string CaptureMode { get; set; } = "summary";
            public bool Completed { get; set; }
            public string StopReason { get; set; } = string.Empty;
            public DateTimeOffset StartedAtUtc { get; set; }
            public DateTimeOffset EndedAtUtc { get; set; }
            public int IterationsRequested { get; set; }
            public int IterationsCompleted { get; set; }
            public int WarmupSeconds { get; set; }
            public int MeasureSeconds { get; set; }
            public int TickIntervalMs { get; set; }
            public int RenderedFrames { get; set; }
            public int EventCount { get; set; }
            public int DroppedEventCount { get; set; }
            public ProfileMetrics Metrics { get; set; } = new ProfileMetrics();
            public RegressionSummary Regression { get; set; } = new RegressionSummary();
        }

        private sealed class ProfileMetrics
        {
            public MetricSummary TickMs { get; set; } = MetricSummary.Empty();
            public MetricSummary RenderMs { get; set; } = MetricSummary.Empty();
            public MetricSummary BuildProjectionMs { get; set; } = MetricSummary.Empty();
            public MetricSummary ChunkScanMs { get; set; } = MetricSummary.Empty();
            public MetricSummary ChunkQueueMs { get; set; } = MetricSummary.Empty();
            public MetricSummary LayerLookupMs { get; set; } = MetricSummary.Empty();
            public MetricSummary TickAllocatedBytes { get; set; } = MetricSummary.Empty();
            public MetricSummary RenderAllocatedBytes { get; set; } = MetricSummary.Empty();
            public MetricSummary ChunkQueueAllocatedBytes { get; set; } = MetricSummary.Empty();
            public MetricSummary LayerLookupAllocatedBytes { get; set; } = MetricSummary.Empty();
            public MetricSummary ChunkQueueProcessedCount { get; set; } = MetricSummary.Empty();
            public MetricSummary LayerLookupCount { get; set; } = MetricSummary.Empty();
        }

        private sealed class RegressionSummary
        {
            public string BaselineName { get; set; } = string.Empty;
            public string BaselinePath { get; set; } = string.Empty;
            public bool Available { get; set; }
            public List<string> Errors { get; set; } = new List<string>();
            public List<RegressionMetric> Metrics { get; set; } = new List<RegressionMetric>();
        }

        private sealed class RegressionMetric
        {
            public string Name { get; set; } = string.Empty;
            public double Baseline { get; set; }
            public double Current { get; set; }
            public double DeltaPercent { get; set; }
            public double ThresholdPercent { get; set; }
            public bool ExceedsThreshold { get; set; }
        }

        private sealed class MetricSummary
        {
            public int Count { get; set; }
            public double Average { get; set; }
            public double Min { get; set; }
            public double P50 { get; set; }
            public double P95 { get; set; }
            public double P99 { get; set; }
            public double Max { get; set; }

            public static MetricSummary Empty()
            {
                return new MetricSummary();
            }

            public static MetricSummary From(List<double> values)
            {
                if (values.Count == 0)
                {
                    return Empty();
                }

                double[] sorted = values.ToArray();
                Array.Sort(sorted);

                return new MetricSummary
                {
                    Count = sorted.Length,
                    Average = sorted.Average(),
                    Min = sorted[0],
                    P50 = Percentile(sorted, 50),
                    P95 = Percentile(sorted, 95),
                    P99 = Percentile(sorted, 99),
                    Max = sorted[^1]
                };
            }

            private static double Percentile(double[] sortedValues, double percentile)
            {
                if (sortedValues.Length == 0)
                {
                    return 0;
                }

                if (sortedValues.Length == 1)
                {
                    return sortedValues[0];
                }

                double rank = (percentile / 100d) * (sortedValues.Length - 1);
                int lower = (int)Math.Floor(rank);
                int upper = (int)Math.Ceiling(rank);

                if (lower == upper)
                {
                    return sortedValues[lower];
                }

                double weight = rank - lower;
                return sortedValues[lower] + ((sortedValues[upper] - sortedValues[lower]) * weight);
            }
        }
    }
}


