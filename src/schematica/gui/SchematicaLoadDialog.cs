using Schematica.Core;
using Newtonsoft.Json;
using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Schematica.Utils;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;

namespace Schematica.GUI
{
    public class SchematicaLoadDialog : GuiDialog
    {
        private readonly SchematicaModSystem modSystem;
        private SchematicaInfoDialog? infoDialog;
        private string selectedSchematic = string.Empty;
        private BlockPos renderPos;
        private bool showAllLayers;
        private bool updatingFromCode;
        private readonly BlockPos worldSpawn;

        public override string? ToggleKeyCombinationCode => null;

        public SchematicaLoadDialog(ICoreClientAPI capi, SchematicaModSystem modSystem) : base(capi ?? throw new ArgumentNullException(nameof(capi)))
        {
            ArgumentNullException.ThrowIfNull(modSystem);
            this.modSystem = modSystem;
            this.worldSpawn = capi.World.DefaultSpawnPosition.AsBlockPos;

            // Load saved state
            renderPos = modSystem.GuiState.RenderPos.Copy();
            selectedSchematic = modSystem.GuiState.SelectedSchematic ?? string.Empty;
            showAllLayers = modSystem.GuiState.ShowAllLayers;

            SetupDialog();
        }

        private void SetupDialog()
        {
            ElementBounds dialogBounds = ElementBounds
                .Fixed(0, 0, 500, 600)
                .WithAlignment(EnumDialogArea.CenterMiddle);

            ElementBounds bgBounds = ElementBounds.Fill.WithFixedPadding(GuiStyle.ElementToDialogPadding);
            bgBounds.BothSizing = ElementSizing.FitToChildren;

            var composer = capi.Gui
                .CreateCompo("schematicaplus_load_dialog", dialogBounds)
                .AddShadedDialogBG(bgBounds)
                .AddDialogTitleBar(Lang.Get("schematicaplus:gui-load-title"), OnTitleBarClose)
                .BeginChildElements(bgBounds);

            const double topOffset = 20;

            // Schematic selection
            composer.AddStaticText(Lang.Get("schematicaplus:gui-select-schematic"), CairoFont.WhiteDetailText(),
                ElementBounds.Fixed(0, 5 + topOffset, 460, 20));
            composer.AddDropDown(GetSchematicsList(), GetSchematicsList(), 0, OnSchematicSelected,
                ElementBounds.Fixed(0, 30 + topOffset, 460, 30), "schematicsList");
            composer.AddRichtext("", CairoFont.WhiteDetailText(),
                ElementBounds.Fixed(0, 70 + topOffset, 460, 80), "infoText");

            // Control buttons
            const double controlRowWidth = 460;
            const int controlButtonCount = 5;
            double controlButtonWidth = controlRowWidth / controlButtonCount;
            double controlRowY = 160 + topOffset;

            composer.AddSmallButton(Lang.Get("schematicaplus:gui-load"), OnLoadClick,
                ElementBounds.Fixed(0 * controlButtonWidth, controlRowY, controlButtonWidth, 30), EnumButtonStyle.Normal);
            composer.AddSmallButton(Lang.Get("schematicaplus:gui-refresh"), OnRefreshClick,
                ElementBounds.Fixed(1 * controlButtonWidth, controlRowY, controlButtonWidth, 30), EnumButtonStyle.Normal);
            composer.AddSmallButton(Lang.Get("schematicaplus:gui-clear"), OnClearClick,
                ElementBounds.Fixed(2 * controlButtonWidth, controlRowY, controlButtonWidth, 30), EnumButtonStyle.Normal);
            composer.AddSmallButton(Lang.Get("schematicaplus:gui-set-here"), OnSetHereClick,
                ElementBounds.Fixed(3 * controlButtonWidth, controlRowY, controlButtonWidth, 30), EnumButtonStyle.Normal);
            composer.AddSmallButton(Lang.Get("schematicaplus:gui-info"), OnInfoClick,
                ElementBounds.Fixed(4 * controlButtonWidth, controlRowY, controlButtonWidth, 30), EnumButtonStyle.Normal);

            var builder = new SchematicaUIBuilder(composer, capi);

            // Position controls with direction buttons
            builder.AddPositionInputs(
                Lang.Get("schematicaplus:gui-render-position"), 210 + (int)topOffset,
                renderPos, worldSpawn,
                OnPosXChanged, OnPosYChanged, OnPosZChanged,
                "posX", "posY", "posZ"
            );

            builder.AddDirectionButtons(280 + (int)topOffset,
                () => { ModifyPosition('z', -1); return true; },
                () => { ModifyPosition('z', 1); return true; },
                () => { ModifyPosition('x', 1); return true; },
                () => { ModifyPosition('x', -1); return true; },
                () => { ModifyPosition('y', 1); return true; },
                () => { ModifyPosition('y', -1); return true; }
            );

            // Transform controls
            builder.AddTransformControls(330 + (int)topOffset,
                OnRotateLeft, OnRotateRight,
                OnMirrorX, OnMirrorY, OnMirrorZ
            );

            // Layer controls
            builder.AddLayerControls(420 + (int)topOffset,
                OnLayerToggle,
                (int value) => { OnLayerChanged(value); return true; },
                "layerToggle", "layerSlider", "layerText"
            );

            SingleComposer = composer.EndChildElements().Compose();

            // Restore selected schematic
            if (!string.IsNullOrEmpty(selectedSchematic))
            {
                var dropdown = SingleComposer.GetDropDown("schematicsList");
                var schematics = GetSchematicsList();
                var index = Array.IndexOf(schematics, selectedSchematic);
                if (index >= 0)
                {
                    dropdown.SetSelectedIndex(index);
                }
            }

            UpdateInfo(selectedSchematic);
            UpdateLayerControls();
            UpdatePositionInputs();
        }

        private string[] GetSchematicsList()
        {
            try
            {
                return BlockSchematicStructure.GetAvailableSchematics(capi).OrderBy(x => x).ToArray();
            }
            catch (IOException ex)
            {
                capi.Logger.Warning($"[Schematica Plus] Failed to list schematics: {ex.Message}");
                return Array.Empty<string>();
            }
            catch (UnauthorizedAccessException ex)
            {
                capi.Logger.Warning($"[Schematica Plus] Failed to list schematics: {ex.Message}");
                return Array.Empty<string>();
            }
            catch (JsonException ex)
            {
                capi.Logger.Warning($"[Schematica Plus] Failed to list schematics: {ex.Message}");
                return Array.Empty<string>();
            }
        }

        private void OnSchematicSelected(string code, bool selected)
        {
            selectedSchematic = code;
            modSystem.GuiState.SelectedSchematic = selectedSchematic;
            UpdateInfo(selectedSchematic);
        }

        private bool OnRotateLeft()
        {
            if (modSystem.CurrentSchematic == null) return true;

            modSystem.CurrentSchematic.TransformWhilePacked(capi.World, EnumOrigin.BottomCenter, -90, null);
            modSystem.CurrentSchematic.Unpack(capi);
            modSystem.Renderer.UpdateRender();
            return true;
        }

        private bool OnRotateRight()
        {
            if (modSystem.CurrentSchematic == null) return true;

            modSystem.CurrentSchematic.TransformWhilePacked(capi.World, EnumOrigin.BottomCenter, 90, null);
            modSystem.CurrentSchematic.Unpack(capi);
            modSystem.Renderer.UpdateRender();
            return true;
        }

        private bool OnMirrorX()
        {
            if (modSystem.CurrentSchematic == null) return true;

            modSystem.CurrentSchematic.TransformWhilePacked(capi.World, EnumOrigin.BottomCenter, 0, EnumAxis.X);
            modSystem.CurrentSchematic.Unpack(capi);
            modSystem.Renderer.UpdateRender();
            return true;
        }

        private bool OnMirrorY()
        {
            if (modSystem.CurrentSchematic == null) return true;

            modSystem.CurrentSchematic.TransformWhilePacked(capi.World, EnumOrigin.BottomCenter, 0, EnumAxis.Y);
            modSystem.CurrentSchematic.Unpack(capi);
            modSystem.Renderer.UpdateRender();
            return true;
        }

        private bool OnMirrorZ()
        {
            if (modSystem.CurrentSchematic == null) return true;

            modSystem.CurrentSchematic.TransformWhilePacked(capi.World, EnumOrigin.BottomCenter, 0, EnumAxis.Z);
            modSystem.CurrentSchematic.Unpack(capi);
            modSystem.Renderer.UpdateRender();
            return true;
        }

        private bool OnInfoClick()
        {
            if (modSystem.CurrentSchematic == null)
            {
                capi.ShowChatMessage(Lang.Get("schematicaplus:msg-please-select"));
                return true;
            }

            infoDialog?.Dispose();
            infoDialog = new SchematicaInfoDialog(capi);
            infoDialog.SetSchematic(modSystem.CurrentSchematic);
            infoDialog.TryOpen();
            return true;
        }

        private void OnPosXChanged(string value)
        {
            if (updatingFromCode) return;
            if (int.TryParse(value, out int relX))
            {
                renderPos.X = relX + worldSpawn.X;
                modSystem.GuiState.RenderPos = renderPos.Copy();
                UpdateRenderPosition();
            }
        }

        private void OnPosYChanged(string value)
        {
            if (updatingFromCode) return;
            if (int.TryParse(value, out int y))
            {
                renderPos.Y = y;
                modSystem.GuiState.RenderPos = renderPos.Copy();
                UpdateRenderPosition();
            }
        }

        private void OnPosZChanged(string value)
        {
            if (updatingFromCode) return;
            if (int.TryParse(value, out int relZ))
            {
                renderPos.Z = relZ + worldSpawn.Z;
                modSystem.GuiState.RenderPos = renderPos.Copy();
                UpdateRenderPosition();
            }
        }

        private bool ModifyPosition(char axis, int delta)
        {
            switch (axis)
            {
                case 'x': renderPos.X += delta; break;
                case 'y': renderPos.Y += delta; break;
                case 'z': renderPos.Z += delta; break;
            }

            modSystem.GuiState.RenderPos = renderPos.Copy();
            UpdatePositionInputs();
            UpdateRenderPosition();
            return true;
        }

        private void UpdateRenderPosition()
        {
            if (modSystem.CurrentSchematic != null)
            {
                modSystem.Renderer.SetRenderOrigin(renderPos);
            }
        }

        private void UpdatePositionInputs()
        {
            updatingFromCode = true;
            var relX = renderPos.X - worldSpawn.X;
            var relZ = renderPos.Z - worldSpawn.Z;
            var culture = CultureInfo.InvariantCulture;

            capi.Logger.Debug($"[Schematica Plus] UpdatePositionInputs: renderPos={renderPos}, worldSpawn={worldSpawn}, relative=({relX}, {renderPos.Y}, {relZ})");

            SingleComposer.GetTextInput("posX").SetValue(relX.ToString(culture));
            SingleComposer.GetTextInput("posY").SetValue(renderPos.Y.ToString(culture));
            SingleComposer.GetTextInput("posZ").SetValue(relZ.ToString(culture));
            updatingFromCode = false;
        }

        private bool OnSetHereClick()
        {
            renderPos = SchematicaHelpers.GetPlayerBlockPos(capi).Copy();
            modSystem.GuiState.RenderPos = renderPos.Copy();
            UpdatePositionInputs();
            UpdateRenderPosition();
            return true;
        }

        private void OnLayerToggle(bool on)
        {
            showAllLayers = on;
            modSystem.GuiState.ShowAllLayers = on;
            if (modSystem.CurrentSchematic != null)
            {
                modSystem.Renderer.SetShowAllLayers(on);
            }
            UpdateLayerControls();
        }

        private bool OnLoadClick()
        {
            if (string.IsNullOrEmpty(selectedSchematic))
            {
                capi.ShowChatMessage(Lang.Get("schematicaplus:msg-please-select"));
                return true;
            }

            try
            {
                var schematic = BlockSchematicStructure.LoadFromFile(capi, selectedSchematic);
                modSystem.LoadSchematic(schematic);

                modSystem.Renderer.SetRenderOrigin(renderPos);
                modSystem.Renderer.SetShowAllLayers(showAllLayers);
                capi.ShowChatMessage(Lang.Get("schematicaplus:msg-schematic-loaded", selectedSchematic, schematic.TotalBlocks, schematic.MaxY + 1));
                UpdateLayerControls();
            }
            catch (InvalidDataException ex)
            {
                capi.ShowChatMessage(Lang.Get("schematicaplus:msg-failed-load", ex.Message));
            }
            catch (IOException ex)
            {
                capi.ShowChatMessage(Lang.Get("schematicaplus:msg-failed-load", ex.Message));
            }
            catch (UnauthorizedAccessException ex)
            {
                capi.ShowChatMessage(Lang.Get("schematicaplus:msg-failed-load", ex.Message));
            }
            catch (JsonException ex)
            {
                capi.ShowChatMessage(Lang.Get("schematicaplus:msg-failed-load", ex.Message));
            }
            catch (ArgumentException ex)
            {
                capi.ShowChatMessage(Lang.Get("schematicaplus:msg-failed-load", ex.Message));
            }

            return true;
        }

        private bool OnRefreshClick()
        {
            RefreshSchematicsList();
            return true;
        }

        private bool OnClearClick()
        {
            modSystem.ClearSchematic();
            UpdateLayerControls();
            capi.ShowChatMessage(Lang.Get("schematicaplus:msg-cleared"));
            return true;
        }

        private void OnLayerChanged(int value)
        {
            if (modSystem.CurrentSchematic == null || showAllLayers) return;

            int layer = (int)((float)value / 100f * modSystem.CurrentSchematic.MaxY);
            modSystem.Renderer.SetLayer(layer);
            UpdateLayerText();
        }

        private void UpdateInfo(string? selectedSchematic)
        {
            var infoLabel = SingleComposer.GetRichtext("infoText");
            var culture = CultureInfo.InvariantCulture;

            if (string.IsNullOrEmpty(selectedSchematic))
            {
                infoLabel.SetNewText(string.Empty, CairoFont.WhiteDetailText());
                return;
            }

            try
            {
                var schematic = BlockSchematicStructure.LoadFromFile(capi, selectedSchematic);
                if (schematic == null)
                {
                    infoLabel.SetNewText(Lang.Get("schematicaplus:msg-failed-load", selectedSchematic), CairoFont.WhiteDetailText());
                    return;
                }

                var infoText = new StringBuilder();
                infoText.AppendLine(string.Format(culture, "Size: {0}x{1}x{2}", schematic.SizeX, schematic.SizeY, schematic.SizeZ));
                infoText.AppendLine(string.Format(culture, "Total blocks: {0}", schematic.TotalBlocks));

                infoLabel.SetNewText(infoText.ToString(), CairoFont.WhiteDetailText());
            }
            catch (InvalidDataException ex)
            {
                infoLabel.SetNewText($"Error loading info: {ex.Message}", CairoFont.WhiteDetailText());
            }
            catch (IOException ex)
            {
                infoLabel.SetNewText($"Error loading info: {ex.Message}", CairoFont.WhiteDetailText());
            }
            catch (JsonException ex)
            {
                infoLabel.SetNewText($"Error loading info: {ex.Message}", CairoFont.WhiteDetailText());
            }
            catch (UnauthorizedAccessException ex)
            {
                infoLabel.SetNewText($"Error loading info: {ex.Message}", CairoFont.WhiteDetailText());
            }
            catch (ArgumentException ex)
            {
                infoLabel.SetNewText($"Error loading info: {ex.Message}", CairoFont.WhiteDetailText());
            }
        }

        private void UpdateLayerControls()
        {
            var slider = SingleComposer.GetSlider("layerSlider");
            var toggle = SingleComposer.GetSwitch("layerToggle");

            bool hasSchematic = modSystem.CurrentSchematic != null;
            slider.Enabled = hasSchematic && !showAllLayers;
            toggle.Enabled = hasSchematic;

            if (hasSchematic)
            {
                var schematic = modSystem.CurrentSchematic!;
                slider.SetValue((int)((float)modSystem.Renderer.CurrentLayer / schematic.MaxY * 100));
                toggle.SetValue(showAllLayers);
            }
            else
            {
                slider.SetValue(0);
                toggle.SetValue(false);
            }

            UpdateLayerText();
        }

        private void UpdateLayerText()
        {
            var layerText = SingleComposer.GetDynamicText("layerText");

            if (modSystem.CurrentSchematic != null && !showAllLayers)
            {
                layerText.SetNewText(Lang.Get("schematicaplus:gui-current-layer",
                    modSystem.Renderer.CurrentLayer, modSystem.CurrentSchematic.MaxY));
            }
            else if (showAllLayers)
            {
                layerText.SetNewText(Lang.Get("schematicaplus:gui-showing-all-layers"));
            }
            else
            {
                layerText.SetNewText(Lang.Get("schematicaplus:gui-no-schematic"));
            }
        }

        private void RefreshSchematicsList()
        {
            var newList = GetSchematicsList();
            var dropdown = SingleComposer.GetDropDown("schematicsList");
            dropdown.SetList(newList, newList);
            selectedSchematic = string.Empty;
            UpdateInfo(selectedSchematic);
        }

        private void OnTitleBarClose()
        {
            // Save state on close
            modSystem.GuiState.RenderPos = renderPos.Copy();
            modSystem.GuiState.SelectedSchematic = selectedSchematic;
            modSystem.GuiState.ShowAllLayers = showAllLayers;
            infoDialog?.Dispose();
            infoDialog = null;

            TryClose();
        }

        public override void OnGuiOpened()
        {
            base.OnGuiOpened();
            RefreshSchematicsList();
            UpdateLayerControls();
        }

        public override void Dispose()
        {
            infoDialog?.Dispose();
            infoDialog = null;
            GC.SuppressFinalize(this);
            base.Dispose();
        }
    }
}


