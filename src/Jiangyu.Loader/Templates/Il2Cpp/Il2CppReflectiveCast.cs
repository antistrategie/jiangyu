using System.Reflection;
using Il2CppInterop.Runtime.InteropTypes;

namespace Jiangyu.Loader.Templates;

/// <summary>
/// Reflective <c>Il2CppObjectBase.Cast&lt;T&gt;</c> and
/// <c>TryCast&lt;T&gt;</c> wrappers. The generic methods have to be
/// constructed per target type because <c>T</c> is only known at runtime.
/// The cast itself is a wrapper-only conversion (same native pointer
/// underneath), so the result reports the concrete type and exposes its
/// members.
///
/// <para>Single helper because the same pattern appears in many places
/// (clone applier identity-field write, conversation-manager registry
/// typed-property read, tagged-string composite construct, SoundBank Stem
/// registration, template runtime-access materialisation, diagnostic
/// dumps). Centralising avoids drifting copies of the same try/catch.</para>
/// </summary>
internal static class Il2CppReflectiveCast
{
    /// <summary>Cast the wrapper to <paramref name="targetType"/> via
    /// <c>Cast&lt;T&gt;</c> (throws on failure inside the cast itself).
    /// On reflection failure, <paramref name="cast"/> is null and
    /// <paramref name="error"/> holds a one-line diagnostic.</summary>
    public static bool TryCast(Il2CppObjectBase wrapper, Type targetType, out object cast, out string error)
    {
        cast = null;
        if (wrapper == null || targetType == null)
        {
            error = "TryCast called with null wrapper or targetType.";
            return false;
        }
        if (targetType.IsInstanceOfType(wrapper))
        {
            cast = wrapper;
            error = null;
            return true;
        }
        try
        {
            cast = GetCastMethod(targetType).Invoke(wrapper, null);
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = $"Cast<{targetType.FullName}> threw: {(ex.InnerException ?? ex).Message}";
            return false;
        }
    }

    /// <summary>Convenience overload returning null on failure. Use when
    /// the caller doesn't need the error string (typically a hot path with
    /// its own fallback log line).</summary>
    public static object CastOrNull(Il2CppObjectBase wrapper, Type targetType)
        => TryCast(wrapper, targetType, out var cast, out _) ? cast : null;

    /// <summary>Resolve <c>Il2CppObjectBase.Cast&lt;T&gt;</c> bound to
    /// <paramref name="targetType"/>. Throws <see cref="InvalidOperationException"/>
    /// if the runtime method is missing, which indicates a fundamental
    /// Il2CppInterop setup failure.</summary>
    public static MethodInfo GetCastMethod(Type targetType)
        => typeof(Il2CppObjectBase)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .First(m => m.Name == nameof(Il2CppObjectBase.Cast) && m.IsGenericMethodDefinition && m.GetParameters().Length == 0)
            .MakeGenericMethod(targetType);

    /// <summary>Resolve <c>Il2CppObjectBase.TryCast&lt;T&gt;</c> bound to
    /// <paramref name="targetType"/> for inner-loop reuse. The returned
    /// method returns null on cast failure (vs <c>Cast&lt;T&gt;</c> which
    /// throws), suitable for filtering heterogeneous candidate lists.
    /// Returns null on resolution failure when
    /// <paramref name="throwIfMissing"/> is false; otherwise throws
    /// <see cref="InvalidOperationException"/>.</summary>
    public static MethodInfo GetTryCastMethod(Type targetType, bool throwIfMissing = true)
    {
        try
        {
            var method = typeof(Il2CppObjectBase)
                .GetMethods(BindingFlags.Instance | BindingFlags.Public)
                .FirstOrDefault(m => m.Name == "TryCast" && m.IsGenericMethodDefinition && m.GetParameters().Length == 0);
            if (method == null)
            {
                if (throwIfMissing)
                    throw new InvalidOperationException(
                        "Il2CppObjectBase.TryCast<T>() not found — Il2CppInterop runtime missing or mismatched.");
                return null;
            }
            return method.MakeGenericMethod(targetType);
        }
        catch when (!throwIfMissing)
        {
            return null;
        }
    }
}
