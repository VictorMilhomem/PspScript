using System.Buffers.Binary;
using System.Text;
using PspScript.Ast;
using PspScript.Semantics;

namespace PspScript.CodeGen;

internal sealed record BakedAudioAsset(
    string Name,
    string AssetKind,
    short[] Samples,
    uint FrameCount,
    byte Channels,
    bool HalfRate
);

/// <summary>
/// Pure C# (.NET 10) compile-time RIFF/WAVE (.wav) decoder and resampler (zero external dependencies).
/// Decodes 8-bit/16-bit PCM WAV files into static <c>int16_t</c> C99 arrays in .rodata.
/// Sound effects (<c>sound</c>) are normalized to mono 44.1 kHz; music (<c>music</c>) preserves
/// mono/stereo and supports 44.1 kHz or 22.05 kHz (<c>rate: 22050</c>).
/// </summary>
internal static class AudioBaker
{
    public static BakedAudioAsset BakeAudio(AssetSymbol asset, string? baseDirectory)
    {
        int targetRate = ResolveRequestedRate(asset.Declaration.Options);
        bool forceMono = string.Equals(asset.AssetKind, "sound", StringComparison.Ordinal);
        string? resolvedPath = AssetBaker.ResolveAssetPath(
            asset.FilePath,
            asset.Declaration.SourceDirectory,
            baseDirectory
        );

        if (resolvedPath is not null && File.Exists(resolvedPath))
        {
            try
            {
                return DecodeWav(asset.Name, asset.AssetKind, resolvedPath, targetRate, forceMono);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Warning: Failed to bake WAV '{resolvedPath}' for asset '{asset.Name}': {ex.Message}. Falling back to silent placeholder.");
            }
        }
        else
        {
            Console.Error.WriteLine($"Warning: Audio file '{asset.FilePath}' not found for '{asset.Name}'. Using silent placeholder.");
        }

        return CreatePlaceholder(asset.Name, asset.AssetKind, targetRate == 22050);
    }

    private static int ResolveRequestedRate(IReadOnlyList<NamedArg> options)
    {
        foreach (var opt in options)
        {
            if (string.Equals(opt.Name, "rate", StringComparison.OrdinalIgnoreCase) &&
                opt.Value is IntLiteralExpr { Value: 22050 })
            {
                return 22050;
            }
        }
        return 44100;
    }

    private static BakedAudioAsset DecodeWav(
        string assetName,
        string assetKind,
        string filePath,
        int targetRate,
        bool forceMono)
    {
        byte[] bytes = File.ReadAllBytes(filePath);
        if (bytes.Length < 44 ||
            Encoding.ASCII.GetString(bytes, 0, 4) != "RIFF" ||
            Encoding.ASCII.GetString(bytes, 8, 4) != "WAVE")
        {
            throw new InvalidDataException("Invalid RIFF/WAVE header.");
        }

        ushort audioFormat = 0;
        ushort numChannels = 0;
        uint sampleRate = 0;
        ushort bitsPerSample = 0;
        int dataOffset = -1;
        int dataLength = 0;

        int pos = 12;
        while (pos + 8 <= bytes.Length)
        {
            string chunkId = Encoding.ASCII.GetString(bytes, pos, 4);
            uint chunkSize = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(pos + 4, 4));
            int chunkDataStart = pos + 8;

            if (chunkDataStart + chunkSize > bytes.Length)
            {
                chunkSize = (uint)Math.Max(0, bytes.Length - chunkDataStart);
            }

            if (chunkId == "fmt " && chunkSize >= 16)
            {
                audioFormat = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(chunkDataStart, 2));
                numChannels = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(chunkDataStart + 2, 2));
                sampleRate = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(chunkDataStart + 4, 4));
                bitsPerSample = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(chunkDataStart + 14, 2));
            }
            else if (chunkId == "data")
            {
                dataOffset = chunkDataStart;
                dataLength = (int)chunkSize;
            }

            // RIFF chunks are word-aligned (padded to even byte boundary)
            long nextPos = chunkDataStart + (long)chunkSize + (chunkSize & 1u);
            if (nextPos <= pos || nextPos > int.MaxValue)
                break;
            pos = (int)nextPos;
        }

        if (audioFormat != 1)
            throw new NotSupportedException($"Unsupported WAV format tag {audioFormat} (only uncompressed PCM format 1 is supported).");
        if (numChannels is not (1 or 2))
            throw new NotSupportedException($"Unsupported WAV channel count {numChannels} (only mono or stereo is supported).");
        if (bitsPerSample is not (8 or 16))
            throw new NotSupportedException($"Unsupported WAV bit depth {bitsPerSample} (only 8-bit or 16-bit PCM is supported).");
        if (sampleRate == 0 || dataOffset < 0 || dataLength <= 0)
            throw new InvalidDataException("WAV missing valid fmt/data chunks.");

        int bytesPerSample = bitsPerSample / 8;
        int srcFrameCount = dataLength / (bytesPerSample * numChannels);
        if (srcFrameCount <= 0)
            return CreatePlaceholder(assetName, assetKind, targetRate == 22050);

        // Decode raw bytes to interleaved int16
        short[] decoded = new short[srcFrameCount * numChannels];
        if (bitsPerSample == 8)
        {
            for (int i = 0; i < decoded.Length; i++)
            {
                decoded[i] = (short)((bytes[dataOffset + i] - 128) << 8);
            }
        }
        else
        {
            for (int i = 0; i < decoded.Length; i++)
            {
                decoded[i] = BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(dataOffset + i * 2, 2));
            }
        }

        // Downmix stereo SFX to mono to halve PSP RAM/EBOOT footprint
        byte outChannels = (byte)(forceMono ? 1 : numChannels);
        short[] channelNormalized;
        if (forceMono && numChannels == 2)
        {
            channelNormalized = new short[srcFrameCount];
            for (int f = 0; f < srcFrameCount; f++)
            {
                channelNormalized[f] = (short)((decoded[f * 2] + decoded[f * 2 + 1]) >> 1);
            }
        }
        else
        {
            channelNormalized = decoded;
        }

        // Linearly resample if source sample rate differs from target rate (44100 or 22050)
        short[] finalSamples;
        int finalFrameCount;
        if (sampleRate == (uint)targetRate)
        {
            finalSamples = channelNormalized;
            finalFrameCount = srcFrameCount;
        }
        else
        {
            double ratio = (double)sampleRate / targetRate;
            finalFrameCount = Math.Max(1, (int)Math.Round(srcFrameCount / ratio));
            finalSamples = new short[finalFrameCount * outChannels];

            for (int outF = 0; outF < finalFrameCount; outF++)
            {
                double srcPos = outF * ratio;
                int idx0 = (int)srcPos;
                int idx1 = Math.Min(idx0 + 1, srcFrameCount - 1);
                double frac = srcPos - idx0;

                if (idx0 >= srcFrameCount)
                {
                    idx0 = srcFrameCount - 1;
                    idx1 = idx0;
                    frac = 0.0;
                }

                for (int ch = 0; ch < outChannels; ch++)
                {
                    double s0 = channelNormalized[idx0 * outChannels + ch];
                    double s1 = channelNormalized[idx1 * outChannels + ch];
                    int interp = (int)Math.Round(s0 + (s1 - s0) * frac);
                    finalSamples[outF * outChannels + ch] = (short)Math.Clamp(interp, short.MinValue, short.MaxValue);
                }
            }
        }

        // Trim trailing silent frames on one-shot SFX ('sound') so padded WAV files don't waste .rodata or hold voices
        if (forceMono && finalFrameCount > 1)
        {
            int lastNonSilent = finalFrameCount - 1;
            while (lastNonSilent > 0 && Math.Abs((int)finalSamples[lastNonSilent]) <= 16)
            {
                lastNonSilent--;
            }

            int trimmedCount = lastNonSilent + 1;
            if (trimmedCount < finalFrameCount)
            {
                finalSamples = finalSamples.AsSpan(0, trimmedCount).ToArray();
                finalFrameCount = trimmedCount;
            }
        }

        return new BakedAudioAsset(
            Name: assetName,
            AssetKind: assetKind,
            Samples: finalSamples,
            FrameCount: (uint)finalFrameCount,
            Channels: outChannels,
            HalfRate: targetRate == 22050
        );
    }

    private static BakedAudioAsset CreatePlaceholder(string name, string assetKind, bool halfRate)
    {
        return new BakedAudioAsset(
            Name: name,
            AssetKind: assetKind,
            Samples: [0],
            FrameCount: 1u,
            Channels: 1,
            HalfRate: halfRate
        );
    }
}
