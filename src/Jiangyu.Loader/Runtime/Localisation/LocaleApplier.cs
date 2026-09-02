using Jiangyu.Loader.Templates;
using Jiangyu.Shared.Bundles;
using Jiangyu.Shared.Localisation;
using MelonLoader;

namespace Jiangyu.Loader.Runtime.Localisation;

/// <summary>
/// Applies the active language's translations by reading each mod's shipped
/// <c>locales/**/&lt;code&gt;.po</c> directly (parsed via <see cref="LocaleTable"/>) and writing them
/// into the game's loca store through <see cref="LocaleTableInjector"/>: the <c>LocaData</c> entry the
/// UI reads, plus the live <c>BaseLocalizedString</c>'s default. Later-loaded mods win by load order.
/// The source language (English) needs no PO: the templates carry the authored defaults.
///
/// <para>The load-time pass self-gates and applies the active language. A mid-session switch
/// (<see cref="Reapply"/>, driven by the <c>SetCurrentLanguage</c> hook, which rebuilds <c>LocaData</c>
/// from the new language's CSV) first lays down the <c>msgid</c> baseline across all shipped PO files,
/// then overlays the new language, and rebuilds injected mod UI so open screens update.</para>
///
/// <para>Once the mods' own text is in, <see cref="LocaleInheritance"/> gives each clone the active
/// language's version of the text it inherited and never overrode, which no PO covers.</para>
/// </summary>
internal sealed class LocaleApplier
{
    private static LocaleApplier _current;

    private readonly IReadOnlyList<DiscoveredMod> _mods;
    private readonly TemplateCloneCatalog _clones;
    private readonly TemplatePatchCatalog _patches;

    // Inheritance progress for the current language: the clones already decided, and the lines
    // written for them. Kept across the passes it takes for every clone to register, so a pass
    // visits only the clones that arrived since the last one and the summary reports the total.
    private (string Token, HashSet<string> Decided, int Written) _inherited;

    // Every loaded mod's PO files, parsed once. They do not change while the game runs.
    private List<LocalePo> _poSources;

    // The language token (locale code, or "<source>") of the last successful apply. Null until the
    // load-time apply lands, which is also the "pending" signal, and the dedup for a repeated apply.
    private string _appliedToken;

    public LocaleApplier(
        IReadOnlyList<DiscoveredMod> mods, TemplateCloneCatalog clones, TemplatePatchCatalog patches)
    {
        _mods = mods;
        _clones = clones;
        _patches = patches;
        _current = this;
    }

    /// <summary>True while the load-time apply has not yet completed.</summary>
    public bool Pending => _appliedToken == null;

    /// <summary>
    /// Re-run the apply once, after the clone or patch appliers have registered more templates. The
    /// inheritance pass reads live templates, so clones that arrive on a later poll are only seen if
    /// it looks again, and this is the signal that looking is worthwhile. Anything already decided
    /// stays decided, so the extra pass is cheap.
    /// </summary>
    public void NotifyTemplatesChanged() => _appliedToken = null;

    /// <summary>Re-apply after an in-game language change. Invoked by the SetCurrentLanguage hook.</summary>
    public static void NotifyLanguageReloaded(MelonLogger.Instance log) => _current?.Reapply(log);

    /// <summary>Load-time pass, called each scene poll until it completes.</summary>
    public void Apply(MelonLogger.Instance log)
    {
        if (_appliedToken != null)
            return;
        if (TryApplyCurrentLanguage(log, revertFirst: false, out var note) && note != null)
            log.Msg($"Locale apply: {note}");
    }

    private void Reapply(MelonLogger.Instance log)
    {
        if (!TryApplyCurrentLanguage(log, revertFirst: true, out var note))
            return;

        // Re-translate @-marked labels in live injected screens so they pick up the new language now,
        // not only when a screen is next rebuilt (a still-open modal would otherwise stay stale).
        try { Jiangyu.Game.Ui.UI.RelocaliseAll(); }
        catch (Exception ex) { log.Warning($"Locale switch: UI refresh failed: {ex.Message}"); }

        if (note != null)
            log.Msg($"Locale switch: {note}");
    }

    // Returns true when the apply is complete (or there was nothing to apply). Returns false when the
    // language is not resolvable yet or the target templates are not live, so the caller retries.
    private bool TryApplyCurrentLanguage(MelonLogger.Instance log, bool revertFirst, out string note)
    {
        note = null;
        var (state, code, language) = LocaleResolver.Resolve(log);
        if (state == LocaleResolver.State.NotReady)
            return false;

        // Skip redundant work when the language has not changed since the last successful apply. This
        // also collapses the double-fire when SetCurrentLanguage internally calls ReloadCurrentLanguage.
        var token = state == LocaleResolver.State.Translatable ? code : "<source>";
        if (_appliedToken == token)
            return true;

        var plan = LocalePlanner.Build(_poSources ??= ReadPoSources(log), state, code, revertFirst);

        // The active language's UI strings, or an empty map for the source language so Locale.Text
        // falls back to the English literal.
        Jiangyu.Sdk.Locale.Install(plan.Ui);

        if (plan.LoadList.Count > 0 || plan.Conversations.Count > 0)
        {
            var fieldsResolved = LocaleTableInjector.Apply(plan.LoadList, log);
            var conversationsResolved = LocaleTableInjector.ApplyConversations(plan.Conversations, log);
            if (!fieldsResolved || !conversationsResolved)
                return false;
        }

        // Inherited text carries no PO entry, so this runs whether or not a translation shipped, and
        // for the source language too: the text is written onto the clone's line, so English has to
        // be put back rather than merely not overwritten. A clone that only registers later is
        // decided on the pass that first sees it, and the count carries across passes.
        if (_inherited.Token != token)
            _inherited = (token, new HashSet<string>(StringComparer.Ordinal), 0);
        _inherited.Written += LocaleInheritance.Apply(
            _clones, _patches, log, state == LocaleResolver.State.Translatable, _inherited.Decided);

        _appliedToken = token;
        note = Describe(state, code, language, plan.TranslatedOps, _inherited.Written);
        return true;
    }

    private static string Describe(
        LocaleResolver.State state, string code, string language, int translatedOps, int inherited)
    {
        if (state != LocaleResolver.State.Translatable)
            return $"language '{language}' is the source, defaults in use";
        if (translatedOps == 0 && inherited == 0)
            return $"language '{code}': no translations shipped, defaults in use";
        var inheritNote = inherited > 0 ? $", {inherited} inherited field(s)" : string.Empty;
        return $"applied '{code}' ({translatedOps} field op(s){inheritNote})";
    }

    // Parse every loaded mod's locales/**/*.po into a LocalePo (all codes; the planner filters to the
    // active one for translations and uses every code's baseline for the revert).
    private List<LocalePo> ReadPoSources(MelonLogger.Instance log)
    {
        var sources = new List<LocalePo>();
        foreach (var mod in _mods)
        {
            var localesDir = Path.Combine(mod.DirectoryPath, CompiledLayout.LocalesDirName);
            if (!Directory.Exists(localesDir))
                continue;

            foreach (var poPath in Directory.EnumerateFiles(localesDir, "*.po", SearchOption.AllDirectories))
            {
                try
                {
                    var result = LocaleTable.Compile(File.ReadAllText(poPath));
                    sources.Add(new LocalePo(mod, Path.GetFileNameWithoutExtension(poPath), result));
                }
                catch (Exception ex)
                {
                    log.Warning($"Locale apply: could not read '{poPath}': {ex.Message}");
                }
            }
        }
        return sources;
    }
}
