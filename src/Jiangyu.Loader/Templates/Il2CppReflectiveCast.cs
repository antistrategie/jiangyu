using System.Reflection;
using Il2CppInterop.Runtime.InteropTypes;

namespace Jiangyu.Loader.Templates;

/// <summary>
/// Reflective <c>Il2CppObjectBase.Cast&lt;T&gt;</c> wrapper. The generic
/// method has to be constructed per target type because <c>T</c> is only
/// known at runtime. The cast itself is a wrapper-only conversion (same
/// native pointer underneath), so the result reports the concrete type
/// and exposes its members.
///
/// <para>Single helper because the same pattern appears in several
/// places (clone applier identity-field write, conversation-manager
/// registry typed-property read, tagged-string composite construct,
/// SoundBank Stem registration). Centralising avoids drifting copies of
/// the same try/catch.</para>
/// </summary>
internal static class Il2CppReflectiveCast
{
    /// <summary>Cast the wrapper to <paramref name="targetType"/>. On
    /// failure, <paramref name="cast"/> is null and <paramref name="error"/>
    /// holds a one-line diagnostic.</summary>
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
            var castMethod = typeof(Il2CppObjectBase)
                .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .First(m => m.Name == nameof(Il2CppObjectBase.Cast) && m.IsGenericMethodDefinition && m.GetParameters().Length == 0)
                .MakeGenericMethod(targetType);
            cast = castMethod.Invoke(wrapper, null);
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
}
