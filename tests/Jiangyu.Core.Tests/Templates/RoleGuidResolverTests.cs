using Jiangyu.Core.Abstractions;
using Jiangyu.Core.Models;
using Jiangyu.Core.Templates;
using Jiangyu.Shared.Templates;

namespace Jiangyu.Core.Tests.Templates;

public class RoleGuidResolverTests
{
    private const int EntityGuid = 1248015120;
    private const int VoymastinaGuid = -987654321;

    [Fact]
    public void Apply_ResolvesPatchRoleStringAgainstIndexedRoles()
    {
        var sayComposite = new CompiledTemplateComposite
        {
            TypeName = "SAY",
            Operations = [SetOp("RoleGuid", StringValue("Entity"))],
        };
        var patch = WrapInPatch("ConversationTemplate", "JeanSy/click_bark", sayComposite);

        var errors = RoleGuidResolver.Apply(
            [patch], clones: null, indexedAssets: IndexedClickBark(), new NullLog());

        Assert.Equal(0, errors);
        var roleOp = sayComposite.Operations.Single(o => o.FieldPath == "RoleGuid");
        Assert.Equal(CompiledTemplateValueKind.Int32, roleOp.Value!.Kind);
        Assert.Equal(EntityGuid, roleOp.Value.Int32);
    }

    [Fact]
    public void Apply_FollowsCloneChainToSourceForRoleLookup()
    {
        // Clone Voymastina/click_bark FROM JeanSy/click_bark; patch the clone's
        // SAY node referencing "Entity". Resolver must hop cloneId → sourceId
        // and pick up the source's roles, not look up the clone's own id.
        var sayComposite = new CompiledTemplateComposite
        {
            TypeName = "SAY",
            Operations = [SetOp("RoleGuid", StringValue("Entity"))],
        };
        var patch = WrapInPatch("ConversationTemplate", "Voymastina/click_bark", sayComposite);
        var clone = new CompiledTemplateClone
        {
            TemplateType = "ConversationTemplate",
            SourceId = "JeanSy/click_bark",
            CloneId = "Voymastina/click_bark",
        };

        var errors = RoleGuidResolver.Apply(
            [patch], [clone], indexedAssets: IndexedClickBark(), new NullLog());

        Assert.Equal(0, errors);
        Assert.Equal(EntityGuid, sayComposite.Operations.Single(o => o.FieldPath == "RoleGuid").Value!.Int32);
    }

    [Fact]
    public void Apply_PathLookupBeatsBareName()
    {
        // Two ConversationTemplate assets share the asset name "click_bark":
        // JeanSy/click_bark (Entity = 1248015120) and Bog/click_bark
        // (Entity = 99). The resolver must match on Path first; otherwise the
        // wrong Guid leaks across speakers.
        var indexed = new List<AssetEntry>
        {
            new()
            {
                Name = "click_bark",
                Path = "JeanSy/click_bark",
                Roles = [new AssetEntryRole { Name = "Entity", Guid = EntityGuid }],
            },
            new()
            {
                Name = "click_bark",
                Path = "Bog/click_bark",
                Roles = [new AssetEntryRole { Name = "Entity", Guid = 99 }],
            },
        };
        var sayComposite = new CompiledTemplateComposite
        {
            TypeName = "SAY",
            Operations = [SetOp("RoleGuid", StringValue("Entity"))],
        };
        var patch = WrapInPatch("ConversationTemplate", "Bog/click_bark", sayComposite);

        RoleGuidResolver.Apply([patch], clones: null, indexed, new NullLog());

        Assert.Equal(99, sayComposite.Operations.Single(o => o.FieldPath == "RoleGuid").Value!.Int32);
    }

    [Fact]
    public void Apply_UnknownRoleEmitsErrorAndReturnsCount()
    {
        var log = new RecordingLog();
        var sayComposite = new CompiledTemplateComposite
        {
            TypeName = "SAY",
            Operations = [SetOp("RoleGuid", StringValue("NotARealRole"))],
        };
        var patch = WrapInPatch("ConversationTemplate", "JeanSy/click_bark", sayComposite);

        var errors = RoleGuidResolver.Apply(
            [patch], clones: null, indexedAssets: IndexedClickBark(), log);

        Assert.Equal(1, errors);
        Assert.Single(log.Errors);
        Assert.Contains("NotARealRole", log.Errors[0]);
        Assert.Contains("Entity", log.Errors[0]); // lists the known role
    }

    [Fact]
    public void Apply_RecursesIntoNestedComposites()
    {
        // VARIATION composite holds a nested SAY composite with a RoleGuid
        // string. Recursion is the contract: missing it means
        // VARIATION/CHOICE bodies fall through to raw catalog validation.
        var inner = new CompiledTemplateComposite
        {
            TypeName = "SAY",
            Operations = [SetOp("RoleGuid", StringValue("Entity"))],
        };
        var outer = new CompiledTemplateComposite
        {
            TypeName = "VARIATION",
            Operations = [SetOp("Inner", CompositeValue(inner))],
        };
        var patch = WrapInPatch("ConversationTemplate", "JeanSy/click_bark", outer);

        RoleGuidResolver.Apply([patch], clones: null, IndexedClickBark(), new NullLog());

        Assert.Equal(EntityGuid, inner.Operations.Single(o => o.FieldPath == "RoleGuid").Value!.Int32);
    }

    [Fact]
    public void Apply_IgnoresAlreadyResolvedInt32Values()
    {
        // Modder wrote `set "RoleGuid" 1248015120` directly. The resolver
        // should pass it through untouched and not double-resolve.
        var sayComposite = new CompiledTemplateComposite
        {
            TypeName = "SAY",
            Operations = [SetOp("RoleGuid", Int32Value(42))],
        };
        var patch = WrapInPatch("ConversationTemplate", "JeanSy/click_bark", sayComposite);

        var errors = RoleGuidResolver.Apply([patch], clones: null, IndexedClickBark(), new NullLog());

        Assert.Equal(0, errors);
        var roleOp = sayComposite.Operations.Single(o => o.FieldPath == "RoleGuid");
        Assert.Equal(CompiledTemplateValueKind.Int32, roleOp.Value!.Kind);
        Assert.Equal(42, roleOp.Value.Int32);
    }

    [Fact]
    public void Apply_NonConversationTemplatePatches_AreSkipped()
    {
        // RoleGuid is only a conversation concept. Other template types
        // happen to have a field literally named RoleGuid? Not in MENACE, but
        // the resolver gates on patch TemplateType to be defensive.
        var composite = new CompiledTemplateComposite
        {
            TypeName = "X",
            Operations = [SetOp("RoleGuid", StringValue("Entity"))],
        };
        var patch = WrapInPatch("EntityTemplate", "hero.elena", composite);

        var errors = RoleGuidResolver.Apply([patch], clones: null, IndexedClickBark(), new NullLog());

        Assert.Equal(0, errors);
        // Untouched: still a string.
        Assert.Equal(CompiledTemplateValueKind.String, composite.Operations[0].Value!.Kind);
    }

    [Fact]
    public void Apply_NullOrEmptyIndex_IsNoOp()
    {
        var sayComposite = new CompiledTemplateComposite
        {
            TypeName = "SAY",
            Operations = [SetOp("RoleGuid", StringValue("Entity"))],
        };
        var patch = WrapInPatch("ConversationTemplate", "JeanSy/click_bark", sayComposite);

        var errors = RoleGuidResolver.Apply([patch], clones: null, indexedAssets: null, new NullLog());

        Assert.Equal(0, errors);
        // No index = caller's responsibility to error. Resolver passes through.
        Assert.Equal(CompiledTemplateValueKind.String, sayComposite.Operations[0].Value!.Kind);
    }

    // --- Helpers ---

    private static List<AssetEntry> IndexedClickBark() =>
    [
        new()
        {
            Name = "click_bark",
            Path = "JeanSy/click_bark",
            Roles =
            [
                new AssetEntryRole { Name = "Entity", Guid = EntityGuid },
                new AssetEntryRole { Name = "Voymastina", Guid = VoymastinaGuid },
            ],
        },
    ];

    private static CompiledTemplatePatch WrapInPatch(
        string templateType, string templateId, CompiledTemplateComposite composite)
        => new()
        {
            TemplateType = templateType,
            TemplateId = templateId,
            Set =
            [
                new()
                {
                    Op = CompiledTemplateOp.Append,
                    FieldPath = "m_SerializedNodes",
                    Value = CompositeValue(composite),
                },
            ],
        };

    private static CompiledTemplateSetOperation SetOp(string fieldPath, CompiledTemplateValue value)
        => new() { Op = CompiledTemplateOp.Set, FieldPath = fieldPath, Value = value };

    private static CompiledTemplateValue StringValue(string v)
        => new() { Kind = CompiledTemplateValueKind.String, String = v };

    private static CompiledTemplateValue Int32Value(int v)
        => new() { Kind = CompiledTemplateValueKind.Int32, Int32 = v };

    private static CompiledTemplateValue CompositeValue(CompiledTemplateComposite composite)
        => new() { Kind = CompiledTemplateValueKind.Composite, Composite = composite };

    private sealed class NullLog : ILogSink
    {
        public void Info(string message) { }
        public void Warning(string message) { }
        public void Error(string message) { }
    }

    private sealed class RecordingLog : ILogSink
    {
        public List<string> Errors { get; } = [];
        public void Info(string message) { }
        public void Warning(string message) { }
        public void Error(string message) => Errors.Add(message);
    }
}
