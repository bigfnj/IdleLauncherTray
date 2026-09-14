using System;
using System.IO;
using System.Runtime.CompilerServices;

namespace IdleLauncherTray.Tests;

/// <summary>
/// Sets a process environment variable for the lifetime of the scope and restores the
/// previous value on dispose, including when the body throws.
/// </summary>
internal sealed class EnvironmentVariableScope : IDisposable
{
    private readonly string _name;
    private readonly string? _previousValue;

    internal EnvironmentVariableScope(string name, string? value)
    {
        _name = name;
        _previousValue = Environment.GetEnvironmentVariable(name);
        Environment.SetEnvironmentVariable(name, value);
    }

    public void Dispose() => Environment.SetEnvironmentVariable(_name, _previousValue);
}

/// <summary>
/// A private data directory for one test: creates it under
/// <see cref="TestDataDirectory.Root"/>, points the product at it, then removes both the
/// redirect and the directory.
/// </summary>
internal sealed class TempDataDirectory : IDisposable
{
    private readonly EnvironmentVariableScope _redirect;

    internal TempDataDirectory([CallerMemberName] string label = "test")
    {
        // Truncated: the caller name is a long test-method name and these folders sit
        // under an already-deep temp path.
        var shortLabel = label.Length > 20 ? label[..20] : label;
        Path = System.IO.Path.Combine(TestDataDirectory.Root, $"{shortLabel}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path);
        _redirect = new EnvironmentVariableScope(TestDataDirectory.OverrideVariableName, Path);

        // Fail here, before the test body runs, rather than after it has written to a
        // directory it should never have been pointed at.
        TestDataDirectory.AssertRedirectIsInEffect();
    }

    internal string Path { get; }

    internal string ConfigFile => System.IO.Path.Combine(Path, "config.json");

    internal void WriteConfigFile(string contents) => File.WriteAllText(ConfigFile, contents);

    public void Dispose()
    {
        _redirect.Dispose();

        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
            // Best effort; the whole root is removed at process exit.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>
/// Reads back whatever the product appended to its log after this object was created.
/// <para>
/// It remembers an offset rather than redirecting the log, because the log cannot be
/// redirected per test: <c>Logger</c> snapshots its path once at type initialisation, and
/// <see cref="TestDataDirectory"/> deliberately forces that to happen while the redirect is
/// known to be on. So the log does not follow a <see cref="TempDataDirectory"/>, and the only
/// way to isolate one test's output is to note where the file ended before it ran.
/// </para>
/// <para>
/// Safe because assembly-wide parallelisation is off (see <c>AssemblyInfo.cs</c>): no other test
/// is writing to the log in between.
/// </para>
/// </summary>
internal sealed class LogCapture
{
    private readonly long _startOffset;

    internal LogCapture() =>
        _startOffset = File.Exists(Sut.Logger.LogPath) ? new FileInfo(Sut.Logger.LogPath).Length : 0L;

    /// <summary>Everything the product has logged since this object was constructed.</summary>
    internal string Text
    {
        get
        {
            if (!File.Exists(Sut.Logger.LogPath))
            {
                return string.Empty;
            }

            // FileShare.ReadWrite because Logger may still hold the file, and Min() because a
            // rotation would leave the remembered offset past the end of a fresh file.
            using var stream = new FileStream(
                Sut.Logger.LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            stream.Seek(Math.Min(_startOffset, stream.Length), SeekOrigin.Begin);

            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
    }
}

/// <summary>
/// Sets the process working directory for the lifetime of the scope. Used to prove that
/// relative-path resolution is anchored on the executable's folder and not on wherever
/// the process happens to be running.
/// </summary>
internal sealed class CurrentDirectoryScope : IDisposable
{
    private readonly string _previousDirectory;

    internal CurrentDirectoryScope(string directory)
    {
        _previousDirectory = Environment.CurrentDirectory;
        Environment.CurrentDirectory = directory;
    }

    public void Dispose() => Environment.CurrentDirectory = _previousDirectory;
}
