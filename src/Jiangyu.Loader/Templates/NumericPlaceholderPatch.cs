using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppMenace.Tools;
using Jiangyu.Loader.Runtime.Patching;
using Jiangyu.Shared.Templates;
using MelonLoader;

namespace Jiangyu.Loader.Templates;

/// <summary>Resolve numeric fields through the game's localised placeholder overrides.
/// See docs/research/verified/numeric-placeholders.md.</summary>
internal sealed class NumericPlaceholderPatch : IHarmonyPatchModule
{
    private static readonly Dictionary<(string Type, string Id, string Path, string Format), string> Tokens = new();
    private static readonly Dictionary<string, NumericPlaceholderBinding> Bindings = new(StringComparer.Ordinal);
    private static readonly HashSet<string> ReportedErrors = new(StringComparer.Ordinal);
    private static MelonLogger.Instance _log;

    internal static string Register(NumericPlaceholderBinding binding)
    {
        var key = (binding.Source.TemplateType, binding.Source.TemplateId, binding.Path, binding.Format);
        if (Tokens.TryGetValue(key, out var token))
            return token;
        // Strings survive deep copies and normal array edits, so a cloned description
        // keeps its binding without retaining references to a source UI object.
        token = $"\u001fjiangyu-placeholder:{Tokens.Count}";
        Tokens.Add(key, token);
        Bindings.Add(token, binding);
        return token;
    }

    public void Install(HarmonyLib.Harmony harmony, LoaderHarmonyPatchContext context)
    {
        _log = context.Log;
        try
        {
            var method = AccessTools.Method(typeof(BaseLocalizedString), nameof(BaseLocalizedString.GetTranslated),
                new[] { typeof(Il2CppStringArray) });
            if (method == null)
            {
                context.Log.Warning("Numeric placeholders: GetTranslated was not found. Bound values cannot be displayed.");
                return;
            }
            harmony.Patch(method, prefix: new HarmonyMethod(typeof(NumericPlaceholderPatch), nameof(TranslatePrefix)));
            HarmonyPatching.Installed(context.Log, "Patched localised numeric placeholders.");
        }
        catch (Exception ex)
        {
            context.Log.Warning($"Numeric placeholders: patching GetTranslated failed: {ex.Message}");
        }
    }

    private static void TranslatePrefix(BaseLocalizedString __instance, ref Il2CppStringArray __0)
    {
        if (Bindings.Count == 0 || !__instance.m_AllowPlaceholders || __instance.m_Placeholders is not { } defaults)
            return;

        Il2CppStringArray resolved = null;
        for (var i = 0; i < defaults.Length; i++)
        {
            // Native caller overrides take precedence, including empty strings.
            var token = __0 != null && i < __0.Length ? __0[i] ?? defaults[i] : defaults[i];
            if (token == null || !Bindings.TryGetValue(token, out var binding))
                continue;
            if (resolved == null)
            {
                resolved = new Il2CppStringArray(System.Math.Max(defaults.Length, __0?.Length ?? 0));
                if (__0 != null)
                    for (var j = 0; j < __0.Length; j++)
                        resolved[j] = __0[j];
            }
            resolved[i] = Resolve(token, binding);
        }
        if (resolved != null)
            __0 = resolved;
    }

    private static string Resolve(string token, NumericPlaceholderBinding binding)
    {
        string error;
        try
        {
            if (TemplatePatchApplier.TryReadNumericPlaceholder(binding, out var text, out error))
                return text;
        }
        catch (Exception ex)
        {
            error = ex.InnerException?.Message ?? ex.Message;
        }
        if (ReportedErrors.Add(token))
            _log?.Error($"Numeric placeholder {binding.Source.TemplateType}/{binding.Source.TemplateId}/{binding.Path}: {error}");
        return "?";
    }
}
