using System.Buffers.Binary;

namespace PspScript.CodeGen;

/// <summary>
/// Validates and stages Sony PSP XMB PNG assets (ICON0.PNG, PIC1.PNG, PIC0.PNG)
/// into the build output directory for <c>pack-pbp</c>.
/// </summary>
internal static class PspXmbStager
{
    public static bool TryStageXmbPng(
        string? configuredPath,
        string configKey,
        string targetFileName,
        int expectedWidth,
        int expectedHeight,
        string? configBaseDir,
        string? entryDir,
        string buildDir,
        out string makeValue,
        TextWriter? errorWriter = null)
    {
        errorWriter ??= Console.Error;
        makeValue = "NULL";
        if (string.IsNullOrWhiteSpace(configuredPath))
            return true;

        string? resolved = null;
        if (Path.IsPathRooted(configuredPath) && File.Exists(configuredPath))
        {
            resolved = configuredPath;
        }
        else if (!string.IsNullOrEmpty(configBaseDir))
        {
            string candidate = Path.GetFullPath(Path.Combine(configBaseDir, configuredPath));
            if (File.Exists(candidate))
                resolved = candidate;
        }

        if (resolved is null && !string.IsNullOrEmpty(entryDir))
        {
            string candidate = Path.GetFullPath(Path.Combine(entryDir, configuredPath));
            if (File.Exists(candidate))
                resolved = candidate;
        }

        if (resolved is null)
        {
            string cwdCandidate = Path.GetFullPath(configuredPath);
            if (File.Exists(cwdCandidate))
                resolved = cwdCandidate;
        }

        if (resolved is null)
        {
            errorWriter.WriteLine($"Error: Game config '{configKey}' image not found: '{configuredPath}'.");
            return false;
        }

        byte[] header = new byte[24];
        using (var fs = File.OpenRead(resolved))
        {
            if (fs.Read(header, 0, header.Length) < 24 ||
                header[0] != 137 || header[1] != 80 || header[2] != 78 || header[3] != 71 ||
                header[4] != 13  || header[5] != 10 || header[6] != 26 || header[7] != 10 ||
                header[12] != (byte)'I' || header[13] != (byte)'H' || header[14] != (byte)'D' || header[15] != (byte)'R')
            {
                errorWriter.WriteLine($"Error: Game config '{configKey}' file '{resolved}' is not a valid PNG image.");
                return false;
            }
        }

        uint width = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(16, 4));
        uint height = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(20, 4));
        if (width != (uint)expectedWidth || height != (uint)expectedHeight)
        {
            errorWriter.WriteLine(
                $"Warning: Game config '{configKey}' ({targetFileName}) is {width}x{height}px; PSP XMB recommends {expectedWidth}x{expectedHeight}px."
            );
        }

        Directory.CreateDirectory(buildDir);
        string destPath = Path.Combine(buildDir, targetFileName);
        File.Copy(resolved, destPath, overwrite: true);
        makeValue = Path.GetRelativePath(Directory.GetCurrentDirectory(), Path.GetFullPath(destPath)).Replace('\\', '/');
        return true;
    }
}
