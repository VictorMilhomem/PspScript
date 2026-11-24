using DotTiled;
using DotTiled.Serialization;
using PspScript.Ast;
using PspScript.Semantics;

namespace PspScript.CodeGen;

internal sealed record BakedTilesetRef(
    ushort FirstGid,
    ushort TileSize,
    string? ExplicitAssetName,
    string? ImagePath,
    BakedPixelFormat Format
);

internal sealed record BakedTilemapAsset(
    string Name,
    ushort Cols,
    ushort Rows,
    ushort TileSize,
    ushort LayerCount,
    ushort[] Layers,
    byte[] Collision,
    bool IsMutable,
    IReadOnlyList<BakedTilesetRef> Tilesets
);

/// <summary>
/// Compile-time Tiled (.tmx / .tmj) map baker powered by DotTiled.
/// Extracts multi-layer uint16_t tile grids (14-bit global tile ID + H/V flip bits),
/// multi-tileset firstgid metadata, and a compact static collision grid for zero-heap C99 emission.
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
        bool? explicitDrawCollision = GetBoolOption(asset.Declaration.Options, "draw_collision");
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

            // Collect all tilesets (sorted ascending by firstgid) and solid GLOBAL tile IDs
            var solidGlobalTileIds = new HashSet<uint>();
            var bakedTilesets = new List<BakedTilesetRef>();
            string? mapDir = Path.GetDirectoryName(resolvedPath);

            if (map.Tilesets is { Count: > 0 })
            {
                var orderedTilesets = map.Tilesets
                    .Select(ts => (Tileset: ts, FirstGid: (ts.FirstGID.HasValue && ts.FirstGID.Value > 0) ? ts.FirstGID.Value : 1u))
                    .OrderBy(static x => x.FirstGid)
                    .ToList();

                for (int tsIdx = 0; tsIdx < orderedTilesets.Count; tsIdx++)
                {
                    var (ts, firstGid) = orderedTilesets[tsIdx];
                    ushort tsTileSize = (ushort)(ts.TileWidth > 0 ? ts.TileWidth : tileSize);

                    if (firstGid > TileIdMask)
                    {
                        Console.Error.WriteLine(
                            $"Warning: Tilemap '{asset.Name}' tileset has firstgid={firstGid} exceeding 14-bit tile ID limit ({TileIdMask}); its tiles may not render properly.");
                    }

                    if (tsIdx == 0 && explicitTileset is not null)
                    {
                        bakedTilesets.Add(new BakedTilesetRef(
                            FirstGid: (ushort)Math.Min(firstGid, TileIdMask),
                            TileSize: tsTileSize,
                            ExplicitAssetName: explicitTileset,
                            ImagePath: null,
                            Format: autoFormat
                        ));
                    }
                    else
                    {
                        string? resolvedImgPath = null;
                        if (ts.Image.HasValue && ts.Image.Value.Source.HasValue)
                        {
                            string rawImgSource = ts.Image.Value.Source.Value;
                            string? tsDir = ts.Source.HasValue && mapDir is not null
                                ? Path.GetDirectoryName(Path.GetFullPath(Path.Combine(mapDir, ts.Source.Value)))
                                : mapDir;

                            resolvedImgPath = AssetBaker.ResolveAssetPath(rawImgSource, tsDir, mapDir);
                        }

                        if (resolvedImgPath is null)
                        {
                            string tsLabel = ts.Source.HasValue ? ts.Source.Value : (ts.Name ?? $"index {tsIdx}");
                            Console.Error.WriteLine(
                                $"Warning: Tilemap '{asset.Name}' tileset '{tsLabel}' (firstgid={firstGid}) has no single atlas image or its PNG was not found; its tiles will not render.");
                        }

                        bakedTilesets.Add(new BakedTilesetRef(
                            FirstGid: (ushort)Math.Min(firstGid, TileIdMask),
                            TileSize: tsTileSize,
                            ExplicitAssetName: null,
                            ImagePath: resolvedImgPath,
                            Format: autoFormat
                        ));
                    }

                    // Collect solid tile IDs using GLOBAL tile IDs (firstGid + local tile.ID)
                    if (ts.Tiles is not null)
                    {
                        foreach (var tile in ts.Tiles)
                        {
                            if (tile.Properties is null)
                                continue;

                            if (HasSolidProperty(tile.Properties))
                            {
                                solidGlobalTileIds.Add(firstGid + tile.ID);
                            }
                        }
                    }
                }
            }
            else if (explicitTileset is not null)
            {
                bakedTilesets.Add(new BakedTilesetRef(
                    FirstGid: 1,
                    TileSize: tileSize,
                    ExplicitAssetName: explicitTileset,
                    ImagePath: null,
                    Format: autoFormat
                ));
            }

            var allTileLayers = EnumerateTileLayers(map.Layers).ToList();
            bool hasOtherVisibleVisualLayer = allTileLayers.Any(l =>
                l.Visible && !string.Equals(l.Name, collisionLayerName, StringComparison.OrdinalIgnoreCase));

            var renderLayers = new List<ushort[]>();
            byte[] collision = new byte[cellCount];
            bool anyVisibleTileRendered = false;
            bool gidOverflowWarned = false;

            foreach (var tileLayer in allTileLayers)
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

                    bool shouldRenderCollision = explicitDrawCollision ?? !hasOtherVisibleVisualLayer;
                    if (!shouldRenderCollision || !tileLayer.Visible)
                        continue;
                }
                else if (!tileLayer.Visible)
                {
                    continue;
                }

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

                        if (solidGlobalTileIds.Contains(gid))
                        {
                            collision[r * cols + c] = 1;
                        }

                        if (gid > TileIdMask && !gidOverflowWarned)
                        {
                            gidOverflowWarned = true;
                            Console.Error.WriteLine(
                                $"Warning: Tilemap '{asset.Name}' contains tile GID {gid} exceeding 14-bit limit ({TileIdMask}).");
                        }

                        ushort packed = (ushort)(gid & TileIdMask);
                        if (flags is not null && srcIdx < flags.Length)
                        {
                            FlippingFlags f = flags[srcIdx];
                            if ((f & FlippingFlags.FlippedHorizontally) != 0)
                                packed |= TileFlipH;
                            if ((f & FlippingFlags.FlippedVertically) != 0)
                                packed |= TileFlipV;
                        }

                        layerCells[r * cols + c] = packed;
                        anyVisibleTileRendered = true;
                    }
                }

                renderLayers.Add(layerCells);
            }

            if (!anyVisibleTileRendered)
            {
                Console.Error.WriteLine(
                    $"Warning: Tilemap '{asset.Name}' produced no visible tiles (check layer visibility or pass 'draw_collision: true').");
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
                Tilesets: bakedTilesets
            );
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Warning: Failed to bake tilemap '{resolvedPath}' for asset '{asset.Name}': {ex.Message}. Using 1x1 empty placeholder.");
            return CreatePlaceholder(asset.Name, asset.IsMutable, explicitTileset, autoFormat);
        }
    }

    private static bool HasSolidProperty(IEnumerable<IProperty> properties)
    {
        foreach (var prop in properties)
        {
            if (prop is BoolProperty boolProp &&
                string.Equals(boolProp.Name, "solid", StringComparison.OrdinalIgnoreCase) &&
                boolProp.Value)
            {
                return true;
            }

            if (prop is IntProperty intProp &&
                string.Equals(intProp.Name, "solid", StringComparison.OrdinalIgnoreCase) &&
                intProp.Value != 0)
            {
                return true;
            }

            if (prop is StringProperty strProp &&
                string.Equals(strProp.Name, "solid", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(strProp.Value, "true", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
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

    private static bool? GetBoolOption(IReadOnlyList<NamedArg> options, string name)
    {
        foreach (var opt in options)
        {
            if (string.Equals(opt.Name, name, StringComparison.OrdinalIgnoreCase) &&
                opt.Value is BooleanLiteralExpr b)
            {
                return b.Value;
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
        var tilesets = explicitTileset is not null
            ? new List<BakedTilesetRef> { new(1, 16, explicitTileset, null, autoFormat) }
            : new List<BakedTilesetRef>();

        return new BakedTilemapAsset(
            Name: name,
            Cols: 1,
            Rows: 1,
            TileSize: 16,
            LayerCount: 1,
            Layers: [0],
            Collision: [0],
            IsMutable: isMutable,
            Tilesets: tilesets
        );
    }
}
