using System;
using System.Reflection;

namespace IdleLauncherTray;

/// <summary>
/// What this build calls itself, and where it keeps its things.
/// </summary>
/// <remarks>
/// Separate from <see cref="AppPaths"/> on purpose: that type answers "where does a file live",
/// which the whole app depends on, while this one exists for the About dialog and the tray's
/// version header. Keeping the version read out of AppPaths avoids putting assembly reflection on
/// a path that <c>DeletionHelper</c> consults to authorise a recursive delete.
/// </remarks>
internal static class AppInfo
{
    /// <summary>
    /// The version to show a human: three parts ("1.2.3"), not four ("1.2.3.0").
    /// </summary>
    /// <remarks>
    /// Read from <see cref="AssemblyInformationalVersionAttribute"/> first, because that is what
    /// the csproj's &lt;Version&gt; produces and what the release workflow overwrites from the git
    /// tag, so it is the value that always matches the tag a user downloaded. It can carry a
    /// "+&lt;commit&gt;" suffix from SourceLink-style builds, which is noise in a menu, so the
    /// suffix is trimmed.
    /// <para>
    /// Falls back to the four-part assembly version. Deliberately NOT
    /// <c>FileVersionInfo.GetVersionInfo(Assembly.Location)</c>: this ships as a single-file
    /// publish, where <c>Assembly.Location</c> is an empty string and that call throws.
    /// </para>
    /// </remarks>
    internal static string VersionDisplay { get; } = ResolveVersionDisplay();

    // NOTE: there is deliberately no combined "name v1.2.3" member here. The tooltip carries the
    // version as a bare "v1.2.3" suffix and no app name at all, so a prebuilt display string would
    // have had exactly zero callers.

    private static string ResolveVersionDisplay()
    {
        try
        {
            var assembly = typeof(AppInfo).Assembly;

            var informational = assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                .InformationalVersion;

            if (!string.IsNullOrWhiteSpace(informational))
            {
                var plus = informational.IndexOf('+');
                return plus > 0 ? informational[..plus] : informational;
            }

            var version = assembly.GetName().Version;
            if (version != null)
            {
                return version.ToString(3);
            }
        }
        catch
        {
            // Never let a cosmetic version lookup take down startup: this runs while the tray menu
            // is being built, and the constructor's failure path tears the whole app down.
        }

        return "unknown";
    }
}
