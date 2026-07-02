using System;
using System.Globalization;
using System.Linq;
using System.Collections.Generic;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;

namespace Schematica.Utils
{
    public static class SchematicaHelpers
    {
        /// <summary>
        /// Highlights blocks in the world with specified color
        /// </summary>
        public static void HighlightBlocks(ICoreClientAPI api, IReadOnlyList<BlockPos> positions, int color, int id = 500)
        {
            ArgumentNullException.ThrowIfNull(api);
            ArgumentNullException.ThrowIfNull(positions);

            var positionList = positions as List<BlockPos> ?? positions.ToList();

            api.World.HighlightBlocks(
                api.World.Player,
                id,
                positionList,
                new List<int> { color },
                EnumHighlightBlocksMode.Absolute,
                EnumHighlightShape.Cubes
            );
        }

        /// <summary>
        /// Clear highlight by ID
        /// </summary>
        public static void ClearHighlight(ICoreClientAPI api, int id = 500)
        {
            ArgumentNullException.ThrowIfNull(api);

            api.World.HighlightBlocks(
                api.World.Player,
                id,
                new List<BlockPos>(),
                new List<int>(),
                EnumHighlightBlocksMode.Absolute,
                EnumHighlightShape.Cubes
            );
        }

        /// <summary>
        /// Get all block positions in a cuboid area
        /// </summary>
        public static IReadOnlyList<BlockPos> GetBlocksInArea(BlockPos start, BlockPos end)
        {
            ArgumentNullException.ThrowIfNull(start);
            ArgumentNullException.ThrowIfNull(end);

            var blocks = new List<BlockPos>();
            var minPos = new BlockPos(
                Math.Min(start.X, end.X),
                Math.Min(start.Y, end.Y),
                Math.Min(start.Z, end.Z)
            );
            var maxPos = new BlockPos(
                Math.Max(start.X, end.X),
                Math.Max(start.Y, end.Y),
                Math.Max(start.Z, end.Z)
            );

            for (int x = minPos.X; x <= maxPos.X; x++)
            {
                for (int y = minPos.Y; y <= maxPos.Y; y++)
                {
                    for (int z = minPos.Z; z <= maxPos.Z; z++)
                    {
                        blocks.Add(new BlockPos(x, y, z));
                    }
                }
            }

            return blocks;
        }

        /// <summary>
        /// Format block position as string
        /// </summary>
        public static string FormatPosition(BlockPos pos)
        {
            ArgumentNullException.ThrowIfNull(pos);

            return string.Format(CultureInfo.InvariantCulture, "X: {0}, Y: {1}, Z: {2}", pos.X, pos.Y, pos.Z);
        }

        public static BlockPos GetPlayerBlockPos(ICoreClientAPI api)
        {
            ArgumentNullException.ThrowIfNull(api);

#pragma warning disable CS0618 // Avoid binding to the pre-1.22 Entity.Pos field in stale build environments.
            return api.World.Player.Entity.SidedPos.AsBlockPos;
#pragma warning restore CS0618
        }

        public static BlockPos GetEntityBlockPos(Entity entity)
        {
            ArgumentNullException.ThrowIfNull(entity);

#pragma warning disable CS0618 // Avoid binding to the pre-1.22 Entity.Pos field in stale build environments.
            return entity.SidedPos.AsBlockPos;
#pragma warning restore CS0618
        }
    }
}
