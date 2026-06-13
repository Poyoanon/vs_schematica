using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Util;
using Vintagestory.GameContent;

namespace Schematica.Core
{
    public class BlockSchematicStructure
    {
        private const int MaxSafeAxisLength = 4096;
        private const int MaxSafeBlockCount = 10_000_000;
        private const long MaxSafeSerializedFileBytes = 512L * 1024L * 1024L;
        private const int MaxLegacyAxisValue = 1023;
        private const int SafeIndexBitsPerAxis = 21;
        private const int SafeIndexMaxAxisValue = (1 << SafeIndexBitsPerAxis) - 1;

        private Dictionary<int, IReadOnlyList<SerializableBlock>>? layerCache;

        [JsonProperty]
        public string GameVersion { get; set; } = "1.20.0";

        [JsonProperty]
        public int SizeX { get; set; }

        [JsonProperty]
        public int SizeY { get; set; }

        [JsonProperty]
        public int SizeZ { get; set; }

        [JsonProperty]
        public Dictionary<int, AssetLocation> BlockCodes { get; } = new Dictionary<int, AssetLocation>();

        [JsonProperty]
        public Dictionary<int, AssetLocation> ItemCodes { get; } = new Dictionary<int, AssetLocation>();

        [JsonProperty]
        public Collection<ulong> Indices { get; } = new Collection<ulong>();

        [JsonProperty]
        public bool UsesLargeIndexing { get; set; }

        [JsonProperty]
        public Collection<IndexedBlockPosition> BlockPositions { get; } = new Collection<IndexedBlockPosition>();

        [JsonProperty]
        public Collection<int> BlockIds { get; } = new Collection<int>();

        [JsonProperty]
        public Dictionary<ulong, string> BlockEntities { get; } = new Dictionary<ulong, string>();

        [JsonProperty]
        public Collection<string> BlockEntityDataList { get; } = new Collection<string>();

        // Runtime data (not serialized)
        [JsonIgnore]
        public Collection<SerializableBlock> Blocks { get; private set; } = new Collection<SerializableBlock>();

        [JsonIgnore]
        public Dictionary<string, int> BlockCounts { get; private set; } = new Dictionary<string, int>();

        [JsonIgnore]
        public Vec3i Size => new Vec3i(SizeX, SizeY, SizeZ);

        [JsonIgnore]
        public int MaxY => SizeY - 1;

        [JsonIgnore]
        public int TotalBlocks => Blocks.Count;

        [JsonIgnore]
        public int StoredCount => UsesLargeIndexing ? BlockPositions.Count : Indices.Count;

        // Static methods for loading/saving
        public static BlockSchematicStructure CreateFromSelection(ICoreClientAPI api, BlockPos startPos, BlockPos endPos)
        {
            ArgumentNullException.ThrowIfNull(api);
            ArgumentNullException.ThrowIfNull(startPos);
            ArgumentNullException.ThrowIfNull(endPos);

            var minPos = new BlockPos(
                Math.Min(startPos.X, endPos.X),
                Math.Min(startPos.Y, endPos.Y),
                Math.Min(startPos.Z, endPos.Z)
            );
            var maxPos = new BlockPos(
                Math.Max(startPos.X, endPos.X),
                Math.Max(startPos.Y, endPos.Y),
                Math.Max(startPos.Z, endPos.Z)
            );

            int spanX = maxPos.X - minPos.X + 1;
            int spanY = maxPos.Y - minPos.Y + 1;
            int spanZ = maxPos.Z - minPos.Z + 1;
            long estimatedBlockCount = (long)spanX * (long)spanY * (long)spanZ;
            ValidateSelectionLimits(minPos, maxPos, estimatedBlockCount);

            var schematic = new BlockSchematicStructure();
            AddArea(api, schematic, minPos, maxPos);
            schematic.Unpack(api);
            return schematic;
        }

        private static void AddArea(ICoreClientAPI api, BlockSchematicStructure schematic, BlockPos startPos, BlockPos endPos)
        {
            ArgumentNullException.ThrowIfNull(api);
            ArgumentNullException.ThrowIfNull(schematic);
            ArgumentNullException.ThrowIfNull(startPos);
            ArgumentNullException.ThrowIfNull(endPos);

            var blockDataMap = new Dictionary<BlockPos, int>();
            var entityDataMap = new Dictionary<BlockPos, string>();

            var readPos = new BlockPos(startPos.X, startPos.Y, startPos.Z);
            for (int x = startPos.X; x <= endPos.X; x++)
            {
                for (int y = startPos.Y; y <= endPos.Y; y++)
                {
                    for (int z = startPos.Z; z <= endPos.Z; z++)
                    {
                        readPos.Set(x, y, z);

                        var block = api.World.BlockAccessor.GetBlock(readPos);
                        if (block?.Id == 0 || block == null) continue;

                        var keyPos = new BlockPos(x, y, z);
                        blockDataMap[keyPos] = block.Id;

                        var blockEntity = api.World.BlockAccessor.GetBlockEntity(readPos);
                        if (blockEntity == null)
                        {
                            continue;
                        }

                        entityDataMap[keyPos] = EncodeBlockEntityData(blockEntity, api);
                        blockEntity.OnStoreCollectibleMappings(schematic.BlockCodes, schematic.ItemCodes);

                        // Special handling for chiseled blocks
                        var microBlock = blockEntity as BlockEntityMicroBlock;
                        if (microBlock?.BlockIds == null)
                        {
                            continue;
                        }

                        foreach (int materialId in microBlock.BlockIds)
                        {
                            var materialBlock = api.World.GetBlock(materialId);
                            if (materialBlock != null && !schematic.BlockCodes.TryGetValue(materialId, out _))
                            {
                                schematic.BlockCodes[materialId] = materialBlock.Code;
                            }
                        }
                    }
                }
            }

            schematic.PackFromMaps(api.World, blockDataMap, entityDataMap, startPos);
        }

        private void PackFromMaps(IWorldAccessor world, Dictionary<BlockPos, int> blockMap, Dictionary<BlockPos, string> entityMap, BlockPos startPos)
        {
            ArgumentNullException.ThrowIfNull(world);
            ArgumentNullException.ThrowIfNull(blockMap);
            ArgumentNullException.ThrowIfNull(entityMap);
            ArgumentNullException.ThrowIfNull(startPos);

            Indices.Clear();
            BlockIds.Clear();
            BlockEntities.Clear();
            BlockPositions.Clear();
            BlockEntityDataList.Clear();
            BlockCounts.Clear();
            layerCache = null;
            UsesLargeIndexing = false;

            if (blockMap.Count == 0)
            {
                SizeX = 0;
                SizeY = 0;
                SizeZ = 0;
                return;
            }

            int minX = int.MaxValue;
            int minY = int.MaxValue;
            int minZ = int.MaxValue;
            int maxX = int.MinValue;
            int maxY = int.MinValue;
            int maxZ = int.MinValue;

            foreach (var pos in blockMap.Keys)
            {
                minX = Math.Min(minX, pos.X);
                minY = Math.Min(minY, pos.Y);
                minZ = Math.Min(minZ, pos.Z);
                maxX = Math.Max(maxX, pos.X);
                maxY = Math.Max(maxY, pos.Y);
                maxZ = Math.Max(maxZ, pos.Z);
            }

            int spanX = checked(maxX - minX + 1);
            int spanY = checked(maxY - minY + 1);
            int spanZ = checked(maxZ - minZ + 1);

            ValidateSelectionLimits(
                new BlockPos(minX, minY, minZ),
                new BlockPos(maxX, maxY, maxZ),
                blockMap.Count
            );

            UsesLargeIndexing = ShouldUseLargeIndexing(blockMap.Count, spanX, spanY, spanZ);

            if (spanX > SafeIndexMaxAxisValue || spanY > SafeIndexMaxAxisValue || spanZ > SafeIndexMaxAxisValue)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(blockMap),
                    $"Schematic span exceeds safe index limit ({SafeIndexMaxAxisValue}). Use a smaller selection or split it."
                );
            }

            SizeX = spanX;
            SizeY = spanY;
            SizeZ = spanZ;

            foreach (var kvp in blockMap)
            {
                int blockid = kvp.Value;
                if (blockid == 0)
                {
                    continue;
                }

                var block = world.BlockAccessor.GetBlock(blockid);
                if (block == null)
                {
                    continue;
                }

                BlockCodes[blockid] = block.Code;

                int dx = kvp.Key.X - minX;
                int dy = kvp.Key.Y - minY;
                int dz = kvp.Key.Z - minZ;

                if (UsesLargeIndexing)
                {
                    BlockPositions.Add(new IndexedBlockPosition(dx, dy, dz));
                    BlockEntityDataList.Add(string.Empty);
                }
                else
                {
                    Indices.Add(PackLegacyIndex(dx, dy, dz));
                }

                BlockIds.Add(blockid);

                if (entityMap.TryGetValue(kvp.Key, out var entity))
                {
                    if (UsesLargeIndexing)
                    {
                        int lastIndex = BlockEntityDataList.Count - 1;
                        BlockEntityDataList[lastIndex] = entity;
                    }
                    else
                    {
                        ulong index = PackLegacyIndex(dx, dy, dz);
                        BlockEntities[index] = entity;
                    }
                }
            }
        }

        private static string EncodeBlockEntityData(BlockEntity be, ICoreClientAPI api)
        {
            ArgumentNullException.ThrowIfNull(be);
            ArgumentNullException.ThrowIfNull(api);

            var tree = new TreeAttribute();
            be.ToTreeAttributes(tree);

            if (be is BlockEntityMicroBlock microBlock && microBlock.BlockIds != null)
            {
                var materialCodes = new StringArrayAttribute
                {
                    value = new string[microBlock.BlockIds.Length]
                };

                for (int i = 0; i < microBlock.BlockIds.Length; i++)
                {
                    var block = api.World.GetBlock(microBlock.BlockIds[i]);
                    materialCodes.value[i] = block?.Code?.ToString() ?? "game:air";
                }

                tree["materialCodes"] = materialCodes;
            }

            using var ms = new MemoryStream();
            using (var writer = new BinaryWriter(ms))
            {
                tree.ToBytes(writer);
            }

            return Ascii85.Encode(ms.ToArray());
        }

        public static void SaveToFile(ICoreClientAPI api, BlockSchematicStructure schematic, string filename)
        {
            ArgumentNullException.ThrowIfNull(api);
            ArgumentNullException.ThrowIfNull(schematic);
            if (string.IsNullOrWhiteSpace(filename)) throw new ArgumentException("Filename cannot be empty.", nameof(filename));
            filename = Path.GetFileName(filename);
            if (string.IsNullOrWhiteSpace(filename))
            {
                throw new ArgumentException("Invalid filename.", nameof(filename));
            }
            if (filename.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                throw new ArgumentException("Filename contains invalid characters.", nameof(filename));
            }

            if (!filename.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                filename += ".json";
            }

            string fullPath = Path.Combine(api.GetOrCreateDataPath("Schematics"), filename);

            var directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            ValidateSavedSchematicLimits(schematic);

            JsonSerializer serializer = new JsonSerializer { Formatting = Formatting.Indented };
            using var fs = new FileStream(fullPath, FileMode.Create, FileAccess.Write, FileShare.None);
            using var writer = new StreamWriter(fs, Encoding.UTF8);
            serializer.Serialize(writer, schematic);
            api.ShowChatMessage($"Schematic saved to: {fullPath}");
        }

        public static BlockSchematicStructure LoadFromFile(ICoreClientAPI api, string filename)
        {
            ArgumentNullException.ThrowIfNull(api);
            if (string.IsNullOrWhiteSpace(filename))
            {
                throw new ArgumentException("Filename cannot be empty.", nameof(filename));
            }
            filename = Path.GetFileName(filename);
            if (string.IsNullOrWhiteSpace(filename))
            {
                throw new ArgumentException("Invalid filename.", nameof(filename));
            }
            if (filename.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                throw new ArgumentException("Filename contains invalid characters.", nameof(filename));
            }

            if (!filename.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                filename += ".json";
            }

            string fullPath = Path.Combine(api.GetOrCreateDataPath("Schematics"), filename);

            if (!File.Exists(fullPath))
            {
                throw new FileNotFoundException($"Schematic file not found: {filename}");
            }

            var fileSize = new FileInfo(fullPath).Length;
            if (fileSize > MaxSafeSerializedFileBytes)
            {
                throw new InvalidDataException($"Schematic file is too large to load safely ({fileSize:N0} bytes).");
            }

            BlockSchematicStructure schematic;
            using (var fs = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var reader = new StreamReader(fs, Encoding.UTF8))
            using (var jsonReader = new JsonTextReader(reader))
            {
                var serializer = new JsonSerializer();
                schematic = serializer.Deserialize<BlockSchematicStructure>(jsonReader)
                    ?? throw new InvalidDataException($"Failed loading {filename}: file contents are invalid");
            }

            ValidateLoadedSchematic(schematic, fullPath);
            schematic.Init(api.World.BlockAccessor);
            schematic.Unpack(api);
            return schematic;
        }

        public static IReadOnlyList<string> GetAvailableSchematics(ICoreClientAPI api)
        {
            ArgumentNullException.ThrowIfNull(api);

            var schematicsPath = api.GetOrCreateDataPath("Schematics");
            if (!Directory.Exists(schematicsPath))
            {
                return Array.Empty<string>();
            }

            return Directory.EnumerateFiles(schematicsPath, "*.json")
                .Select(f => Path.GetFileNameWithoutExtension(f))
                .OrderBy(x => x)
                .ToArray();
        }

        public void Init(IBlockAccessor blockAccessor)
        {
            ArgumentNullException.ThrowIfNull(blockAccessor);
            _ = BlockCodes.Count;
        }

        public void Pack(IWorldAccessor world, BlockPos startPos)
        {
            ArgumentNullException.ThrowIfNull(world);
            ArgumentNullException.ThrowIfNull(startPos);
            _ = SizeX;
        }

        public void Unpack(ICoreClientAPI api)
        {
            ArgumentNullException.ThrowIfNull(api);

            Blocks.Clear();
            BlockCounts.Clear();

            int expectedCount = BlockIds.Count;
            if (expectedCount == 0)
            {
                layerCache = new Dictionary<int, IReadOnlyList<SerializableBlock>>();
                return;
            }

            if (UsesLargeIndexing)
            {
                if (BlockPositions.Count != expectedCount)
                {
                    throw new InvalidDataException("Invalid large-index schematic payload: position count does not match block count.");
                }

                if (BlockEntityDataList.Count != 0 && BlockEntityDataList.Count != expectedCount)
                {
                    throw new InvalidDataException("Invalid large-index schematic payload: block entity list size does not match block count.");
                }
            }
            else if (Indices.Count != expectedCount)
            {
                throw new InvalidDataException("Invalid legacy schematic payload: index count does not match block count.");
            }

            if (!UsesLargeIndexing)
            {
                ValidateLegacyIndices(Indices, expectedCount);
            }

            var localBlocks = new List<SerializableBlock>(expectedCount);
            var localLayerCache = new Dictionary<int, List<SerializableBlock>>();

            for (int i = 0; i < expectedCount; i++)
            {
                int blockId = BlockIds[i];
                if (blockId == 0)
                {
                    continue;
                }

                int dx;
                int dy;
                int dz;

                if (UsesLargeIndexing)
                {
                    var position = BlockPositions[i];
                    dx = position.X;
                    dy = position.Y;
                    dz = position.Z;
                }
                else
                {
                    DecodeLegacyIndexWithLayoutFallback(Indices[i], SizeX, SizeY, SizeZ, out dx, out dy, out dz);
                }

                if (dx < 0 || dy < 0 || dz < 0
                    || dx >= SizeX || dy >= SizeY || dz >= SizeZ)
                {
                    throw new InvalidDataException("Invalid schematic payload: decoded position is outside schematic bounds.");
                }

                string blockCode = "unknown";
                if (BlockCodes.TryGetValue(blockId, out var blockCodeValue) && blockCodeValue != null)
                {
                    blockCode = blockCodeValue.ToString();
                }

                var block = new SerializableBlock
                {
                    X = dx,
                    Y = dy,
                    Z = dz,
                    BlockId = blockId,
                    BlockCode = blockCode
                };

                if (UsesLargeIndexing)
                {
                    if (i < BlockEntityDataList.Count && !string.IsNullOrWhiteSpace(BlockEntityDataList[i]))
                    {
                        block.BlockEntityData = Encoding.UTF8.GetBytes(BlockEntityDataList[i]);
                    }
                }
                else
                {
                    ulong legacyIndex = Indices[i];
                    if (BlockEntities.TryGetValue(legacyIndex, out var entity) && !string.IsNullOrWhiteSpace(entity))
                    {
                        block.BlockEntityData = Encoding.UTF8.GetBytes(entity);
                    }
                }

                localBlocks.Add(block);

                if (!localLayerCache.TryGetValue(block.Y, out var layerBlocks))
                {
                    layerBlocks = new List<SerializableBlock>();
                    localLayerCache[block.Y] = layerBlocks;
                }

                layerBlocks.Add(block);

                if (BlockCounts.TryGetValue(blockCode, out var count))
                {
                    BlockCounts[blockCode] = count + 1;
                }
                else
                {
                    BlockCounts[blockCode] = 1;
                }
            }

            for (int i = 0; i < localBlocks.Count; i++)
            {
                Blocks.Add(localBlocks[i]);
            }

            var convertedLayerCache = new Dictionary<int, IReadOnlyList<SerializableBlock>>(localLayerCache.Count);
            foreach (var kvp in localLayerCache)
            {
                convertedLayerCache[kvp.Key] = kvp.Value.AsReadOnly();
            }

            layerCache = convertedLayerCache;
        }

        public void TransformWhilePacked(IWorldAccessor worldForResolve, EnumOrigin aroundOrigin, int angle, EnumAxis? flipAxis = null)
        {
            ArgumentNullException.ThrowIfNull(worldForResolve);
            if (BlockIds.Count == 0)
            {
                return;
            }

            angle = GameMath.Mod(angle, 360);
            if (angle == 0 && flipAxis == null)
            {
                return;
            }

            var tempBlocksMap = new Dictionary<BlockPos, int>(BlockIds.Count);
            var tempEntitiesMap = new Dictionary<BlockPos, string>(BlockIds.Count);
            int expectedCount = Math.Min(BlockIds.Count, UsesLargeIndexing ? BlockPositions.Count : Indices.Count);

            for (int i = 0; i < expectedCount; i++)
            {
                int storedBlockId = BlockIds[i];
                if (!BlockCodes.TryGetValue(storedBlockId, out var blockCode) || blockCode == null)
                {
                    continue;
                }

                Block? newBlock = worldForResolve.GetBlock(blockCode);
                if (newBlock == null)
                {
                    continue;
                }

                int dx;
                int dy;
                int dz;
                if (UsesLargeIndexing)
                {
                    var sourcePos = BlockPositions[i];
                    dx = sourcePos.X;
                    dy = sourcePos.Y;
                    dz = sourcePos.Z;
                }
                else
                {
                    DecodeLegacyIndexWithLayoutFallback(Indices[i], SizeX, SizeY, SizeZ, out dx, out dy, out dz);
                }

                // Apply flip
                if (flipAxis != null)
                {
                    if (flipAxis.Value == EnumAxis.Y)
                    {
                        dy = SizeY - dy - 1;
                        var newCode = newBlock.GetVerticallyFlippedBlockCode();
                        newBlock = worldForResolve.GetBlock(newCode) ?? newBlock;
                    }

                    if (flipAxis.Value == EnumAxis.X)
                    {
                        dx = SizeX - dx - 1;
                        var newCode = newBlock.GetHorizontallyFlippedBlockCode(flipAxis.Value);
                        newBlock = worldForResolve.GetBlock(newCode) ?? newBlock;
                    }

                    if (flipAxis.Value == EnumAxis.Z)
                    {
                        dz = SizeZ - dz - 1;
                        var newCode = newBlock.GetHorizontallyFlippedBlockCode(flipAxis.Value);
                        newBlock = worldForResolve.GetBlock(newCode) ?? newBlock;
                    }
                }

                // Apply rotation
                if (angle != 0)
                {
                    var newCode = newBlock.GetRotatedBlockCode(angle);
                    Block? rotBlock = worldForResolve.GetBlock(newCode);
                    if (rotBlock != null)
                    {
                        newBlock = rotBlock;
                    }
                }

                var pos = GetRotatedPos(aroundOrigin, angle, dx, dy, dz);
                tempBlocksMap[pos] = newBlock.BlockId;

                if (UsesLargeIndexing)
                {
                    if (i < BlockEntityDataList.Count && !string.IsNullOrWhiteSpace(BlockEntityDataList[i]))
                    {
                        tempEntitiesMap[pos] = TransformBlockEntity(BlockEntityDataList[i], angle, flipAxis, worldForResolve);
                    }
                }
                else if (BlockEntities.TryGetValue(Indices[i], out var entity) && !string.IsNullOrWhiteSpace(entity))
                {
                    tempEntitiesMap[pos] = TransformBlockEntity(entity, angle, flipAxis, worldForResolve);
                }
            }

            PackFromMaps(worldForResolve, tempBlocksMap, tempEntitiesMap, new BlockPos(0, 0, 0));
        }

        private string TransformBlockEntity(string beData, int angle, EnumAxis? flipAxis, IWorldAccessor world)
        {
            if (string.IsNullOrWhiteSpace(beData))
            {
                return string.Empty;
            }

            ArgumentNullException.ThrowIfNull(world);

            var tree = DecodeBlockEntityData(beData);
            var rotatable = new BlockEntityMicroBlock();
            rotatable.Initialize(world.Api);
            rotatable.OnTransformed(world, tree, angle, BlockCodes, ItemCodes, flipAxis);

            using var ms = new MemoryStream();
            using (var writer = new BinaryWriter(ms))
            {
                tree.ToBytes(writer);
            }

            return Ascii85.Encode(ms.ToArray());
        }

        private static TreeAttribute DecodeBlockEntityData(string data)
        {
            byte[] buffer = Ascii85.Decode(data);
            var tree = new TreeAttribute();
            using var ms = new MemoryStream(buffer);
            using (var reader = new BinaryReader(ms))
            {
                tree.FromBytes(reader);
            }

            return tree;
        }

        private BlockPos GetRotatedPos(EnumOrigin aroundOrigin, int angle, int dx, int dy, int dz)
        {
            if (aroundOrigin != EnumOrigin.StartPos)
            {
                dx -= SizeX / 2;
                dz -= SizeZ / 2;
            }

            var pos = new BlockPos(dx, dy, dz);

            switch (angle)
            {
                case 90:
                    pos.Set(-dz, dy, dx);
                    break;
                case 180:
                    pos.Set(-dx, dy, -dz);
                    break;
                case 270:
                    pos.Set(dz, dy, -dx);
                    break;
            }

            if (aroundOrigin != EnumOrigin.StartPos)
            {
                pos.X += SizeX / 2;
                pos.Z += SizeZ / 2;
            }

            return pos;
        }

        public IReadOnlyList<SerializableBlock> GetBlocksAtLayer(int y)
        {
            if (layerCache == null)
            {
                var fallbackCache = new Dictionary<int, List<SerializableBlock>>();
                foreach (var block in Blocks)
                {
                    if (!fallbackCache.TryGetValue(block.Y, out var layerBlocks))
                    {
                        layerBlocks = new List<SerializableBlock>();
                        fallbackCache[block.Y] = layerBlocks;
                    }

                    layerBlocks.Add(block);
                }

                var readOnlyFallbackCache = new Dictionary<int, IReadOnlyList<SerializableBlock>>(fallbackCache.Count);
                foreach (var kvp in fallbackCache)
                {
                    readOnlyFallbackCache[kvp.Key] = kvp.Value.AsReadOnly();
                }

                layerCache = readOnlyFallbackCache;
            }

            if (layerCache.TryGetValue(y, out var cachedBlocks))
            {
                return cachedBlocks;
            }

            return Array.Empty<SerializableBlock>();
        }

        public BlockSchematicStructure ClonePacked()
        {
            var clone = new BlockSchematicStructure
            {
                SizeX = SizeX,
                SizeY = SizeY,
                SizeZ = SizeZ,
                GameVersion = GameVersion,
                UsesLargeIndexing = UsesLargeIndexing
            };

            foreach (var blockCode in BlockCodes)
            {
                clone.BlockCodes[blockCode.Key] = blockCode.Value;
            }

            foreach (var itemCode in ItemCodes)
            {
                clone.ItemCodes[itemCode.Key] = itemCode.Value;
            }

            foreach (var index in Indices)
            {
                clone.Indices.Add(index);
            }

            foreach (var position in BlockPositions)
            {
                clone.BlockPositions.Add(new IndexedBlockPosition(position.X, position.Y, position.Z));
            }

            foreach (var blockId in BlockIds)
            {
                clone.BlockIds.Add(blockId);
            }

            foreach (var blockEntity in BlockEntities)
            {
                clone.BlockEntities[blockEntity.Key] = blockEntity.Value;
            }

            foreach (var blockEntityData in BlockEntityDataList)
            {
                clone.BlockEntityDataList.Add(blockEntityData);
            }

            return clone;
        }

        private static ulong PackLegacyIndex(int x, int y, int z)
        {
            if (x < 0 || x > MaxLegacyAxisValue || y < 0 || y > MaxLegacyAxisValue || z < 0 || z > MaxLegacyAxisValue)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(x),
                    $"Legacy schematic payload position out of range. Maximum per-axis value is {MaxLegacyAxisValue}."
                );
            }

            // Keep legacy ordering for backward compatibility:
            // bits 0..9 = X, bits 10..19 = Z, bits 20..29 = Y
            return (ulong)(uint)x
                | ((ulong)(uint)z << 10)
                | ((ulong)(uint)y << 20);
        }

        private static void DecodeLegacyIndex(ulong index, out int x, out int y, out int z)
        {
            x = (int)(index & 0x3FFu);
            y = (int)((index >> 20) & 0x3FFu);
            z = (int)((index >> 10) & 0x3FFu);
        }

        private static void DecodeLegacyIndexWithLayoutFallback(ulong index, int sizeX, int sizeY, int sizeZ, out int x, out int y, out int z)
        {
            DecodeLegacyIndex(index, out x, out y, out z);
            if (IsWithinBounds(x, y, z, sizeX, sizeY, sizeZ))
            {
                return;
            }

            // Compatibility fallback for files written with transient alternate ordering:
            // bits 0..9 = X, bits 10..19 = Y, bits 20..29 = Z
            int altX = (int)(index & 0x3FFu);
            int altY = (int)((index >> 10) & 0x3FFu);
            int altZ = (int)((index >> 20) & 0x3FFu);
            if (IsWithinBounds(altX, altY, altZ, sizeX, sizeY, sizeZ))
            {
                x = altX;
                y = altY;
                z = altZ;
            }
        }

        private static bool IsWithinBounds(int x, int y, int z, int sizeX, int sizeY, int sizeZ)
        {
            return x >= 0 && y >= 0 && z >= 0
                && x < sizeX && y < sizeY && z < sizeZ;
        }

        private static void ValidateSelectionLimits(BlockPos minPos, BlockPos maxPos, long blockCount)
        {
            int spanX = maxPos.X - minPos.X + 1;
            int spanY = maxPos.Y - minPos.Y + 1;
            int spanZ = maxPos.Z - minPos.Z + 1;

            if (spanX <= 0 || spanY <= 0 || spanZ <= 0)
            {
                throw new ArgumentException("Selection size must be greater than zero on all axes.");
            }

            if (spanX > MaxSafeAxisLength || spanY > MaxSafeAxisLength || spanZ > MaxSafeAxisLength)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(maxPos),
                    $"Selection axis length exceeds safe limit ({MaxSafeAxisLength})."
                );
            }

            if (blockCount <= 0)
            {
                throw new InvalidOperationException("Selection contains no blocks to save.");
            }

            if (blockCount > MaxSafeBlockCount)
            {
                throw new InvalidOperationException($"Selection has too many blocks ({blockCount:N0}). Limit is {MaxSafeBlockCount:N0}.");
            }
        }

        private static void ValidateSavedSchematicLimits(BlockSchematicStructure schematic)
        {
            ArgumentNullException.ThrowIfNull(schematic);

            if (schematic.BlockIds.Count > MaxSafeBlockCount)
            {
                throw new InvalidOperationException($"Schematic has too many blocks ({schematic.BlockIds.Count:N0}). Limit is {MaxSafeBlockCount:N0}.");
            }

            if (schematic.SizeX <= 0 || schematic.SizeY <= 0 || schematic.SizeZ <= 0)
            {
                throw new InvalidDataException("Schematic dimensions are invalid.");
            }

            if (schematic.SizeX > SafeIndexMaxAxisValue || schematic.SizeY > SafeIndexMaxAxisValue || schematic.SizeZ > SafeIndexMaxAxisValue)
            {
                throw new InvalidDataException($"Schematic dimensions exceed index limits ({SafeIndexMaxAxisValue}).");
            }
        }

        private static void ValidateLoadedSchematic(BlockSchematicStructure schematic, string sourcePath)
        {
            ArgumentNullException.ThrowIfNull(schematic);
            if (string.IsNullOrWhiteSpace(sourcePath))
            {
                throw new ArgumentException("Source path is required.");
            }

            if (string.IsNullOrWhiteSpace(schematic.GameVersion))
            {
                schematic.GameVersion = "1.20.0";
            }

            if (schematic.BlockIds == null || schematic.BlockIds.Count == 0)
            {
                return;
            }

            if (schematic.SizeX <= 0 || schematic.SizeY <= 0 || schematic.SizeZ <= 0)
            {
                throw new InvalidDataException($"Invalid dimensions in schematic '{sourcePath}'.");
            }

            if (schematic.SizeX > SafeIndexMaxAxisValue || schematic.SizeY > SafeIndexMaxAxisValue || schematic.SizeZ > SafeIndexMaxAxisValue)
            {
                throw new InvalidDataException($"Schematic '{sourcePath}' exceeds safe dimensions.");
            }

            if (schematic.BlockIds.Count > MaxSafeBlockCount)
            {
                throw new InvalidDataException($"Schematic '{sourcePath}' exceeds safe block count.");
            }

            if (schematic.UsesLargeIndexing)
            {
                if (schematic.BlockPositions.Count != schematic.BlockIds.Count)
                {
                    throw new InvalidDataException($"Schematic '{sourcePath}' has invalid large-index payload.");
                }

                if (schematic.BlockEntityDataList.Count != 0 && schematic.BlockEntityDataList.Count != schematic.BlockIds.Count)
                {
                    throw new InvalidDataException($"Schematic '{sourcePath}' has invalid block entity payload.");
                }

                if (schematic.BlockPositions.Any(pos => pos == null))
                {
                    throw new InvalidDataException($"Schematic '{sourcePath}' has invalid block positions.");
                }
            }
            else
            {
                if (schematic.Indices.Count != schematic.BlockIds.Count)
                {
                    throw new InvalidDataException($"Schematic '{sourcePath}' has invalid legacy payload.");
                }
            }

            int invalidIds = schematic.BlockIds.Count(blockId => blockId <= 0);
            if (invalidIds > 0)
            {
                throw new InvalidDataException($"Schematic '{sourcePath}' contains invalid block IDs.");
            }
        }

        private static void ValidateLegacyIndices(Collection<ulong> indices, int expectedCount)
        {
            if (indices == null)
            {
                throw new InvalidDataException("Legacy index list is missing.");
            }

            if (indices.Count != expectedCount)
            {
                throw new InvalidDataException("Legacy index count does not match block count.");
            }

            foreach (var index in indices)
            {
                DecodeLegacyIndex(index, out int x, out int y, out int z);
                if (x < 0 || y < 0 || z < 0 || x > MaxLegacyAxisValue || y > MaxLegacyAxisValue || z > MaxLegacyAxisValue)
                {
                    throw new InvalidDataException("Legacy index list contains values outside supported range.");
                }
            }
        }

        private static bool ShouldUseLargeIndexing(int blockCount, int spanX, int spanY, int spanZ)
        {
            return blockCount > MaxSafeBlockCount
                || spanX > MaxLegacyAxisValue
                || spanY > MaxLegacyAxisValue
                || spanZ > MaxLegacyAxisValue;
        }
    }

    public sealed class IndexedBlockPosition
    {
        public IndexedBlockPosition()
        {
        }

        public IndexedBlockPosition(int x, int y, int z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        [JsonProperty]
        public int X { get; set; }

        [JsonProperty]
        public int Y { get; set; }

        [JsonProperty]
        public int Z { get; set; }
    }
}


