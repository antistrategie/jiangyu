using Jiangyu.Loader.Logging;
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

    // Inheritance progress for the current language: the clones already decided (keyed by
    // LateTemplateSet.Key), and the lines written for them. Kept across the passes it takes for every clone to register, so a pass
    // visits only the clones that arrived since the last one and the summary reports the total.
    private (string Token, HashSet<string> Decided, int Written) _inherited;

    // Every loaded mod's PO files, parsed once. They do not change while the game runs.
    private List<LocalePo> _poSources;

    // The last state line written. The load-time pass re-runs whenever the appliers register
    // more templates, and the switch hook fires during boot ahead of it, so one state would
    // otherwise print more than once.
    private string _lastNote;

    // The language token (locale code, or "<source>") of the last successful apply. Null until the
    // load-time apply lands, which is also the "pending" signal, and the dedup for a repeated apply.
    private string _appliedToken;

    // Templates registered or patched after the load-time apply landed, keyed by
    // LateTemplateSet.Key. The next Apply writes the plan's text for these alone.
    private HashSet<string> _scope;

    /// <summary>Whether a template (type name, id) waits on another loader, under any name it
    /// is registered in. The load-time apply leaves its text out, so it completes without it,
    /// and a scoped re-run writes it when it lands.</summary>
    public Func<string, string, bool> TemplateHeld { get; set; }

    /// <summary>Whether a mod's block (owner, type name, id) is held. The mod's own
    /// translations of that block are left out, another mod's translations of the same
    /// template stay.</summary>
    public Func<string, string, string, bool> BlockHeld { get; set; }

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

    /// <summary>True while a scoped re-run for late templates is queued and has not completed.</summary>
    public bool ScopePending => _scope is { Count: > 0 };

    /// <summary>
    /// Re-run the apply once, after the clone or patch appliers have registered more templates. The
    /// inheritance pass reads live templates, so clones that arrive on a later poll are only seen if
    /// it looks again, and this is the signal that looking is worthwhile. Anything already decided
    /// stays decided, so the extra pass is cheap.
    /// </summary>
    public void NotifyTemplatesChanged()
    {
        _appliedToken = null;
        // Every chained clone is rebuilt from its source on a full pass: all inherited text
        // is decided again.
        _inherited.Decided?.Clear();
    }

    /// <summary>
    /// Re-run for the templates in <paramref name="changed"/> only (keyed by
    /// <see cref="LateTemplateSet.Key"/>). Text already settled on every other template is left as
    /// it is, so an edit a mod made after the load-time apply survives. Null re-runs everything.
    /// Before the load-time apply has landed there is nothing to scope: that apply covers them.
    /// </summary>
    public void NotifyTemplatesChanged(ISet<string> changed)
    {
        if (changed == null)
        {
            NotifyTemplatesChanged();
            return;
        }

        if (changed.Count == 0)
            return;
        // A changed clone was rebuilt from its source: its inherited text is decided again,
        // by the scoped pass or by the load-time pass still to come. Decided is keyed by
        // template, so an unrelated clone of another type sharing the id stays decided.
        _inherited.Decided?.RemoveWhere(key =>
        {
            var separator = key.IndexOf('\0');
            return separator >= 0 && LateTemplateSet.KeyedUnderAnyName(changed, key[..separator], key[(separator + 1)..]);
        });

        if (_appliedToken == null)
            return;
        // A translation may address a DataTemplate clone under an ancestor's name or another
        // spelling of its type: the scope is matched by template, not by key.
        _scope ??= new HashSet<string>(StringComparer.Ordinal);
        _scope.UnionWith(changed);
    }

    // The plan without the fields of held blocks and the conversations of held templates.
    private LocalePlan WithoutHeld(LocalePlan plan)
    {
        if (BlockHeld == null && TemplateHeld == null)
            return plan;
        return LocalePlanner.Without(
            plan,
            BlockHeld ?? ((_, _, _) => false),
            TemplateHeld ?? ((_, _) => false));
    }

    // Whether the template a scope key names is still held, under any name.
    private bool IsHeld(string key)
    {
        var separator = key.IndexOf('\0');
        return separator >= 0 && TemplateHeld(key[..separator], key[(separator + 1)..]);
    }

    /// <summary>Re-apply after an in-game language change. Invoked by the SetCurrentLanguage hook.</summary>
    public static void NotifyLanguageReloaded(MelonLogger.Instance log) => _current?.Reapply(log);

    /// <summary>Load-time pass, called each scene poll until it completes.</summary>
    public void Apply(MelonLogger.Instance log)
    {
        if (_appliedToken == null)
        {
            if (TryApplyCurrentLanguage(log, revertFirst: false, out var note))
                Report(log, "Locale apply", note);
            return;
        }

        if (_scope is { Count: > 0 })
            TryApplyScoped(log);
    }

    // Writes the plan's text for the scoped templates only. Returns false when the language is not
    // resolvable yet or a target is not live, so the caller retries with the scope kept. A language
    // change since the last apply makes the full pass run instead, which covers the scope.
    private bool TryApplyScoped(MelonLogger.Instance log)
    {
        var (state, code, language) = LocaleResolver.Resolve(log);
        if (state == LocaleResolver.State.NotReady)
            return false;

        var token = state == LocaleResolver.State.Translatable ? code : "<source>";
        if (_appliedToken != token)
        {
            _appliedToken = null;
            if (TryApplyCurrentLanguage(log, revertFirst: false, out var note))
                Report(log, "Locale apply", note);
            return _appliedToken != null;
        }

        // A held block's ops have not run, and its translations address the fields those
        // ops write, so they are left out. Its template stays in the scope for the pass that
        // follows the block. The other translations of a template in scope apply now.
        var scope = new HashSet<string>(_scope, StringComparer.Ordinal);
        var plan = WithoutHeld(LocalePlanner.ScopeTo(
            LocalePlanner.Build(_poSources ??= ReadPoSources(log), state, code, revertFirst: false),
            (type, id) => LateTemplateSet.KeyedUnderAnyName(scope, type, id)));

        if (plan.LoadList.Count > 0 || plan.Conversations.Count > 0)
        {
            var fieldsResolved = LocaleTableInjector.Apply(plan.LoadList, log);
            var conversationsResolved = LocaleTableInjector.ApplyConversations(plan.Conversations, log);
            if (!fieldsResolved || !conversationsResolved)
                return false;
        }

        if (_inherited.Token != token)
            _inherited = (token, new HashSet<string>(StringComparer.Ordinal), 0);
        _inherited.Written += LocaleInheritance.Apply(
            _clones, _patches, log, state == LocaleResolver.State.Translatable, _inherited.Decided, TemplateHeld);

        LoaderDebug.Write(log,
            $"Locale apply: {scope.Count} late template(s) for {language ?? "source"} "
            + $"({plan.LoadList.Count} manifest(s), {plan.Conversations.Count} subtitle op(s)).");
        _scope.ExceptWith(scope);
        if (TemplateHeld != null)
            _scope.UnionWith(scope.Where(IsHeld));
        if (_scope.Count == 0)
            _scope = null;
        return true;
    }

    private void Reapply(MelonLogger.Instance log)
    {
        if (!TryApplyCurrentLanguage(log, revertFirst: true, out var note))
            return;

        // Re-translate @-marked labels in live injected screens so they pick up the new language now,
        // not only when a screen is next rebuilt (a still-open modal would otherwise stay stale).
        try { Jiangyu.Game.Ui.UI.RelocaliseAll(); }
        catch (Exception ex) { log.Warning($"Locale switch: UI refresh failed: {ex.Message}"); }

        Report(log, "Locale switch", note);
    }

    // One line per change of state.
    private void Report(MelonLogger.Instance log, string prefix, string note)
    {
        if (note == null || note == _lastNote)
            return;
        _lastNote = note;
        log.Msg($"{prefix}: {note}");
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
        plan = WithoutHeld(plan);

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
            _clones, _patches, log, state == LocaleResolver.State.Translatable, _inherited.Decided, TemplateHeld);

        _appliedToken = token;
        // A full pass covers every template, so a scoped re-run queued before it has nothing left to do.
        _scope = null;
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
