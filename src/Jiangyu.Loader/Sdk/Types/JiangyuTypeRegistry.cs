using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Il2CppInterop.Runtime.Injection;
using Jiangyu.Loader.Logging;

namespace Jiangyu.Loader.Sdk.Types;

/// <summary>
/// Injects a mod's <c>[JiangyuType]</c> subtypes into the IL2CPP type system and
/// maps each ns:Name to its managed type, so the template applier can resolve a
/// KDL <c>type="ns:Name"</c> to the injected type and construct it into a slot.
/// </summary>
internal static class JiangyuTypeRegistry
{
    private static readonly Dictionary<string, Type> ByQualifiedName = new(StringComparer.Ordinal);
    private static readonly HashSet<Type> Injected = new();
    // IL2CPP-side full names of the injected types, for recognising a native
    // instance whose class is mod-injected (its wrapper is typed as the
    // declared base, so the managed Type alone cannot tell).
    private static readonly HashSet<string> InjectedFullNames = new(StringComparer.Ordinal);

    // ClassInjector only exposes the interface-options overload generically
    // (RegisterTypeInIl2Cpp<T>(RegisterTypeOptions)), so an interface-satisfying
    // type registered from a runtime Type goes through the generic via reflection.
    private static readonly MethodInfo RegisterWithOptions = typeof(ClassInjector).GetMethods()
        .First(m => m.Name == nameof(ClassInjector.RegisterTypeInIl2Cpp)
                    && m.IsGenericMethodDefinition
                    && m.GetParameters() is { Length: 1 } p
                    && p[0].ParameterType == typeof(RegisterTypeOptions));

    /// <summary>Inject and record every discovered type from a mod-assembly scan.</summary>
    public static void Register(JiangyuTypeScan scan, IModHostLog log)
    {
        foreach (var error in scan.Errors)
            log.Error(error);

        foreach (var entry in scan.Entries)
        {
            try
            {
                // Mark injected only after Inject() succeeds: a throw must neither latch
                // the type as done (so a re-scan can retry) nor publish a ns:Name that
                // resolves to a type the runtime never actually injected.
                if (!Injected.Contains(entry.ManagedType))
                {
                    Inject(entry);
                    Injected.Add(entry.ManagedType);
                    if (entry.ManagedType.FullName != null)
                        InjectedFullNames.Add(entry.ManagedType.FullName);
                }

                ByQualifiedName[entry.QualifiedName] = entry.ManagedType;
                log.Info($"injected {entry.QualifiedName} ({entry.ManagedType.Name})");
            }
            catch (Exception ex)
            {
                log.Error($"injection failed for {entry.QualifiedName}: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    private static void Inject(JiangyuTypeEntry entry)
    {
        if (entry.Interfaces.Count == 0)
        {
            ClassInjector.RegisterTypeInIl2Cpp(entry.ManagedType);
            return;
        }

        var options = new RegisterTypeOptions { Interfaces = new Il2CppInterfaceCollection(entry.Interfaces) };
        RegisterWithOptions.MakeGenericMethod(entry.ManagedType).Invoke(null, new object[] { options });
    }

    /// <summary>Resolve a ns:Name to its injected managed type, if registered.</summary>
    public static bool TryResolve(string qualifiedName, out Type type)
        => ByQualifiedName.TryGetValue(qualifiedName, out type);

    /// <summary>
    /// Whether the native instance's IL2CPP class is a mod-injected type. Matched by the
    /// runtime class full name because the managed wrapper in hand is typed as the declared
    /// base (e.g. SkillEventHandlerTemplate), never as the injected subtype.
    /// </summary>
    public static bool IsInjectedInstance(Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase instance)
    {
        if (instance == null || InjectedFullNames.Count == 0)
            return false;
        try
        {
            var fullName = instance.Cast<Il2CppSystem.Object>().GetIl2CppType()?.FullName;
            return fullName != null && InjectedFullNames.Contains(fullName);
        }
        catch
        {
            return false;
        }
    }
}
