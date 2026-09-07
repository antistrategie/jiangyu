using System.Diagnostics.CodeAnalysis;
using AsmResolver.DotNet;
using AsmResolver.PE.DotNet.Metadata.Tables;
using AssetRipper.Import.Structure.Assembly;
using AssetRipper.Import.Structure.Assembly.Managers;
using AssetRipper.Import.Structure.Platforms;
using AssetRipper.IO.Files;
using AssetRipper.Primitives;
using AssetRipper.SerializationLogic;
using Jiangyu.Core.Assets;

namespace Jiangyu.Core.Tests.Assets;

public class AssemblyTypeResolverTests
{
    [Fact]
    public void For_ResolvesTopLevelAndNestedTypesByFullName()
    {
        // The Odin binder spells nested types Outer+Inner, as
        // TypeDefinition.FullName does, so both shapes must resolve.
        var module = new ModuleDefinition("TestModule");
        var outer = new TypeDefinition("Ns", "Outer", TypeAttributes.Public);
        var inner = new TypeDefinition(null, "Inner", TypeAttributes.NestedPublic);
        outer.NestedTypes.Add(inner);
        var plain = new TypeDefinition("Ns", "Plain", TypeAttributes.Public);
        module.TopLevelTypes.Add(outer);
        module.TopLevelTypes.Add(plain);
        using var manager = new FakeAssemblyManager(WrapInAssembly(module));

        var resolve = AssemblyTypeResolver.For(manager);

        Assert.Same(outer, resolve("Ns.Outer"));
        Assert.Same(inner, resolve("Ns.Outer+Inner"));
        Assert.Same(plain, resolve("Ns.Plain"));
        Assert.Null(resolve("Plain"));
        Assert.Null(resolve("Ns.Missing"));
    }

    [Fact]
    public void Warm_BuildsTheSameMapForIsUsed()
    {
        var module = new ModuleDefinition("TestModule");
        var plain = new TypeDefinition("Ns", "Plain", TypeAttributes.Public);
        module.TopLevelTypes.Add(plain);
        using var manager = new FakeAssemblyManager(WrapInAssembly(module));

        var stats = AssemblyTypeResolver.Warm(manager);

        // Every top-level type counts, including the module's own <Module> type.
        Assert.Equal(module.TopLevelTypes.Count, stats.Types);
        Assert.Equal(0, stats.SkippedAssemblies);
        Assert.Same(plain, AssemblyTypeResolver.For(manager)("Ns.Plain"));
        Assert.Same(plain, AssemblyTypeResolver.For(manager)("Ns.Plain"));
        // Warm built the map once; the resolvers reuse it.
        Assert.Equal(1, manager.GetAssembliesCalls);
    }

    [Fact]
    public void Warm_SkipsAnAssemblyWhoseTypesCannotBeRead()
    {
        // One unreadable assembly loses only its own types: the good one
        // still resolves, and the stats say what was skipped.
        var module = new ModuleDefinition("GoodModule");
        var plain = new TypeDefinition("Ns", "Plain", TypeAttributes.Public);
        module.TopLevelTypes.Add(plain);
        using var manager = new FakeAssemblyManager(WrapInAssembly(module), new UnreadableAssembly());

        var stats = AssemblyTypeResolver.Warm(manager);

        Assert.Equal(module.TopLevelTypes.Count, stats.Types);
        Assert.Equal(1, stats.SkippedAssemblies);
        Assert.Same(plain, AssemblyTypeResolver.For(manager)("Ns.Plain"));
    }

    private sealed class UnreadableAssembly() : AssemblyDefinition("Unreadable", new Version(1, 0, 0, 0))
    {
        protected override IList<ModuleDefinition> GetModules() => throw new IOException("bad image");
    }

    private static AssemblyDefinition WrapInAssembly(ModuleDefinition module)
    {
        var assembly = new AssemblyDefinition("TestAssembly", new Version(1, 0, 0, 0));
        assembly.Modules.Add(module);
        return assembly;
    }

    // Only GetAssemblies is exercised; every other member is unreachable from the resolver.
    private sealed class FakeAssemblyManager(params AssemblyDefinition[] assemblies) : IAssemblyManager
    {
        public int GetAssembliesCalls { get; private set; }

        public IEnumerable<AssemblyDefinition> GetAssemblies()
        {
            GetAssembliesCalls++;
            return assemblies;
        }

        public bool IsSet => true;
        public ScriptingBackend ScriptingBackend => ScriptingBackend.Unknown;
        public void Dispose() { }

        public void Initialize(PlatformGameStructure gameStructure) => throw new NotSupportedException();
        public void Load(string filePath, FileSystem fileSystem) => throw new NotSupportedException();
        public void Add(AssemblyDefinition assembly) => throw new NotSupportedException();
        public void Read(Stream stream, string fileName) => throw new NotSupportedException();
        public void Unload(string fileName) => throw new NotSupportedException();
        public bool IsAssemblyLoaded(string assembly) => throw new NotSupportedException();
        public bool IsPresent(ScriptIdentifier scriptID) => throw new NotSupportedException();
        public bool IsValid(ScriptIdentifier scriptID) => throw new NotSupportedException();
        public bool TryGetSerializableType(
            ScriptIdentifier scriptID,
            UnityVersion version,
            [NotNullWhen(true)] out SerializableType? scriptType,
            [NotNullWhen(false)] out string? failureReason) => throw new NotSupportedException();
        public TypeDefinition GetTypeDefinition(ScriptIdentifier scriptID) => throw new NotSupportedException();
        public ScriptIdentifier GetScriptID(string assembly, string @namespace, string name) => throw new NotSupportedException();
        public Stream GetStreamForAssembly(AssemblyDefinition assembly) => throw new NotSupportedException();
        public void ClearStreamCache() => throw new NotSupportedException();
    }
}
