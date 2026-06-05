using System.Reflection;
using Jiangyu.Core.Config;

namespace Jiangyu.Core.Validation;

/// <summary>
/// Loads the game's Il2CppInterop Assembly-CSharp for read-only reflection (no
/// execution) through a <see cref="MetadataLoadContext"/>, resolving against the
/// game's interop assemblies, MelonLoader's net6, and this runtime. Shared by the
/// codegen tools so each resolves its manifest against the live game surface and
/// fails the build when that surface moves.
/// </summary>
public static class GameAssembly
{
    /// <summary>A loaded game assembly and the context that owns it.</summary>
    public sealed class Loaded : IDisposable
    {
        private readonly MetadataLoadContext _context;

        internal Loaded(MetadataLoadContext context, Assembly assembly)
        {
            _context = context;
            Assembly = assembly;
        }

        /// <summary>The loaded Assembly-CSharp, for reflection only.</summary>
        public Assembly Assembly { get; }

        public void Dispose() => _context.Dispose();
    }

    /// <summary>
    /// Load Assembly-CSharp for reflection. Returns null and sets <paramref name="error"/>
    /// when the game path or the assembly cannot be found.
    /// </summary>
    public static Loaded? Load(out string? error)
    {
        var (gameDir, _) = GlobalConfig.ResolveGamePath(GlobalConfig.Load());
        if (gameDir is null)
        {
            error = "no game path in global config.";
            return null;
        }

        var il2cppDir = Path.Combine(gameDir, "MelonLoader", "Il2CppAssemblies");
        var asmCSharp = Path.Combine(il2cppDir, "Assembly-CSharp.dll");
        if (!File.Exists(asmCSharp))
        {
            error = $"Assembly-CSharp.dll not found at {asmCSharp}";
            return null;
        }

        var resolverPaths = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var dir in new[] { il2cppDir, Path.Combine(gameDir, "MelonLoader", "net6"), Path.GetDirectoryName(typeof(object).Assembly.Location)! })
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var dll in Directory.EnumerateFiles(dir, "*.dll"))
                if (seen.Add(Path.GetFileName(dll))) resolverPaths.Add(dll);
        }

        var mlc = new MetadataLoadContext(new PathAssemblyResolver(resolverPaths));
        error = null;
        return new Loaded(mlc, mlc.LoadFromAssemblyPath(asmCSharp));
    }
}
