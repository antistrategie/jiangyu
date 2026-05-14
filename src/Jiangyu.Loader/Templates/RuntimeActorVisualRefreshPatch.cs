using System.Reflection;
using HarmonyLib;
using Jiangyu.Loader.Runtime.Patching;
using MelonLoader;
using UnityEngine;

namespace Jiangyu.Loader.Templates;

/// <summary>
/// Postfix on <c>EntityVisuals.DetermineArmorPrefab</c> that routes soldier
/// visual dispatch through a modder-cloned <c>ArmorTemplate</c> when the
/// element belongs to a modder-cloned <c>UnitLeaderTemplate</c>. The leader→
/// armor pairing is declared explicitly in KDL via:
///
/// <code>
/// bind "leader_armor" leader="&lt;UnitLeaderTemplateCloneId&gt;" armor="&lt;ArmorTemplateCloneId&gt;"
/// </code>
///
/// Why this is needed: MENACE's runtime ignores per-squad
/// <c>EntityTemplate.Items[]</c> when resolving element visuals — each unit's
/// <c>ItemContainer</c> holds the shared vanilla <c>ArmorTemplate</c>, so
/// patching the cloned armor or redirecting <c>Items[]</c> on a cloned entity
/// has no effect. Redirecting the cloned <c>UnitLeaderTemplate.InfantryUnitTemplate</c>
/// to a cloned EntityTemplate breaks the strategy squad UI (its list
/// rendering crashes when the leader's data walk hits a cloned EntityTemplate).
/// So we leave the template chain vanilla and route the dispatch at the
/// postfix boundary: when <c>DetermineArmorPrefab</c> is called with a cloned
/// <c>UnitLeaderTemplate</c>, we look up the bound cloned <c>ArmorTemplate</c>
/// and return the model from its <c>SquadLeaderModel*</c> /
/// <c>MaleModels</c> / <c>FemaleModels</c> fields instead.
/// </summary>
internal sealed class RuntimeActorVisualRefreshPatch : IHarmonyPatchModule
{
    private const string LeaderArmorBindingKind = "leader_armor";

    private static readonly string[] EntityVisualsTypeCandidates =
    {
        "Il2CppMenace.Tactical.EntityVisuals",
        "Menace.Tactical.EntityVisuals",
    };
    private static readonly string[] ArmorTemplateTypeCandidates =
    {
        "Il2CppMenace.Items.ArmorTemplate",
        "Menace.Items.ArmorTemplate",
    };

    private static MelonLogger.Instance _log;
    private static TemplateBindingCatalog _bindings;

    // Cloned-UnitLeaderTemplate-name → cloned-ArmorTemplate (typed wrapper).
    // Populated lazily on first DetermineArmorPrefab call from the explicit
    // bind "leader_armor" directives.
    private static readonly Dictionary<string, object> _leaderToOverrideArmor = new(StringComparer.Ordinal);
    private static bool _overrideMapPopulated;

    public RuntimeActorVisualRefreshPatch(TemplateBindingCatalog bindings)
    {
        _bindings = bindings;
    }

    public void Install(HarmonyLib.Harmony harmony, LoaderHarmonyPatchContext context)
    {
        _log = context.Log;

        var entityVisualsType = ResolveType(EntityVisualsTypeCandidates);
        if (entityVisualsType == null)
        {
            _log.Warning("Runtime actor visual refresh: EntityVisuals type not found.");
            return;
        }

        var determine = entityVisualsType.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .FirstOrDefault(m => m.Name == "DetermineArmorPrefab");
        if (determine == null)
        {
            _log.Warning("Runtime actor visual refresh: EntityVisuals.DetermineArmorPrefab not found.");
            return;
        }

        harmony.Patch(
            determine,
            postfix: new HarmonyMethod(typeof(RuntimeActorVisualRefreshPatch), nameof(DetermineArmorPrefabPostfix)));
        _log.Msg("Patched EntityVisuals.DetermineArmorPrefab for cloned-leader visual override.");
    }

    private static void DetermineArmorPrefabPostfix(
        object _template,
        object _squaddie,
        int _elementIndex,
        object _gender,
        object _items,
        object _leaderTemplate,
        ref GameObject __result)
    {
        try
        {
            if (_leaderTemplate == null) return;
            var leaderName = TryGetTemplateName(_leaderTemplate);
            if (string.IsNullOrEmpty(leaderName)) return;

            var overrideArmor = TryGetOverrideArmor(leaderName);
            if (overrideArmor == null) return;

            var picked = PickModelFromArmor(overrideArmor, _gender, _squaddie, _elementIndex);
            if (picked != null)
                __result = picked;
        }
        catch (Exception ex)
        {
            _log.Warning($"DetermineArmorPrefab postfix failure: {ex.Message}");
        }
    }

    private static object TryGetOverrideArmor(string leaderTemplateName)
    {
        EnsureOverrideMap();
        return _leaderToOverrideArmor.TryGetValue(leaderTemplateName, out var armor) ? armor : null;
    }

    private static void EnsureOverrideMap()
    {
        if (_overrideMapPopulated) return;
        _overrideMapPopulated = true;
        if (_bindings == null || !_bindings.HasBindings) return;

        try
        {
            var armorTemplateType = ResolveType(ArmorTemplateTypeCandidates);
            if (armorTemplateType == null) return;

            foreach (var binding in _bindings.ByKind(LeaderArmorBindingKind))
            {
                var leaderId = binding.Get("leader");
                var armorId = binding.Get("armor");
                if (string.IsNullOrEmpty(leaderId) || string.IsNullOrEmpty(armorId))
                {
                    _log.Warning(
                        $"Binding '{LeaderArmorBindingKind}' from mod '{binding.OwnerLabel}' is missing leader or armor attribute; skipped.");
                    continue;
                }

                var armor = ResolveClonedArmor(armorId, armorTemplateType);
                if (armor == null)
                {
                    _log.Warning(
                        $"Binding '{LeaderArmorBindingKind}' leader='{leaderId}' armor='{armorId}' "
                        + $"(mod '{binding.OwnerLabel}'): ArmorTemplate '{armorId}' not found at runtime; skipped.");
                    continue;
                }

                _leaderToOverrideArmor[leaderId] = armor;
                _log.Msg(
                    $"Registered visual override: UnitLeaderTemplate '{leaderId}' → ArmorTemplate '{armorId}' (mod '{binding.OwnerLabel}').");
            }
        }
        catch (Exception ex)
        {
            _log.Warning($"Override map population failure: {ex.Message}");
        }
    }

    private static object ResolveClonedArmor(string armorName, Type armorTemplateType)
    {
        // Resources.FindObjectsOfTypeAll narrows by IL2CPP type but returns
        // wrappers typed at the polymorphic ItemTemplate base — cast each
        // match to the concrete ArmorTemplate wrapper via Il2CppObjectBase.Cast
        // so its MaleModels / FemaleModels / SquadLeaderModel* fields are
        // visible to reflection.
        var castMethod = typeof(Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase)
            .GetMethod("Cast", BindingFlags.Instance | BindingFlags.Public)
            ?.MakeGenericMethod(armorTemplateType);
        if (castMethod == null) return null;
        var all = Resources.FindObjectsOfTypeAll(Il2CppInterop.Runtime.Il2CppType.From(armorTemplateType));
        foreach (var obj in all)
        {
            if ((obj as UnityEngine.Object)?.name != armorName) continue;
            try { return castMethod.Invoke(obj, null); }
            catch (Exception ex) { _log.Warning($"Cast to ArmorTemplate failed for '{armorName}': {ex.Message}"); }
        }
        return null;
    }

    private static GameObject PickModelFromArmor(object armor, object gender, object squaddie, int elementIndex)
    {
        // Leader (idx 0): SquadLeaderModel{Gender}{SkinColor}.
        // Grunt  (idx > 0): MaleModels[0] / FemaleModels[0].
        // ArmorTemplate fields are exposed as properties in IL2CPP-interop.
        var armorType = armor.GetType();
        var isFemale = (gender?.ToString() ?? "Male").Equals("Female", StringComparison.Ordinal);

        if (elementIndex == 0 && squaddie != null)
        {
            var skin = TryGetSkinColor(squaddie);
            var slot = $"SquadLeaderModel{(isFemale ? "Female" : "Male")}{skin}";
            var leaderModel = ReadMemberValue(armor, armorType, slot) as GameObject;
            if (leaderModel != null) return leaderModel;
        }

        var poolName = isFemale ? "FemaleModels" : "MaleModels";
        var pool = ReadMemberValue(armor, armorType, poolName);
        if (pool == null) return null;
        var poolType = pool.GetType();
        var lengthProp = poolType.GetProperty("Length", BindingFlags.Instance | BindingFlags.Public)
            ?? poolType.GetProperty("Count", BindingFlags.Instance | BindingFlags.Public);
        var indexer = poolType.GetProperty("Item", BindingFlags.Instance | BindingFlags.Public);
        if (lengthProp == null || indexer == null) return null;
        var len = (int)lengthProp.GetValue(pool);
        if (len == 0) return null;
        return indexer.GetValue(pool, new object[] { 0 }) as GameObject;
    }

    private static string TryGetSkinColor(object squaddie)
    {
        var method = squaddie.GetType().GetMethod("GetSkinColor", BindingFlags.Instance | BindingFlags.Public);
        if (method == null) return "White";
        var value = method.Invoke(squaddie, null);
        return value?.ToString() ?? "White";
    }

    private static object ReadMemberValue(object instance, Type type, string memberName)
    {
        var prop = type.GetProperty(memberName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (prop != null && prop.CanRead) return prop.GetValue(instance);
        var field = type.GetField(memberName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (field != null) return field.GetValue(instance);
        return null;
    }

    private static string TryGetTemplateName(object template)
    {
        var u = template as UnityEngine.Object;
        if (u != null) return u.name;
        var nameProp = template?.GetType().GetProperty("name", BindingFlags.Instance | BindingFlags.Public);
        return nameProp?.GetValue(template) as string;
    }

    private static Type ResolveType(IEnumerable<string> candidates)
    {
        foreach (var candidate in candidates)
        {
            var resolved = AccessTools.TypeByName(candidate);
            if (resolved != null) return resolved;
        }
        return null;
    }
}
