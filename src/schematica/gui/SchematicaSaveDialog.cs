using System;
using System.Globalization;
using System.IO;
using Newtonsoft.Json;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Schematica.Core;
using Schematica.Utils;

namespace Schematica.GUI
{
    public class SchematicaSaveDialog : GuiDialog
    {
        private readonly SchematicaModSystem modSystem;
        private BlockPos firstPoint;
        private BlockPos secondPoint;
        private bool updatingFromCode;
        private readonly BlockPos worldSpawn;

        public override string? ToggleKeyCombinationCode => null;

        public SchematicaSaveDialog(ICoreClientAPI capi, SchematicaModSystem modSystem) : base(capi ?? throw new ArgumentNullException(nameof(capi)))
        {
            ArgumentNullException.ThrowIfNull(modSystem);
            this.modSystem = modSystem;

            this.worldSpawn = capi.World.DefaultSpawnPosition.AsBlockPos;

            // Load saved coordinates
            firstPoint = modSystem.GuiState.FirstPoint.Copy();
            secondPoint = modSystem.GuiState.SecondPoint.Copy();

            // Set defaults if not set
            if (firstPoint.X == 0 && firstPoint.Y == 0 && firstPoint.Z == 0)
            {
                var playerPos = SchematicaHelpers.GetPlayerBlockPos(capi);
                firstPoint = new BlockPos(playerPos.X - 5, playerPos.Y - 1, playerPos.Z - 5);
                secondPoint = new BlockPos(playerPos.X + 5, playerPos.Y + 5, playerPos.Z + 5);
            }

            SetupDialog();
        }

        private void SetupDialog()
        {
            ElementBounds dialogBounds = ElementBounds
                .Fixed(0, 0, 500, 350)
                .WithAlignment(EnumDialogArea.CenterMiddle);

            ElementBounds bgBounds = ElementBounds.Fill.WithFixedPadding(GuiStyle.ElementToDialogPadding);
            bgBounds.BothSizing = ElementSizing.FitToChildren;

            var composer = capi.Gui
                .CreateCompo("schematicaplus_save_dialog", dialogBounds)
                .AddShadedDialogBG(bgBounds)
                .AddDialogTitleBar(Lang.Get("schematicaplus:gui-save-title"), OnTitleBarClose, CairoFont.WhiteDetailText())
                .BeginChildElements(bgBounds);

            var builder = new SchematicaUIBuilder(composer, capi);

            // First point controls
            builder.AddCoordinateInputs(
                Lang.Get("schematicaplus:gui-first-point"), 30,
                OnFirstXChanged, OnFirstYChanged, OnFirstZChanged,
                () => { ModifyCoord(true, 'x', -1); return true; },
                () => { ModifyCoord(true, 'x', 1); return true; },
                () => { ModifyCoord(true, 'y', -1); return true; },
                () => { ModifyCoord(true, 'y', 1); return true; },
                () => { ModifyCoord(true, 'z', -1); return true; },
                () => { ModifyCoord(true, 'z', 1); return true; },
                "firstX", "firstY", "firstZ"
            );

            composer.AddSmallButton(Lang.Get("schematicaplus:gui-use-player-pos"), OnFirstPlayerPos,
                ElementBounds.Fixed(20, 120, 150, 30), EnumButtonStyle.Normal);

            // Second point controls
            builder.AddCoordinateInputs(
                Lang.Get("schematicaplus:gui-second-point"), 160,
                OnSecondXChanged, OnSecondYChanged, OnSecondZChanged,
                () => { ModifyCoord(false, 'x', -1); return true; },
                () => { ModifyCoord(false, 'x', 1); return true; },
                () => { ModifyCoord(false, 'y', -1); return true; },
                () => { ModifyCoord(false, 'y', 1); return true; },
                () => { ModifyCoord(false, 'z', -1); return true; },
                () => { ModifyCoord(false, 'z', 1); return true; },
                "secondX", "secondY", "secondZ"
            );

            composer.AddSmallButton(Lang.Get("schematicaplus:gui-use-player-pos"), OnSecondPlayerPos,
                ElementBounds.Fixed(280, 120, 150, 30), EnumButtonStyle.Normal);

            // Filename and save
            composer.AddStaticText(Lang.Get("schematicaplus:gui-filename"), CairoFont.WhiteDetailText(),
                ElementBounds.Fixed(20, 280, 70, 25));
            composer.AddTextInput(ElementBounds.Fixed(100, 275, 200, 30), null, CairoFont.TextInput(), "filename");
            composer.AddSmallButton(Lang.Get("schematicaplus:gui-save"), OnSaveClick,
                ElementBounds.Fixed(320, 275, 80, 30), EnumButtonStyle.Normal);

            SingleComposer = composer.EndChildElements().Compose();

            UpdateTextInputs();

            // Restore last filename
            if (!string.IsNullOrEmpty(modSystem.GuiState.LastFilename))
            {
                SingleComposer.GetTextInput("filename").SetValue(modSystem.GuiState.LastFilename);
            }
        }

        private bool ModifyCoord(bool isFirst, char axis, int delta)
        {
            var pos = isFirst ? firstPoint : secondPoint;

            switch (axis)
            {
                case 'x': pos.X += delta; break;
                case 'y': pos.Y += delta; break;
                case 'z': pos.Z += delta; break;
            }

            if (isFirst)
            {
                firstPoint = pos;
                modSystem.GuiState.FirstPoint = firstPoint.Copy();
            }
            else
            {
                secondPoint = pos;
                modSystem.GuiState.SecondPoint = secondPoint.Copy();
            }

            UpdateTextInputs();
            return true;
        }

        private void OnFirstXChanged(string value)
        {
            if (updatingFromCode) return;
            if (int.TryParse(value, out int relX))
            {
                firstPoint.X = relX + worldSpawn.X;
                modSystem.GuiState.FirstPoint = firstPoint.Copy();
            }
        }

        private void OnFirstYChanged(string value)
        {
            if (updatingFromCode) return;
            if (int.TryParse(value, out int y))
            {
                firstPoint.Y = y;
                modSystem.GuiState.FirstPoint = firstPoint.Copy();
            }
        }

        private void OnFirstZChanged(string value)
        {
            if (updatingFromCode) return;
            if (int.TryParse(value, out int relZ))
            {
                firstPoint.Z = relZ + worldSpawn.Z;
                modSystem.GuiState.FirstPoint = firstPoint.Copy();
            }
        }

        private void OnSecondXChanged(string value)
        {
            if (updatingFromCode) return;
            if (int.TryParse(value, out int relX))
            {
                secondPoint.X = relX + worldSpawn.X;
                modSystem.GuiState.SecondPoint = secondPoint.Copy();
            }
        }

        private void OnSecondYChanged(string value)
        {
            if (updatingFromCode) return;
            if (int.TryParse(value, out int y))
            {
                secondPoint.Y = y;
                modSystem.GuiState.SecondPoint = secondPoint.Copy();
            }
        }

        private void OnSecondZChanged(string value)
        {
            if (updatingFromCode) return;
            if (int.TryParse(value, out int relZ))
            {
                secondPoint.Z = relZ + worldSpawn.Z;
                modSystem.GuiState.SecondPoint = secondPoint.Copy();
            }
        }

        private bool OnFirstPlayerPos()
        {
            updatingFromCode = true;
            firstPoint = SchematicaHelpers.GetPlayerBlockPos(capi).Copy();
            modSystem.GuiState.FirstPoint = firstPoint.Copy();
            UpdateTextInputs();
            updatingFromCode = false;
            return true;
        }

        private bool OnSecondPlayerPos()
        {
            updatingFromCode = true;
            secondPoint = SchematicaHelpers.GetPlayerBlockPos(capi).Copy();
            modSystem.GuiState.SecondPoint = secondPoint.Copy();
            UpdateTextInputs();
            updatingFromCode = false;
            return true;
        }

        private void UpdateTextInputs()
        {
            if (updatingFromCode) return;
            updatingFromCode = true;

            int relFirstX = firstPoint.X - worldSpawn.X;
            int relFirstZ = firstPoint.Z - worldSpawn.Z;
            int relSecondX = secondPoint.X - worldSpawn.X;
            int relSecondZ = secondPoint.Z - worldSpawn.Z;

            var culture = CultureInfo.InvariantCulture;
            SingleComposer.GetTextInput("firstX").SetValue(relFirstX.ToString(culture));
            SingleComposer.GetTextInput("firstY").SetValue(firstPoint.Y.ToString(culture));
            SingleComposer.GetTextInput("firstZ").SetValue(relFirstZ.ToString(culture));

            SingleComposer.GetTextInput("secondX").SetValue(relSecondX.ToString(culture));
            SingleComposer.GetTextInput("secondY").SetValue(secondPoint.Y.ToString(culture));
            SingleComposer.GetTextInput("secondZ").SetValue(relSecondZ.ToString(culture));

            updatingFromCode = false;
        }

        private bool OnSaveClick()
        {
            var filename = SingleComposer.GetTextInput("filename").GetText();
            if (string.IsNullOrEmpty(filename))
            {
                capi.ShowChatMessage(Lang.Get("schematicaplus:msg-please-filename"));
                return true;
            }

            try
            {
                var schematic = BlockSchematicStructure.CreateFromSelection(capi, firstPoint, secondPoint);
                BlockSchematicStructure.SaveToFile(capi, schematic, filename);
                capi.ShowChatMessage(Lang.Get("schematicaplus:msg-schematic-saved", filename, schematic.TotalBlocks));

                // Save state
                modSystem.GuiState.FirstPoint = firstPoint.Copy();
                modSystem.GuiState.SecondPoint = secondPoint.Copy();
                modSystem.GuiState.LastFilename = filename;

                TryClose();
            }
            catch (JsonException ex)
            {
                capi.ShowChatMessage(Lang.Get("schematicaplus:msg-failed-save", ex.Message));
            }
            catch (IOException ex)
            {
                capi.ShowChatMessage(Lang.Get("schematicaplus:msg-failed-save", ex.Message));
            }
            catch (UnauthorizedAccessException ex)
            {
                capi.ShowChatMessage(Lang.Get("schematicaplus:msg-failed-save", ex.Message));
            }
            catch (InvalidOperationException ex)
            {
                capi.ShowChatMessage(Lang.Get("schematicaplus:msg-failed-save", ex.Message));
            }

            return true;
        }

        private void OnTitleBarClose()
        {
            // Save state on close
            var filename = SingleComposer.GetTextInput("filename")?.GetText() ?? string.Empty;

            modSystem.GuiState.FirstPoint = firstPoint.Copy();
            modSystem.GuiState.SecondPoint = secondPoint.Copy();
            modSystem.GuiState.LastFilename = filename;

            TryClose();
        }
    }
}


