using System;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace IdleLauncherTray.Tests.Sut;

/// <summary>
/// Mirror of the product's <c>LaunchEvaluation</c>.
/// <para>
/// The record used to be a <c>private</c> type nested inside <c>TrayAppContext</c>. It now lives
/// in its own file so that <c>LaunchDecision</c> can produce it without a WinForms context in
/// scope, but it is still <c>internal</c>, so the reflection facade is still how the suite reaches
/// it. The record is pure state plus three derived members, and those are the interesting part —
/// <c>Ready</c> in particular decides whether the app launches into a locked desktop.
/// </para>
/// <para>
/// Modelled on <see cref="CpuUsageMonitorProxy"/>: resolve the nested type once, create it with
/// <see cref="Activator.CreateInstance(Type, bool)"/> (records get an implicit parameterless
/// constructor when they declare no primary one), and read/write through
/// <see cref="PropertyInfo"/>.
/// </para>
/// </summary>
internal sealed class LaunchEvaluationProxy
{
    private static readonly Type EvaluationType = Product.TypeNamed("LaunchEvaluation");

    private static readonly MethodInfo StateKeyMethod =
        EvaluationType.GetMethod("StateKey", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
        ?? throw new MissingMethodException(EvaluationType.FullName, "StateKey");

    private static readonly MethodInfo DescribeMethod =
        EvaluationType.GetMethod("Describe", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
        ?? throw new MissingMethodException(EvaluationType.FullName, "Describe");

    internal LaunchEvaluationProxy() => Instance = Activator.CreateInstance(EvaluationType, nonPublic: true)!;

    /// <summary>Wraps an evaluation the product already produced, e.g. from <c>LaunchDecision</c>.</summary>
    internal LaunchEvaluationProxy(object instance) => Instance = instance;

    internal object Instance { get; }

    internal string TargetPath { get => Get<string>(); set => Set(value); }

    internal bool HasTarget { get => Get<bool>(); set => Set(value); }

    internal bool TargetSupported { get => Get<bool>(); set => Set(value); }

    internal bool TargetExists { get => Get<bool>(); set => Set(value); }

    internal bool CooldownOk { get => Get<bool>(); set => Set(value); }

    internal double CooldownRemainingSeconds { get => Get<double>(); set => Set(value); }

    internal int IdleSeconds { get => Get<int>(); set => Set(value); }

    internal int RequiredIdleSeconds { get => Get<int>(); set => Set(value); }

    internal bool InputIdleOk { get => Get<bool>(); set => Set(value); }

    internal bool IdleMeasured { get => Get<bool>(); set => Set(value); }

    internal double CpuPercent { get => Get<double>(); set => Set(value); }

    internal bool CpuSampleValid { get => Get<bool>(); set => Set(value); }

    internal int CpuThresholdPercent { get => Get<int>(); set => Set(value); }

    internal bool CpuOk { get => Get<bool>(); set => Set(value); }

    internal bool WorkstationLocked { get => Get<bool>(); set => Set(value); }

    internal bool SessionOk { get => Get<bool>(); set => Set(value); }

    /// <summary>The product's <c>LaunchReasonCode</c>, boxed. Use <see cref="LaunchReasonCode"/>
    /// to obtain a value.</summary>
    internal object ReasonCode
    {
        get => PropertyFor(nameof(ReasonCode)).GetValue(Instance)!;
        set => PropertyFor(nameof(ReasonCode)).SetValue(Instance, value);
    }

    internal bool Ready => Get<bool>();

    internal string StateKey(bool armed) => (string)Product.Call(StateKeyMethod, Instance, armed)!;

    internal string Describe(bool armed) => (string)Product.Call(DescribeMethod, Instance, armed)!;

    /// <summary>
    /// An evaluation whose every gate passes, so a single flipped field is the only reason
    /// <see cref="Ready"/> could be false. Without this the "locked blocks the launch" test would
    /// pass against a default instance for which <see cref="Ready"/> is false anyway.
    /// </summary>
    internal static LaunchEvaluationProxy FullyReady() => new()
    {
        TargetPath = "C:\\tools\\app.exe",
        HasTarget = true,
        TargetSupported = true,
        TargetExists = true,
        SessionOk = true,
        CooldownOk = true,
        IdleMeasured = true,
        InputIdleOk = true,
        CpuSampleValid = true,
        CpuOk = true,
        IdleSeconds = 300,
        RequiredIdleSeconds = 300,
        CpuThresholdPercent = 10,
        ReasonCode = LaunchReasonCode.Named("Ready"),
    };

    private T Get<T>([CallerMemberName] string propertyName = "") =>
        (T)PropertyFor(propertyName).GetValue(Instance)!;

    private void Set<T>(T value, [CallerMemberName] string propertyName = "") =>
        PropertyFor(propertyName).SetValue(Instance, value);

    private static PropertyInfo PropertyFor(string propertyName) =>
        EvaluationType.GetProperty(propertyName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
        ?? throw new MissingMemberException(EvaluationType.FullName, propertyName);
}
