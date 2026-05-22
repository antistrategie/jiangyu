using Jiangyu.Loader.Templates;
using Xunit;

namespace Il2CppMenace.Conversations
{
    // The registry's lookup uses Type.FullName. Declaring a class here
    // whose namespace + name produce the exact FQN we register lets the
    // FullName-keyed branch be exercised in a CLR-only test, no IL2CPP
    // runtime needed. The real Il2CppInterop-generated ConversationTemplate
    // wrapper is in Assembly-CSharp.dll; the test process never loads it,
    // so there's no CLR-level collision.
    public class ConversationTemplate
    {
    }
}

namespace Jiangyu.Loader.Tests
{
    public class NonDataTemplateIdentityRegistryTests
    {
        [Fact]
        public void GetIdentityField_ReturnsPath_ForConversationTemplateShortName()
        {
            var field = NonDataTemplateIdentityRegistry.GetIdentityField("ConversationTemplate", resolvedType: null);
            Assert.Equal("Path", field);
        }

        [Fact]
        public void GetIdentityField_ReturnsPath_ForConversationTemplateFullName()
        {
            // The catalogue may pass either the short name or the Il2Cpp
            // FQN depending on the resolution path. A fixture type whose
            // FullName matches the wrapper FQN exercises the FullName
            // branch without needing IL2CPP at runtime.
            var fakeType = typeof(Il2CppMenace.Conversations.ConversationTemplate);
            Assert.Equal("Il2CppMenace.Conversations.ConversationTemplate", fakeType.FullName);

            var field = NonDataTemplateIdentityRegistry.GetIdentityField(
                templateTypeName: "",
                resolvedType: fakeType);
            Assert.Equal("Path", field);
        }

        [Fact]
        public void GetIdentityField_ReturnsNull_ForUnregisteredType()
        {
            var field = NonDataTemplateIdentityRegistry.GetIdentityField(
                templateTypeName: "SomeRandomTemplate",
                resolvedType: null);
            Assert.Null(field);
        }

        [Fact]
        public void GetIdentityField_NullsOnNullInputs()
        {
            Assert.Null(NonDataTemplateIdentityRegistry.GetIdentityField(null!, null));
        }
    }
}
