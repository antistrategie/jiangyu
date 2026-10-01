using System.Diagnostics;
using System.Reflection;
using Il2CppInterop.Common;
using Il2CppInterop.Runtime;
using Jiangyu.Loader.Logging;

namespace Jiangyu.Loader.Sdk.Patches;

/// <summary>
/// Decides whether a mod patch may attach to a game method, by looking at the method's
/// native code before Harmony detours it. The game's compiler folds identical machine
/// code, so a tiny body (a field getter, a constant return, a trivial forward) can be
/// one function shared by several unrelated methods. A detour on that function runs the
/// mod's handler for every one of them, with the wrong argument and return marshalling,
/// and nothing at compile time shows it. A target whose code is shared is refused. The
/// verdict is taken once per target and cached, so every mod registering the same target
/// gets the same answer.
/// </summary>
internal static unsafe class NativeTargetGuard
{
    // Functions are padded with int3 to this alignment, so a body that ends before the
    // first boundary is followed by 0xCC up to it.
    internal const int BlockSize = 16;

    private const byte Int3 = 0xCC;

    private static readonly object Gate = new();
    private static readonly Dictionary<MethodBase, bool> Verdicts = new();

    // Native code address to the Il2CppMethodInfo pointers whose methodPointer is that
    // address. Filled per image, on demand, the first time a tiny target needs it.
    private static readonly Dictionary<IntPtr, List<IntPtr>> CodeOwners = new();
    private static readonly HashSet<IntPtr> ScannedImages = new();
    private static IntPtr _mainImage;

    /// <summary>Whether <paramref name="target"/> may be patched. Logs the refusal once,
    /// against the mod that registered the target first.</summary>
    public static bool Allows(MethodBase target, string label, string modId, IModHostLog log)
    {
        lock (Gate)
        {
            if (Verdicts.TryGetValue(target, out var known))
            {
                if (!known)
                    log.Warn($"[{modId}] patch on {label} not registered: the target was refused (see the earlier error).");
                return known;
            }

            var allowed = Evaluate(target, label, modId, log);
            Verdicts[target] = allowed;
            return allowed;
        }
    }

    private static bool Evaluate(MethodBase target, string label, string modId, IModHostLog log)
    {
        IntPtr methodInfo, code;
        try
        {
            var field = Il2CppInteropUtils.GetIl2CppMethodInfoPointerFieldForGeneratedMethod(target);
            methodInfo = field?.GetValue(null) is IntPtr value ? value : IntPtr.Zero;
            code = methodInfo == IntPtr.Zero ? IntPtr.Zero : *(IntPtr*)methodInfo;
        }
        catch (Exception ex)
        {
            log.Debug($"patch: native code of {label} not resolved ({ex.GetType().Name}); patching without the shared-code check.");
            return true;
        }

        if (code == IntPtr.Zero)
        {
            log.Debug($"patch: native code of {label} not resolved; patching without the shared-code check.");
            return true;
        }

        // Reading only up to the next boundary keeps the read inside the function's own
        // padded block, which never crosses a page.
        var length = BlockSize - (int)((long)code & (BlockSize - 1));
        var head = new ReadOnlySpan<byte>((void*)code, length);
        if (!NeedsSharingCheck(head))
            return true;

        List<IntPtr> sharing;
        try
        {
            sharing = SharingMethods(methodInfo, code, log);
        }
        catch (Exception ex)
        {
            log.Debug($"patch: shared-code check for {label} failed ({ex.GetType().Name}: {ex.Message}); patching anyway.");
            return true;
        }

        if (sharing.Count == 0)
        {
            var reason = IsTinyBody(head) ? "has a tiny body" : "is already detoured";
            log.Debug($"patch: {label} {reason} but shares no code; patching.");
            return true;
        }

        var names = sharing.Take(3).Select(MethodName).ToList();
        var more = sharing.Count > 3 ? $" and {sharing.Count - 3} more" : "";
        log.Error(
            $"[{modId}] patch on {label} refused: the game shares this method's code with " +
            $"{string.Join(", ", names)}{more}, so a patch on it would also run for those. " +
            "Hook a caller of this method that has a real body instead.");
        return false;
    }

    /// <summary>
    /// Whether the code at the start of a function is worth checking for sharing.
    /// <paramref name="head"/> runs from the function's first byte to the next
    /// <see cref="BlockSize"/> boundary. A terminator (ret, jmp rel32, jmp rel8) followed
    /// by int3 padding up to the boundary marks a body that fits in one block, which is
    /// where identical-code folding happens. A function that already opens with a jump
    /// counts too: another patcher may have detoured it, which overwrites the bytes the
    /// size test would read. Scanning raw bytes can mistake an operand for a terminator,
    /// and that only costs a sharing lookup on a method that turns out to be unique.
    /// </summary>
    internal static bool NeedsSharingCheck(ReadOnlySpan<byte> head)
    {
        if (head.Length == 0)
            return false;
        if (head[0] == 0xE9 || head[0] == 0xEB || (head.Length > 1 && head[0] == 0xFF && head[1] == 0x25))
            return true;
        return IsTinyBody(head);
    }

    /// <summary>Whether <paramref name="head"/> holds a terminator followed by int3
    /// padding that runs to its end. See <see cref="NeedsSharingCheck"/>.</summary>
    internal static bool IsTinyBody(ReadOnlySpan<byte> head)
    {
        for (var i = 0; i < head.Length; i++)
        {
            var end = head[i] switch
            {
                0xC3 => i + 1,
                0xEB => i + 2,
                0xE9 => i + 5,
                _ => -1,
            };
            // At least one padding byte: a terminator that ends exactly on the boundary
            // cannot be told apart from an operand without reading past the block.
            if (end < 0 || end >= head.Length)
                continue;
            if (IsPadding(head[end..]))
                return true;
        }
        return false;
    }

    private static bool IsPadding(ReadOnlySpan<byte> bytes)
    {
        foreach (var b in bytes)
        {
            if (b != Int3)
                return false;
        }
        return true;
    }

    // The other methods whose methodPointer is the target's code. Scans the target's own
    // image and Assembly-CSharp, which holds the game's methods. Folding can also pair a
    // game method with one in another image, which this does not see, but every image
    // would multiply the one-off cost for a rare case.
    private static List<IntPtr> SharingMethods(IntPtr methodInfo, IntPtr code, IModHostLog log)
    {
        var klass = IL2CPP.il2cpp_method_get_class(methodInfo);
        if (klass != IntPtr.Zero)
            ScanImage(IL2CPP.il2cpp_class_get_image(klass), log);
        ScanImage(MainImage(), log);

        if (!CodeOwners.TryGetValue(code, out var owners))
            return new List<IntPtr>();
        return owners.Where(owner => owner != methodInfo).ToList();
    }

    private static IntPtr MainImage()
    {
        if (_mainImage != IntPtr.Zero)
            return _mainImage;
        uint count = 0;
        var assemblies = IL2CPP.il2cpp_domain_get_assemblies(IL2CPP.il2cpp_domain_get(), ref count);
        for (var i = 0; i < count; i++)
        {
            var image = IL2CPP.il2cpp_assembly_get_image(assemblies[i]);
            if (string.Equals(IL2CPP.il2cpp_image_get_name_(image), "Assembly-CSharp.dll", StringComparison.OrdinalIgnoreCase))
                return _mainImage = image;
        }
        return IntPtr.Zero;
    }

    private static void ScanImage(IntPtr image, IModHostLog log)
    {
        if (image == IntPtr.Zero || !ScannedImages.Add(image))
            return;

        var watch = Stopwatch.StartNew();
        var classCount = IL2CPP.il2cpp_image_get_class_count(image);
        var methodCount = 0;
        for (uint i = 0; i < classCount; i++)
        {
            var klass = IL2CPP.il2cpp_image_get_class(image, i);
            // An open generic class has no code of its own, only its instantiations do.
            if (klass == IntPtr.Zero || IL2CPP.il2cpp_class_is_generic(klass))
                continue;

            var iter = IntPtr.Zero;
            IntPtr method;
            while ((method = IL2CPP.il2cpp_class_get_methods(klass, ref iter)) != IntPtr.Zero)
            {
                if (IL2CPP.il2cpp_method_is_generic(method))
                    continue;
                // methodPointer is the first field of Il2CppMethodInfo.
                var pointer = *(IntPtr*)method;
                if (pointer == IntPtr.Zero)
                    continue;
                if (!CodeOwners.TryGetValue(pointer, out var owners))
                    CodeOwners[pointer] = owners = new List<IntPtr>(1);
                owners.Add(method);
                methodCount++;
            }
        }
        watch.Stop();
        log.Debug($"patch: mapped native code of {methodCount} methods in {classCount} classes of {IL2CPP.il2cpp_image_get_name_(image)} in {watch.ElapsedMilliseconds} ms.");
    }

    private static string MethodName(IntPtr method)
    {
        var klass = IL2CPP.il2cpp_method_get_class(method);
        var ns = IL2CPP.il2cpp_class_get_namespace_(klass);
        var type = IL2CPP.il2cpp_class_get_name_(klass);
        var name = IL2CPP.il2cpp_method_get_name_(method);
        return string.IsNullOrEmpty(ns) ? $"{type}.{name}" : $"{ns}.{type}.{name}";
    }
}
