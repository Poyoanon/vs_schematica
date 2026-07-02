using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace Schematica.Api;

public interface IRustmaticaEasyPlaceApi
{
    bool TryGetTarget(BlockSelection selection, out RustmaticaEasyPlaceTarget? target);

    bool TryGetTarget(BlockPos worldPosition, out RustmaticaEasyPlaceTarget? target);

    void NotifyPlacementAttempt(BlockPos worldPosition, int holdMilliseconds);
}
