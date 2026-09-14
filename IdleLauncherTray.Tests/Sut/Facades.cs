using System;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace IdleLauncherTray.Tests.Sut;

/// <summary>Mirror of the product's <c>IdleLauncherTray.AppPaths</c>.</summary>
internal static class AppPaths
{
    internal const string TypeName = "AppPaths";

    internal static string DataDirOverrideVariable =>
        Product.ReadConst<string>(TypeName, nameof(DataDirOverrideVariable));

    internal static string AppName => Product.ReadConst<string>(TypeName, nameof(AppName));

    internal static string BaseDir => Product.ReadStaticProperty<string>(TypeName, nameof(BaseDir));

    internal static string ConfigPath => Product.ReadStaticProperty<string>(TypeName, nameof(ConfigPath));

    internal static string TrayIconFile => Product.ReadStaticProperty<string>(TypeName, nameof(TrayIconFile));

    internal static string LegacyInstalledExePath =>
        Product.ReadStaticProperty<string>(TypeName, nameof(LegacyInstalledExePath));
}

/// <summary>Mirror of the product's <c>IdleLauncherTray.TargetFilePolicy</c>.</summary>
internal static class TargetFilePolicy
{
    private const string TypeName = "TargetFilePolicy";

    internal static string SupportedExtensionsDisplay =>
        Product.ReadConst<string>(TypeName, nameof(SupportedExtensionsDisplay));

    internal static bool IsSupportedTarget(string? path) =>
        (bool)Product.CallStatic(TypeName, nameof(IsSupportedTarget), path)!;

    internal static string NormalizePath(string? path) =>
        (string)Product.CallStatic(TypeName, nameof(NormalizePath), path)!;

    internal static string GetUnsupportedTargetMessage(string? path) =>
        (string)Product.CallStatic(TypeName, nameof(GetUnsupportedTargetMessage), path)!;
}

/// <summary>
/// Mirror of the product's <c>IdleLauncherTray.DeletionHelper</c>. Both members are
/// <c>private</c> in the product: they are the guard rails around a recursive delete,
/// so they are deliberately not callable from anywhere else in the app.
/// </summary>
internal static class DeletionHelper
{
    private const string TypeName = "DeletionHelper";

    internal static bool IsSafeDeleteTarget(string? folderPath) =>
        (bool)Product.CallStatic(TypeName, nameof(IsSafeDeleteTarget), folderPath)!;

    internal static string NormalizeFolderPath(string? folderPath) =>
        (string)Product.CallStatic(TypeName, nameof(NormalizeFolderPath), folderPath)!;
}

/// <summary>Mirror of the product's <c>IdleLauncherTray.ConfigManager</c>.</summary>
internal static class ConfigManager
{
    private const string TypeName = "ConfigManager";

    internal static ConfigProxy Load() => new(Product.CallStatic(TypeName, nameof(Load))!);

    internal static void Save(ConfigProxy config) =>
        Product.CallStatic(TypeName, nameof(Save), config.Instance);
}

/// <summary>
/// Mirror of the product's <c>IdleLauncherTray.PhysicalIdle</c>, limited to its
/// hook-liveness surface.
/// <para>
/// The hooks themselves are global, need a message pump and block all system input while a
/// callback runs, so nothing here installs one. What is reachable is the decision made
/// <em>about</em> them: <c>IsHookDropSuspected</c> is a pure function of its arguments, and
/// the state the reporting reads from is a handful of static fields that
/// <see cref="HookStateScope"/> can arrange and put back.
/// </para>
/// </summary>
internal static class PhysicalIdle
{
    internal const string TypeName = "PhysicalIdle";

    internal static long HookSilenceGraceMs => Product.ReadConst<long>(TypeName, nameof(HookSilenceGraceMs));

    internal static int HookSilenceTicksRequired => Product.ReadConst<int>(TypeName, nameof(HookSilenceTicksRequired));

    internal static string ReasonBothHooksMissing => Product.ReadConst<string>(TypeName, nameof(ReasonBothHooksMissing));

    internal static string ReasonKeyboardHookMissing => Product.ReadConst<string>(TypeName, nameof(ReasonKeyboardHookMissing));

    internal static string ReasonMouseHookMissing => Product.ReadConst<string>(TypeName, nameof(ReasonMouseHookMissing));

    internal static string ReasonHooksStoppedFiring => Product.ReadConst<string>(TypeName, nameof(ReasonHooksStoppedFiring));

    internal static bool IsHookDropSuspected(
        double systemIdleMs,
        long lastCallbackMs,
        long nowMs,
        int consecutiveSuspectTicks,
        int requiredTicks) =>
        (bool)Product.CallStatic(
            TypeName,
            nameof(IsHookDropSuspected),
            systemIdleMs,
            lastCallbackMs,
            nowMs,
            consecutiveSuspectTicks,
            requiredTicks)!;

    internal static string? GetHookDegradationReason() =>
        (string?)Product.CallStatic(TypeName, nameof(GetHookDegradationReason));

    /// <summary>Declared as nullable so a test can pass the argument the product guards against.</summary>
    internal static void NotifyExternalActivity(string? reason) =>
        Product.CallStatic(TypeName, nameof(NotifyExternalActivity), reason);

    /// <summary>
    /// Invokes the real keyboard hook callback with <c>nCode &lt; 0</c>, which is the
    /// "pass this straight through" case: the product must not dereference
    /// <c>lParam</c> on that path, so it is safe to hand it a null pointer.
    /// </summary>
    internal static void InvokeKeyboardHookCallbackWithNegativeCode() =>
        Product.CallStatic(TypeName, "KeyboardHookCallback", -1, IntPtr.Zero, IntPtr.Zero);

    /// <summary>Mouse counterpart of <see cref="InvokeKeyboardHookCallbackWithNegativeCode"/>.</summary>
    internal static void InvokeMouseHookCallbackWithNegativeCode() =>
        Product.CallStatic(TypeName, "MouseHookCallback", -1, IntPtr.Zero, IntPtr.Zero);

    internal static bool UseSystemIdleFailSafe
    {
        get => (bool)Product.PropertyNamed(TypeName, nameof(UseSystemIdleFailSafe)).GetValue(null)!;
        set => Product.PropertyNamed(TypeName, nameof(UseSystemIdleFailSafe)).SetValue(null, value);
    }

    internal static double GetIdleMilliseconds() =>
        (double)Product.CallStatic(TypeName, nameof(GetIdleMilliseconds))!;

    /// <summary>The product's GetLastInputInfo wrapper, so a test can compare against the same reading it used.</summary>
    internal static double GetSystemIdleMilliseconds() =>
        (double)Product.CallStatic(TypeName, nameof(GetSystemIdleMilliseconds))!;

    internal static int GetEffectiveSystemIdleFailSafeWindowMs() =>
        (int)Product.CallStatic(TypeName, nameof(GetEffectiveSystemIdleFailSafeWindowMs))!;

    internal static long MonotonicMilliseconds => Environment.TickCount64;

    internal static long LastHookCallbackMilliseconds
    {
        get => ReadField<long>("_lastHookCallbackMilliseconds");
        set => WriteField("_lastHookCallbackMilliseconds", value);
    }

    internal static long LastPhysicalInputMilliseconds
    {
        get => ReadField<long>("_lastPhysicalInputMilliseconds");
        set => WriteField("_lastPhysicalInputMilliseconds", value);
    }

    internal static int ConsecutiveHookSilenceTicks
    {
        get => ReadField<int>("_consecutiveHookSilenceTicks");
        set => WriteField("_consecutiveHookSilenceTicks", value);
    }

    internal static bool DropSuspected
    {
        get => ReadField<int>("_hookDropSuspected") != 0;
        set => WriteField("_hookDropSuspected", value ? 1 : 0);
    }

    internal static T ReadField<T>(string fieldName) =>
        (T)Product.FieldNamed(Product.TypeNamed(TypeName), fieldName).GetValue(null)!;

    internal static void WriteField(string fieldName, object value) =>
        Product.FieldNamed(Product.TypeNamed(TypeName), fieldName).SetValue(null, value);
}

/// <summary>
/// Arranges the product's hook-liveness state for one test and puts every field back
/// afterwards, including when the body throws.
/// <para>
/// The hook handles are set to a non-zero stand-in rather than a real <c>HHOOK</c>. That is
/// enough for <c>KeyboardHookInstalled</c> / <c>MouseHookInstalled</c>, which only ask
/// whether the handle is non-null, and it never reaches user32: nothing in this suite calls
/// <c>Stop</c>, and the only product code that passes a handle on to Windows is the hook
/// callback, which is exercised outside this scope with the real (zero) handles in place.
/// </para>
/// </summary>
internal sealed class HookStateScope : IDisposable
{
    private static readonly IntPtr InstalledHandle = new(1);

    private readonly IntPtr _previousKeyboardHook;
    private readonly IntPtr _previousMouseHook;
    private readonly int _previousDropSuspected;
    private readonly int _previousSilenceTicks;
    private readonly long _previousCallbackMilliseconds;
    private readonly long _previousPhysicalInputMilliseconds;
    private readonly bool _previousUseSystemIdleFailSafe;

    internal HookStateScope(bool keyboardInstalled = true, bool mouseInstalled = true, bool dropSuspected = false)
    {
        // Saved but not set: a test that exercises GetIdleMilliseconds has to move it, and
        // leaving it moved would change what every later test in the run measures.
        _previousUseSystemIdleFailSafe = PhysicalIdle.UseSystemIdleFailSafe;

        _previousKeyboardHook = PhysicalIdle.ReadField<IntPtr>("_kbHook");
        _previousMouseHook = PhysicalIdle.ReadField<IntPtr>("_msHook");
        _previousDropSuspected = PhysicalIdle.ReadField<int>("_hookDropSuspected");
        _previousSilenceTicks = PhysicalIdle.ReadField<int>("_consecutiveHookSilenceTicks");
        _previousCallbackMilliseconds = PhysicalIdle.LastHookCallbackMilliseconds;
        _previousPhysicalInputMilliseconds = PhysicalIdle.LastPhysicalInputMilliseconds;

        PhysicalIdle.WriteField("_kbHook", keyboardInstalled ? InstalledHandle : IntPtr.Zero);
        PhysicalIdle.WriteField("_msHook", mouseInstalled ? InstalledHandle : IntPtr.Zero);
        PhysicalIdle.DropSuspected = dropSuspected;
    }

    public void Dispose()
    {
        PhysicalIdle.WriteField("_kbHook", _previousKeyboardHook);
        PhysicalIdle.WriteField("_msHook", _previousMouseHook);
        PhysicalIdle.WriteField("_hookDropSuspected", _previousDropSuspected);
        PhysicalIdle.WriteField("_consecutiveHookSilenceTicks", _previousSilenceTicks);
        PhysicalIdle.LastHookCallbackMilliseconds = _previousCallbackMilliseconds;
        PhysicalIdle.LastPhysicalInputMilliseconds = _previousPhysicalInputMilliseconds;
        PhysicalIdle.UseSystemIdleFailSafe = _previousUseSystemIdleFailSafe;
    }
}

/// <summary>Mirror of the product's <c>IdleLauncherTray.Logger</c>.</summary>
internal static class Logger
{
    private const string TypeName = "Logger";

    internal static string LogPath => Product.ReadStaticProperty<string>(TypeName, nameof(LogPath));
}

/// <summary>
/// Strongly-typed view over an <c>IdleLauncherTray.AppConfig</c> instance, which the
/// test assembly cannot name because the type is internal to the product.
/// </summary>
internal sealed class ConfigProxy
{
    private static readonly Type ConfigType = Product.TypeNamed("AppConfig");

    internal ConfigProxy()
        : this(Activator.CreateInstance(ConfigType, nonPublic: true)!)
    {
    }

    internal ConfigProxy(object instance) => Instance = instance;

    internal object Instance { get; }

    internal static int MinimumSystemIdleFailSafeWindowMs =>
        Product.ReadConst<int>("AppConfig", nameof(MinimumSystemIdleFailSafeWindowMs));

    internal static int NormalizeCpuThresholdPercent(int percent) =>
        (int)Product.CallStatic("AppConfig", nameof(NormalizeCpuThresholdPercent), percent)!;

    internal int IdleMinutes { get => Get<int>(); set => Set(value); }

    internal int CpuThresholdPercent { get => Get<int>(); set => Set(value); }

    internal string AppPath { get => Get<string>(); set => Set(value); }

    internal string AppArguments { get => Get<string>(); set => Set(value); }

    internal bool RunAtStartup { get => Get<bool>(); set => Set(value); }

    internal bool BlockInjectedWhileRunning { get => Get<bool>(); set => Set(value); }

    internal bool LockPcOnAppClose { get => Get<bool>(); set => Set(value); }

    internal bool GamepadCountsAsActivity { get => Get<bool>(); set => Set(value); }

    internal bool UseSystemIdleFailSafe { get => Get<bool>(); set => Set(value); }

    internal int SystemIdleFailSafeWindowMs { get => Get<int>(); set => Set(value); }

    internal bool TrayIconEnabled { get => Get<bool>(); set => Set(value); }

    internal string TrayIconPath { get => Get<string>(); set => Set(value); }

    internal string LastLaunchUtc { get => Get<string>(); set => Set(value); }

    /// <summary>Assigns a property that the strongly-typed surface declares as non-null.</summary>
    internal void SetRaw(string propertyName, object? value) => PropertyFor(propertyName).SetValue(Instance, value);

    private T Get<T>([CallerMemberName] string propertyName = "") =>
        (T)PropertyFor(propertyName).GetValue(Instance)!;

    private void Set<T>(T value, [CallerMemberName] string propertyName = "") =>
        PropertyFor(propertyName).SetValue(Instance, value);

    private static PropertyInfo PropertyFor(string propertyName) =>
        ConfigType.GetProperty(propertyName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
        ?? throw new MissingMemberException(ConfigType.FullName, propertyName);
}
