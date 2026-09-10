using HarmonyLib;
using Il2CppMenace.Conversations;
using Il2CppMenace.States;
using Il2CppMenace.Strategy;
using Il2CppMenace.Tactical;
using Il2CppMenace.UI;
using Il2CppMenace.UI.Strategy;
using Il2CppMenace.UI.Tactical;
using Jiangyu.Loader.Runtime.Patching;
using MonoMod.RuntimeDetour;
using UnityEngine;

namespace Jiangyu.Loader.Templates;

/// <summary>Load standing artwork before native UI or managed code reads a speaker's texture fields.
/// See docs/research/verified/standing-portrait-textures.md.</summary>
internal sealed class StandingPortraitDisplayPatch : IHarmonyPatchModule
{
    private static DeferredStandingPortraits _portraits;
    private readonly DeferredStandingPortraits _loader;
    private readonly List<Hook> _fieldHooks = new();

    public StandingPortraitDisplayPatch(DeferredStandingPortraits loader) => _loader = loader;

    public void Install(HarmonyLib.Harmony harmony, LoaderHarmonyPatchContext context)
    {
        _portraits = _loader;
        try
        {
            Patch(typeof(BaseUnitLeader), nameof(BaseUnitLeader.GetStandingImage), Type.EmptyTypes, nameof(LeaderPrefix));
            Patch(typeof(ConversationUIScreen), nameof(ConversationUIScreen.ShowRole),
                new[] { typeof(Role), typeof(SpeakerTemplate) }, nameof(RolePrefix));
            Patch(typeof(EventDialog), nameof(EventDialog.ShowRole),
                new[] { typeof(Role), typeof(SpeakerTemplate) }, nameof(RolePrefix));
            Patch(typeof(SelectedUnitPanel), nameof(SelectedUnitPanel.SetActor),
                new[] { typeof(Actor), typeof(bool) }, nameof(ActorPrefix));
            // JIANGYU-CONTRACT: generated field accessors need managed detours as native reads bypass them.
            // MelonLoader requires named static hook methods, including for managed calls.
            foreach (var (field, get, set) in new[]
            {
                (nameof(SpeakerTemplate.StandLookLeftImage), nameof(GetLeft), nameof(SetLeft)),
                (nameof(SpeakerTemplate.StandLookRightImage), nameof(GetRight), nameof(SetRight)),
                (nameof(SpeakerTemplate.StandLookRightInactiveImage), nameof(GetInactive), nameof(SetInactive)),
            })
            {
                var property = AccessTools.Property(typeof(SpeakerTemplate), field)
                    ?? throw new MissingMemberException(typeof(SpeakerTemplate).FullName, field);
                AddFieldHook(new Hook(property.GetMethod, AccessTools.Method(typeof(StandingPortraitDisplayPatch), get)));
                AddFieldHook(new Hook(property.SetMethod, AccessTools.Method(typeof(StandingPortraitDisplayPatch), set)));
            }
            _loader.Enable(context.Log);
            HarmonyPatching.Installed(context.Log, "Patched standing portrait display for deferred loading.");
        }
        catch (Exception ex)
        {
            foreach (var hook in _fieldHooks)
            {
                try { hook.Dispose(); }
                catch (Exception cleanup)
                {
                    context.Log.Warning($"Standing portrait hook cleanup failed: {cleanup.Message}");
                }
            }
            _fieldHooks.Clear();
            context.Log.Warning($"Deferred standing portraits unavailable, using eager loading: {ex}");
        }

        void AddFieldHook(Hook hook)
        {
            _fieldHooks.Add(hook);
            if (!hook.IsApplied)
                throw new InvalidOperationException($"Standing portrait accessor hook was not applied: {hook.Method.Name}");
        }

        void Patch(Type type, string method, Type[] parameters, string prefix)
        {
            var target = AccessTools.Method(type, method, parameters)
                ?? throw new MissingMethodException(type.FullName, method);
            harmony.Patch(target, prefix: new HarmonyMethod(typeof(StandingPortraitDisplayPatch), prefix));
        }
    }

    private static Texture2D GetLeft(Func<SpeakerTemplate, Texture2D> read, SpeakerTemplate speaker)
        => Read(read, speaker, nameof(SpeakerTemplate.StandLookLeftImage));

    private static Texture2D GetRight(Func<SpeakerTemplate, Texture2D> read, SpeakerTemplate speaker)
        => Read(read, speaker, nameof(SpeakerTemplate.StandLookRightImage));

    private static Texture2D GetInactive(Func<SpeakerTemplate, Texture2D> read, SpeakerTemplate speaker)
        => Read(read, speaker, nameof(SpeakerTemplate.StandLookRightInactiveImage));

    private static Texture2D Read(Func<SpeakerTemplate, Texture2D> read, SpeakerTemplate speaker, string field)
    {
        _portraits.Resolve(speaker, field);
        return read(speaker);
    }

    private static void SetLeft(Action<SpeakerTemplate, Texture2D> write, SpeakerTemplate speaker, Texture2D texture)
        => Write(write, speaker, texture, nameof(SpeakerTemplate.StandLookLeftImage));

    private static void SetRight(Action<SpeakerTemplate, Texture2D> write, SpeakerTemplate speaker, Texture2D texture)
        => Write(write, speaker, texture, nameof(SpeakerTemplate.StandLookRightImage));

    private static void SetInactive(Action<SpeakerTemplate, Texture2D> write, SpeakerTemplate speaker, Texture2D texture)
        => Write(write, speaker, texture, nameof(SpeakerTemplate.StandLookRightInactiveImage));

    private static void Write(Action<SpeakerTemplate, Texture2D> write, SpeakerTemplate speaker, Texture2D texture, string field)
    {
        write(speaker, texture);
        _portraits.Forget(speaker, field);
    }

    private static void LeaderPrefix(BaseUnitLeader __instance)
    {
        var speaker = __instance.LeaderTemplate?.SpeakerTemplate;
        if (speaker == null || !_portraits.Enabled)
            return;
        // GetStandingImage uses this roster check to select the inactive artwork.
        var inactive = StrategyState.Get()?.Roster?.IsPermanentlyDead(__instance) == true;
        _portraits.Resolve(speaker, inactive
            ? nameof(SpeakerTemplate.StandLookRightInactiveImage)
            : nameof(SpeakerTemplate.StandLookRightImage));
    }

    private static void RolePrefix(Role __0, SpeakerTemplate __1)
    {
        if (__0 == null || __1 == null || !_portraits.Enabled)
            return;
        // Conversation portraits face towards the other side of the screen.
        if (__0.Position == RolePosition.Left)
            _portraits.Resolve(__1, nameof(SpeakerTemplate.StandLookRightImage));
        else if (__0.Position == RolePosition.Right)
            _portraits.Resolve(__1, nameof(SpeakerTemplate.StandLookLeftImage));
    }

    private static void ActorPrefix(Actor __0)
    {
        if (__0 != null && _portraits.Enabled)
            _portraits.Resolve(__0.GetSpeakerTemplate(), nameof(SpeakerTemplate.StandLookRightImage));
    }
}
