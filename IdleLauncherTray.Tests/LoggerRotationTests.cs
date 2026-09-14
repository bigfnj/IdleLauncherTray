using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using IdleLauncherTray.Tests.Sut;

namespace IdleLauncherTray.Tests;

/// <summary>
/// The log is this app's entire diagnostic surface, and rotation is the one operation that
/// destroys part of it. Until this change it did so silently: the file was moved to
/// <c>.old</c> (overwriting whatever <c>.old</c> already held) and the new file simply began
/// mid-sentence, with nothing to tell a reader that anything was missing.
/// <para>
/// Everything here goes through <c>TryRotate(logPath, maxBytes)</c> rather than the production
/// <c>RotateIfNeeded()</c>. That is not a convenience: <c>RotateIfNeeded</c> reads the
/// <c>static readonly</c> <c>_logPath</c>, and the CLR throws <c>FieldAccessException</c> from
/// <c>FieldInfo.SetValue</c> on an initonly static field, so there is no way to point the
/// production entry point at a temp file. The parameterised seam is the only testable surface.
/// </para>
/// </summary>
public sealed class LoggerRotationTests
{
    /// <summary>
    /// The shape every line written by <c>Logger.FormatLine</c> has. The marker must match it,
    /// which is the point of routing the marker through the same formatter: a hand-assembled
    /// string would be a stray sentence sitting in a log file rather than a log line, invisible
    /// to anything (or anyone) that scans the left margin for a timestamp.
    /// </summary>
    private static readonly Regex LogLineShape = new(
        @"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3} \[pid=\d+ tid=\d+\] INFO ",
        RegexOptions.CultureInvariant);

    private const long SmallLimit = 64;

    private static string WriteOversizedLog(string directory, string fileName)
    {
        var logPath = Path.Combine(directory, fileName);
        File.WriteAllText(logPath, new string('x', (int)SmallLimit * 4) + Environment.NewLine);
        return logPath;
    }

    [Fact]
    public void TryRotate_WhenTheLogIsUnderTheLimit_LeavesEverythingAlone()
    {
        using var data = new TempDataDirectory();
        var logPath = Path.Combine(data.Path, "under-limit.log");
        File.WriteAllText(logPath, "a short line" + Environment.NewLine);
        var before = File.ReadAllText(logPath);

        Assert.False(Logger.TryRotate(logPath, SmallLimit));

        Assert.Equal(before, File.ReadAllText(logPath));
        Assert.False(File.Exists(logPath + ".old"), "Nothing was over the limit, so nothing should have been archived.");
    }

    [Fact]
    public void TryRotate_WhenThereIsNoLogFile_DoesNothingAndDoesNotCreateOne()
    {
        using var data = new TempDataDirectory();
        var logPath = Path.Combine(data.Path, "not-created-yet.log");

        Assert.False(Logger.TryRotate(logPath, maxBytes: 1));

        Assert.False(File.Exists(logPath), "Rotation must not conjure a log file that the app never wrote.");
        Assert.False(File.Exists(logPath + ".old"));
    }

    [Fact]
    public void TryRotate_WhenTheLogIsOverTheLimit_MovesItToDotOld()
    {
        using var data = new TempDataDirectory();
        var logPath = WriteOversizedLog(data.Path, "over-limit.log");
        var archived = File.ReadAllText(logPath);

        Assert.True(Logger.TryRotate(logPath, SmallLimit));

        Assert.True(File.Exists(logPath + ".old"));
        Assert.Equal(archived, File.ReadAllText(logPath + ".old"));
    }

    [Fact]
    public void TryRotate_WritesTheMarkerAsTheVeryFirstLineOfTheNewLog()
    {
        using var data = new TempDataDirectory();
        var logPath = WriteOversizedLog(data.Path, "marker-first.log");

        Assert.True(Logger.TryRotate(logPath, SmallLimit));

        var firstLine = File.ReadLines(logPath).First();

        // Read from the PRODUCT, not copied. A literal here would keep passing after the
        // product's marker was reworded away or deleted outright, which is the single thing
        // this assertion exists to catch.
        Assert.Contains(Logger.RotationMarkerPrefix, firstLine, StringComparison.Ordinal);
    }

    [Fact]
    public void TryRotate_MarkerIsShapedLikeEveryOtherLogLine()
    {
        using var data = new TempDataDirectory();
        var logPath = WriteOversizedLog(data.Path, "marker-shape.log");

        Assert.True(Logger.TryRotate(logPath, SmallLimit));

        var firstLine = File.ReadLines(logPath).First();

        Assert.Matches(LogLineShape, firstLine);
    }

    [Fact]
    public void TryRotate_MarkerNamesTheArchivePathAndHowManyBytesWentIntoIt()
    {
        using var data = new TempDataDirectory();
        var logPath = WriteOversizedLog(data.Path, "marker-details.log");
        var archivedBytes = new FileInfo(logPath).Length;

        Assert.True(Logger.TryRotate(logPath, SmallLimit));

        var firstLine = File.ReadLines(logPath).First();

        // Both details are what turn the marker from an apology into a lead: the path says
        // where the missing history went, and the size says how much of it there was.
        Assert.Contains(logPath + ".old", firstLine, StringComparison.Ordinal);
        Assert.Contains(archivedBytes.ToString(CultureInfo.InvariantCulture), firstLine, StringComparison.Ordinal);
    }

    [Fact]
    public void TryRotate_MarkerEndsWithANewline_SoTheNextWriteLandsBelowIt()
    {
        using var data = new TempDataDirectory();
        var logPath = WriteOversizedLog(data.Path, "marker-newline.log");

        Assert.True(Logger.TryRotate(logPath, SmallLimit));
        Assert.EndsWith(Environment.NewLine, File.ReadAllText(logPath), StringComparison.Ordinal);

        // Proved rather than asserted in the abstract: append exactly the way Logger.Write does
        // and confirm the result is two lines, not one run-together line beginning with the
        // marker. Without the trailing newline the first real line of the new log would be
        // swallowed into the marker and lost to anything reading line by line.
        File.AppendAllText(logPath, "the next line" + Environment.NewLine);

        var lines = File.ReadAllLines(logPath);

        Assert.Equal(2, lines.Length);
        Assert.Equal("the next line", lines[1]);
    }

    [Fact]
    public void TryRotate_WhenAnArchiveAlreadyExists_ReplacesIt()
    {
        using var data = new TempDataDirectory();
        var logPath = WriteOversizedLog(data.Path, "second-rotation.log");
        var oldPath = logPath + ".old";

        const string PreviousArchive = "history from an earlier rotation";
        File.WriteAllText(oldPath, PreviousArchive);

        var archived = File.ReadAllText(logPath);

        Assert.True(Logger.TryRotate(logPath, SmallLimit));

        // Pinned as a DECISION, not discovered as a bug. Rotation keeps exactly one archive, so
        // the earlier one is gone -- and the fix for this was never to keep more history, it was
        // to stop the log pretending nothing had been lost.
        Assert.Equal(archived, File.ReadAllText(oldPath));
        Assert.DoesNotContain(PreviousArchive, File.ReadAllText(oldPath), StringComparison.Ordinal);
    }

    [Fact]
    public void TryRotate_MarkerAdmitsThatItReplacedTheEarlierArchive()
    {
        using var data = new TempDataDirectory();
        var logPath = WriteOversizedLog(data.Path, "marker-honesty.log");

        Assert.True(Logger.TryRotate(logPath, SmallLimit));

        var firstLine = File.ReadLines(logPath).First();

        // The claim under test is the honesty of the marker, so it has to be asserted on the
        // text -- there is nowhere else for it to live. Matched on the stem so a reword between
        // "replacing" / "replaced" / "replaces" survives, while a marker that quietly stops
        // mentioning the loss at all does not.
        Assert.Contains("replac", firstLine, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MaxLogBytes_IsTheTwoMegabyteLimitTheProductShipsWith()
    {
        // Promoted from a local const inside the rotation check so the seam above can be driven
        // with a small limit without the production value becoming untestable folklore.
        Assert.Equal(2_000_000L, Logger.MaxLogBytes);
    }
}
