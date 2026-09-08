using System.Collections.Generic;
using System.Linq;
using Jiangyu.Loader.Runtime.Localisation;
using Jiangyu.Loader.Templates;
using Jiangyu.Shared.Bundles;
using Jiangyu.Shared.Localisation;
using Jiangyu.Shared.Templates;
using Xunit;

namespace Jiangyu.Loader.Tests.Localisation;

public class LocalePlannerTests
{
    private const string Fr = """
        msgctxt "MyMod::WeaponTemplate/weapon.ak15/Title"
        msgid "Kalashnikova-15"
        msgstr "Kalachnikova-15"

        msgctxt "MyMod::ui/swap"
        msgid "SWAP"
        msgstr "CHANGER"
        """;

    private const string De = """
        msgctxt "MyMod::WeaponTemplate/weapon.ak15/Title"
        msgid "Kalashnikova-15"
        msgstr "Kalaschnikow-15"
        """;

    private static DiscoveredMod Mod(string name) =>
        new(name, "", null, null, "", "", "", new List<string>(), new List<ManifestDependency>(), new List<ManifestDependency>());

    private static LocalePo Po(DiscoveredMod mod, string code, string po) =>
        new(mod, code, LocaleTable.Compile(po));

    private static string OnlyOpValue((DiscoveredMod Mod, CompiledTemplatePatchManifest Templates) entry) =>
        entry.Templates.TemplatePatches!.Single().Set.Single().Value!.String!;

    [Fact]
    public void Load_AppliesOnlyTranslations_NoBaseline()
    {
        var mod = Mod("A");
        var plan = LocalePlanner.Build([Po(mod, "fr", Fr)], LocaleResolver.State.Translatable, "fr", revertFirst: false);

        var entry = Assert.Single(plan.LoadList);
        Assert.Equal("Kalachnikova-15", OnlyOpValue(entry));
        Assert.Equal(1, plan.TranslatedOps);
        Assert.Equal("CHANGER", Assert.Contains("MyMod::ui/swap", plan.Ui));
    }

    [Fact]
    public void Switch_AppliesBaselineThenTranslation_InThatOrder()
    {
        var mod = Mod("A");
        var plan = LocalePlanner.Build([Po(mod, "fr", Fr)], LocaleResolver.State.Translatable, "fr", revertFirst: true);

        Assert.Equal(2, plan.LoadList.Count);
        Assert.Equal("Kalashnikova-15", OnlyOpValue(plan.LoadList[0]));   // English baseline first
        Assert.Equal("Kalachnikova-15", OnlyOpValue(plan.LoadList[1]));   // translation overlays
    }

    [Fact]
    public void SwitchToSource_RevertsViaBaselineOnly_NoTranslationsNoUi()
    {
        var mod = Mod("A");
        var plan = LocalePlanner.Build([Po(mod, "fr", Fr)], LocaleResolver.State.Source, null, revertFirst: true);

        var entry = Assert.Single(plan.LoadList);
        Assert.Equal("Kalashnikova-15", OnlyOpValue(entry));   // reverted to English
        Assert.Equal(0, plan.TranslatedOps);
        Assert.Empty(plan.Ui);
    }

    [Fact]
    public void Switch_BaselineCoversEveryShippedLanguage_NotJustActive()
    {
        var mod = Mod("A");
        var plan = LocalePlanner.Build(
            [Po(mod, "fr", Fr), Po(mod, "de", De)], LocaleResolver.State.Translatable, "de", revertFirst: true);

        // fr and de baselines (revert all), then the active de translation.
        Assert.Equal(3, plan.LoadList.Count);
        Assert.Equal("Kalaschnikow-15", OnlyOpValue(plan.LoadList[^1]));
        Assert.Equal(1, plan.TranslatedOps);
        // The fr-only UI string is not pulled in for the de switch.
        Assert.Empty(plan.Ui);
    }

    [Fact]
    public void LaterLoadedModsTranslationComesLast_SoItWinsOnDedup()
    {
        var plan = LocalePlanner.Build(
            [Po(Mod("A"), "fr", Fr), Po(Mod("B"), "fr", De)], LocaleResolver.State.Translatable, "fr", revertFirst: true);

        var lastTranslation = plan.LoadList.Last(e => OnlyOpValue(e) is "Kalachnikova-15" or "Kalaschnikow-15");
        Assert.Equal("B", lastTranslation.Mod.Name);
        Assert.Equal("Kalaschnikow-15", OnlyOpValue(lastTranslation));
    }

    [Fact]
    public void NoSources_ProducesEmptyPlan()
    {
        var plan = LocalePlanner.Build([], LocaleResolver.State.Translatable, "fr", revertFirst: true);
        Assert.Empty(plan.LoadList);
        Assert.Empty(plan.Conversations);
        Assert.Empty(plan.Ui);
        Assert.Equal(0, plan.TranslatedOps);
    }

    private const string FrConv = """
        msgctxt "MyMod::conv/A/click_bark/123"
        msgid "Your orders."
        msgstr "À vos ordres."
        """;

    [Fact]
    public void Load_AppliesConversationTranslationsOnly_NoBaseline()
    {
        var plan = LocalePlanner.Build([Po(Mod("A"), "fr", FrConv)], LocaleResolver.State.Translatable, "fr", revertFirst: false);

        var conv = Assert.Single(plan.Conversations);
        Assert.Equal("A/click_bark", conv.ConvId);
        Assert.Equal(123, conv.NodeGuid);
        Assert.Equal("À vos ordres.", conv.Value);
        Assert.Equal(1, plan.TranslatedOps);
    }

    [Fact]
    public void Switch_AppliesConversationBaselineThenTranslation_InThatOrder()
    {
        var plan = LocalePlanner.Build([Po(Mod("A"), "fr", FrConv)], LocaleResolver.State.Translatable, "fr", revertFirst: true);

        Assert.Equal(2, plan.Conversations.Count);
        Assert.Equal("Your orders.", plan.Conversations[0].Value);    // English baseline first
        Assert.Equal("À vos ordres.", plan.Conversations[1].Value);   // translation overlays
    }

    [Fact]
    public void ScopeTo_KeepsOnlyTheChangedTemplatesAndConversations()
    {
        var mod = Mod("A");
        var plan = LocalePlanner.Build(
            [Po(mod, "fr", Fr), Po(mod, "fr", FrConv)],
            LocaleResolver.State.Translatable, "fr", revertFirst: false);
        Assert.NotEmpty(plan.LoadList);
        Assert.NotEmpty(plan.Conversations);

        // Nothing changed: nothing to write, the UI map carries over.
        var none = LocalePlanner.ScopeTo(plan, (_, _) => false);
        Assert.Empty(none.LoadList);
        Assert.Empty(none.Conversations);
        Assert.Same(plan.Ui, none.Ui);

        // The translated weapon changed: its op stays, the subtitle op does not.
        var weapon = LocalePlanner.ScopeTo(plan, (type, id) => (type, id) == ("WeaponTemplate", "weapon.ak15"));
        Assert.Equal("Kalachnikova-15", OnlyOpValue(Assert.Single(weapon.LoadList)));
        Assert.Empty(weapon.Conversations);

        // The conversation changed: its subtitle op stays, the weapon op does not.
        var conv = LocalePlanner.ScopeTo(plan, (type, id) => (type, id) == ("ConversationTemplate", "A/click_bark"));
        Assert.Empty(conv.LoadList);
        Assert.Single(conv.Conversations);

        // A different type with the same id is another template.
        var other = LocalePlanner.ScopeTo(plan, (type, id) => (type, id) == ("ItemTemplate", "weapon.ak15"));
        Assert.Empty(other.LoadList);
    }

    [Fact]
    public void Without_LeavesOutTheHeldTemplatesOnly()
    {
        var mod = Mod("A");
        var plan = LocalePlanner.Build(
            [Po(mod, "fr", Fr), Po(mod, "fr", FrConv)],
            LocaleResolver.State.Translatable, "fr", revertFirst: false);

        var none = LocalePlanner.Without(plan, (_, _, _) => false, (_, _) => false);
        Assert.Equal(plan.LoadList.Count, none.LoadList.Count);
        Assert.Equal(plan.Conversations.Count, none.Conversations.Count);

        // The coordinate names MyMod's patch: MyMod's held block leaves the translation out.
        static bool Weapon(string type, string id) => (type, id) == ("WeaponTemplate", "weapon.ak15");
        var heldByOwner = LocalePlanner.Without(plan, (owner, type, id) => owner == "MyMod" && Weapon(type, id), Weapon);
        Assert.Empty(heldByOwner.LoadList);
        Assert.Single(heldByOwner.Conversations);

        // Another mod's block on the same template is held: MyMod's translation stays.
        var heldByOther = LocalePlanner.Without(plan, (owner, type, id) => owner == "B" && Weapon(type, id), Weapon);
        Assert.Single(heldByOther.LoadList);

        var convHeld = LocalePlanner.Without(plan, (_, _, _) => false, (type, id) => (type, id) == ("ConversationTemplate", "A/click_bark"));
        Assert.Single(convHeld.LoadList);
        Assert.Empty(convHeld.Conversations);
    }

    [Fact]
    public void Without_MatchesTheOwnerTheCoordinateNames_NotTheModShippingThePo()
    {
        // Mod B ships a translation of MyMod's patch: the coordinate names MyMod, so MyMod's
        // held block leaves it out and B's own held block does not.
        var plan = LocalePlanner.Build([Po(Mod("B"), "fr", Fr)], LocaleResolver.State.Translatable, "fr", revertFirst: false);
        var entry = Assert.Single(plan.LoadList);
        Assert.Equal("B", entry.Mod.Name);
        Assert.All(entry.Templates.TemplatePatches!, patch => Assert.Equal("MyMod", patch.Owner));

        static bool Weapon(string type, string id) => (type, id) == ("WeaponTemplate", "weapon.ak15");
        Assert.Empty(LocalePlanner.Without(plan, (owner, type, id) => owner == "MyMod" && Weapon(type, id), (_, _) => false).LoadList);
        Assert.Single(LocalePlanner.Without(plan, (owner, type, id) => owner == "B" && Weapon(type, id), (_, _) => false).LoadList);
    }
}
