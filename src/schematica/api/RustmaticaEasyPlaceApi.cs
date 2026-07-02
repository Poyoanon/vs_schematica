using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Schematica.Core;
using Schematica.Rendering;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace Schematica.Api;

public sealed class RustmaticaEasyPlaceApi : IRustmaticaEasyPlaceApi
{
    private readonly ICoreClientAPI capi;
    private readonly SchematicaModSystem modSystem;

    public RustmaticaEasyPlaceApi(ICoreClientAPI capi, SchematicaModSystem modSystem)
    {
        this.capi = capi ?? throw new ArgumentNullException(nameof(capi));
        this.modSystem = modSystem ?? throw new ArgumentNullException(nameof(modSystem));
    }

    public bool TryGetTarget(BlockSelection selection, out RustmaticaEasyPlaceTarget? target)
    {
        target = null;
        if (selection?.Position == null)
        {
            return false;
        }

        return TryGetTarget(selection.Position, out target);
    }

    public bool TryGetTarget(BlockPos worldPosition, out RustmaticaEasyPlaceTarget? target)
    {
        ArgumentNullException.ThrowIfNull(worldPosition);

        target = null;
        if (!modSystem.Renderer.TryGetEasyPlaceTarget(worldPosition, out var blockData, out bool isCorrect) || blockData == null)
        {
            return false;
        }

        var blockEntityData = blockData.BlockEntityData.ToArray();
        bool isChiseled = IsChiseledTarget(blockData);
        target = new RustmaticaEasyPlaceTarget(
            worldPosition,
            blockData.BlockCode,
            isChiseled ? RustmaticaEasyPlaceTargetKind.ChiseledBlock : RustmaticaEasyPlaceTargetKind.NormalBlock,
            isCorrect,
            blockEntityData,
            isChiseled ? ReadMaterialCodes(blockEntityData) : Array.Empty<string>()
        );
        return true;
    }

    public void NotifyPlacementAttempt(BlockPos worldPosition, int holdMilliseconds)
    {
        ArgumentNullException.ThrowIfNull(worldPosition);
        modSystem.Renderer.HoldEasyPlaceTarget(worldPosition, holdMilliseconds);
    }

    private bool IsChiseledTarget(SerializableBlock blockData)
    {
        if (blockData.BlockEntityData.IsEmpty)
        {
            return false;
        }

        var block = capi.World.GetBlock(new AssetLocation(blockData.BlockCode));
        return block?.Code?.Path.Contains("chiseled", StringComparison.Ordinal) == true;
    }

    private IReadOnlyList<string> ReadMaterialCodes(byte[] blockEntityData)
    {
        if (blockEntityData.Length == 0)
        {
            return Array.Empty<string>();
        }

        try
        {
            string ascii85Data = Encoding.UTF8.GetString(blockEntityData);
            byte[] decodedData = Ascii85.Decode(ascii85Data);

            var tree = new TreeAttribute();
            using (var stream = new MemoryStream(decodedData))
            {
                using var reader = new BinaryReader(stream);
                tree.FromBytes(reader);
            }

            if (tree["materialCodes"] is StringArrayAttribute materialCodesAttribute
                && materialCodesAttribute.value != null
                && materialCodesAttribute.value.Length > 0)
            {
                return materialCodesAttribute.value;
            }

            int[] materialIds = BlockEntityMicroBlock.MaterialIdsFromAttributes(tree, capi.World);
            var materialCodes = new List<string>(materialIds.Length);
            foreach (int materialId in materialIds)
            {
                var materialBlock = capi.World.GetBlock(materialId);
                materialCodes.Add(materialBlock?.Code?.ToString() ?? "unknown");
            }

            return materialCodes;
        }
        catch (FormatException ex)
        {
            capi.Logger.Warning($"[Schematica Plus] Failed to decode EasyPlace material codes: {ex.Message}");
            return Array.Empty<string>();
        }
        catch (IOException ex)
        {
            capi.Logger.Warning($"[Schematica Plus] Failed to decode EasyPlace material codes: {ex.Message}");
            return Array.Empty<string>();
        }
    }
}
