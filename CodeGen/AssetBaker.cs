using System.Buffers.Binary;
using System.IO.Compression;
using PspScript.Ast;
using PspScript.Semantics;

namespace PspScript.CodeGen;

internal enum BakedPixelFormat
{
    Rgba4444,
    Abgr8888
}

internal sealed record BakedTextureAsset(
    string Name,
    uint Width,
    uint Height,
    BakedPixelFormat Format,
    uint[] Pixels
);

/// <summary>
/// Pure C# (.NET 10) compile-time PNG decoder and PSP texture baker (zero external dependencies).
/// Decodes PNG assets into static <c>RGBA4444</c> or <c>ABGR8888</c> C99 arrays for zero-heap VRAM upload.
/// </summary>
internal static class AssetBaker
{
    private static readonly byte[] PngSignature = [137, 80, 78, 71, 13, 10, 26, 10];

    public static BakedTextureAsset BakeTexture(AssetSymbol asset, string? baseDirectory)
    {
        BakedPixelFormat format = ResolveRequestedFormat(asset.Declaration.Options);
        string? resolvedPath = ResolveAssetPath(asset.FilePath, baseDirectory);

        if (resolvedPath is not null && File.Exists(resolvedPath))
        {
            try
            {
                return DecodePng(asset.Name, resolvedPath, format);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Warning: Failed to bake PNG '{resolvedPath}' for asset '{asset.Name}': {ex.Message}. Falling back to 8x8 placeholder.");
            }
        }
        else
        {
            Console.Error.WriteLine($"Warning: Asset file '{asset.FilePath}' not found for '{asset.Name}'. Using 8x8 placeholder.");
        }

        return CreatePlaceholder(asset.Name, format);
    }

    private static BakedPixelFormat ResolveRequestedFormat(IReadOnlyList<NamedArg> options)
    {
        foreach (var opt in options)
        {
            if (string.Equals(opt.Name, "format", StringComparison.OrdinalIgnoreCase))
            {
                string? fmtName = opt.Value switch
                {
                    IdentifierExpr id => id.Name,
                    StringLiteralExpr str => str.Text,
                    _ => null
                };

                if (string.Equals(fmtName, "ABGR8888", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(fmtName, "RGBA8888", StringComparison.OrdinalIgnoreCase))
                {
                    return BakedPixelFormat.Abgr8888;
                }
            }
        }

        return BakedPixelFormat.Rgba4444;
    }

    private static string? ResolveAssetPath(string rawPath, string? baseDirectory)
    {
        if (Path.IsPathRooted(rawPath) && File.Exists(rawPath))
            return rawPath;

        if (!string.IsNullOrEmpty(baseDirectory))
        {
            string candidate = Path.GetFullPath(Path.Combine(baseDirectory, rawPath));
            if (File.Exists(candidate))
                return candidate;
        }

        string cwdCandidate = Path.GetFullPath(rawPath);
        return File.Exists(cwdCandidate) ? cwdCandidate : null;
    }

    private static BakedTextureAsset DecodePng(string assetName, string filePath, BakedPixelFormat format)
    {
        byte[] bytes = File.ReadAllBytes(filePath);
        if (bytes.Length < 8 || !bytes.AsSpan(0, 8).SequenceEqual(PngSignature))
            throw new InvalidDataException("Invalid PNG signature.");

        uint width = 0;
        uint height = 0;
        byte bitDepth = 0;
        byte colorType = 0;
        byte[]? palette = null;
        byte[]? transparency = null;
        using var idatStream = new MemoryStream();

        int pos = 8;
        while (pos + 12 <= bytes.Length)
        {
            uint chunkLen = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(pos, 4));
            string chunkType = System.Text.Encoding.ASCII.GetString(bytes, pos + 4, 4);
            int dataStart = pos + 8;

            if (dataStart + chunkLen + 4 > bytes.Length)
                throw new InvalidDataException($"Truncated PNG chunk '{chunkType}'.");

            switch (chunkType)
            {
                case "IHDR":
                    width = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(dataStart, 4));
                    height = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(dataStart + 4, 4));
                    bitDepth = bytes[dataStart + 8];
                    colorType = bytes[dataStart + 9];
                    byte interlace = bytes[dataStart + 12];
                    if (bitDepth != 8)
                        throw new NotSupportedException($"Unsupported PNG bit depth {bitDepth} (only 8-bit is supported).");
                    if (interlace != 0)
                        throw new NotSupportedException("Interlaced PNGs are not supported.");
                    break;

                case "PLTE":
                    palette = bytes.AsSpan(dataStart, (int)chunkLen).ToArray();
                    break;

                case "tRNS":
                    transparency = bytes.AsSpan(dataStart, (int)chunkLen).ToArray();
                    break;

                case "IDAT":
                    idatStream.Write(bytes, dataStart, (int)chunkLen);
                    break;

                case "IEND":
                    pos = bytes.Length;
                    continue;
            }

            pos = dataStart + (int)chunkLen + 4;
        }

        if (width == 0 || height == 0)
            throw new InvalidDataException("PNG missing IHDR dimensions.");

        int bpp = colorType switch
        {
            0 => 1, // Grayscale
            2 => 3, // RGB
            3 => 1, // Indexed Palette
            4 => 2, // Grayscale + Alpha
            6 => 4, // RGBA
            _ => throw new NotSupportedException($"Unsupported PNG color type {colorType}.")
        };

        int stride = checked((int)width * bpp);
        int expectedLen = checked((int)height * (stride + 1));

        idatStream.Position = 0;
        byte[] raw = new byte[expectedLen];
        using (var zlib = new ZLibStream(idatStream, CompressionMode.Decompress))
        {
            int totalRead = 0;
            while (totalRead < raw.Length)
            {
                int n = zlib.Read(raw, totalRead, raw.Length - totalRead);
                if (n == 0)
                    break;
                totalRead += n;
            }
        }

        byte[] prevRow = new byte[stride];
        byte[] currRow = new byte[stride];
        uint[] pixels = new uint[checked((int)(width * height))];

        for (int y = 0; y < height; y++)
        {
            int rowStart = y * (stride + 1);
            byte filterType = raw[rowStart];

            for (int x = 0; x < stride; x++)
            {
                byte rawByte = raw[rowStart + 1 + x];
                byte left = x >= bpp ? currRow[x - bpp] : (byte)0;
                byte up = prevRow[x];
                byte upLeft = x >= bpp ? prevRow[x - bpp] : (byte)0;

                currRow[x] = filterType switch
                {
                    0 => rawByte,
                    1 => (byte)(rawByte + left),
                    2 => (byte)(rawByte + up),
                    3 => (byte)(rawByte + ((left + up) >> 1)),
                    4 => (byte)(rawByte + PaethPredictor(left, up, upLeft)),
                    _ => throw new InvalidDataException($"Unknown PNG filter type {filterType}.")
                };
            }

            for (int x = 0; x < width; x++)
            {
                byte r, g, b, a;
                int offset = x * bpp;

                switch (colorType)
                {
                    case 6: // RGBA
                        r = currRow[offset];
                        g = currRow[offset + 1];
                        b = currRow[offset + 2];
                        a = currRow[offset + 3];
                        break;

                    case 2: // RGB
                        r = currRow[offset];
                        g = currRow[offset + 1];
                        b = currRow[offset + 2];
                        a = 255;
                        break;

                    case 3: // Indexed Palette
                        int idx = currRow[offset];
                        if (palette is null || idx * 3 + 2 >= palette.Length)
                            throw new InvalidDataException("Invalid PNG palette index.");
                        r = palette[idx * 3];
                        g = palette[idx * 3 + 1];
                        b = palette[idx * 3 + 2];
                        a = (transparency is not null && idx < transparency.Length) ? transparency[idx] : (byte)255;
                        break;

                    case 4: // Grayscale + Alpha
                        r = g = b = currRow[offset];
                        a = currRow[offset + 1];
                        break;

                    default: // Grayscale
                        r = g = b = currRow[offset];
                        a = 255;
                        break;
                }

                pixels[y * (int)width + x] = PackPixel(r, g, b, a, format);
            }

            Buffer.BlockCopy(currRow, 0, prevRow, 0, stride);
        }

        return new BakedTextureAsset(assetName, width, height, format, pixels);
    }

    private static uint PackPixel(byte r, byte g, byte b, byte a, BakedPixelFormat format)
    {
        if (format == BakedPixelFormat.Abgr8888)
        {
            return ((uint)a << 24) | ((uint)b << 16) | ((uint)g << 8) | r;
        }

        // SDL_PIXELFORMAT_RGBA4444 (16-bit: RRRR GGGG BBBB AAAA)
        uint r4 = (uint)(r >> 4);
        uint g4 = (uint)(g >> 4);
        uint b4 = (uint)(b >> 4);
        uint a4 = (uint)(a >> 4);
        return (r4 << 12) | (g4 << 8) | (b4 << 4) | a4;
    }

    private static byte PaethPredictor(byte a, byte b, byte c)
    {
        int p = a + b - c;
        int pa = Math.Abs(p - a);
        int pb = Math.Abs(p - b);
        int pc = Math.Abs(p - c);
        if (pa <= pb && pa <= pc) return a;
        if (pb <= pc) return b;
        return c;
    }

    private static BakedTextureAsset CreatePlaceholder(string name, BakedPixelFormat format)
    {
        uint[] pixels = new uint[64];
        for (int i = 0; i < 64; i++)
        {
            pixels[i] = PackPixel(255, 255, 255, 255, format);
        }
        return new BakedTextureAsset(name, 8, 8, format, pixels);
    }
}
