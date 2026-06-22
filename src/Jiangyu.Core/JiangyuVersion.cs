using System.Reflection;
using Jiangyu.Shared;

namespace Jiangyu.Core;

/// <summary>
/// The Jiangyu toolchain version, read from the compiler assembly's MinVer-stamped
/// informational version. The compiler writes it into each compiled mod's manifest as
/// <c>compiledForJiangyu</c>, so the loader can warn when a mod was built against a newer
/// Jiangyu than the one installed.
/// </summary>
public static class JiangyuVersion
{
    /// <summary>The clean version string (build metadata after <c>+</c> dropped), e.g.
    /// <c>1.2.3</c> or <c>1.2.3-alpha.1</c>. Falls back to <c>0.0.0</c> when the assembly
    /// carries no informational version.</summary>
    public static string Current { get; } = Resolve();

    private static string Resolve()
    {
        var info = typeof(JiangyuVersion).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrWhiteSpace(info))
            return "0.0.0";
        return SemVer.TryParse(info, out var version) ? version.ToString() : info!;
    }
}
