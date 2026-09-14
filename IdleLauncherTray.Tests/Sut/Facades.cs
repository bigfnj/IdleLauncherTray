using System;
using System.Collections.Generic;
using System.Linq;
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

/// <summary>Mirror of the product's <c>IdleLauncherTray.Logger</c>.</summary>
internal static class Logger
{
    private const string TypeName = "Logger";

    internal static string LogPath => Product.ReadStaticProperty<string>(TypeName, nameof(LogPath));
}

/// <summary>
/// Mirror of the two <c>private static</c> classifiers on the product's
/// <c>IdleLauncherTray.TrayAppContext</c>. They decide whether a failed launch is retried, and
/// the retry costs a <c>Thread.Sleep</c> on the UI thread — so a misclassification is a visibly
/// frozen tray menu in exchange for an attempt that cannot succeed.
/// <para>
/// Reached by reflection because constructing a <c>TrayAppContext</c> would build a real tray
/// icon, install real input hooks and start a real timer. The methods are static and pure, so
/// nothing about the owner is needed to exercise them.
/// </para>
/// </summary>
internal static class LaunchFailureClassifier
{
    private const string TypeName = "TrayAppContext";

    internal static bool IsTransientLaunchFailure(Exception ex) =>
        (bool)Product.CallStatic(TypeName, nameof(IsTransientLaunchFailure), ex)!;

    internal static bool IsRetryableWin32Error(int nativeErrorCode) =>
        (bool)Product.CallStatic(TypeName, nameof(IsRetryableWin32Error), nativeErrorCode)!;
}

/// <summary>
/// Mirror of the product's <c>IdleLauncherTray.LaunchReasonCode</c> enum, which the test
/// assembly cannot name because the product type is internal.
/// <para>
/// <see cref="All"/> is deliberately produced by <see cref="Enum.GetValues(Type)"/> rather than
/// by a literal list here. That is the entire reason the product's reason codes stopped being
/// strings: a test that enumerates the real type cannot fall behind a member added tomorrow,
/// whereas a hand-maintained mirror silently stops covering it and stays green.
/// </para>
/// </summary>
internal static class LaunchReasonCode
{
    private const string TypeName = "LaunchReasonCode";

    internal static Type EnumType { get; } = Product.TypeNamed(TypeName);

    /// <summary>Every declared member, boxed as the product enum type.</summary>
    internal static IReadOnlyList<object> All { get; } = Enum.GetValues(EnumType).Cast<object>().ToArray();

    internal static IReadOnlyList<string> Names { get; } = Enum.GetNames(EnumType);

    internal static object Named(string name) => Enum.Parse(EnumType, name);
}

/// <summary>
/// Mirror of the product's <c>IdleLauncherTray.TrayStatusText</c>.
/// <para>
/// <c>FormatDuration</c> and <c>Percent</c> are <c>private</c> in the product — they are
/// implementation details of the three entry points — but they carry length guarantees of their
/// own, so they are worth pinning directly rather than only through their callers.
/// </para>
/// </summary>
internal static class TrayStatusText
{
    private const string TypeName = "TrayStatusText";

    internal static int MaxLength => Product.ReadConst<int>(TypeName, nameof(MaxLength));

    internal static string ForEvaluation(
        string appName,
        string? degradationReason,
        object reasonCode,
        bool armed,
        string? targetFileName,
        int idleSeconds,
        int requiredIdleSeconds,
        double cpuPercent,
        int cpuThresholdPercent) =>
        (string)Product.CallStatic(
            TypeName,
            nameof(ForEvaluation),
            appName,
            degradationReason,
            reasonCode,
            armed,
            targetFileName,
            idleSeconds,
            requiredIdleSeconds,
            cpuPercent,
            cpuThresholdPercent)!;

    internal static string ForRunningTarget(string appName, string? degradationReason, string? targetFileName) =>
        (string)Product.CallStatic(TypeName, nameof(ForRunningTarget), appName, degradationReason, targetFileName)!;

    internal static string ForTickFailure(string appName) =>
        (string)Product.CallStatic(TypeName, nameof(ForTickFailure), appName)!;

    internal static string Clamp(string value, int maxLength) =>
        (string)Product.CallStatic(TypeName, nameof(Clamp), value, maxLength)!;

    internal static string FormatDuration(int seconds) =>
        (string)Product.CallStatic(TypeName, nameof(FormatDuration), seconds)!;

    internal static string Percent(double value) =>
        (string)Product.CallStatic(TypeName, nameof(Percent), value)!;
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

    internal bool AllowLaunchWhileLocked { get => Get<bool>(); set => Set(value); }

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
