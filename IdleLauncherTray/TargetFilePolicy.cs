using System;
using System.Collections.Generic;
using System.IO;

namespace IdleLauncherTray;

/// <summary>
/// Why a candidate target was accepted or refused.
/// </summary>
/// <remarks>
/// The two rejections are kept apart because they deserve opposite handling. An unsupported
/// extension is a property of the string itself and can never become launchable. An unparseable
/// path is a property of the machine the string is read on: the stored value now keeps its
/// environment variables (see <see cref="TargetFilePolicy.PrepareForStorage"/>), so the same
/// config can parse on one box and not on another.
/// <para>
/// Collapsing both into a single <c>false</c> is what told a user their <c>.exe</c> was the wrong
/// kind of file, over a list of supported extensions that included <c>.exe</c> — and then cleared
/// it for them.
/// </para>
/// </remarks>
internal enum TargetPathStatus
{
    /// <summary>Nothing was selected: null, blank, or nothing but quotes and whitespace.</summary>
    Empty,

    /// <summary>Windows cannot turn the string into a path at all — too long, or illegal characters.</summary>
    Unparseable,

    /// <summary>A usable path, but not a type this app is willing to launch.</summary>
    UnsupportedType,

    /// <summary>A usable path whose extension is on the supported list.</summary>
    Supported
}

internal static class TargetFilePolicy
{
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe",
        ".scr",
        ".bat",
        ".cmd",
        ".lnk",
        ".msi",
        ".ps1",
        ".vbs",
        ".jar",
        ".py"
    };

    public const string SupportedExtensionsDisplay = ".exe, .scr, .bat, .cmd, .lnk, .msi, .ps1, .vbs, .jar, or .py";

    // A path that fails to parse is usually enormous -- 33,000 characters is the canonical case --
    // and both a MessageBox and a 2 MB-rotating log file have to survive being handed one. MAX_PATH
    // is the cut-off because anything past it is already the anomaly being reported rather than
    // information the reader needs in full.
    private const int MaxDisplayPathLength = 260;

    /// <summary>
    /// The form of a user-supplied path that is safe to <b>persist</b>: trimmed and unquoted, and
    /// nothing else.
    /// </summary>
    /// <remarks>
    /// Both of those are lossless — they drop decoration the user never meant to store. Expansion
    /// is not: it replaces <c>%APPDATA%\tools\app.exe</c> with one machine's answer and discards
    /// the portable spelling, which is exactly the USB-stick case this app exists for, and it
    /// destroys any literal path that genuinely contains <c>%…%</c>. Expansion belongs in
    /// <see cref="ResolveForUse"/>, at the moment the path is handed to the OS.
    /// </remarks>
    public static string PrepareForStorage(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        var trimmed = path.Trim().Trim('"');
        return string.IsNullOrWhiteSpace(trimmed) ? string.Empty : trimmed;
    }

    private static string PrepareCandidatePath(string? path)
    {
        var stored = PrepareForStorage(path);

        return stored.Length == 0
            ? string.Empty
            : Environment.ExpandEnvironmentVariables(stored);
    }

    private static bool TryGetFullPath(string candidate, out string fullPath)
    {
        fullPath = string.Empty;

        if (string.IsNullOrWhiteSpace(candidate))
        {
            return false;
        }

        try
        {
            // A relative target is anchored on the executable's folder, not on the working
            // directory: this app is portable and the Run registry key starts it with no working
            // directory of its own, so anchoring anywhere else would make a stored relative path
            // mean something different on the next launch.
            fullPath = Path.IsPathRooted(candidate)
                ? Path.GetFullPath(candidate)
                : Path.GetFullPath(candidate, AppContext.BaseDirectory);

            return true;
        }
        catch (Exception)
        {
            fullPath = string.Empty;
            return false;
        }
    }

    /// <summary>
    /// Turns a stored path into the absolute path to hand to the OS: environment variables
    /// expanded, separators and dot segments collapsed, a relative path anchored on the
    /// executable's folder. Falls back to the expanded candidate when Windows cannot parse it, so
    /// a caller always has something to display rather than an exception.
    /// </summary>
    /// <remarks>
    /// The result is for <b>use</b> only. Writing it back into the config is the bug this name
    /// exists to prevent — <see cref="ConfigManager"/> persists <see cref="PrepareForStorage"/>
    /// instead, and every consumer resolves at its own point of use.
    /// </remarks>
    public static string ResolveForUse(string? path)
    {
        var candidate = PrepareCandidatePath(path);

        return TryGetFullPath(candidate, out var fullPath) ? fullPath : candidate;
    }

    /// <summary>
    /// The former name of <see cref="ResolveForUse"/>, with identical behaviour. Kept because
    /// <c>TrayAppContext</c> still calls it at each of its points of use; the newer name is the one
    /// that says what the result may and may not be used for.
    /// </summary>
    public static string NormalizePath(string? path) => ResolveForUse(path);

    /// <summary>
    /// Decides what, if anything, is wrong with a candidate target — and which of the two possible
    /// faults it is, so a caller can act on and report the real one.
    /// </summary>
    public static TargetPathStatus ClassifyTarget(string? path)
    {
        var candidate = PrepareCandidatePath(path);
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return TargetPathStatus.Empty;
        }

        if (!TryGetFullPath(candidate, out var fullPath))
        {
            return TargetPathStatus.Unparseable;
        }

        // Decided on the full path rather than on the raw string so that "app.exe\" and
        // "app.exe/../notes.txt" are judged as what they actually resolve to.
        var extension = Path.GetExtension(fullPath);

        return !string.IsNullOrWhiteSpace(extension) && SupportedExtensions.Contains(extension)
            ? TargetPathStatus.Supported
            : TargetPathStatus.UnsupportedType;
    }

    public static bool IsSupportedTarget(string? path) =>
        ClassifyTarget(path) == TargetPathStatus.Supported;

    /// <summary>
    /// A path capped to a length a MessageBox and a log line can both carry, with the number of
    /// discarded characters named so the reader can see that the length <i>is</i> the fault.
    /// </summary>
    public static string ForDisplay(string? path)
    {
        var value = path ?? string.Empty;

        return value.Length <= MaxDisplayPathLength
            ? value
            : $"{value[..MaxDisplayPathLength]}… (+{value.Length - MaxDisplayPathLength} more characters)";
    }

    /// <summary>
    /// The message for a user whose chosen target was refused. It answers for whichever fault
    /// actually applies: an unparseable path is <b>not</b> described as an unsupported type, because
    /// that message handed the user a list of supported extensions that already contained theirs.
    /// </summary>
    public static string GetUnsupportedTargetMessage(string? path)
    {
        var status = ClassifyTarget(path);
        if (status == TargetPathStatus.Empty)
        {
            return $"Select a supported target type: {SupportedExtensionsDisplay}.";
        }

        var display = ForDisplay(ResolveForUse(path));

        return status == TargetPathStatus.Unparseable
            ? "Windows cannot interpret that path, so the target cannot be checked or launched.\n\n" +
              "It is either too long or contains characters a path cannot hold. The target type is not the problem.\n\n" +
              $"Selected path:\n{display}"
            : $"Only {SupportedExtensionsDisplay} files are supported.\n\nSelected path:\n{display}";
    }
}
