using System;
using System.Collections.Generic;
using System.IO;

namespace IdleLauncherTray;

internal static class TargetFilePolicy
{
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe",
        ".scr",
        ".bat"
    };

    public const string SupportedExtensionsDisplay = ".exe, .scr, or .bat";

    private static string PrepareCandidatePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        var trimmed = path.Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return string.Empty;
        }

        return Environment.ExpandEnvironmentVariables(trimmed);
    }

    private static bool TryGetNormalizedFullPath(string? path, out string normalizedPath)
    {
        normalizedPath = string.Empty;

        var candidate = PrepareCandidatePath(path);
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return false;
        }

        try
        {
            normalizedPath = Path.IsPathRooted(candidate)
                ? Path.GetFullPath(candidate)
                : Path.GetFullPath(candidate, AppContext.BaseDirectory);

            return true;
        }
        catch (Exception)
        {
            normalizedPath = string.Empty;
            return false;
        }
    }

    public static string NormalizePath(string? path)
    {
        return TryGetNormalizedFullPath(path, out var normalizedPath)
            ? normalizedPath
            : PrepareCandidatePath(path);
    }

    public static bool IsSupportedTarget(string? path)
    {
        if (!TryGetNormalizedFullPath(path, out var normalizedPath))
        {
            return false;
        }

        var extension = Path.GetExtension(normalizedPath);
        return !string.IsNullOrWhiteSpace(extension) && SupportedExtensions.Contains(extension);
    }

    public static string GetUnsupportedTargetMessage(string? path)
    {
        var normalized = NormalizePath(path);
        return string.IsNullOrWhiteSpace(normalized)
            ? $"Select a supported target type: {SupportedExtensionsDisplay}."
            : $"Only {SupportedExtensionsDisplay} files are supported.\n\nSelected path:\n{normalized}";
    }
}
