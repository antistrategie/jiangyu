using Jiangyu.Core.Templates;
using Jiangyu.Core.Tests.Templates.Fixtures.Gameplay;
using Jiangyu.Shared.Templates;

// Fixture types in the IL2CPP-aligned namespace so the auto-filler's
// FQN-based catalog lookup finds them. The real wrapper FullNames are
// "Il2CppMenace.Conversations.BaseConversationNode" and
// "Il2CppMenace.Conversations.ConversationNodeContainer"; declaring CLR
// classes with matching FullNames lets the test exercise the production
// lookup without loading Assembly-CSharp at test runtime.
namespace Il2CppMenace.Conversations
{
    public abstract class BaseConversationNode
    {
        public int Guid { get; set; }
    }

    public class ConversationNodeContainer
    {
        public int Guid { get; set; }
    }

    public class FakeSayNode : BaseConversationNode
    {
        public string? Text { get; set; }
    }
}

namespace Jiangyu.Core.Tests.Templates
{
    public class NodeGuidAutoFillerTests
    {
        private static string FixtureAssemblyPath =>
            typeof(FixtureEntity).Assembly.Location;

        private static TemplateTypeCatalog Load() => TemplateTypeCatalog.Load(FixtureAssemblyPath);

        [Fact]
        public void Apply_InjectsGuidOnConversationNodeComposite()
        {
            using var catalog = Load();
            var composite = new CompiledTemplateComposite
            {
                TypeName = "Il2CppMenace.Conversations.FakeSayNode",
                Operations = [SetOp("Text", StringValue("hello"))],
            };
            var patch = WrapInPatch("ConversationTemplate", "JeanSy/test", "m_SerializedNodes", composite);

            NodeGuidAutoFiller.Apply([patch], catalog);

            var guidOp = composite.Operations.First(o => o.FieldPath == "Guid");
            Assert.Equal(CompiledTemplateOp.Set, guidOp.Op);
            Assert.Equal(CompiledTemplateValueKind.Int32, guidOp.Value!.Kind);
            Assert.NotEqual(0, guidOp.Value.Int32);
        }

        [Fact]
        public void Apply_SkipsWhenModderSetGuidExplicitly()
        {
            using var catalog = Load();
            var composite = new CompiledTemplateComposite
            {
                TypeName = "Il2CppMenace.Conversations.FakeSayNode",
                Operations =
                [
                    SetOp("Guid", Int32Value(42)),
                    SetOp("Text", StringValue("hi")),
                ],
            };
            var patch = WrapInPatch("ConversationTemplate", "x", "m_SerializedNodes", composite);

            NodeGuidAutoFiller.Apply([patch], catalog);

            var guidOps = composite.Operations.Where(o => o.FieldPath == "Guid").ToList();
            Assert.Single(guidOps);
            Assert.Equal(42, guidOps[0].Value!.Int32);
        }

        [Fact]
        public void Apply_DeterministicAcrossRebuilds()
        {
            // Same scope + counter = same Guid. Critical for save-state stability:
            // a modder rebuilding their KDL must not produce different Guids
            // than the last compile, otherwise per-node save references break.
            using var catalog = Load();
            var first = MakeNodeAt("JeanSy/test", 0);
            var second = MakeNodeAt("JeanSy/test", 0);
            NodeGuidAutoFiller.Apply([first.patch], catalog);
            NodeGuidAutoFiller.Apply([second.patch], catalog);

            Assert.Equal(GuidOf(first.composite), GuidOf(second.composite));
        }

        [Fact]
        public void Apply_DistinctGuidsPerNodeInOneScope()
        {
            using var catalog = Load();
            var node1 = new CompiledTemplateComposite
            {
                TypeName = "Il2CppMenace.Conversations.FakeSayNode",
                Operations = [SetOp("Text", StringValue("a"))],
            };
            var node2 = new CompiledTemplateComposite
            {
                TypeName = "Il2CppMenace.Conversations.FakeSayNode",
                Operations = [SetOp("Text", StringValue("b"))],
            };
            var patch = new CompiledTemplatePatch
            {
                TemplateType = "ConversationTemplate",
                TemplateId = "JeanSy/test",
                Set =
                [
                    AppendOp("m_SerializedNodes", CompositeValue(node1)),
                    AppendOp("m_SerializedNodes", CompositeValue(node2)),
                ],
            };

            NodeGuidAutoFiller.Apply([patch], catalog);

            Assert.NotEqual(GuidOf(node1), GuidOf(node2));
        }

        [Fact]
        public void Apply_DistinctGuidsAcrossScopes()
        {
            // Two patches with different TemplateId. Node Guids must differ
            // so a save-state reference into one conversation doesn't collide
            // with the other.
            using var catalog = Load();
            var first = MakeNodeAt("JeanSy/test", 0);
            var second = MakeNodeAt("Bog/test", 0);
            NodeGuidAutoFiller.Apply([first.patch], catalog);
            NodeGuidAutoFiller.Apply([second.patch], catalog);

            Assert.NotEqual(GuidOf(first.composite), GuidOf(second.composite));
        }

        [Fact]
        public void Apply_IgnoresNonNodeComposites()
        {
            using var catalog = Load();
            var composite = new CompiledTemplateComposite
            {
                TypeName = "FixtureEntity",
                Operations = [SetOp("Name", StringValue("x"))],
            };
            var patch = WrapInPatch("FixtureEntity", "x", "Names", composite);

            NodeGuidAutoFiller.Apply([patch], catalog);

            Assert.DoesNotContain(composite.Operations, o => o.FieldPath == "Guid");
        }

        [Fact]
        public void Apply_NullPatchesDoesNotThrow()
        {
            using var catalog = Load();
            NodeGuidAutoFiller.Apply(null, catalog);
        }

        // --- Helpers ---

        private static (CompiledTemplatePatch patch, CompiledTemplateComposite composite) MakeNodeAt(
            string templateId, int index)
        {
            var composite = new CompiledTemplateComposite
            {
                TypeName = "Il2CppMenace.Conversations.FakeSayNode",
                Operations = [SetOp("Text", StringValue($"node_{index}"))],
            };
            return (WrapInPatch("ConversationTemplate", templateId, "m_SerializedNodes", composite), composite);
        }

        private static int GuidOf(CompiledTemplateComposite composite)
            => composite.Operations.First(o => o.FieldPath == "Guid").Value!.Int32!.Value;

        private static CompiledTemplatePatch WrapInPatch(
            string templateType, string templateId, string topField, CompiledTemplateComposite composite)
            => new()
            {
                TemplateType = templateType,
                TemplateId = templateId,
                Set = [AppendOp(topField, CompositeValue(composite))],
            };

        private static CompiledTemplateSetOperation SetOp(string fieldPath, CompiledTemplateValue value)
            => new() { Op = CompiledTemplateOp.Set, FieldPath = fieldPath, Value = value };

        private static CompiledTemplateSetOperation AppendOp(string fieldPath, CompiledTemplateValue value)
            => new() { Op = CompiledTemplateOp.Append, FieldPath = fieldPath, Value = value };

        private static CompiledTemplateValue StringValue(string v)
            => new() { Kind = CompiledTemplateValueKind.String, String = v };

        private static CompiledTemplateValue Int32Value(int v)
            => new() { Kind = CompiledTemplateValueKind.Int32, Int32 = v };

        private static CompiledTemplateValue CompositeValue(CompiledTemplateComposite composite)
            => new() { Kind = CompiledTemplateValueKind.Composite, Composite = composite };
    }
}
