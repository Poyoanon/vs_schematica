using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Schematica.Profiling;
using Schematica.Core;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Util;
using Vintagestory.GameContent;

namespace Schematica.Rendering
{
    public class SchematicRenderer : IRenderer
    {
        private static readonly JsonSerializerOptions JsonIndentedOptions = new JsonSerializerOptions { WriteIndented = true };
        private readonly ICoreClientAPI capi;
        private readonly SchematicaModSystem modSystem;
        private readonly Dictionary<Vec3i, GhostChunk> ghostChunks = new Dictionary<Vec3i, GhostChunk>();
        private BlockPos? currentOrigin;
        private IReadOnlyList<SerializableBlock>? currentBlocks;
        private readonly int chunkSize = 4;
        private readonly Dictionary<Vec3i, List<SerializableBlock>> blocksByChunkIndex = new Dictionary<Vec3i, List<SerializableBlock>>();
        private readonly Dictionary<string, AssetLocation> assetLocationCache = new Dictionary<string, AssetLocation>(StringComparer.Ordinal);
        private readonly Dictionary<string, Block?> blockByCodeCache = new Dictionary<string, Block?>(StringComparer.Ordinal);
        private readonly Dictionary<string, long> easyPlaceHoldUntilByPosition = new Dictionary<string, long>(StringComparer.Ordinal);
        private readonly Queue<Vec3i> chunkUpdateQueue = new Queue<Vec3i>();
        private readonly HashSet<Vec3i> queuedChunkPositions = new HashSet<Vec3i>();
        private bool chunkIndexValid;
        private bool chunkIndexFallbackActive;
        private bool projectionSafeMode;
        private long projectionVersion;
        private long indexedProjectionVersion;
        private SchematicRendererRuntimeConfig runtimeConfig = SchematicRendererRuntimeConfig.CreateDefault();
        private long debugBurstUntilMs;
        private long lastRenderLogAtMs = long.MinValue;
        private long lastDistanceWarningAtMs = long.MinValue;

        // Layer rendering
        private int currentLayer;
        private bool showAllLayers;
        private BlockSchematicStructure? currentSchematic;
        private bool disposed;

        public double RenderOrder => 0.5;
        public int RenderRange => 999;
        public int CurrentLayer => currentLayer;
        public bool ShowAllLayers => showAllLayers;
        public bool HasProjection => ghostChunks.Count > 0 || chunkUpdateQueue.Count > 0;
        public int LoadedGhostChunkCount => ghostChunks.Count;
        public ISchematicaProfilingSink? ProfilingSink { get; set; }
        public string RuntimeConfigPath => Path.Combine(capi.GetOrCreateDataPath("ModData/schematicaplus"), "schematicaplus.runtime.json");

        public SchematicRenderer(ICoreClientAPI api, SchematicaModSystem modSystem)
        {
            this.capi = api;
            this.modSystem = modSystem;
            ReloadRuntimeConfig();
        }

        public void ReloadRuntimeConfig()
        {
            try
            {
                string path = RuntimeConfigPath;
                string directory = Path.GetDirectoryName(path) ?? capi.GetOrCreateDataPath("ModData/schematicaplus");
                Directory.CreateDirectory(directory);

                if (!File.Exists(path))
                {
                    runtimeConfig = SchematicRendererRuntimeConfig.CreateDefault().Normalize();
                    File.WriteAllText(path, JsonSerializer.Serialize(runtimeConfig, JsonIndentedOptions), Encoding.UTF8);
                }
                else
                {
                    string json = File.ReadAllText(path, Encoding.UTF8);
                    var loaded = JsonSerializer.Deserialize<SchematicRendererRuntimeConfig>(json);
                    runtimeConfig = (loaded ?? SchematicRendererRuntimeConfig.CreateDefault()).Normalize();
                    File.WriteAllText(path, JsonSerializer.Serialize(runtimeConfig, JsonIndentedOptions), Encoding.UTF8);
                }

                InvalidateChunkIndex();
                ClearBlockCodeCache();
            }
            catch (IOException ex)
            {
                capi.Logger.Warning($"[Schematica Plus] Failed to load runtime config. Using defaults. Reason: {ex.Message}");
                runtimeConfig = SchematicRendererRuntimeConfig.CreateDefault().Normalize();
            }
            catch (UnauthorizedAccessException ex)
            {
                capi.Logger.Warning($"[Schematica Plus] Failed to load runtime config. Using defaults. Reason: {ex.Message}");
                runtimeConfig = SchematicRendererRuntimeConfig.CreateDefault().Normalize();
            }
            catch (NotSupportedException ex)
            {
                capi.Logger.Warning($"[Schematica Plus] Failed to load runtime config. Using defaults. Reason: {ex.Message}");
                runtimeConfig = SchematicRendererRuntimeConfig.CreateDefault().Normalize();
            }
            catch (ArgumentException ex)
            {
                capi.Logger.Warning($"[Schematica Plus] Failed to load runtime config. Using defaults. Reason: {ex.Message}");
                runtimeConfig = SchematicRendererRuntimeConfig.CreateDefault().Normalize();
            }
            catch (JsonException ex)
            {
                capi.Logger.Warning($"[Schematica Plus] Failed to load runtime config. Using defaults. Reason: {ex.Message}");
                runtimeConfig = SchematicRendererRuntimeConfig.CreateDefault().Normalize();
            }
        }

        public void EnableDebugBurst(int seconds)
        {
            int clampedSeconds = Math.Clamp(seconds, 1, 300);
            debugBurstUntilMs = capi.ElapsedMilliseconds + (clampedSeconds * 1000L);
        }

        public void ClearRuntimeCaches()
        {
            ClearBlockCodeCache();
            InvalidateChunkIndex();
        }

        public void SetSchematic(BlockSchematicStructure schematic)
        {
            ArgumentNullException.ThrowIfNull(schematic);
            currentSchematic = schematic;
            currentLayer = 0;
            projectionSafeMode = false;
            ClearAllProjections();
        }

        public void SetRenderOrigin(BlockPos origin)
        {
            ArgumentNullException.ThrowIfNull(origin);

            currentOrigin = origin.Copy();
            UpdateRender();
        }

        public void NextLayer()
        {
            if (currentSchematic == null) return;

            if (currentLayer < currentSchematic.MaxY)
            {
                currentLayer++;
                UpdateRender();
                capi.ShowChatMessage(Lang.Get("schematicaplus:msg-layer", currentLayer, currentSchematic.MaxY));
            }
        }

        public void PreviousLayer()
        {
            if (currentSchematic == null) return;

            if (currentLayer > 0)
            {
                currentLayer--;
                UpdateRender();
                capi.ShowChatMessage(Lang.Get("schematicaplus:msg-layer", currentLayer, currentSchematic.MaxY));
            }
        }

        public void SetShowAllLayers(bool show)
        {
            showAllLayers = show;
            UpdateRender();
        }

        public void ToggleAllLayers()
        {
            showAllLayers = !showAllLayers;
            UpdateRender();
        }

        public void SetLayer(int layer)
        {
            if (currentSchematic == null) return;

            currentLayer = Math.Max(0, Math.Min(layer, currentSchematic.MaxY));
            UpdateRender();
        }

        public void UpdateRender()
        {
            if (currentSchematic == null) return;

            if (currentOrigin == null) return;

            var origin = currentOrigin.Copy();
            ClearAllProjections();

            var profilingSink = ProfilingSink;
            bool captureMetrics = profilingSink?.IsCapturing == true;
            long layerLookupStarted = captureMetrics ? System.Diagnostics.Stopwatch.GetTimestamp() : 0L;
            long layerLookupAllocatedBefore = captureMetrics ? GC.GetAllocatedBytesForCurrentThread() : 0L;

            IReadOnlyList<SerializableBlock> blocksToRender = showAllLayers
                ? (IReadOnlyList<SerializableBlock>)currentSchematic.Blocks
                : currentSchematic.GetBlocksAtLayer(currentLayer);

            if (captureMetrics)
            {
                double elapsedMs = (System.Diagnostics.Stopwatch.GetTimestamp() - layerLookupStarted) * 1000d / System.Diagnostics.Stopwatch.Frequency;
                long allocatedBytes = Math.Max(0, GC.GetAllocatedBytesForCurrentThread() - layerLookupAllocatedBefore);
                profilingSink!.OnLayerLookupMeasured(
                    elapsedMs,
                    showAllLayers ? -1 : currentLayer,
                    blocksToRender.Count,
                    allocatedBytes
                );
            }

            BuildProjection(blocksToRender, origin);
        }

        public void Clear()
        {
            currentSchematic = null;
            currentOrigin = null;
            currentLayer = 0;
            showAllLayers = false;
            InvalidateChunkIndex();
            ClearBlockCodeCache();
            ClearAllProjections();
        }

        public void BuildProjection(IReadOnlyList<SerializableBlock> blocks, BlockPos startPos)
        {
            ArgumentNullException.ThrowIfNull(blocks);
            ArgumentNullException.ThrowIfNull(startPos);

            var profilingSink = ProfilingSink;
            bool captureMetrics = profilingSink?.IsCapturing == true;
            long startedTicks = captureMetrics ? System.Diagnostics.Stopwatch.GetTimestamp() : 0L;
            long allocatedBefore = captureMetrics ? GC.GetAllocatedBytesForCurrentThread() : 0L;

            int chunkCount = 0;
            bool useAdaptiveSafeMode = runtimeConfig.SafeMode
                || (runtimeConfig.EnableAdaptiveSafeMode && blocks.Count >= runtimeConfig.SafeModeBlockThreshold);

            ClearAllProjections();
            if (blocks.Count == 0)
            {
                if (captureMetrics)
                {
                    double elapsedMs = (System.Diagnostics.Stopwatch.GetTimestamp() - startedTicks) * 1000d / System.Diagnostics.Stopwatch.Frequency;
                    long allocatedBytes = Math.Max(0, GC.GetAllocatedBytesForCurrentThread() - allocatedBefore);
                    profilingSink!.OnBuildProjectionMeasured(elapsedMs, blockCount: 0, chunkCount: 0, allocatedBytes);
                }
                return;
            }

            currentOrigin = startPos.Copy();
            currentBlocks = blocks;
            projectionVersion++;
            projectionSafeMode = useAdaptiveSafeMode;
            BuildChunkIndexForCurrentProjection();

            if (!runtimeConfig.SafeMode && projectionSafeMode)
            {
                ClearBlockCodeCache();
            }

            if (ShouldEmitHotPathLog(ref lastRenderLogAtMs, runtimeConfig.RenderLogCooldownMs))
            {
                capi.Logger.Debug($"[Schematica Plus] BuildProjection: startPos={startPos}, blocks={blocks.Count}");
            }

            if (!projectionSafeMode)
            {
                var blocksByChunk = new Dictionary<Vec3i, List<GhostBlock>>();

                foreach (var blockData in blocks)
                {
                    var worldPos = new BlockPos(
                        currentOrigin.X + blockData.X,
                        currentOrigin.Y + blockData.Y,
                        currentOrigin.Z + blockData.Z
                    );

                    var chunkPos = new Vec3i(
                        FloorDiv(worldPos.X, chunkSize),
                        FloorDiv(worldPos.Y, chunkSize),
                        FloorDiv(worldPos.Z, chunkSize)
                    );

                    if (!blocksByChunk.TryGetValue(chunkPos, out var blocksInChunk))
                    {
                        blocksInChunk = new List<GhostBlock>();
                        blocksByChunk[chunkPos] = blocksInChunk;
                    }

                    var block = ResolveBlockByCode(blockData.BlockCode);
                    if (block != null && block.Id != 0)
                    {
                        var currentBlock = capi.World.BlockAccessor.GetBlock(worldPos);
                        bool isEmpty = currentBlock?.Id == 0;
                        bool isCorrect = BlockValidator.IsBlockCorrect(capi, worldPos, blockData);

                        if (!isCorrect || IsEasyPlaceHeld(worldPos))
                        {
                            blocksInChunk.Add(new GhostBlock
                            {
                                Position = worldPos,
                                Block = block,
                                BlockData = blockData,
                                IsEmpty = isEmpty
                            });
                        }
                    }
                }

                foreach (var kvp in blocksByChunk)
                {
                    var chunk = new GhostChunk(capi, kvp.Key, chunkSize);
                    chunk.BuildMesh(kvp.Value);
                    ghostChunks[kvp.Key] = chunk;
                }

                chunkCount = blocksByChunk.Count;
            }
            else
            {
                if (!runtimeConfig.EnableChunkIndex || blocksByChunkIndex.Count == 0)
                {
                    foreach (var blockData in blocks)
                    {
                        var worldPos = new BlockPos(
                            currentOrigin.X + blockData.X,
                            currentOrigin.Y + blockData.Y,
                            currentOrigin.Z + blockData.Z
                        );

                        var chunkPos = new Vec3i(
                            FloorDiv(worldPos.X, chunkSize),
                            FloorDiv(worldPos.Y, chunkSize),
                            FloorDiv(worldPos.Z, chunkSize)
                        );

                        EnqueueChunkForUpdate(chunkPos);
                    }
                }
                else
                {
                    foreach (var chunkPos in blocksByChunkIndex.Keys)
                    {
                        EnqueueChunkForUpdate(chunkPos);
                    }
                }

                chunkCount = ProcessChunkUpdateQueue(runtimeConfig.ChunkBuildsPerFrame);
            }

            if (captureMetrics)
            {
                double elapsedMs = (System.Diagnostics.Stopwatch.GetTimestamp() - startedTicks) * 1000d / System.Diagnostics.Stopwatch.Frequency;
                long allocatedBytes = Math.Max(0, GC.GetAllocatedBytesForCurrentThread() - allocatedBefore);
                profilingSink!.OnBuildProjectionMeasured(elapsedMs, blocks.Count, chunkCount, allocatedBytes);
            }
        }

        public void OnRenderFrame(float deltaTime, EnumRenderStage stage)
        {
            var profilingSink = ProfilingSink;
            bool captureMetrics = profilingSink?.IsCapturing == true;
            long startedTicks = captureMetrics ? System.Diagnostics.Stopwatch.GetTimestamp() : 0L;
            long allocatedBefore = captureMetrics ? GC.GetAllocatedBytesForCurrentThread() : 0L;

            int renderedChunkCount = 0;

            int queueProcessed = 0;
            if (chunkUpdateQueue.Count > 0)
            {
                int queueBefore = chunkUpdateQueue.Count;
                long queueUpdateStartTicks = captureMetrics ? System.Diagnostics.Stopwatch.GetTimestamp() : 0L;
                long queueUpdateAllocatedBefore = captureMetrics ? GC.GetAllocatedBytesForCurrentThread() : 0L;
                queueProcessed = ProcessChunkUpdateQueue(runtimeConfig.ChunkBuildsPerFrame);
                if (captureMetrics && queueProcessed > 0)
                {
                    double queueElapsedMs = (System.Diagnostics.Stopwatch.GetTimestamp() - queueUpdateStartTicks) * 1000d / System.Diagnostics.Stopwatch.Frequency;
                    long queueAllocatedBytes = Math.Max(0, GC.GetAllocatedBytesForCurrentThread() - queueUpdateAllocatedBefore);
                    profilingSink!.OnChunkUpdateQueueMeasured(
                        queueElapsedMs,
                        queueBefore,
                        queueProcessed,
                        queueBefore - queueProcessed,
                        queueAllocatedBytes
                    );
                }
            }

            if (ghostChunks.Count == 0 && chunkUpdateQueue.Count == 0)
            {
                if (captureMetrics)
                {
                    double elapsedMsEmpty = (System.Diagnostics.Stopwatch.GetTimestamp() - startedTicks) * 1000d / System.Diagnostics.Stopwatch.Frequency;
                    long allocatedBytesEmpty = Math.Max(0, GC.GetAllocatedBytesForCurrentThread() - allocatedBefore);
                    profilingSink!.OnRenderFrameMeasured(elapsedMsEmpty, allocatedBytesEmpty, renderedChunkCount);
                }
                return;
            }

            if (ghostChunks.Count > 0 && deltaTime > 0 && ShouldEmitHotPathLog(ref lastRenderLogAtMs, runtimeConfig.RenderLogCooldownMs))
            {
                capi.Logger.VerboseDebug($"[Schematica Plus] Rendering {ghostChunks.Count} chunks");
            }

            var prog = capi.Render.PreparedStandardShader(0, 0, 0);
            prog.Use();

            capi.Render.GlToggleBlend(true, EnumBlendMode.Standard);

            var cameraPos = capi.World.Player.Entity.CameraPos;

            // Check distance to the nearest chunk for diagnostics.
            if (ghostChunks.Count > 0)
            {
                var firstChunk = ghostChunks.First().Key;
                var chunkWorldPos = new Vec3d(
                    firstChunk.X * chunkSize,
                    firstChunk.Y * chunkSize,
                    firstChunk.Z * chunkSize
                );
                var distance = chunkWorldPos.DistanceTo(cameraPos);

                if (distance > 1000 && ShouldEmitHotPathLog(ref lastDistanceWarningAtMs, runtimeConfig.DistanceWarningCooldownMs))
                {
                    capi.Logger.Warning($"[Schematica Plus] Chunks too far from camera: {distance} blocks");
                }
            }

            prog.ViewMatrix = capi.Render.CameraMatrixOriginf;
            prog.ProjectionMatrix = capi.Render.CurrentProjectionMatrix;
            prog.ExtraGlow = 20;

            foreach (var chunk in ghostChunks.Values)
            {
                chunk.Render(prog, cameraPos);
                renderedChunkCount++;
            }

            prog.Stop();
            capi.Render.GlToggleBlend(false);

            if (captureMetrics)
            {
                double elapsedMs = (System.Diagnostics.Stopwatch.GetTimestamp() - startedTicks) * 1000d / System.Diagnostics.Stopwatch.Frequency;
                long allocatedBytes = Math.Max(0, GC.GetAllocatedBytesForCurrentThread() - allocatedBefore);
                profilingSink!.OnRenderFrameMeasured(elapsedMs, allocatedBytes, renderedChunkCount);
            }
        }

        public void ClearAllProjections()
        {
            foreach (var chunk in ghostChunks.Values)
            {
                chunk.Dispose();
            }
            ghostChunks.Clear();
            currentOrigin = null;
            currentBlocks = null;
            projectionSafeMode = false;
            easyPlaceHoldUntilByPosition.Clear();
            chunkUpdateQueue.Clear();
            queuedChunkPositions.Clear();
            InvalidateChunkIndex();
        }

        public void UpdateChunksNearPlayer(BlockPos playerPos, int radius = 10)
        {
            ArgumentNullException.ThrowIfNull(playerPos);

            var profilingSink = ProfilingSink;
            bool captureMetrics = profilingSink?.IsCapturing == true;
            long startedTicks = captureMetrics ? System.Diagnostics.Stopwatch.GetTimestamp() : 0L;
            long allocatedBefore = captureMetrics ? GC.GetAllocatedBytesForCurrentThread() : 0L;

            if (currentBlocks == null || currentOrigin == null) return;

            var chunksToUpdate = new List<Vec3i>();
            bool useChunkIteration = !runtimeConfig.SafeMode && runtimeConfig.EnableChunkRangeIteration;
            if (useChunkIteration)
            {
                int margin = runtimeConfig.ChunkRangeConservativeMargin;
                int playerChunkX = FloorDiv(playerPos.X, chunkSize);
                int playerChunkY = FloorDiv(playerPos.Y, chunkSize);
                int playerChunkZ = FloorDiv(playerPos.Z, chunkSize);
                int chunkRadius = (int)Math.Ceiling(radius / (double)chunkSize) + margin;

                for (int cx = playerChunkX - chunkRadius; cx <= playerChunkX + chunkRadius; cx++)
                {
                    for (int cy = playerChunkY - chunkRadius; cy <= playerChunkY + chunkRadius; cy++)
                    {
                        for (int cz = playerChunkZ - chunkRadius; cz <= playerChunkZ + chunkRadius; cz++)
                        {
                            chunksToUpdate.Add(new Vec3i(cx, cy, cz));
                        }
                    }
                }
            }
            else
            {
                var fallbackSet = new HashSet<Vec3i>();
                for (int x = playerPos.X - radius; x <= playerPos.X + radius; x++)
                {
                    for (int y = playerPos.Y - radius; y <= playerPos.Y + radius; y++)
                    {
                        for (int z = playerPos.Z - radius; z <= playerPos.Z + radius; z++)
                        {
                            fallbackSet.Add(new Vec3i(FloorDiv(x, chunkSize), FloorDiv(y, chunkSize), FloorDiv(z, chunkSize)));
                        }
                    }
                }

                chunksToUpdate.AddRange(fallbackSet);
            }

            int updatedChunkCount = 0;
            foreach (var chunkPos in chunksToUpdate)
            {
                if (runtimeConfig.SafeMode || projectionSafeMode)
                {
                    if (EnqueueChunkForUpdate(chunkPos))
                    {
                        updatedChunkCount++;
                    }
                    continue;
                }

                if (ghostChunks.ContainsKey(chunkPos))
                {
                    UpdateSingleChunk(chunkPos);
                    updatedChunkCount++;
                }
            }

            if ((runtimeConfig.SafeMode || projectionSafeMode) && chunkUpdateQueue.Count > 0)
            {
                updatedChunkCount += ProcessChunkUpdateQueue(runtimeConfig.ChunkBuildsPerFrame);
            }

            if (!runtimeConfig.SafeMode && runtimeConfig.EnableChunkParityCheckInDebugBurst && IsDebugBurstActive())
            {
                var paritySet = new HashSet<Vec3i>();
                for (int x = playerPos.X - radius; x <= playerPos.X + radius; x++)
                {
                    for (int y = playerPos.Y - radius; y <= playerPos.Y + radius; y++)
                    {
                        for (int z = playerPos.Z - radius; z <= playerPos.Z + radius; z++)
                        {
                            paritySet.Add(new Vec3i(FloorDiv(x, chunkSize), FloorDiv(y, chunkSize), FloorDiv(z, chunkSize)));
                        }
                    }
                }

                if (paritySet.Count != chunksToUpdate.Count && ShouldEmitHotPathLog(ref lastDistanceWarningAtMs, runtimeConfig.DistanceWarningCooldownMs))
                {
                    capi.Logger.Warning($"[Schematica Plus] Chunk range parity mismatch. legacy={paritySet.Count}, optimized={chunksToUpdate.Count}");
                }
            }

            if (captureMetrics)
            {
                double elapsedMs = (System.Diagnostics.Stopwatch.GetTimestamp() - startedTicks) * 1000d / System.Diagnostics.Stopwatch.Frequency;
                long allocatedBytes = Math.Max(0, GC.GetAllocatedBytesForCurrentThread() - allocatedBefore);
                profilingSink!.OnChunkScanMeasured(elapsedMs, chunksToUpdate.Count, updatedChunkCount, allocatedBytes);
                profilingSink!.OnChunkUpdateQueueMeasured(elapsedMs, chunksToUpdate.Count, updatedChunkCount, chunkUpdateQueue.Count, allocatedBytes);
            }
        }

        private void UpdateSingleChunk(Vec3i chunkPos)
        {
            if (currentBlocks == null || currentOrigin == null) return;

            var blocks = currentBlocks;
            var origin = currentOrigin;

            var blocksInChunk = new List<GhostBlock>();

            var sourceBlocks = GetBlocksForChunk(chunkPos, blocks);
            foreach (var blockData in sourceBlocks)
            {
                var worldPos = new BlockPos(
                    origin.X + blockData.X,
                    origin.Y + blockData.Y,
                    origin.Z + blockData.Z
                );

                var blockChunkPos = new Vec3i(
                    FloorDiv(worldPos.X, chunkSize),
                    FloorDiv(worldPos.Y, chunkSize),
                    FloorDiv(worldPos.Z, chunkSize)
                );

                if (!blockChunkPos.Equals(chunkPos))
                {
                    continue;
                }

                var block = ResolveBlockByCode(blockData.BlockCode);
                if (block != null && block.Id != 0)
                {
                    var currentBlock = capi.World.BlockAccessor.GetBlock(worldPos);
                    bool isCorrect = BlockValidator.IsBlockCorrect(capi, worldPos, blockData);
                    bool isEmpty = currentBlock?.Id == 0;

                    if (!isCorrect || IsEasyPlaceHeld(worldPos))
                    {
                        blocksInChunk.Add(new GhostBlock
                        {
                            Position = worldPos,
                            Block = block,
                            BlockData = blockData,
                            IsEmpty = isEmpty
                        });
                    }
                }
            }

            if (ghostChunks.TryGetValue(chunkPos, out var existingChunk))
            {
                existingChunk.Dispose();
                ghostChunks.Remove(chunkPos);
            }

            if (blocksInChunk.Count > 0)
            {
                var chunk = new GhostChunk(capi, chunkPos, chunkSize);
                chunk.BuildMesh(blocksInChunk);
                ghostChunks[chunkPos] = chunk;
            }
        }

        public bool TryGetEasyPlaceTarget(BlockPos worldPos, out SerializableBlock? blockData, out bool isCorrect)
        {
            ArgumentNullException.ThrowIfNull(worldPos);

            blockData = null;
            isCorrect = false;

            if (currentBlocks == null || currentOrigin == null)
            {
                return false;
            }

            var origin = currentOrigin;
            var chunkPos = new Vec3i(
                FloorDiv(worldPos.X, chunkSize),
                FloorDiv(worldPos.Y, chunkSize),
                FloorDiv(worldPos.Z, chunkSize)
            );

            foreach (var candidate in GetBlocksForChunk(chunkPos, currentBlocks))
            {
                if (origin.X + candidate.X != worldPos.X
                    || origin.Y + candidate.Y != worldPos.Y
                    || origin.Z + candidate.Z != worldPos.Z)
                {
                    continue;
                }

                blockData = candidate;
                isCorrect = BlockValidator.IsBlockCorrect(capi, worldPos, candidate);
                return !isCorrect;
            }

            return false;
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (disposed) return;
            if (disposing)
            {
                ClearAllProjections();
                ClearBlockCodeCache();
            }

            disposed = true;
        }

        private IReadOnlyList<SerializableBlock> GetBlocksForChunk(Vec3i chunkPos, IReadOnlyList<SerializableBlock> fallbackSource)
        {
            if (!runtimeConfig.EnableChunkIndex || !chunkIndexValid || indexedProjectionVersion != projectionVersion || chunkIndexFallbackActive)
            {
                return fallbackSource;
            }

            if (blocksByChunkIndex.TryGetValue(chunkPos, out var indexedBlocks))
            {
                return indexedBlocks;
            }

            if (runtimeConfig.EnableFallbackOnIndexMiss)
            {
                chunkIndexFallbackActive = true;
                if (ShouldEmitHotPathLog(ref lastDistanceWarningAtMs, runtimeConfig.DistanceWarningCooldownMs))
                {
                    capi.Logger.Warning($"[Schematica Plus] Chunk index miss detected for {chunkPos}. Falling back to safe scan mode.");
                }
                return fallbackSource;
            }

            return Array.Empty<SerializableBlock>();
        }

        private bool EnqueueChunkForUpdate(Vec3i chunkPos)
        {
            if (blocksByChunkIndex.Count > 0 && !blocksByChunkIndex.ContainsKey(chunkPos))
            {
                if (runtimeConfig.EnableFallbackOnIndexMiss)
                {
                    return false;
                }
            }

            if (queuedChunkPositions.Count >= runtimeConfig.ChunkUpdateQueueMaxSize)
            {
                return false;
            }

            if (queuedChunkPositions.Add(chunkPos))
            {
                chunkUpdateQueue.Enqueue(chunkPos);
                return true;
            }

            return false;
        }

        public void HoldEasyPlaceTarget(BlockPos worldPos, int holdMilliseconds)
        {
            ArgumentNullException.ThrowIfNull(worldPos);

            int clampedMilliseconds = Math.Clamp(holdMilliseconds, 250, 10000);
            easyPlaceHoldUntilByPosition[FormatBlockPosKey(worldPos)] = capi.ElapsedMilliseconds + clampedMilliseconds;

            var chunkPos = new Vec3i(
                FloorDiv(worldPos.X, chunkSize),
                FloorDiv(worldPos.Y, chunkSize),
                FloorDiv(worldPos.Z, chunkSize)
            );
            EnqueueChunkForUpdate(chunkPos);
        }

        private bool IsEasyPlaceHeld(BlockPos worldPos)
        {
            string key = FormatBlockPosKey(worldPos);
            if (!easyPlaceHoldUntilByPosition.TryGetValue(key, out long holdUntilMs))
            {
                return false;
            }

            if (holdUntilMs >= capi.ElapsedMilliseconds)
            {
                return true;
            }

            easyPlaceHoldUntilByPosition.Remove(key);
            return false;
        }

        private static string FormatBlockPosKey(BlockPos pos)
        {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"{pos.X}/{pos.Y}/{pos.Z}"
            );
        }

        private int ProcessChunkUpdateQueue(int maxPerFrame)
        {
            int processed = 0;
            int limit = Math.Max(1, maxPerFrame);

            while (chunkUpdateQueue.Count > 0 && processed < limit)
            {
                var chunkPos = chunkUpdateQueue.Dequeue();
                queuedChunkPositions.Remove(chunkPos);
                UpdateSingleChunk(chunkPos);
                processed++;
            }

            return processed;
        }

        private void BuildChunkIndexForCurrentProjection()
        {
            blocksByChunkIndex.Clear();
            chunkIndexValid = false;
            chunkIndexFallbackActive = false;

            if (!runtimeConfig.EnableChunkIndex || currentBlocks == null || currentOrigin == null)
            {
                return;
            }

            foreach (var blockData in currentBlocks)
            {
                var worldPos = new BlockPos(
                    currentOrigin.X + blockData.X,
                    currentOrigin.Y + blockData.Y,
                    currentOrigin.Z + blockData.Z
                );

                var chunkPos = new Vec3i(
                    FloorDiv(worldPos.X, chunkSize),
                    FloorDiv(worldPos.Y, chunkSize),
                    FloorDiv(worldPos.Z, chunkSize)
                );

                if (!blocksByChunkIndex.TryGetValue(chunkPos, out var list))
                {
                    list = new List<SerializableBlock>();
                    blocksByChunkIndex[chunkPos] = list;
                }

                list.Add(blockData);
            }

            indexedProjectionVersion = projectionVersion;
            chunkIndexValid = true;
        }

        private void InvalidateChunkIndex()
        {
            blocksByChunkIndex.Clear();
            chunkIndexValid = false;
            chunkIndexFallbackActive = false;
            indexedProjectionVersion = -1;
        }

        private Block? ResolveBlockByCode(string blockCode)
        {
            if (runtimeConfig.SafeMode || !runtimeConfig.EnableBlockCodeCache)
            {
                return capi.World.GetBlock(new AssetLocation(blockCode));
            }

            if (blockByCodeCache.TryGetValue(blockCode, out var cachedBlock))
            {
                return cachedBlock;
            }

            if (blockByCodeCache.Count >= runtimeConfig.BlockCodeCacheMaxEntries)
            {
                ClearBlockCodeCache();
            }

            if (!assetLocationCache.TryGetValue(blockCode, out var location))
            {
                location = new AssetLocation(blockCode);
                assetLocationCache[blockCode] = location;
            }

            var resolvedBlock = capi.World.GetBlock(location);
            blockByCodeCache[blockCode] = resolvedBlock;
            return resolvedBlock;
        }

        private void ClearBlockCodeCache()
        {
            assetLocationCache.Clear();
            blockByCodeCache.Clear();
        }

        private bool ShouldEmitHotPathLog(ref long lastLogAtMs, int cooldownMs)
        {
            if (runtimeConfig.SafeMode)
            {
                return false;
            }

            bool activeByFlag = runtimeConfig.EnableHotPathLogs;
            bool activeByBurst = IsDebugBurstActive();
            if (!activeByFlag && !activeByBurst)
            {
                return false;
            }

            if (!runtimeConfig.EnableRenderLogThrottling || cooldownMs <= 0)
            {
                lastLogAtMs = capi.ElapsedMilliseconds;
                return true;
            }

            long nowMs = capi.ElapsedMilliseconds;
            if (lastLogAtMs == long.MinValue || nowMs - lastLogAtMs >= cooldownMs)
            {
                lastLogAtMs = nowMs;
                return true;
            }

            return false;
        }

        private bool IsDebugBurstActive()
        {
            return capi.ElapsedMilliseconds <= debugBurstUntilMs;
        }

        private static int FloorDiv(int value, int divisor)
        {
            int quotient = value / divisor;
            int remainder = value % divisor;
            if (remainder != 0 && ((remainder < 0) != (divisor < 0)))
            {
                quotient--;
            }

            return quotient;
        }

        private sealed class GhostChunk
        {
            private readonly Vec3i chunkPos;
            private readonly int chunkSize;
            private MultiTextureMeshRef? meshRef;
            private readonly ICoreClientAPI capi;

            public GhostChunk(ICoreClientAPI api, Vec3i pos, int size)
            {
                this.capi = api;
                this.chunkPos = pos;
                this.chunkSize = size;
            }

            public void BuildMesh(List<GhostBlock> blocks)
            {
                var meshData = new MeshData(24 * blocks.Count, 36 * blocks.Count, false, true, true, false);

                foreach (var ghostBlock in blocks)
                {
                    var blockMesh = GenerateMeshForBlock(ghostBlock);
                    if (blockMesh == null || blockMesh.VerticesCount == 0) continue;

                    var clonedMesh = blockMesh.Clone();

                    byte alpha = 120;
                    for (int i = 0; i < clonedMesh.VerticesCount; i++)
                    {
                        if (ghostBlock.IsEmpty)
                        {
                            clonedMesh.Rgba[i * 4] = 255;
                            clonedMesh.Rgba[i * 4 + 1] = 255;
                            clonedMesh.Rgba[i * 4 + 2] = 255;
                        }
                        else
                        {
                            clonedMesh.Rgba[i * 4] = 255;
                            clonedMesh.Rgba[i * 4 + 1] = 100;
                            clonedMesh.Rgba[i * 4 + 2] = 100;
                        }
                        clonedMesh.Rgba[i * 4 + 3] = alpha;
                    }

                    var localPos = new Vec3i(
                        ghostBlock.Position.X - (chunkPos.X * chunkSize),
                        ghostBlock.Position.Y - (chunkPos.Y * chunkSize),
                        ghostBlock.Position.Z - (chunkPos.Z * chunkSize)
                    );

                    meshData.AddMeshData(clonedMesh, localPos.X, localPos.Y, localPos.Z);
                }

                if (meshData.VerticesCount > 0)
                {
                    meshRef = capi.Render.UploadMultiTextureMesh(meshData);
                }
            }

            private MeshData? GenerateMeshForBlock(GhostBlock ghostBlock)
            {
                if (ghostBlock.Block.Code.Path.Contains("chiseled", StringComparison.Ordinal) && !ghostBlock.BlockData.BlockEntityData.IsEmpty)
                {
                    try
                    {
                        string ascii85Data = System.Text.Encoding.UTF8.GetString(ghostBlock.BlockData.BlockEntityData.ToArray());
                        byte[] decodedData = Ascii85.Decode(ascii85Data);

                        TreeAttribute tree = new TreeAttribute();
                        using (var ms = new MemoryStream(decodedData))
                        {
                            using var reader = new BinaryReader(ms);
                            tree.FromBytes(reader);
                        }

                        int[] materials = Array.Empty<int>();
                        var materialCodesAttr = tree["materialCodes"] as StringArrayAttribute;
                        if (materialCodesAttr != null && materialCodesAttr.value.Length > 0)
                        {
                            materials = new int[materialCodesAttr.value.Length];
                            for (int i = 0; i < materialCodesAttr.value.Length; i++)
                            {
                                var block = capi.World.GetBlock(new AssetLocation(materialCodesAttr.value[i]));
                                materials[i] = block?.Id ?? 0;
                            }
                        }
                        else
                        {
                            materials = BlockEntityMicroBlock.MaterialIdsFromAttributes(tree, capi.World);
                        }

                        uint[] cuboids = BlockEntityMicroBlock.GetVoxelCuboids(tree);

                        if (cuboids != null && materials.Length > 0 && cuboids.Length > 0)
                        {
                            var voxelCuboids = new List<uint>(cuboids);
                            var meshData = BlockEntityMicroBlock.CreateMesh(capi, voxelCuboids, materials, null, null, cuboids, 0);

                            if (meshData != null && meshData.VerticesCount > 0)
                            {
                                return meshData.Clone();
                            }
                        }
                    }
                    catch (FormatException ex)
                    {
                        capi.Logger.Warning($"[Schematica Plus] Failed to create chiseled mesh: {ex.Message}");
                    }
                    catch (IOException ex)
                    {
                        capi.Logger.Warning($"[Schematica Plus] Failed to create chiseled mesh: {ex.Message}");
                    }
                }

                return capi.TesselatorManager.GetDefaultBlockMesh(ghostBlock.Block)?.Clone();
            }

            public void Render(IStandardShaderProgram prog, Vec3d cameraPos)
            {
                if (meshRef == null) return;

                var chunkWorldPos = new Vec3d(
                    chunkPos.X * chunkSize,
                    chunkPos.Y * chunkSize,
                    chunkPos.Z * chunkSize
                );

                prog.ModelMatrix = new Matrixf()
                    .Identity()
                    .Translate(
                        (float)(chunkWorldPos.X - cameraPos.X),
                        (float)(chunkWorldPos.Y - cameraPos.Y),
                        (float)(chunkWorldPos.Z - cameraPos.Z))
                    .Values;

                capi.Render.RenderMultiTextureMesh(meshRef, "tex");
            }

            public void Dispose()
            {
                meshRef?.Dispose();
            }
        }

        private sealed class GhostBlock
        {
            public BlockPos Position { get; set; } = new BlockPos(0, 0, 0);
            public Block Block { get; set; } = null!;
            public SerializableBlock BlockData { get; set; } = new SerializableBlock();
            public bool IsEmpty { get; set; }
        }
    }
}
