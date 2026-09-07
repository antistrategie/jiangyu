using System;
using Jiangyu.Codegen.Handlers;
using Xunit;

namespace UnityEngine
{
    public class Object { }
    public class ScriptableObject : Object { }
}

namespace Sirenix.OdinInspector
{
    public class SerializedScriptableObject : UnityEngine.ScriptableObject { }
}

namespace Jiangyu.Codegen.Tests.WalkFixtures
{
    public class TemplateBase : Sirenix.OdinInspector.SerializedScriptableObject { }
    public class Handler : TemplateBase { }
    public interface IProvider { }
    public class OdinProvider : Sirenix.OdinInspector.SerializedScriptableObject, IProvider { }
    public class PlainProvider : IProvider { }
}

namespace Jiangyu.Codegen.Tests.WalkFixtures.A
{
    public class Dup { }
}

namespace Jiangyu.Codegen.Tests.WalkFixtures.B
{
    public class Dup { }
}

namespace Jiangyu.Codegen.Tests
{
    using WalkFixtures;

    public class HandlerTypeWalkTests
    {
        [Fact]
        public void DeclaringTypeNames_keeps_the_chain_up_to_an_inclusive_base()
        {
            var names = HandlerTypeWalk.DeclaringTypeNames(typeof(Handler), typeof(TemplateBase), inclusiveBase: true);
            Assert.Equal(2, names.Count);
            Assert.Contains(typeof(Handler).FullName!, names);
            Assert.Contains(typeof(TemplateBase).FullName!, names);
            Assert.DoesNotContain(typeof(Sirenix.OdinInspector.SerializedScriptableObject).FullName!, names);
        }

        [Fact]
        public void DeclaringTypeNames_drops_an_exclusive_base()
        {
            var names = HandlerTypeWalk.DeclaringTypeNames(typeof(Handler), typeof(TemplateBase), inclusiveBase: false);
            Assert.Equal([typeof(Handler).FullName!], names);
        }

        [Fact]
        public void DeclaringTypeNames_stops_at_the_odin_root_when_the_stop_base_is_an_interface()
        {
            // An interface never appears in the class chain, so the walk must
            // end at SerializedScriptableObject rather than listing Odin's and
            // Unity's own members as authoring surface.
            var names = HandlerTypeWalk.DeclaringTypeNames(typeof(OdinProvider), typeof(IProvider), inclusiveBase: false);
            Assert.Equal([typeof(OdinProvider).FullName!], names);
        }

        [Fact]
        public void DeclaringTypeNames_stops_at_the_clr_root_for_a_plain_class()
        {
            var names = HandlerTypeWalk.DeclaringTypeNames(typeof(PlainProvider), typeof(IProvider), inclusiveBase: false);
            Assert.Equal([typeof(PlainProvider).FullName!], names);
        }

        [Fact]
        public void DisplayName_uses_the_short_name_unless_a_sibling_shares_it()
        {
            Type[] siblings = [typeof(WalkFixtures.A.Dup), typeof(WalkFixtures.B.Dup), typeof(Handler)];
            Assert.Equal("Handler", HandlerTypeWalk.DisplayName(typeof(Handler), siblings));
            Assert.Equal(typeof(WalkFixtures.A.Dup).FullName, HandlerTypeWalk.DisplayName(typeof(WalkFixtures.A.Dup), siblings));
            Assert.Equal(typeof(WalkFixtures.B.Dup).FullName, HandlerTypeWalk.DisplayName(typeof(WalkFixtures.B.Dup), siblings));
        }
    }
}
