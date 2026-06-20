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
/// </summary>
internal sealed class LocaleApplier
{
    private static LocaleApplier _current;

    private readonly IReadOnlyList<DiscoveredMod> _mods;

    // The language token (locale code, or "<source>") of the last successful apply. Null until the
    // load-time apply lands, which is also the "pending" signal, and the dedup for a repeated apply.
    private string _appliedToken;

    public LocaleApplier(IReadOnlyList<DiscoveredMod> mods)
    {
        _mods = mods;
        _current = this;
    }

    /// <summary>True while the load-time apply has not yet completed.</summary>
    public bool Pending => _appliedToken == null;

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

        var plan = LocalePlanner.Build(ReadPoSources(log), state, code, revertFirst);

        // The active language's UI strings, or an empty map for the source language so Locale.Text
        // falls back to the English literal.
        Jiangyu.Sdk.Locale.Install(plan.Ui);

        if (plan.LoadList.Count == 0 && plan.Conversations.Count == 0)
        {
            _appliedToken = token;
            note = state == LocaleResolver.State.Translatable
                ? $"language '{code}': no translations shipped, defaults in use"
                : $"language '{language}' is the source, defaults in use";
            return true;
        }

        var fieldsResolved = LocaleTableInjector.Apply(plan.LoadList, log);
        var conversationsResolved = LocaleTableInjector.ApplyConversations(plan.Conversations, log);
        if (!fieldsResolved || !conversationsResolved)
            return false;

        _appliedToken = token;
        note = state == LocaleResolver.State.Translatable
            ? $"applied '{code}' ({plan.TranslatedOps} field op(s))"
            : $"restored source ({language})";
        return true;
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
