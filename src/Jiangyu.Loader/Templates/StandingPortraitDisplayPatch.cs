using HarmonyLib;
using Il2CppMenace.Conversations;
using Il2CppMenace.States;
using Il2CppMenace.Strategy;
using Il2CppMenace.Tactical;
using Il2CppMenace.UI;
using Il2CppMenace.UI.Strategy;
using Il2CppMenace.UI.Tactical;
using Jiangyu.Game.Ui;
using Jiangyu.Loader.Runtime.Patching;

namespace Jiangyu.Loader.Templates;

/// <summary>Load standing artwork before native UI reads a speaker's texture fields.
/// See docs/research/verified/standing-portrait-textures.md.</summary>
internal sealed class StandingPortraitDisplayPatch : IHarmonyPatchModule
{
    private static DeferredStandingPortraits _portraits;
    private readonly DeferredStandingPortraits _loader;

    public StandingPortraitDisplayPatch(DeferredStandingPortraits loader) => _loader = loader;

    public void Install(HarmonyLib.Harmony harmony, LoaderHarmonyPatchContext context)
    {
        if (!_loader.Configured)
            return;
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
            _loader.Enable(context.Log);
            HarmonyPatching.Installed(context.Log, "Patched standing portrait display for deferred loading.");
        }
        catch (Exception ex)
        {
            context.Log.Warning($"Deferred standing portraits unavailable, using eager loading: {ex.Message}");
        }

        void Patch(Type type, string method, Type[] parameters, string prefix)
        {
            var target = AccessTools.Method(type, method, parameters)
                ?? throw new MissingMethodException(type.FullName, method);
            harmony.Patch(target, prefix: new HarmonyMethod(typeof(StandingPortraitDisplayPatch), prefix));
        }
    }

    private static void LeaderPrefix(BaseUnitLeader __instance)
    {
        var speaker = __instance.LeaderTemplate?.SpeakerTemplate;
        if (speaker == null || !_portraits.Enabled)
            return;
        // GetStandingImage uses this roster check to select the inactive artwork.
        var inactive = StrategyState.Get()?.Roster?.IsPermanentlyDead(__instance) == true;
        _portraits.Resolve(speaker, inactive ? StandingPortrait.Inactive : StandingPortrait.Right);
    }

    private static void RolePrefix(Role __0, SpeakerTemplate __1)
    {
        if (__0 == null || __1 == null || !_portraits.Enabled)
            return;
        // Conversation portraits face towards the other side of the screen.
        if (__0.Position == RolePosition.Left)
            _portraits.Resolve(__1, StandingPortrait.Right);
        else if (__0.Position == RolePosition.Right)
            _portraits.Resolve(__1, StandingPortrait.Left);
    }

    private static void ActorPrefix(Actor __0)
    {
        if (__0 != null && _portraits.Enabled)
            _portraits.Resolve(__0.GetSpeakerTemplate(), StandingPortrait.Right);
    }
}
