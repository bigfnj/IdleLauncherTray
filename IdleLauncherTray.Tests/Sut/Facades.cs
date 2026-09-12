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
