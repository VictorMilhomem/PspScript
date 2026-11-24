using DotTiled;
using DotTiled.Serialization;
using PspScript.Ast;
using PspScript.Semantics;

namespace PspScript.CodeGen;

internal sealed record BakedTilemapAsset(
    string Name,
    ushort Cols,
    ushort Rows,
    ushort TileSize,
    ushort LayerCount,
    ushort[] Layers,
    byte[] Collision,
    bool IsMutable,
    string? ExplicitTilesetAssetName,
    string? AutoTilesetImagePath,
    BakedPixelFormat AutoTilesetFormat
);

/// <summary>
/// Compile-time Tiled (.tmx / .tmj) map baker powered by DotTiled.
/// Extracts multi-layer uint16_t tile grids (14-bit tile ID + H/V flip bits)
/// and a compact static collision grid for zero-heap C99 emission.
/// </summary>
internal static class TilemapBaker
{
    private const ushort TileIdMask = 0x3FFF;
    private const ushort TileFlipH  = 0x8000;
    private const ushort TileFlipV  = 0x4000;

    public static BakedTilemapAsset BakeTilemap(AssetSymbol asset, string? baseDirectory)
    {
        string? explicitTileset = GetIdentifierOption(asset.Declaration.Options, "tileset");
        string collisionLayerName = GetStringOption(asset.Declaration.Options, "collision_layer") ?? "Collision";
        BakedPixelFormat autoFormat = AssetBaker.ResolveRequestedFormat(asset.Declaration.Options);

        string? resolvedPath = AssetBaker.ResolveAssetPath(
            asset.FilePath,
            asset.Declaration.SourceDirectory,
            baseDirectory
        );

        if (resolvedPath is null || !File.Exists(resolvedPath))
        {
            Console.Error.WriteLine($"Warning: Tilemap file '{asset.FilePath}' not found for '{asset.Name}'. Using 1x1 empty placeholder.");
            return CreatePlaceholder(asset.Name, asset.IsMutable, explicitTileset, autoFormat);
        }

        try
        {
            var loader = Loader.Default();
            Map map = loader.LoadMap(resolvedPath);

            ushort cols = (ushort)Math.Max(1, map.Width);
            ushort rows = (ushort)Math.Max(1, map.Height);
            ushort tileSize = (ushort)Math.Max(1, map.TileWidth);
            int cellCount = cols * rows;

            // Collect solid tile IDs (0-based local tile IDs) and optional auto-baked tileset image path
            var solidLocalTileIds = new HashSet<uint>();
            uint primaryFirstGid = 1u;
            string? autoTilesetImagePath = null;

            if (map.Tilesets is { Count: > 0 })
            {
                var primaryTs = map.Tilesets[0];
                primaryFirstGid = (primaryTs.FirstGID.HasValue && primaryTs.FirstGID.Value > 0)
                    ? primaryTs.FirstGID.Value
                    : 1u;

                if (explicitTileset is null && primaryTs.Image.HasValue && primaryTs.Image.Value.Source.HasValue)
                {
                    string rawImgSource = primaryTs.Image.Value.Source.Value;
                    string? mapDir = Path.GetDirectoryName(resolvedPath);
                    string? tsDir = primaryTs.Source.HasValue && mapDir is not null
                        ? Path.GetDirectoryName(Path.GetFullPath(Path.Combine(mapDir, primaryTs.Source.Value)))
                        : mapDir;

                    autoTilesetImagePath = AssetBaker.ResolveAssetPath(rawImgSource, tsDir, mapDir);
                }

                foreach (var ts in map.Tilesets)
                {
                    if (ts.Tiles is null)
                        continue;

                    foreach (var tile in ts.Tiles)
                    {
                        if (tile.Properties is null)
                            continue;

                        foreach (var prop in tile.Properties)
                        {
                            if (prop is BoolProperty boolProp &&
                                string.Equals(boolProp.Name, "solid", StringComparison.OrdinalIgnoreCase) &&
                                boolProp.Value)
                            {
                                solidLocalTileIds.Add(tile.ID);
                            }
                        }
                    }
                }
            }

            var renderLayers = new List<ushort[]>();
            byte[] collision = new byte[cellCount];

            foreach (var tileLayer in EnumerateTileLayers(map.Layers))
            {
                if (!tileLayer.Data.HasValue || !tileLayer.Data.Value.GlobalTileIDs.HasValue)
                    continue;

                uint[] gids = tileLayer.Data.Value.GlobalTileIDs.Value;
                FlippingFlags[]? flags = tileLayer.Data.Value.FlippingFlags.HasValue
                    ? tileLayer.Data.Value.FlippingFlags.Value
                    : null;

                int layerWidth = Math.Max(1, tileLayer.Width);
                int layerHeight = Math.Max(1, tileLayer.Height);

                bool isCollisionLayer = string.Equals(
                    tileLayer.Name,
                    collisionLayerName,
                    StringComparison.OrdinalIgnoreCase
                );

                if (isCollisionLayer)
                {
                    for (int r = 0; r < Math.Min(rows, layerHeight); r++)
                    {
                        for (int c = 0; c < Math.Min(cols, layerWidth); c++)
                        {
                            int srcIdx = r * layerWidth + c;
                            if (srcIdx < gids.Length && gids[srcIdx] != 0u)
                            {
                                collision[r * cols + c] = 1;
                            }
                        }
                    }
                    continue;
                }

                if (!tileLayer.Visible)
                    continue;

                ushort[] layerCells = new ushort[cellCount];
                for (int r = 0; r < Math.Min(rows, layerHeight); r++)
                {
                    for (int c = 0; c < Math.Min(cols, layerWidth); c++)
                    {
                        int srcIdx = r * layerWidth + c;
                        if (srcIdx >= gids.Length)
                            continue;

                        uint gid = gids[srcIdx];
                        if (gid == 0u)
                            continue;

                        uint localOneBased = gid >= primaryFirstGid ? (gid - primaryFirstGid + 1u) : gid;
                        if (localOneBased > 0u && solidLocalTileIds.Contains(localOneBased - 1u))
                        {
                            collision[r * cols + c] = 1;
                        }

                        ushort packed = (ushort)(localOneBased & TileIdMask);
                        if (flags is not null && srcIdx < flags.Length)
                        {
                            FlippingFlags f = flags[srcIdx];
                            if ((f & FlippingFlags.FlippedHorizontally) != 0)
                                packed |= TileFlipH;
                            if ((f & FlippingFlags.FlippedVertically) != 0)
                                packed |= TileFlipV;
                        }

                        layerCells[r * cols + c] = packed;
                    }
                }

                renderLayers.Add(layerCells);
            }

            if (renderLayers.Count == 0)
            {
                renderLayers.Add(new ushort[cellCount]);
            }

            ushort layerCount = (ushort)renderLayers.Count;
            ushort[] flatLayers = new ushort[layerCount * cellCount];
            for (int l = 0; l < layerCount; l++)
            {
                Array.Copy(renderLayers[l], 0, flatLayers, l * cellCount, cellCount);
            }

            return new BakedTilemapAsset(
                Name: asset.Name,
                Cols: cols,
                Rows: rows,
                TileSize: tileSize,
                LayerCount: layerCount,
                Layers: flatLayers,
                Collision: collision,
                IsMutable: asset.IsMutable,
                ExplicitTilesetAssetName: explicitTileset,
                AutoTilesetImagePath: autoTilesetImagePath,
                AutoTilesetFormat: autoFormat
            );
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Warning: Failed to bake tilemap '{resolvedPath}' for asset '{asset.Name}': {ex.Message}. Using 1x1 empty placeholder.");
            return CreatePlaceholder(asset.Name, asset.IsMutable, explicitTileset, autoFormat);
        }
    }

    private static IEnumerable<TileLayer> EnumerateTileLayers(IEnumerable<BaseLayer>? layers)
    {
        if (layers is null)
            yield break;

        foreach (var layer in layers)
        {
            if (layer is TileLayer tl)
            {
                yield return tl;
            }
            else if (layer is Group group && group.Visible)
            {
                foreach (var nested in EnumerateTileLayers(group.Layers))
                {
                    yield return nested;
                }
            }
        }
    }

    private static string? GetIdentifierOption(IReadOnlyList<NamedArg> options, string name)
    {
        foreach (var opt in options)
        {
            if (string.Equals(opt.Name, name, StringComparison.OrdinalIgnoreCase) &&
                opt.Value is IdentifierExpr id)
            {
                return id.Name;
            }
        }
        return null;
    }

    private static string? GetStringOption(IReadOnlyList<NamedArg> options, string name)
    {
        foreach (var opt in options)
        {
            if (string.Equals(opt.Name, name, StringComparison.OrdinalIgnoreCase) &&
                opt.Value is StringLiteralExpr str)
            {
                return str.Text;
            }
        }
        return null;
    }

    private static BakedTilemapAsset CreatePlaceholder(
        string name,
        bool isMutable,
        string? explicitTileset,
        BakedPixelFormat autoFormat)
    {
        return new BakedTilemapAsset(
            Name: name,
            Cols: 1,
            Rows: 1,
            TileSize: 16,
            LayerCount: 1,
            Layers: [0],
            Collision: [0],
            IsMutable: isMutable,
            ExplicitTilesetAssetName: explicitTileset,
            AutoTilesetImagePath: null,
            AutoTilesetFormat: autoFormat
        );
    }
}
