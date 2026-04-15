using AsmResolver.DotNet;
using Jiangyu.Core.Assets;

namespace Jiangyu.Core.Tests.Assets;

public class TemplateClassifierInheritanceTests
{
    [Fact]
    public void FindTemplateAncestor_ReturnsFirstTemplateNamedAncestor()
    {
        // DataTemplate → TileEffectTemplate → ApplySkillTileEffect
        var module = new ModuleDefinition("TestModule");

        var dataTemplate = new TypeDefinition("Menace", "DataTemplate", AsmResolver.PE.DotNet.Metadata.Tables.TypeAttributes.Public);
        module.TopLevelTypes.Add(dataTemplate);

        var tileEffectTemplate = new TypeDefinition("Menace.Tactical", "TileEffectTemplate", AsmResolver.PE.DotNet.Metadata.Tables.TypeAttributes.Public);
        tileEffectTemplate.BaseType = dataTemplate;
        module.TopLevelTypes.Add(tileEffectTemplate);

        var concreteType = new TypeDefinition("Menace.Tactical", "ApplySkillTileEffect", AsmResolver.PE.DotNet.Metadata.Tables.TypeAttributes.Public);
        concreteType.BaseType = tileEffectTemplate;
        module.TopLevelTypes.Add(concreteType);

        string? ancestor = TemplateClassifier.FindTemplateAncestor(concreteType);

        Assert.Equal("TileEffectTemplate", ancestor);
    }

    [Fact]
    public void FindTemplateAncestor_ReturnsNull_WhenNoTemplateNamedAncestorExists()
    {
        var module = new ModuleDefinition("TestModule");

        var baseType = new TypeDefinition("UnityEngine", "MonoBehaviour", AsmResolver.PE.DotNet.Metadata.Tables.TypeAttributes.Public);
        module.TopLevelTypes.Add(baseType);

        var runtimeComponent = new TypeDefinition("Menace", "HDAdditionalLightData", AsmResolver.PE.DotNet.Metadata.Tables.TypeAttributes.Public);
        runtimeComponent.BaseType = baseType;
        module.TopLevelTypes.Add(runtimeComponent);

        string? ancestor = TemplateClassifier.FindTemplateAncestor(runtimeComponent);

        Assert.Null(ancestor);
    }

    [Fact]
    public void FindTemplateAncestor_ReturnsNull_WhenTypeHasNoBaseType()
    {
        var module = new ModuleDefinition("TestModule");

        var rootType = new TypeDefinition("", "RootType", AsmResolver.PE.DotNet.Metadata.Tables.TypeAttributes.Public);
        module.TopLevelTypes.Add(rootType);

        string? ancestor = TemplateClassifier.FindTemplateAncestor(rootType);

        Assert.Null(ancestor);
    }

    [Fact]
    public void FindTemplateAncestor_ReturnsImmediateParent_WhenParentIsTemplateNamed()
    {
        // SkillEventHandlerTemplate → Attack
        var module = new ModuleDefinition("TestModule");

        var handlerBase = new TypeDefinition("Menace.Tactical", "SkillEventHandlerTemplate", AsmResolver.PE.DotNet.Metadata.Tables.TypeAttributes.Public);
        module.TopLevelTypes.Add(handlerBase);

        var attack = new TypeDefinition("Menace.Tactical", "Attack", AsmResolver.PE.DotNet.Metadata.Tables.TypeAttributes.Public);
        attack.BaseType = handlerBase;
        module.TopLevelTypes.Add(attack);

        string? ancestor = TemplateClassifier.FindTemplateAncestor(attack);

        Assert.Equal("SkillEventHandlerTemplate", ancestor);
    }

    [Fact]
    public void FindTemplateAncestor_SkipsTypeItself_EvenIfTemplateNamed()
    {
        // Verifies the walk starts from the parent, not the type itself.
        var module = new ModuleDefinition("TestModule");

        var scriptableObject = new TypeDefinition("UnityEngine", "ScriptableObject", AsmResolver.PE.DotNet.Metadata.Tables.TypeAttributes.Public);
        module.TopLevelTypes.Add(scriptableObject);

        var templateType = new TypeDefinition("Menace", "EntityTemplate", AsmResolver.PE.DotNet.Metadata.Tables.TypeAttributes.Public);
        templateType.BaseType = scriptableObject;
        module.TopLevelTypes.Add(templateType);

        // EntityTemplate's parent is ScriptableObject — not Template-named.
        string? ancestor = TemplateClassifier.FindTemplateAncestor(templateType);

        Assert.Null(ancestor);
    }
}
