using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using ImGuiNET;
using Newtonsoft.Json;
using Schematica.Core;
using Schematica.Rendering;
using Schematica.Utils;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Util;
using Vintagestory.GameContent;
using VSImGui;
using VSImGui.API;

namespace Schematica.GUI
{
    public sealed class SchematicaImGuiWindow : IDisposable
    {
        private const float BlockCounterIconSize = 24f;

        private readonly ICoreClientAPI capi;
        private readonly SchematicaModSystem modSystem;
        private readonly BlockPos worldSpawn;
        private readonly ImGuiModSystem imGuiSystem;
        private readonly Dictionary<string, string> blockNameCache = new(StringComparer.Ordinal);
        private readonly Dictionary<string, ItemStack?> blockIconStackCache = new(StringComparer.Ordinal);
        private readonly Dictionary<string, BlockCounterIcon> blockIconCache = new(StringComparer.Ordinal);

        private bool open;
        private bool disposed;
        private bool showAllLayers;
        private string filename = string.Empty;
        private string selectedSchematic = string.Empty;
        private string[] schematics = Array.Empty<string>();
        private BlockPos firstPoint = new BlockPos(0, 0, 0);
        private BlockPos secondPoint = new BlockPos(0, 0, 0);
        private BlockPos renderPos = new BlockPos(0, 0, 0);
        private BlockCounterRow[] counterRows = Array.Empty<BlockCounterRow>();

        public SchematicaImGuiWindow(ICoreClientAPI capi, SchematicaModSystem modSystem)
        {
            this.capi = capi ?? throw new ArgumentNullException(nameof(capi));
            this.modSystem = modSystem ?? throw new ArgumentNullException(nameof(modSystem));
            worldSpawn = capi.World.DefaultSpawnPosition.AsBlockPos;
            imGuiSystem = capi.ModLoader.GetModSystem<ImGuiModSystem>();
            imGuiSystem.Draw += Draw;
            imGuiSystem.Closed += OnClosed;

            LoadState();
            RefreshSchematicsList();
        }

        public void Toggle()
        {
            if (open)
            {
                open = false;
                return;
            }

            LoadState();
            RefreshSchematicsList();
            RebuildCounterRows();
            open = true;
            imGuiSystem.Show();
        }

        public void Hide()
        {
            open = false;
        }

        public void InvalidateCounter()
        {
            blockNameCache.Clear();
            blockIconStackCache.Clear();
            blockIconCache.Clear();
            RebuildCounterRows();
        }

        private void LoadState()
        {
            firstPoint = modSystem.GuiState.FirstPoint.Copy();
            secondPoint = modSystem.GuiState.SecondPoint.Copy();
            renderPos = modSystem.GuiState.RenderPos.Copy();
            selectedSchematic = modSystem.GuiState.SelectedSchematic ?? string.Empty;
            filename = modSystem.GuiState.LastFilename ?? string.Empty;
            showAllLayers = modSystem.GuiState.ShowAllLayers;

            if (firstPoint.X == 0 && firstPoint.Y == 0 && firstPoint.Z == 0)
            {
                var playerPos = SchematicaHelpers.GetPlayerBlockPos(capi);
                firstPoint = new BlockPos(playerPos.X - 5, playerPos.Y - 1, playerPos.Z - 5);
                secondPoint = new BlockPos(playerPos.X + 5, playerPos.Y + 5, playerPos.Z + 5);
            }
        }

        private void OnClosed()
        {
            open = false;
        }

        private CallbackGUIStatus Draw(float deltaSeconds)
        {
            if (!open)
            {
                return CallbackGUIStatus.Closed;
            }

            ImGui.SetNextWindowSize(new Vector2(760, 620), ImGuiCond.FirstUseEver);
            if (!ImGui.Begin("Rustmatica", ref open, ImGuiWindowFlags.NoCollapse))
            {
                ImGui.End();
                return CallbackGUIStatus.GrabMouse;
            }

            DrawSummary();

            if (ImGui.BeginTabBar("schematicaplusTabs"))
            {
                if (ImGui.BeginTabItem(Lang.Get("schematicaplus:gui-save-menu")))
                {
                    DrawSaveTab();
                    ImGui.EndTabItem();
                }

                if (ImGui.BeginTabItem(Lang.Get("schematicaplus:gui-load-menu")))
                {
                    DrawLoadTab();
                    ImGui.EndTabItem();
                }

                if (ImGui.BeginTabItem(Lang.Get("schematicaplus:gui-block-counter-title")))
                {
                    DrawBlockCounterTab();
                    ImGui.EndTabItem();
                }

                ImGui.EndTabBar();
            }

            ImGui.End();
            return CallbackGUIStatus.GrabMouse;
        }

        private void DrawSummary()
        {
            var schematic = modSystem.CurrentSchematic;
            if (schematic == null)
            {
                ImGui.TextUnformatted(Lang.Get("schematicaplus:gui-no-schematic"));
                ImGui.Separator();
                return;
            }

            ImGui.TextUnformatted(string.Format(
                CultureInfo.InvariantCulture,
                "{0}: {1} x {2} x {3}    {4}: {5}    {6}: {7}/{8}",
                Lang.Get("schematicaplus:gui-size"),
                schematic.SizeX,
                schematic.SizeY,
                schematic.SizeZ,
                Lang.Get("schematicaplus:gui-total-blocks"),
                schematic.TotalBlocks,
                Lang.Get("schematicaplus:gui-current-layer-label"),
                modSystem.Renderer.CurrentLayer,
                schematic.MaxY));
            ImGui.Separator();
        }

        private void DrawSaveTab()
        {
            DrawBlockPosEditor(Lang.Get("schematicaplus:gui-first-point"), ref firstPoint, saveToState: () => modSystem.GuiState.FirstPoint = firstPoint.Copy());
            ImGui.SameLine();
            if (ImGui.Button(Lang.Get("schematicaplus:gui-use-player-pos") + "##first"))
            {
                firstPoint = SchematicaHelpers.GetPlayerBlockPos(capi).Copy();
                modSystem.GuiState.FirstPoint = firstPoint.Copy();
            }

            DrawBlockPosEditor(Lang.Get("schematicaplus:gui-second-point"), ref secondPoint, saveToState: () => modSystem.GuiState.SecondPoint = secondPoint.Copy());
            ImGui.SameLine();
            if (ImGui.Button(Lang.Get("schematicaplus:gui-use-player-pos") + "##second"))
            {
                secondPoint = SchematicaHelpers.GetPlayerBlockPos(capi).Copy();
                modSystem.GuiState.SecondPoint = secondPoint.Copy();
            }

            ImGui.Separator();
            ImGui.SetNextItemWidth(320);
            ImGui.InputText(Lang.Get("schematicaplus:gui-filename"), ref filename, 256);
            if (ImGui.Button(Lang.Get("schematicaplus:gui-save")))
            {
                SaveSchematic();
            }
        }

        private void DrawLoadTab()
        {
            if (ImGui.Button(Lang.Get("schematicaplus:gui-refresh")))
            {
                RefreshSchematicsList();
            }

            ImGui.SameLine();
            if (ImGui.Button(Lang.Get("schematicaplus:gui-clear")))
            {
                modSystem.ClearSchematic();
                RebuildCounterRows();
                capi.ShowChatMessage(Lang.Get("schematicaplus:msg-cleared"));
            }

            DrawSchematicCombo();

            if (ImGui.Button(Lang.Get("schematicaplus:gui-load")))
            {
                LoadSelectedSchematic();
            }

            ImGui.Separator();
            DrawBlockPosEditor(Lang.Get("schematicaplus:gui-render-position"), ref renderPos, saveToState: UpdateRenderPosition);
            ImGui.SameLine();
            if (ImGui.Button(Lang.Get("schematicaplus:gui-set-here")))
            {
                renderPos = SchematicaHelpers.GetPlayerBlockPos(capi).Copy();
                UpdateRenderPosition();
            }

            if (ImGui.Button(Lang.Get("schematicaplus:gui-rotate-left")))
            {
                TransformCurrentSchematic(-90, null);
            }

            ImGui.SameLine();
            if (ImGui.Button(Lang.Get("schematicaplus:gui-rotate-right")))
            {
                TransformCurrentSchematic(90, null);
            }

            ImGui.SameLine();
            if (ImGui.Button(Lang.Get("schematicaplus:gui-mirror-x")))
            {
                TransformCurrentSchematic(0, EnumAxis.X);
            }

            ImGui.SameLine();
            if (ImGui.Button(Lang.Get("schematicaplus:gui-mirror-y")))
            {
                TransformCurrentSchematic(0, EnumAxis.Y);
            }

            ImGui.SameLine();
            if (ImGui.Button(Lang.Get("schematicaplus:gui-mirror-z")))
            {
                TransformCurrentSchematic(0, EnumAxis.Z);
            }

            DrawLayerControls();
        }

        private void DrawBlockCounterTab()
        {
            if (modSystem.CurrentSchematic == null)
            {
                ImGui.TextUnformatted(Lang.Get("schematicaplus:gui-no-schematic-data"));
                return;
            }

            var flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.ScrollY | ImGuiTableFlags.Resizable;
            if (ImGui.BeginTable("schematicaplusBlockCounter", 2, flags, new Vector2(0, -1)))
            {
                ImGui.TableSetupColumn(Lang.Get("schematicaplus:gui-material"), ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableSetupColumn(Lang.Get("schematicaplus:gui-count"), ImGuiTableColumnFlags.WidthFixed, 120);
                ImGui.TableHeadersRow();

                foreach (var row in counterRows)
                {
                    ImGui.TableNextRow();
                    ImGui.TableSetColumnIndex(0);
                    DrawBlockCounterMaterial(row);
                    ImGui.TableSetColumnIndex(1);
                    DrawBlockCounterCount(row.Count);
                }

                ImGui.EndTable();
            }
        }

        private void DrawBlockCounterMaterial(BlockCounterRow row)
        {
            if (TryGetBlockIcon(row.BlockCode, out var icon))
            {
                ImGui.Image(
                    new IntPtr(icon.TextureId),
                    new Vector2(BlockCounterIconSize, BlockCounterIconSize),
                    new Vector2(icon.UvMinX, icon.UvMinY),
                    new Vector2(icon.UvMaxX, icon.UvMaxY));
                ImGui.SameLine();
                CenterTextInBlockCounterRow();
            }

            ImGui.PushTextWrapPos();
            try
            {
                ImGui.TextUnformatted(row.Name);
            }
            finally
            {
                ImGui.PopTextWrapPos();
            }
        }

        private static void DrawBlockCounterCount(int count)
        {
            CenterTextInBlockCounterRow();
            ImGui.TextUnformatted(count.ToString(CultureInfo.InvariantCulture));
        }

        private static void CenterTextInBlockCounterRow()
        {
            ImGui.SetCursorPosY(ImGui.GetCursorPosY() + MathF.Max(0, (BlockCounterIconSize - ImGui.GetTextLineHeight()) / 2f));
        }

        private bool TryGetBlockIcon(string blockCode, out BlockCounterIcon icon)
        {
            if (blockIconCache.TryGetValue(blockCode, out icon))
            {
                return icon.TextureId != 0;
            }

            var block = GetBlockIconBlock(blockCode);
            if (block == null)
            {
                blockIconCache[blockCode] = BlockCounterIcon.Missing;
                icon = BlockCounterIcon.Missing;
                return false;
            }

            icon = CreateBlockTextureIcon(block);
            if (icon.TextureId != 0)
            {
                blockIconCache[blockCode] = icon;
                return true;
            }

            var stack = GetBlockIconStack(blockCode);
            if (stack == null)
            {
                blockIconCache[blockCode] = BlockCounterIcon.Missing;
                icon = BlockCounterIcon.Missing;
                return false;
            }

            icon = CreateFallbackIcon(stack);
            blockIconCache[blockCode] = icon;

            return icon.TextureId != 0;
        }

        private BlockCounterIcon CreateBlockTextureIcon(Block block)
        {
            int textureSubId = block.FirstTextureInventory?.Baked?.TextureSubId ?? -1;
            if (textureSubId >= 0 && textureSubId < capi.BlockTextureAtlas.Positions.Length)
            {
                return BlockCounterIcon.FromAtlasPosition(capi.BlockTextureAtlas.Positions[textureSubId]);
            }

            if (block.TexturesInventory != null)
            {
                foreach (string textureName in block.TexturesInventory.Keys)
                {
                    var atlasPosition = capi.BlockTextureAtlas.GetPosition(block, textureName, true);
                    if (atlasPosition != null)
                    {
                        return BlockCounterIcon.FromAtlasPosition(atlasPosition);
                    }
                }
            }

            if (block.Textures != null)
            {
                foreach (string textureName in block.Textures.Keys)
                {
                    var atlasPosition = capi.BlockTextureAtlas.GetPosition(block, textureName, true);
                    if (atlasPosition != null)
                    {
                        return BlockCounterIcon.FromAtlasPosition(atlasPosition);
                    }
                }
            }

            return BlockCounterIcon.Missing;
        }

        private BlockCounterIcon CreateFallbackIcon(ItemStack stack)
        {
            var atlasPosition = capi.Render.GetTextureAtlasPosition(stack);
            return atlasPosition == null ? BlockCounterIcon.Missing : BlockCounterIcon.FromAtlasPosition(atlasPosition);
        }

        private Block? GetBlockIconBlock(string blockCode)
        {
            try
            {
                return capi.World.GetBlock(new AssetLocation(blockCode));
            }
            catch (ArgumentException)
            {
                return null;
            }
        }

        private ItemStack? GetBlockIconStack(string blockCode)
        {
            if (blockIconStackCache.TryGetValue(blockCode, out var cached))
            {
                return cached;
            }

            ItemStack? stack = null;
            var block = GetBlockIconBlock(blockCode);
            if (block != null)
            {
                stack = new ItemStack(block);
            }

            blockIconStackCache[blockCode] = stack;
            return stack;
        }

        private void DrawBlockPosEditor(string label, ref BlockPos pos, Action saveToState)
        {
            int relX = pos.X - worldSpawn.X;
            int y = pos.Y;
            int relZ = pos.Z - worldSpawn.Z;

            ImGui.PushID(label);
            ImGui.TextUnformatted(label);
            ImGui.SetNextItemWidth(90);
            bool changed = ImGui.InputInt("X", ref relX);
            ImGui.SameLine();
            ImGui.SetNextItemWidth(90);
            changed |= ImGui.InputInt("Y", ref y);
            ImGui.SameLine();
            ImGui.SetNextItemWidth(90);
            changed |= ImGui.InputInt("Z", ref relZ);

            if (changed)
            {
                pos = new BlockPos(relX + worldSpawn.X, y, relZ + worldSpawn.Z);
                saveToState();
            }

            ImGui.PopID();
        }

        private void DrawSchematicCombo()
        {
            int currentIndex = Array.IndexOf(schematics, selectedSchematic);
            if (currentIndex < 0)
            {
                currentIndex = 0;
            }

            string preview = schematics.Length == 0
                ? Lang.Get("schematicaplus:msg-no-schematics")
                : schematics[currentIndex];

            ImGui.SetNextItemWidth(360);
            if (ImGui.BeginCombo(Lang.Get("schematicaplus:gui-select-schematic"), preview))
            {
                for (int i = 0; i < schematics.Length; i++)
                {
                    bool selected = schematics[i] == selectedSchematic;
                    if (ImGui.Selectable(schematics[i], selected))
                    {
                        selectedSchematic = schematics[i];
                        modSystem.GuiState.SelectedSchematic = selectedSchematic;
                    }

                    if (selected)
                    {
                        ImGui.SetItemDefaultFocus();
                    }
                }

                ImGui.EndCombo();
            }
        }

        private void DrawLayerControls()
        {
            var schematic = modSystem.CurrentSchematic;
            if (schematic == null)
            {
                return;
            }

            if (ImGui.Checkbox(Lang.Get("schematicaplus:gui-showing-all-layers"), ref showAllLayers))
            {
                modSystem.GuiState.ShowAllLayers = showAllLayers;
                modSystem.Renderer.SetShowAllLayers(showAllLayers);
                RebuildCounterRows();
            }

            int layer = modSystem.Renderer.CurrentLayer;
            ImGui.BeginDisabled(showAllLayers);
            ImGui.SetNextItemWidth(360);
            if (ImGui.SliderInt(Lang.Get("schematicaplus:gui-current-layer-label"), ref layer, 0, schematic.MaxY))
            {
                modSystem.Renderer.SetLayer(layer);
            }
            ImGui.EndDisabled();
        }

        private void RefreshSchematicsList()
        {
            try
            {
                schematics = BlockSchematicStructure.GetAvailableSchematics(capi).OrderBy(x => x).ToArray();
                if (schematics.Length > 0 && string.IsNullOrEmpty(selectedSchematic))
                {
                    selectedSchematic = schematics[0];
                    modSystem.GuiState.SelectedSchematic = selectedSchematic;
                }
            }
            catch (IOException ex)
            {
                capi.Logger.Warning($"[Schematica Plus] Failed to list schematics: {ex.Message}");
                schematics = Array.Empty<string>();
            }
            catch (UnauthorizedAccessException ex)
            {
                capi.Logger.Warning($"[Schematica Plus] Failed to list schematics: {ex.Message}");
                schematics = Array.Empty<string>();
            }
            catch (JsonException ex)
            {
                capi.Logger.Warning($"[Schematica Plus] Failed to list schematics: {ex.Message}");
                schematics = Array.Empty<string>();
            }
        }

        private void SaveSchematic()
        {
            if (string.IsNullOrWhiteSpace(filename))
            {
                capi.ShowChatMessage(Lang.Get("schematicaplus:msg-please-filename"));
                return;
            }

            try
            {
                var schematic = BlockSchematicStructure.CreateFromSelection(capi, firstPoint, secondPoint);
                BlockSchematicStructure.SaveToFile(capi, schematic, filename);
                modSystem.GuiState.FirstPoint = firstPoint.Copy();
                modSystem.GuiState.SecondPoint = secondPoint.Copy();
                modSystem.GuiState.LastFilename = filename;
                RefreshSchematicsList();
                capi.ShowChatMessage(Lang.Get("schematicaplus:msg-schematic-saved", filename, schematic.TotalBlocks));
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or JsonException or ArgumentException)
            {
                capi.ShowChatMessage(Lang.Get("schematicaplus:msg-failed-save", ex.Message));
            }
        }

        private void LoadSelectedSchematic()
        {
            if (string.IsNullOrEmpty(selectedSchematic))
            {
                capi.ShowChatMessage(Lang.Get("schematicaplus:msg-please-select"));
                return;
            }

            try
            {
                var schematic = BlockSchematicStructure.LoadFromFile(capi, selectedSchematic);
                modSystem.LoadSchematic(schematic);
                modSystem.Renderer.SetRenderOrigin(renderPos);
                modSystem.Renderer.SetShowAllLayers(showAllLayers);
                modSystem.GuiState.SelectedSchematic = selectedSchematic;
                RebuildCounterRows();
                capi.ShowChatMessage(Lang.Get("schematicaplus:msg-schematic-loaded", selectedSchematic, schematic.TotalBlocks, schematic.MaxY + 1));
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or JsonException or ArgumentException)
            {
                capi.ShowChatMessage(Lang.Get("schematicaplus:msg-failed-load", ex.Message));
            }
        }

        private void UpdateRenderPosition()
        {
            modSystem.GuiState.RenderPos = renderPos.Copy();
            if (modSystem.CurrentSchematic != null)
            {
                modSystem.Renderer.SetRenderOrigin(renderPos);
                RebuildCounterRows();
            }
        }

        private void TransformCurrentSchematic(int angle, EnumAxis? flipAxis)
        {
            if (modSystem.CurrentSchematic == null)
            {
                return;
            }

            modSystem.CurrentSchematic.TransformWhilePacked(capi.World, EnumOrigin.BottomCenter, angle, flipAxis);
            modSystem.CurrentSchematic.Unpack(capi);
            modSystem.Renderer.UpdateRender();
            RebuildCounterRows();
        }

        private void RebuildCounterRows()
        {
            var schematic = modSystem.CurrentSchematic;
            if (schematic == null)
            {
                counterRows = Array.Empty<BlockCounterRow>();
                return;
            }

            var materialCounts = BuildMaterialCounts(schematic);

            counterRows = materialCounts
                .OrderByDescending(x => x.Value)
                .ThenBy(x => x.Key, StringComparer.Ordinal)
                .Select(kvp => new BlockCounterRow(kvp.Key, GetBlockName(kvp.Key), kvp.Value))
                .ToArray();
        }

        private Dictionary<string, int> BuildMaterialCounts(BlockSchematicStructure schematic)
        {
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var blockData in schematic.Blocks)
            {
                if (TryAddChiseledMaterialCounts(counts, blockData))
                {
                    continue;
                }

                AddCount(counts, blockData.BlockCode, 1);
            }

            return counts;
        }

        private bool TryAddChiseledMaterialCounts(Dictionary<string, int> counts, SerializableBlock blockData)
        {
            if (blockData.BlockEntityData.IsEmpty || blockData.BlockCode.IndexOf("chiseled", StringComparison.OrdinalIgnoreCase) < 0)
            {
                return false;
            }

            try
            {
                string ascii85Data = System.Text.Encoding.UTF8.GetString(blockData.BlockEntityData.ToArray());
                byte[] decodedData = Ascii85.Decode(ascii85Data);

                var tree = new TreeAttribute();
                using (var ms = new MemoryStream(decodedData))
                {
                    using var reader = new BinaryReader(ms);
                    tree.FromBytes(reader);
                }

                string[] materialCodes = GetChiseledMaterialCodes(tree);
                if (materialCodes.Length == 0)
                {
                    return false;
                }

                foreach (string materialCode in materialCodes.Distinct(StringComparer.Ordinal))
                {
                    if (!string.IsNullOrWhiteSpace(materialCode) && !string.Equals(materialCode, "game:air", StringComparison.Ordinal))
                    {
                        AddCount(counts, materialCode, 1);
                    }
                }

                return true;
            }
            catch (Exception ex) when (ex is FormatException or IOException or ArgumentException)
            {
                capi.Logger.Warning($"[Schematica Plus] Failed to count chiseled block materials for {blockData.BlockCode}: {ex.Message}");
                return false;
            }
        }

        private string[] GetChiseledMaterialCodes(TreeAttribute tree)
        {
            var materialCodesAttr = tree["materialCodes"] as StringArrayAttribute;
            if (materialCodesAttr?.value != null && materialCodesAttr.value.Length > 0)
            {
                return materialCodesAttr.value;
            }

            int[] materialIds = BlockEntityMicroBlock.MaterialIdsFromAttributes(tree, capi.World);
            var materialCodes = new string[materialIds.Length];
            for (int i = 0; i < materialIds.Length; i++)
            {
                var block = capi.World.GetBlock(materialIds[i]);
                materialCodes[i] = block?.Code?.ToString() ?? Lang.Get("schematicaplus:gui-unknown");
            }

            return materialCodes;
        }

        private static void AddCount(Dictionary<string, int> counts, string key, int amount)
        {
            if (counts.TryGetValue(key, out int existing))
            {
                counts[key] = existing + amount;
            }
            else
            {
                counts[key] = amount;
            }
        }

        private string GetBlockName(string blockCode)
        {
            if (blockNameCache.TryGetValue(blockCode, out var cached))
            {
                return cached;
            }

            string name = CleanBlockName(blockCode);
            try
            {
                var block = capi.World.GetBlock(new AssetLocation(blockCode));
                if (block != null)
                {
                    string placedName = block.GetPlacedBlockName(capi.World, new BlockPos(0, 0, 0));
                    if (!string.IsNullOrWhiteSpace(placedName))
                    {
                        name = placedName;
                    }
                }
            }
            catch (ArgumentException)
            {
                name = CleanBlockName(blockCode);
            }

            blockNameCache[blockCode] = name;
            return name;
        }

        private static string CleanBlockName(string blockCode)
        {
            if (string.IsNullOrWhiteSpace(blockCode))
            {
                return Lang.Get("schematicaplus:gui-unknown");
            }

            string clean = blockCode.Replace("game:", string.Empty, StringComparison.Ordinal);
            clean = clean.Replace("-", " ", StringComparison.Ordinal);
            clean = clean.Replace("_", " ", StringComparison.Ordinal);
            return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(clean);
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            imGuiSystem.Draw -= Draw;
            imGuiSystem.Closed -= OnClosed;
            disposed = true;
        }

        private readonly record struct BlockCounterIcon(int TextureId, float UvMinX, float UvMinY, float UvMaxX, float UvMaxY)
        {
            public static BlockCounterIcon Missing { get; } = new(0, 0, 0, 0, 0);

            public static BlockCounterIcon FromAtlasPosition(TextureAtlasPosition atlasPosition)
            {
                return new BlockCounterIcon(
                    atlasPosition.atlasTextureId,
                    atlasPosition.x1,
                    atlasPosition.y1,
                    atlasPosition.x2,
                    atlasPosition.y2);
            }
        }

        private readonly record struct BlockCounterRow(string BlockCode, string Name, int Count);
    }
}
