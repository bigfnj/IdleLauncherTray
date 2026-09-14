using System;
using System.ComponentModel;
using System.IO;
using IdleLauncherTray.Tests.Sut;

namespace IdleLauncherTray.Tests;

/// <summary>
/// Automatic launches are retried once on failure. Until this classification existed the wrapper
/// retried EVERY failure, including "no such file" and "not a valid application", and paid a
/// 250&#160;ms <c>Thread.Sleep</c> on the UI thread — with the message pump held — for each one.
/// </summary>
public sealed class LaunchFailureClassificationTests
{
    [Fact]
    public void IsTransientLaunchFailure_ForAPlainIoError_IsTransient()
    {
        // The case the retry exists for: an antivirus scanner holding the target's handle for a
        // moment, or a share that blinked.
        Assert.True(LaunchFailureClassifier.IsTransientLaunchFailure(new IOException("device not ready")));
    }

    [Fact]
    public void IsTransientLaunchFailure_ForUnauthorizedAccess_IsTransient()
    {
        Assert.True(LaunchFailureClassifier.IsTransientLaunchFailure(new UnauthorizedAccessException()));
    }

    [Fact]
    public void IsTransientLaunchFailure_ForAMissingFile_IsNotTransient()
    {
        // FileNotFoundException DERIVES from IOException, so this is the ordering assertion: put
        // the broad arm first and the file that will never exist gets retried forever.
        Assert.IsAssignableFrom<IOException>(new FileNotFoundException());
        Assert.False(LaunchFailureClassifier.IsTransientLaunchFailure(new FileNotFoundException()));
    }

    [Fact]
    public void IsTransientLaunchFailure_ForAMissingDirectory_IsNotTransient()
    {
        Assert.IsAssignableFrom<IOException>(new DirectoryNotFoundException());
        Assert.False(LaunchFailureClassifier.IsTransientLaunchFailure(new DirectoryNotFoundException()));
    }

    [Theory]
    [InlineData(5)]
    [InlineData(32)]
    [InlineData(33)]
    [InlineData(64)]
    [InlineData(1450)]
    public void IsTransientLaunchFailure_ForARetryableShellError_IsTransient(int nativeErrorCode)
    {
        Assert.True(LaunchFailureClassifier.IsTransientLaunchFailure(new Win32Exception(nativeErrorCode)));
        Assert.True(LaunchFailureClassifier.IsRetryableWin32Error(nativeErrorCode));
    }

    [Theory]
    [InlineData(2)]      // ERROR_FILE_NOT_FOUND
    [InlineData(3)]      // ERROR_PATH_NOT_FOUND
    [InlineData(193)]    // ERROR_BAD_EXE_FORMAT
    [InlineData(1155)]   // ERROR_NO_ASSOCIATION
    [InlineData(1223)]   // ERROR_CANCELLED -- the user dismissed the elevation prompt
    public void IsTransientLaunchFailure_ForASettledShellError_IsNotTransient(int nativeErrorCode)
    {
        // 1223 is the one that matters most: retrying re-prompts a person who just said no.
        Assert.False(LaunchFailureClassifier.IsTransientLaunchFailure(new Win32Exception(nativeErrorCode)));
        Assert.False(LaunchFailureClassifier.IsRetryableWin32Error(nativeErrorCode));
    }

    [Theory]
    [InlineData(typeof(InvalidOperationException))]
    [InlineData(typeof(NotSupportedException))]
    [InlineData(typeof(TimeoutException))]
    [InlineData(typeof(ArgumentException))]
    public void IsTransientLaunchFailure_ForAnUnrecognisedException_DefaultsToNotTransient(Type exceptionType)
    {
        // The default has to be "do not retry". Retrying an exception nobody classified burns a
        // UI-thread sleep on a guess; not retrying it costs one launch that was failing anyway
        // and will be reattempted on the next idle episode.
        var exception = (Exception)Activator.CreateInstance(exceptionType)!;

        Assert.False(LaunchFailureClassifier.IsTransientLaunchFailure(exception));
    }

    [Fact]
    public void IsTransientLaunchFailure_ForADisposedObject_IsNotTransient()
    {
        // Separate from the theory above only because ObjectDisposedException has no
        // parameterless constructor.
        Assert.False(LaunchFailureClassifier.IsTransientLaunchFailure(new ObjectDisposedException("Process")));
    }
}
