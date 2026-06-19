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
        { "armor_restricted",   2 }, // InfantryArmor
        { "weapon_restricted",  0 }, // InfantryWeapon
        { "vehicle_restricted", 4 }, // Vehicle
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
    private static bool _warnedMissingInstanceTemplate;

    // Resolved once in Install: UIManager.Get() (static) and GetActiveScreen() (instance). The trade
    // guard uses them to act only while the black market is the active screen. Null when not resolved,
    // in which case the guard falls back to filtering every owned-item list.
    private static MethodInfo _uiManagerGet;
    private static MethodInfo _getActiveScreen;

    public void Install(HarmonyLib.Harmony harmony, LoaderHarmonyPatchContext context)
    {
        _log = context.Log;
        ResolveActiveScreenAccessors();

        var panelMethod = Il2CppMethodResolver.Find(PanelTypeNameSuffix, PanelMethodName, new[] { "EquipmentSlot", "ItemSlot" }, exact: false, _log, "Inventory filter");
        var filterMethod = FindFilterMethod("ItemTemplate");

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

        // The BaseItem overload drives the black market's owned-item lists. Restriction-tagged items
        // are bound to a unit (a pilot's chassis, a leader's custom armour), so trading them would
        // soft-lock the unit. The guard strips them only while the black market is the active screen,
        // leaving other owned-item views untouched. Equipping is unaffected (the ItemTemplate overload).
        var ownedFilterMethod = FindFilterMethod("BaseItem");
        if (ownedFilterMethod == null)
        {
            _log.Warning($"Inventory filter: {FilterTypeNameSuffix}.{FilterMethodName} (BaseItem variant) not found; trade guard inactive.");
            return;
        }
        try
        {
            harmony.Patch(ownedFilterMethod,
                postfix: new HarmonyMethod(typeof(InventoryFilterPatch), nameof(OwnedItemFilterPostfix)));
            _log.Msg($"Inventory filter: hooked {ownedFilterMethod.DeclaringType?.Name}.{FilterMethodName} (BaseItem variant) for the restricted-item trade guard.");
        }
        catch (Exception ex)
        {
            _log.Warning($"Inventory filter: trade-guard patch failed: {ex.Message}");
        }
    }

    /// <summary>
    /// <c>GetSortedAndFilteredItems</c> has two overloads, one over
    /// <c>BaseItem</c> and one over <c>ItemTemplate</c>. Both take two
    /// <c>List</c> parameters, so pick by element type:
    /// <c>ItemTemplate</c> drives the armor/weapon dropdown; <c>BaseItem</c>
    /// drives the black-market owned-item lists.
    /// </summary>
    private static MethodInfo FindFilterMethod(string elementTypeName)
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
                if (!Il2CppMethodResolver.StripGenericArity(type.Name).EndsWith(FilterTypeNameSuffix, StringComparison.Ordinal)) continue;
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
                    // Exact match: "BaseItem" must not also match the "BaseItemTemplate" overload.
                    if (ga[0].Name != elementTypeName) continue;

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

            // Fail OPEN: a null result means the tags could not be read (reflection failure), distinct
            // from a unit that simply has no tags. Leave the dropdown intact rather than treating the
            // unit as unrestricted and stripping every restriction-tagged item from it.
            var unitTags = TryReadEntityTags(_activeLeader);
            if (unitTags == null)
            {
                if (!_warnedMissingUnitTags)
                {
                    _warnedMissingUnitTags = true;
                    _log?.Warning("Inventory filter: unit tags not readable; filter left open for this slot.");
                }
                return;
            }
            // Two-way exclusivity. A unit tagged with the slot's restriction sees only matching
            // items; a unit WITHOUT it never sees items that are exclusive to restricted units.
            // This replaces vanilla OnlyEquipableBy for vehicles (removed so a pilot still sees its
            // own mech with nothing equipped) while keeping the chassis exclusive to the mech pilot.
            bool unitRestricted = applicableTags.Any(t => unitTags.Contains(t));

            // __args[1] is the OUTPUT List<ItemTemplate> that the downstream
            // visual-slot loop iterates. Filtering this is what actually
            // affects what the player sees.
            var resultList = __args[1];
            if (resultList == null) return;
            FilterResultList(resultList, unitTags, applicableTags, unitRestricted);
        }
        catch (Exception ex)
        {
            _log?.Warning($"Inventory filter FilterPostfix: {ex.Message}");
        }
    }

    // Postfix on the BaseItem overload of GetSortedAndFilteredItems (owned-item lists, e.g. the black
    // market). Drops instances whose template carries a slot-restriction tag so a unit-bound item
    // cannot be sold and soft-lock its unit.
    public static void OwnedItemFilterPostfix(object[] __args)
    {
        try
        {
            if (!IsBlackMarketActive()) return;
            if (__args == null || __args.Length < 2) return;
            var resultList = __args[1];
            if (resultList == null) return;
            RemoveRestrictedInstances(resultList);
        }
        catch (Exception ex)
        {
            _log?.Warning($"Inventory filter OwnedItemFilterPostfix: {ex.Message}");
        }
    }

    private static void RemoveRestrictedInstances(object resultList)
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
            if (item == null) continue;
            var tags = ReadInstanceTemplateTags(item);
            if (tags == null) continue;
            bool restricted = false;
            foreach (var t in RestrictionTagToSlot.Keys)
                if (tags.Contains(t)) { restricted = true; break; }
            if (restricted)
            {
                try { removeAt.Invoke(resultList, new object[] { i }); } catch { /* skip */ }
            }
        }
    }

    // A BaseItem instance's template tags: GetBaseItemTemplate() (or m_Template), then Tags.
    private static HashSet<string> ReadInstanceTemplateTags(object item)
    {
        var itemType = item.GetType();
        object template = null;
        var getTmpl = GetNoArgMethod(itemType, "GetBaseItemTemplate") ?? GetNoArgMethod(itemType, "GetTemplate");
        if (getTmpl != null)
        {
            try { template = getTmpl.Invoke(item, null); } catch { /* fall through */ }
        }
        if (template == null) template = ReadMember(item, itemType, "m_Template");
        if (template == null)
        {
            if (!_warnedMissingInstanceTemplate)
            {
                _warnedMissingInstanceTemplate = true;
                _log?.Warning("Inventory filter: BaseItem template accessor (GetBaseItemTemplate/GetTemplate/m_Template) not readable; trade guard inert.");
            }
            return null;
        }
        var tagsObj = ReadMember(template, template.GetType(), "Tags");
        return tagsObj == null ? null : ReadTagNames(tagsObj);
    }

    // Resolve UIManager.Get() + GetActiveScreen() once, so the trade guard can tell whether the
    // black market is the active screen.
    private static void ResolveActiveScreenAccessors()
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type[] types;
            try { types = asm.GetTypes(); } catch { continue; }
            foreach (var type in types)
            {
                if (type?.FullName == null || !type.FullName.StartsWith("Il2Cpp")) continue;
                if (type.Name != "UIManager") continue;
                try
                {
                    _uiManagerGet = type.GetMethod("Get", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
                    _getActiveScreen = type.GetMethod("GetActiveScreen", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, Type.EmptyTypes, null);
                }
                catch { /* keep scanning */ }
                if (_uiManagerGet != null && _getActiveScreen != null) return;
            }
        }
        _log?.Warning("Inventory filter: UIManager.GetActiveScreen not resolved; trade guard will filter every owned-item list.");
    }

    // True while the black market is the active screen. Falls back to FALSE when the accessors are
    // unavailable, so the owned-item guard fails OPEN. This guard shares the BaseItem overload with the
    // armoury/equip lists, so a false positive would strip a unit's own restricted gear from views it
    // must stay visible in. A reflection failure should disable the guard, not filter everywhere.
    private static bool IsBlackMarketActive()
    {
        if (_uiManagerGet == null || _getActiveScreen == null) return false;
        try
        {
            var manager = _uiManagerGet.Invoke(null, null);
            if (manager == null) return false;
            var screen = _getActiveScreen.Invoke(manager, null);
            var name = screen?.GetType().Name;
            return name != null && name.Contains("BlackMarket");
        }
        catch { return false; }
    }

    private static HashSet<string> TryReadEntityTags(object unit)
    {
        if (unit == null) return null;
        var unitType = unit.GetType();
        var tags = new HashSet<string>(StringComparer.Ordinal);

        // Infantry path: the leader's own EntityTemplate.Tags (direct, GetTemplate, m_Template).
        var direct = ReadMember(unit, unitType, "Tags");
        if (direct != null) AddTagNames(direct, tags);

        var template = TryResolveTemplate(unit, unitType);
        if (template != null)
        {
            var t = ReadMember(template, template.GetType(), "Tags");
            if (t != null) AddTagNames(t, tags);
        }

        // Pilot path: a vehicle pilot has no infantry template, and GetTemplate() is null while
        // no vehicle is equipped, so the slot restriction would go inert (and the player would
        // see every base-game vehicle). The leader's persistent identity is its SpeakerTemplate,
        // whose Tags (a space-separated string) we union in, so the restriction triggers
        // regardless of equipped vehicle. Harmless for infantry leaders.
        var getSpeaker = GetNoArgMethod(unitType, "GetSpeakerTemplate");
        if (getSpeaker != null)
        {
            try
            {
                var speaker = getSpeaker.Invoke(unit, null);
                if (speaker != null)
                    AddStringOrListTags(ReadMember(speaker, speaker.GetType(), "Tags"), tags);
            }
            catch { /* fall through */ }
        }

        if (tags.Count > 0) return tags;

        if (!_warnedMissingUnitTags)
        {
            _warnedMissingUnitTags = true;
            _log?.Warning("Inventory filter: unit Tags not readable via direct/GetTemplate/m_Template/vehicle; filter inert.");
        }
        return null;
    }

    private static void AddTagNames(object il2cppList, HashSet<string> into)
    {
        foreach (var n in ReadTagNames(il2cppList)) into.Add(n);
    }

    /// <summary>
    /// Add tag names from a value that is either a space-separated string (SpeakerTemplate.Tags)
    /// or an Il2Cpp List of TagTemplate (EntityTemplate.Tags).
    /// </summary>
    private static void AddStringOrListTags(object value, HashSet<string> into)
    {
        if (value == null) return;
        if (value is string || value.GetType().Name.Contains("String"))
        {
            var s = value.ToString();
            if (!string.IsNullOrEmpty(s))
                foreach (var part in s.Split(new[] { ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
                    into.Add(part);
            return;
        }
        AddTagNames(value, into);
    }

    /// <summary>Resolve a leader/unit's EntityTemplate via GetTemplate() then m_Template/Template.</summary>
    private static object TryResolveTemplate(object obj, Type type)
    {
        var get = GetNoArgMethod(type, "GetTemplate");
        if (get != null)
        {
            try
            {
                var t = get.Invoke(obj, null);
                if (t != null) return t;
            }
            catch { /* fall through */ }
        }
        foreach (var name in new[] { "m_Template", "Template" })
        {
            var t = ReadMember(obj, type, name);
            if (t != null) return t;
        }
        return null;
    }
    private static MethodInfo GetNoArgMethod(Type type, string name)
    {
        try
        {
            return type.GetMethod(name,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                binder: null, types: Type.EmptyTypes, modifiers: null);
        }
        catch { return null; }
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

    private static void FilterResultList(object resultList, HashSet<string> unitTags, HashSet<string> applicableTags, bool unitRestricted)
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
            bool keep;
            if (unitRestricted)
            {
                // Restricted unit: keep items it can equip (OnlyEquipableBy intersecting its tags,
                // armor/weapon) OR items carrying the restriction tag in their own Tags (vehicles:
                // a chassis tagged vehicle_restricted). Tags is on the base ItemTemplate, so it
                // reads off the list element directly, unlike VehicleItemTemplate.EntityTemplate.
                keep = ItemAllowedFor(item, unitTags) || ItemOwnTagsHaveAny(item, applicableTags);
            }
            else
            {
                // Non-restricted unit: drop items exclusive to restricted units (their own Tags
                // carry the restriction tag); everything else stays. For armor/weapon this removes
                // nothing (those items aren't self-tagged; vanilla OnlyEquipableBy already hid them).
                keep = !ItemOwnTagsHaveAny(item, applicableTags);
            }
            if (!keep)
            {
                try { removeAt.Invoke(resultList, new object[] { i }); } catch { /* skip */ }
            }
        }
    }

    /// <summary>
    /// True if the item's own Tags (on the base ItemTemplate, readable off the list element)
    /// contain any of the given tags. Used for the Vehicle slot: a chassis item tagged
    /// vehicle_restricted is kept even without OnlyEquipableBy.
    /// </summary>
    private static bool ItemOwnTagsHaveAny(object item, HashSet<string> tags)
    {
        if (item == null || tags == null || tags.Count == 0) return false;
        var t = ReadMember(item, item.GetType(), "Tags");
        if (t == null) return false;
        foreach (var n in ReadTagNames(t))
            if (tags.Contains(n)) return true;
        return false;
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
