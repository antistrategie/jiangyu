using Jiangyu.Loader.Bundles;
using Xunit;

namespace Jiangyu.Loader.Tests;

/// <summary>
/// Materials inside an addition bundle arrive on one of three kinds of shader:
/// an extraction stub of a shader the running game owns, a shader the mod
/// authored and shipped in the bundle, or a stub of a shader nothing provides.
/// The first is rebound onto the runtime shader, the second is left alone, and
/// the third is reported. These tests pin which name lands where.
/// </summary>
public class BundleShaderClassificationTests
{
    // Expected action passed by name: the enum is internal to the loader, so it
    // cannot appear in a public xUnit test signature.
    private static string Classify(string shaderName, bool resolvedAtRuntime)
        => BundleReplacementCatalog.ClassifyShader(shaderName, resolvedAtRuntime).ToString();

    // Every shader name an AssetRipper extraction of MENACE actually produces.
    [Theory]
    [InlineData("Menace/character")]
    [InlineData("Menace/building")]
    [InlineData("Menace/lit_highlight")]
    [InlineData("HDRP/Unlit")]
    [InlineData("Shader Graphs/ParticleLitSoft")]
    [InlineData("Shader Graphs/UnlitVFX")]
    [InlineData("Hidden/HDRP/FallbackError")]
    public void ExtractionStubsRebindWhenTheRuntimeOwnsTheName(string shaderName)
    {
        Assert.Equal("Rebind", Classify(shaderName, resolvedAtRuntime: true));
    }

    // A mod-authored shader travels in the bundle already compiled, so the
    // material is already on the right shader and must not be touched.
    [Theory]
    [InlineData("Womenace/DollToon")]
    [InlineData("Womenace/DollToon Outline")]
    [InlineData("MyMod/Custom Lit")]
    [InlineData("NoNamespace")]
    public void ModAuthoredShadersAreKept(string shaderName)
    {
        Assert.Equal("KeepModShipped", Classify(shaderName, resolvedAtRuntime: false));
    }

    // An engine or MENACE namespace that the runtime cannot resolve can only be
    // a stub of a shader this build does not ship. Keeping it would render the
    // stub's dummy pass and hide the mis-authoring.
    [Theory]
    [InlineData("Universal Render Pipeline/Lit")]
    [InlineData("Menace/removed_shader")]
    [InlineData("HDRP/Lit")]
    [InlineData("Shader Graphs/SomethingElse")]
    [InlineData("Hidden/Core/FallbackError")]
    public void UnresolvableExtractedNamespacesAreReported(string shaderName)
    {
        Assert.Equal("BrokenStub", Classify(shaderName, resolvedAtRuntime: false));
    }

    // The builtin error shader resolves through Shader.Find, so the name test
    // has to beat the resolved case or a dangling bake-time reference counts as
    // a successful rebind while rendering magenta in-game.
    [Fact]
    public void TheInternalErrorShaderIsBrokenEvenThoughItResolves()
    {
        Assert.Equal("BrokenStub", Classify("Hidden/InternalErrorShader", resolvedAtRuntime: true));
        Assert.Equal("BrokenStub", Classify("Hidden/InternalErrorShader", resolvedAtRuntime: false));
    }

    // A mod is free to name a shader into the game's namespace, and if the
    // runtime owns that name the rebind is the correct outcome: the game's
    // shader is the real one and the bundled copy is the stub.
    [Fact]
    public void ResolvedNamesRebindRegardlessOfNamespace()
    {
        Assert.Equal("Rebind", Classify("Womenace/DollToon", resolvedAtRuntime: true));
    }
}
