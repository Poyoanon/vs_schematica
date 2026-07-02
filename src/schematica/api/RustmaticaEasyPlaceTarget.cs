using Vintagestory.API.MathTools;

namespace Schematica.Api;

public sealed class RustmaticaEasyPlaceTarget
{
    public RustmaticaEasyPlaceTarget(
        BlockPos worldPosition,
        string blockCode,
        RustmaticaEasyPlaceTargetKind kind,
        bool isCorrect,
        byte[] blockEntityData,
        IReadOnlyList<string> materialCodes)
    {
        ArgumentNullException.ThrowIfNull(worldPosition);
        ArgumentException.ThrowIfNullOrWhiteSpace(blockCode);
        ArgumentNullException.ThrowIfNull(blockEntityData);
        ArgumentNullException.ThrowIfNull(materialCodes);

        WorldPosition = worldPosition.Copy();
        BlockCode = blockCode;
        Kind = kind;
        IsCorrect = isCorrect;
        BlockEntityData = blockEntityData.ToArray();
        MaterialCodes = materialCodes.ToArray();
    }

    public BlockPos WorldPosition { get; }

    public string BlockCode { get; }

    public RustmaticaEasyPlaceTargetKind Kind { get; }

    public bool IsCorrect { get; }

    public ReadOnlyMemory<byte> BlockEntityData { get; }

    public IReadOnlyList<string> MaterialCodes { get; }
}
