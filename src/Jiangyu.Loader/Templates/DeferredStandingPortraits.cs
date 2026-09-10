using Il2CppInterop.Runtime.InteropTypes;
using Il2CppMenace.Conversations;
using Jiangyu.Game.Ui;
using Jiangyu.Loader.Bundles;
using Jiangyu.Loader.Logging;
using Jiangyu.Shared.Replacements;
using Jiangyu.Shared.Templates;
using MelonLoader;

namespace Jiangyu.Loader.Templates;

/// <summary>Opted-in speaker fields retain their authored asset assignment until the
/// portrait is requested. See docs/research/verified/standing-portrait-textures.md.</summary>
internal sealed class DeferredStandingPortraits
{
    private readonly BundleReplacementCatalog _bundles;
    private readonly TemplateCloneCatalog _clones;
    private readonly Dictionary<(IntPtr Speaker, string Field), Action> _pending = new();
    private HashSet<string> _cloneSources;
    private MelonLogger.Instance _log;

    public bool Configured => _bundles.DeferredPortraitMods.Count > 0;
    public bool Enabled { get; private set; }

    public DeferredStandingPortraits(BundleReplacementCatalog bundles, TemplateCloneCatalog clones)
    {
        _bundles = bundles;
        _clones = clones;
    }

    public void Enable(MelonLogger.Instance log)
    {
        _log = log;
        Enabled = true;
        Portraits.BindStandingLoader(Resolve);
    }

    public bool TryDefer(object parent, string field, LoadedPatchOperation op, Func<object> getter, Action apply)
    {
        if (!Enabled || !IsPortraitField(field) || field != op.FieldPath
            || op.Op != CompiledTemplateOp.Set || op.Descent is { Count: > 0 }
            || op.Value is not { Kind: CompiledTemplateValueKind.AssetReference, Asset: not null }
            || !_bundles.DeferredPortraitMods.Contains(op.OwnerLabel)
            || parent is not SpeakerTemplate speaker)
            return false;

        var asset = AssetCategory.ToBundleAssetName(op.Value.Asset.Name);
        if (!_bundles.Assets.HasTexture(asset) || _bundles.Assets.HasTextureReplacement(asset))
            return false;

        // A source must hold its final fields before another template inherits them.
        // These uncommon portraits remain eager so clone order cannot change their art.
        if (_cloneSources == null)
        {
            _cloneSources = new HashSet<string>(StringComparer.Ordinal);
            foreach (var type in _clones.EnumerateByType())
                foreach (var clone in type.Value.Values)
                    if (!string.IsNullOrEmpty(clone.SourceId))
                        _cloneSources.Add(clone.SourceId);
        }
        if (_cloneSources.Contains(speaker.GetID()))
            return false;

        var original = NativePointer(getter());
        _pending[(speaker.Pointer, field)] = () =>
        {
            // A runtime mod may replace this field before its first display.
            if (NativePointer(getter()) == original)
                apply();
        };
        LoaderDebug.Write(_log, $"Deferred standing portrait: {speaker.GetID()}.{field} = {op.Value.Asset.Name}.");
        return true;
    }

    public void Forget(object parent, string field)
    {
        if (IsPortraitField(field) && parent is SpeakerTemplate speaker)
            _pending.Remove((speaker.Pointer, field));
    }

    public void Resolve(SpeakerTemplate speaker, StandingPortrait portrait)
    {
        if (speaker == null)
            return;
        var field = portrait switch
        {
            StandingPortrait.Left => nameof(SpeakerTemplate.StandLookLeftImage),
            StandingPortrait.Right => nameof(SpeakerTemplate.StandLookRightImage),
            StandingPortrait.Inactive => nameof(SpeakerTemplate.StandLookRightInactiveImage),
            _ => throw new ArgumentOutOfRangeException(nameof(portrait)),
        };
        if (!_pending.Remove((speaker.Pointer, field), out var apply))
            return;
        try
        {
            apply();
        }
        catch (Exception ex)
        {
            _log.Warning($"Standing portrait {speaker.GetID()}.{field} could not load: {ex.Message}");
        }
    }

    private static bool IsPortraitField(string field) => field is nameof(SpeakerTemplate.StandLookLeftImage)
        or nameof(SpeakerTemplate.StandLookRightImage) or nameof(SpeakerTemplate.StandLookRightInactiveImage);

    private static IntPtr NativePointer(object value) => (value as Il2CppObjectBase)?.Pointer ?? IntPtr.Zero;
}
