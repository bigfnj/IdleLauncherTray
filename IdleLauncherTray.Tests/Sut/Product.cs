using System;
using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;

namespace IdleLauncherTray.Tests.Sut;

/// <summary>
/// Reflection access to the product assembly.
/// <para>
/// Every type in IdleLauncherTray is <c>internal</c> and several of the functions
/// worth testing are <c>private</c> (most importantly
/// <c>DeletionHelper.IsSafeDeleteTarget</c>, which guards a recursive self-delete).
/// Reflection reaches all of them without changing a single accessibility modifier
/// in the shipping code, and it tests the assembly that actually ships rather than
/// a recompiled copy of its source.
/// </para>
/// </summary>
internal static class Product
{
    private const string ProductAssemblyName = "IdleLauncherTray";

    private const BindingFlags AnyMember =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;

    internal static Assembly ProductAssembly { get; } = LoadProductAssembly();

    private static Assembly LoadProductAssembly()
    {
        try
        {
            return Assembly.Load(new AssemblyName(ProductAssemblyName));
        }
        catch (Exception ex) when (ex is FileNotFoundException or FileLoadException or BadImageFormatException)
        {
            return Assembly.LoadFrom(Path.Combine(AppContext.BaseDirectory, ProductAssemblyName + ".dll"));
        }
    }

    /// <summary>Resolves a product type, throwing if it has been renamed or removed.</summary>
    internal static Type TypeNamed(string simpleName) =>
        ProductAssembly.GetType($"{ProductAssemblyName}.{simpleName}", throwOnError: true)!;

    internal static MethodInfo MethodNamed(string typeName, string methodName) =>
        TypeNamed(typeName).GetMethod(methodName, AnyMember)
        ?? throw new MissingMethodException(typeName, methodName);

    internal static FieldInfo FieldNamed(Type type, string fieldName) =>
        type.GetField(fieldName, AnyMember)
        ?? throw new MissingFieldException(type.FullName, fieldName);

    internal static PropertyInfo PropertyNamed(string typeName, string propertyName) =>
        TypeNamed(typeName).GetProperty(propertyName, AnyMember)
        ?? throw new MissingMemberException(typeName, propertyName);

    /// <summary>
    /// Invokes a product method and rethrows the *original* exception rather than the
    /// <see cref="TargetInvocationException"/> reflection wraps it in. Tests that assert
    /// "this input must not blow up" have to see the real exception type, otherwise the
    /// assertion is about reflection plumbing instead of about the code under test.
    /// </summary>
    internal static object? Call(MethodInfo method, object? target, params object?[] arguments)
    {
        try
        {
            return method.Invoke(target, arguments);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw; // unreachable; keeps the compiler happy about definite return.
        }
    }

    internal static object? CallStatic(string typeName, string methodName, params object?[] arguments) =>
        Call(MethodNamed(typeName, methodName), target: null, arguments);

    internal static T ReadStaticProperty<T>(string typeName, string propertyName) =>
        (T)PropertyNamed(typeName, propertyName).GetValue(null)!;

    internal static T ReadConst<T>(string typeName, string fieldName) =>
        (T)FieldNamed(TypeNamed(typeName), fieldName).GetRawConstantValue()!;
}
