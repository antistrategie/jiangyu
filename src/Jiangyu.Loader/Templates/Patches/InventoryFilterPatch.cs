using System.Reflection;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes;
using Jiangyu.Loader.Runtime.Patching;
using MelonLoader;

namespace Jiangyu.Loader.Templates;

/// <summary>
/// Filters the strategy-mode loadout UI's armor/weapon dropdown so a
/// unit with a slot-specific restriction tag only sees items whose
/// <c>OnlyEquipableBy</c> intersects its own <c>EntityTemplate.Tags</c>.
///
/// Two-patch chain because the UI's sort/filter pass runs inside the
/// parent panel's update method and we need both contexts:
/// <list type="number">
/// <item>Prefix on <c>UnitWindowEquipment.UpdateEquipmentAlternatives</c>:
///   capture the panel's owning unit + the slot being opened into a
///   static context. A Harmony finalizer (not just postfix) clears the
///   context so an exception in the original body cannot leak stale
///   state into the next dropdown open.</item>
/// <item>Postfix on <c>SortedFilteredItemList.GetSortedAndFilteredItems</c>:
///   read the captured context and filter the result list before the
///   panel's downstream loop turns it into visual slots.</item>
/// </list>
///
/// The single Postfix-only design on <c>UpdateEquipmentAlternatives</c>
/// doesn't work because the visual slots are added INSIDE that method.
/// Mutating the unfiltered source list after the fact is invisible to
/// the already-populated SortedFilteredItemList.
/// </summary>
internal sealed class InventoryFilterPatch : IHarmonyPatchModule
{
    private const string PanelTypeNameSuffix = "UnitWindowEquipment";
    private const string PanelMethodName = "UpdateEquipmentAlternatives";
    private const string FilterTypeNameSuffix = "SortedFilteredItemList";
    private const string FilterMethodName = "GetSortedAndFilteredItems";

    // JIANGYU-CONTRACT: Jiangyu-invented tag-name convention. NOT a
    // MENACE authoring pattern; this is a stop-gap until the SDK can
    // express slot filtering in code. Scope: strategy loadout UI only.
    // Values are the numeric Il2CppMenace.Items.ItemSlot enum
    // (InfantryWeapon=0, InfantrySpecial=1, InfantryArmor=2,
    // InfantryAccessory=3, Vehicle=4). A unit carrying one of these
    // tags plus a matching OnlyEquipableBy tag on the item gates the
    // slot's dropdown. Replace when the SDK lands.
    private static readonly Dictionary<string, int> RestrictionTagToSlot = new(StringComparer.Ordinal)
    {
        { "armor_restricted",  2 }, // InfantryArmor
        { "weapon_restricted", 0 }, // InfantryWeapon
    };

    private static MelonLogger.Instance _log;

    // Captured by the panel-update Prefix, consumed by the filter Postfix,
    // cleared by the panel-update Finalizer (runs even when the original
    // method throws). Static is fine: MENACE is single-threaded for UI
    // updates and the static is set/cleared in a tight prefix/finalizer
    // bracket around the SortedFilteredItemList call.
    private static object _activeLeader;
    private static int _activeSlot = -1;

    // One-shot warning flags so a MENACE rename of m_Leader, Tags, or
    // OnlyEquipableBy surfaces in the log instead of silently disabling
    // the filter, without spamming on every dropdown open.
    private static bool _warnedMissingLeader;
    private static bool _warnedMissingUnitTags;
    private static bool _warnedMissingOnlyEquipable;

    public void Install(HarmonyLib.Harmony harmony, LoaderHarmonyPatchContext context)
    {
        _log = context.Log;

        var panelMethod = FindMethod(PanelTypeNameSuffix, PanelMethodName, paramTypeSuffixes: new[] { "EquipmentSlot", "ItemSlot" });
        var filterMethod = FindFilterMethod();

        if (panelMethod == null)
        {
            _log.Warning($"Inventory filter: {PanelTypeNameSuffix}.{PanelMethodName} not found.");
            return;
        }
        if (filterMethod == null)
        {
            _log.Warning($"Inventory filter: {FilterTypeNameSuffix}.{FilterMethodName} (ItemTemplate variant) not found.");
            return;
        }

        try
        {
            harmony.Patch(panelMethod,
                prefix: new HarmonyMethod(typeof(InventoryFilterPatch), nameof(PanelPrefix)),
                finalizer: new HarmonyMethod(typeof(InventoryFilterPatch), nameof(PanelFinalizer)));
            harmony.Patch(filterMethod,
                postfix: new HarmonyMethod(typeof(InventoryFilterPatch), nameof(FilterPostfix)));
            _log.Msg($"Inventory filter: hooked {panelMethod.DeclaringType?.Name}.{PanelMethodName} + {filterMethod.DeclaringType?.Name}.{FilterMethodName}.");
        }
        catch (Exception ex)
        {
            _log.Warning($"Inventory filter: patch failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Find a method by class-name-suffix match (avoids hardcoding the
    /// full Il2Cpp namespace) and parameter-type-name suffix matching.
    /// Generic-arity backticks are stripped before comparison so a
    /// generic wrapper like <c>SortedFilteredItemList`1</c> still matches
    /// the suffix. If multiple types match, the first wins and a warning
    /// is logged so an ambiguous resolution is visible.
    /// </summary>
    private static MethodInfo FindMethod(string classNameSuffix, string methodName, string[] paramTypeSuffixes)
    {
        MethodInfo best = null;
        string bestTypeName = null;
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type[] types;
            try { types = asm.GetTypes(); } catch { continue; }
            foreach (var type in types)
            {
                if (type == null) continue;
                if (type.FullName == null || !type.FullName.StartsWith("Il2Cpp")) continue;
                if (!StripGenericArity(type.Name).EndsWith(classNameSuffix, StringComparison.Ordinal)) continue;
                MethodInfo m;
                try
                {
                    m = type.GetMethod(methodName,
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                }
                catch { continue; }
                if (m == null) continue;
                var p = m.GetParameters();
                if (p.Length != paramTypeSuffixes.Length) continue;
                bool match = true;
                for (int i = 0; i < p.Length; i++)
                {
                    if (!p[i].ParameterType.Name.Contains(paramTypeSuffixes[i])) { match = false; break; }
                }
                if (!match) continue;

                if (best == null)
                {
                    best = m;
                    bestTypeName = type.FullName;
                }
                else if (type.FullName != bestTypeName)
                {
                    _log?.Warning($"Inventory filter: multiple {classNameSuffix} candidates; keeping {bestTypeName}, ignoring {type.FullName}.");
                }
            }
        }
        return best;
    }

    /// <summary>
    /// <c>GetSortedAndFilteredItems</c> has two overloads, one over
    /// <c>BaseItem</c> and one over <c>ItemTemplate</c>. We want the
    /// ItemTemplate variant (the armor/weapon dropdown). Both take two
    /// <c>List</c> parameters, so pick by element type.
    /// </summary>
    private static MethodInfo FindFilterMethod()
    {
        MethodInfo best = null;
        string bestTypeName = null;
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type[] types;
            try { types = asm.GetTypes(); } catch { continue; }
            foreach (var type in types)
            {
                if (type == null) continue;
                if (type.FullName == null || !type.FullName.StartsWith("Il2Cpp")) continue;
                if (!StripGenericArity(type.Name).EndsWith(FilterTypeNameSuffix, StringComparison.Ordinal)) continue;
                MethodInfo[] methods;
                try { methods = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance); }
                catch { continue; }
                foreach (var m in methods)
                {
                    if (m.Name != FilterMethodName) continue;
                    var p = m.GetParameters();
                    if (p.Length != 2) continue;
                    var pt = p[0].ParameterType;
                    var ga = pt.IsByRef ? pt.GetElementType().GetGenericArguments() : pt.GetGenericArguments();
                    if (ga == null || ga.Length == 0) continue;
                    if (!ga[0].Name.Contains("ItemTemplate")) continue;

                    if (best == null)
                    {
                        best = m;
                        bestTypeName = type.FullName;
                    }
                    else if (type.FullName != bestTypeName)
                    {
                        _log?.Warning($"Inventory filter: multiple {FilterTypeNameSuffix} candidates; keeping {bestTypeName}, ignoring {type.FullName}.");
                    }
                }
            }
        }
        return best;
    }

    private static string StripGenericArity(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;
        var backtick = name.IndexOf('`');
        return backtick < 0 ? name : name[..backtick];
    }

    public static void PanelPrefix(Il2CppObjectBase __instance, object[] __args)
    {
        // Clear at the top: belt-and-braces against any path that left
        // stale state from a prior call.
        _activeLeader = null;
        _activeSlot = -1;

        try
        {
            if (__instance == null || __args == null || __args.Length < 2) return;
            var leader = ReadMember(__instance, __instance.GetType(), "m_Leader");
            if (leader == null)
            {
                if (!_warnedMissingLeader)
                {
                    _warnedMissingLeader = true;
                    _log?.Warning("Inventory filter: m_Leader not readable on UnitWindowEquipment; filter disabled.");
                }
                return;
            }
            _activeLeader = leader;
            _activeSlot = Convert.ToInt32(__args[1]);
        }
        catch (Exception ex)
        {
            _log?.Warning($"Inventory filter PanelPrefix: {ex.Message}");
            _activeLeader = null;
            _activeSlot = -1;
        }
    }

    public static void PanelFinalizer()
    {
        _activeLeader = null;
        _activeSlot = -1;
    }

    public static void FilterPostfix(object[] __args)
    {
        try
        {
            if (_activeLeader == null || _activeSlot < 0) return;
            if (__args == null || __args.Length < 2) return;

            // Which restriction tags apply to the active slot?
            var applicableTags = new HashSet<string>(StringComparer.Ordinal);
            foreach (var kv in RestrictionTagToSlot)
                if (kv.Value == _activeSlot) applicableTags.Add(kv.Key);
            if (applicableTags.Count == 0) return;

            var unitTags = TryReadEntityTags(_activeLeader);
            if (unitTags == null || unitTags.Count == 0) return;
            if (!applicableTags.Any(t => unitTags.Contains(t))) return;

            // __args[1] is the OUTPUT List<ItemTemplate> that the downstream
            // visual-slot loop iterates. Filtering this is what actually
            // affects what the player sees.
            var resultList = __args[1];
            if (resultList == null) return;
            FilterResultList(resultList, unitTags);
        }
        catch (Exception ex)
        {
            _log?.Warning($"Inventory filter FilterPostfix: {ex.Message}");
        }
    }

    private static HashSet<string> TryReadEntityTags(object unit)
    {
        if (unit == null) return null;
        var unitType = unit.GetType();

        var direct = ReadMember(unit, unitType, "Tags");
        if (direct != null) return ReadTagNames(direct);

        try
        {
            var getTemplate = unitType.GetMethod("GetTemplate",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                binder: null, types: Type.EmptyTypes, modifiers: null);
            if (getTemplate != null)
            {
                var template = getTemplate.Invoke(unit, null);
                if (template != null)
                {
                    var t = ReadMember(template, template.GetType(), "Tags");
                    if (t != null) return ReadTagNames(t);
                }
            }
        }
        catch { /* fall through */ }

        foreach (var name in new[] { "m_Template", "Template" })
        {
            var template = ReadMember(unit, unitType, name);
            if (template == null) continue;
            var t = ReadMember(template, template.GetType(), "Tags");
            if (t != null) return ReadTagNames(t);
        }

        if (!_warnedMissingUnitTags)
        {
            _warnedMissingUnitTags = true;
            _log?.Warning("Inventory filter: unit Tags not readable via direct/GetTemplate/m_Template; filter inert.");
        }
        return null;
    }

    private static object ReadMember(object obj, Type type, string name)
    {
        var f = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (f != null) return f.GetValue(obj);
        var p = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (p != null && p.CanRead) return p.GetValue(obj);
        return null;
    }

    private static HashSet<string> ReadTagNames(object il2cppList)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        var listType = il2cppList.GetType();
        var countProp = listType.GetProperty("Count", BindingFlags.Instance | BindingFlags.Public);
        var indexer = listType.GetProperty("Item", BindingFlags.Instance | BindingFlags.Public);
        if (countProp == null || indexer == null) return names;
        int count;
        try { count = (int)countProp.GetValue(il2cppList); } catch { return names; }
        for (int i = 0; i < count; i++)
        {
            object tag;
            try { tag = indexer.GetValue(il2cppList, new object[] { i }); } catch { continue; }
            if (tag == null) continue;
            var nameProp = tag.GetType().GetProperty("name", BindingFlags.Instance | BindingFlags.Public);
            if (nameProp == null || !nameProp.CanRead) continue;
            string n;
            try { n = nameProp.GetValue(tag) as string; } catch { continue; }
            if (!string.IsNullOrEmpty(n)) names.Add(n);
        }
        return names;
    }

    private static void FilterResultList(object resultList, HashSet<string> unitTags)
    {
        var listType = resultList.GetType();
        var countProp = listType.GetProperty("Count", BindingFlags.Instance | BindingFlags.Public);
        var indexer = listType.GetProperty("Item", BindingFlags.Instance | BindingFlags.Public);
        var removeAt = listType.GetMethod("RemoveAt", BindingFlags.Instance | BindingFlags.Public);
        if (countProp == null || indexer == null || removeAt == null) return;

        int count;
        try { count = (int)countProp.GetValue(resultList); } catch { return; }
        for (int i = count - 1; i >= 0; i--)
        {
            object item;
            try { item = indexer.GetValue(resultList, new object[] { i }); } catch { continue; }
            if (!ItemAllowedFor(item, unitTags))
            {
                try { removeAt.Invoke(resultList, new object[] { i }); } catch { /* skip */ }
            }
        }
    }

    private static bool ItemAllowedFor(object item, HashSet<string> unitTags)
    {
        if (item == null) return true;
        var onlyEquipable = ReadMember(item, item.GetType(), "OnlyEquipableBy");
        if (onlyEquipable == null)
        {
            if (!_warnedMissingOnlyEquipable)
            {
                _warnedMissingOnlyEquipable = true;
                _log?.Warning("Inventory filter: OnlyEquipableBy not readable on ItemTemplate; treating as not-equippable.");
            }
            return false;
        }
        var tagNames = ReadTagNames(onlyEquipable);
        foreach (var t in tagNames)
            if (unitTags.Contains(t)) return true;
        return false;
    }
}
