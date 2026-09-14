using System;
using System.Threading;
using IdleLauncherTray.Tests.Sut;

namespace IdleLauncherTray.Tests;

/// <summary>
/// Covers the single-instance decision in <c>Program</c>: take the named mutex, and when it
/// cannot be taken, work out from the failure whether a copy of the app is already running.
/// <para>
/// Every test here passes its own throwaway mutex name. The product takes the name as a
/// parameter for exactly that reason -- the real one may be held by a copy of the app the
/// developer is running, and a test that fought over it would be reporting on the desktop
/// rather than on the code.
/// </para>
/// <para>
/// The failure this exists for is a constructor that THROWS, from a line that used to sit
/// outside every try in <c>Main</c> and before any exception handler is wired. An escape there
/// ends the process with no dialog, no tray icon and nothing in the log, which is
/// indistinguishable from "I double-clicked it and nothing happened".
/// </para>
/// </summary>
public sealed class ProgramSingleInstanceTests
{
    private const string TypeName = "Program";
    private const string MethodName = "TryAcquireSingleInstanceMutex";

    [Fact]
    public void TryAcquireSingleInstanceMutex_WhenTheNameIsFree_TakesItAndReportsTheFirstInstance()
    {
        // The ordinary startup, and the baseline the other two are measured against: if this
        // ever stops being true, the app refuses to start rather than merely mis-reporting why.
        using var mutex = Acquire(UniqueMutexName(), out var isFirstInstance);

        Assert.NotNull(mutex);
        Assert.True(isFirstInstance, "A free name must produce a first instance that goes on to build the tray.");
    }

    [Fact]
    public void TryAcquireSingleInstanceMutex_WhenAnotherInstanceHoldsTheName_ReportsTheSecondInstance()
    {
        var mutexName = UniqueMutexName();

        // Stands in for the already-running copy. A named mutex is a kernel object, so a second
        // handle in this same process sees it exactly as a second process would.
        using var firstInstance = new Mutex(initiallyOwned: true, name: mutexName, createdNew: out var createdNew);
        Assert.True(createdNew, "The arrangement did not create the mutex, so there is no first instance to be second to.");

        using var mutex = Acquire(mutexName, out var isFirstInstance);

        Assert.False(isFirstInstance, "A name another instance already holds must produce a second instance, which exits.");
    }

    [Fact]
    public void TryAcquireSingleInstanceMutex_WhenTheNameIsHeldByAnotherKindOfKernelObject_StartsAnywayAndLogsIt()
    {
        // The reachable form of the crash. `new Mutex(..., out createdNew)` throws when the name
        // is taken by a kernel object that is not a mutex, by the same mechanism that makes it
        // throw UnauthorizedAccessException when an elevated first instance owns the name with
        // an ACL this process cannot open -- which is the case that actually bites users, and
        // the one no test can arrange without editing an ACL.
        var mutexName = UniqueMutexName();
        using var impostor = new EventWaitHandle(false, EventResetMode.ManualReset, mutexName, out var createdNew);
        Assert.True(createdNew, "The arrangement did not create the event, so the name is not occupied and nothing will throw.");

        var log = new LogCapture();

        using var mutex = Acquire(mutexName, out var isFirstInstance);

        // Not throwing is half the requirement. The other half is that the app still starts:
        // nothing of this name is a mutex, so no instance of this app is holding it, and
        // refusing to start would be the silent failure this method exists to prevent.
        Assert.Null(mutex);
        Assert.True(isFirstInstance, "A name that no instance of this app can be holding must not stop startup.");

        // And the log says so. A startup that ends, or continues without its guard, with nothing
        // written anywhere is the exact symptom the second-instance line was added to explain.
        Assert.Contains(mutexName, log.Text, StringComparison.Ordinal);
    }

    /// <summary>
    /// The product's decision, whose second result comes back as an <c>out</c> parameter --
    /// which reflection surfaces as a slot in the boxed argument array rather than as a return
    /// value.
    /// </summary>
    private static Mutex? Acquire(string mutexName, out bool isFirstInstance)
    {
        var arguments = new object?[] { mutexName, false };
        var mutex = (Mutex?)Product.Call(Product.MethodNamed(TypeName, MethodName), target: null, arguments);

        isFirstInstance = (bool)arguments[1]!;
        return mutex;
    }

    /// <summary>
    /// A name no other test, and no running copy of the app, can be using. Kept out of the
    /// Global namespace so nothing here needs a privilege the test runner may not have.
    /// </summary>
    private static string UniqueMutexName() => $"IdleLauncherTray.Tests.SingleInstance.{Guid.NewGuid():N}";
}
