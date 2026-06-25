using System.Diagnostics;
using System.Reflection;
using System.Text;
using Jiangyu.Core.Abstractions;

namespace Jiangyu.Core.Code;

/// <summary>
/// Builds a mod's <c>code/</c> C# project during compile, injecting the game and
/// SDK paths from global config so a fresh clone compiles without running
/// <c>jiangyu code sync</c> first. Reads the built DLL reflection-only to surface
/// its <c>[JiangyuType]</c> names for the <c>type="ns:Name"</c> cross-check.
/// </summary>
public sealed class CodeBuildService(ILogSink log)
{
    /// <summary>
    /// Builds <c>&lt;projectDir&gt;/code/</c> if it contains a project. Returns null
    /// when there is no code project to build, a failed result on build error, or a
    /// built result carrying the output DLLs and their <c>[JiangyuType]</c> names.
    /// </summary>
    public async Task<CodeBuildResult?> BuildAsync(string projectDir, string gameDir, string? sdkDir, bool devSources = true)
    {
        var codeDir = Path.Combine(projectDir, "code");
        if (!Directory.Exists(codeDir)
            || !Directory.EnumerateFiles(codeDir, "*.csproj", SearchOption.TopDirectoryOnly).Any())
            return null;

        // A scaffolded code/ ships the .csproj and build props before any C# is written,
        // so the .csproj alone does not mean there is code to build: the SDK build would
        // still emit an empty assembly that there is no reason to package. Treat "csproj
        // but no .cs source" the same as "no code project" and skip the build.
        if (!HasCSharpSources(codeDir))
        {
            log.Info("  Skipping code/ build: no .cs source files.");
            return null;
        }

        if (string.IsNullOrEmpty(sdkDir))
            return CodeBuildResult.Failed(
                "code/ present but the Jiangyu SDK path is unresolved. Set \"sdk\" in your global config, or build src/Jiangyu.Sdk.");

        var binDir = Path.Combine(codeDir, "bin", "Release");
        try { if (Directory.Exists(binDir)) Directory.Delete(binDir, recursive: true); }
        catch { /* a locked stale output is non-fatal; the build overwrites it */ }

        // A release build must force a full code rebuild: MSBuild's up-to-date check keys off file
        // timestamps, not the JiangyuDev property, so after a dev build it would reuse the cached DLL
        // and leave the *.Dev.cs sources in. Dropping obj/ guarantees the dev verbs are recompiled out.
        // (A release also clears obj for the next dev build, so that one rebuilds the dev sources back.)
        if (!devSources)
        {
            var objDir = Path.Combine(codeDir, "obj");
            try { if (Directory.Exists(objDir)) Directory.Delete(objDir, recursive: true); }
            catch { /* non-fatal */ }
        }

        log.Info("  Building code/ ...");
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = codeDir,
        };
        psi.ArgumentList.Add("build");
        psi.ArgumentList.Add(codeDir);
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("Release");
        psi.ArgumentList.Add("--nologo");
        psi.ArgumentList.Add($"-p:GameDir={gameDir}");
        psi.ArgumentList.Add($"-p:JiangyuSdkDir={sdkDir}");
        // Dev builds include *.Dev.cs sources (dev verbs, debug probes); a release build leaves
        // JiangyuDev unset so Directory.Build.props strips them from the shipped DLL.
        if (devSources)
            psi.ArgumentList.Add("-p:JiangyuDev=true");

        var output = new StringBuilder();
        var outputLock = new object();
        using var process = new Process { StartInfo = psi };
        // stdout and stderr fire on separate threadpool threads, so guard the shared
        // builder or interleaved build output corrupts the captured failure text.
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (outputLock) output.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (outputLock) output.AppendLine(e.Data); };
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await process.WaitForExitAsync();

        if (process.ExitCode != 0)
            return CodeBuildResult.Failed($"code/ build failed (dotnet exit {process.ExitCode}):\n{output}");

        var dlls = Directory.Exists(binDir)
            ? Directory.EnumerateFiles(binDir, "*.dll", SearchOption.TopDirectoryOnly).ToList()
            : new List<string>();
        if (dlls.Count == 0)
            return CodeBuildResult.Failed($"code/ build succeeded but produced no DLL under {binDir}.");

        var typeNames = ReadJiangyuTypeNames(dlls, gameDir, sdkDir);
        log.Info($"  Built code/ -> {dlls.Count} dll(s); [JiangyuType]: {(typeNames.Count == 0 ? "(none)" : string.Join(", ", typeNames))}");
        return CodeBuildResult.Built(dlls, typeNames);
    }

    // True when code/ holds at least one hand-written C# source. The build's own bin/ and
    // obj/ are ignored: obj/ carries generated .cs (AssemblyInfo, global usings) that exist
    // even for an empty project, so counting them would defeat the check.
    private static bool HasCSharpSources(string codeDir)
    {
        foreach (var path in Directory.EnumerateFiles(codeDir, "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(codeDir, path);
            var top = relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries)[0];
            if (top is "bin" or "obj")
                continue;
            return true;
        }
        return false;
    }

    private HashSet<string> ReadJiangyuTypeNames(IReadOnlyList<string> dllPaths, string gameDir, string sdkDir)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        var gameRoot = NormaliseDir(gameDir);

        JiangyuTypeMetadataReader.Read(dllPaths, gameDir, sdkDir,
            visit: (type, attr, bareName) =>
            {
                names.Add(bareName);
                if (!DerivesFromGameType(type, gameRoot) && !AttributeDeclaresInterfaces(attr))
                    log.Warning(
                        $"[JiangyuType] '{bareName}' does not derive from a game type and declares no Interfaces, so the game "
                        + "cannot construct or dispatch it. Derive a game base (a condition, handler, or template type), or list "
                        + "the game interface it satisfies via [JiangyuType(Interfaces = ...)].");
            },
            onError: detail => log.Warning(
                $"could not read [JiangyuType] names from the built DLL ({detail}); skipping the type= cross-check."));

        return names;
    }

    // Injectable types derive a base the game constructs and dispatches through; a type
    // rooted only in plain managed classes has no game vtable slot. The structural test
    // is where the base type is defined: a game/Unity/IL2CPP base lives in an assembly
    // shipped under the game install (Il2CppMenace, the UnityEngine proxies, Il2CppSystem),
    // while a plain managed base resolves to the BCL or the SDK. Interface-satisfying
    // types (declared on the attribute) are handled by the caller's separate check.
    private static bool DerivesFromGameType(Type type, string? gameRoot)
    {
        // Without the game directory the origin cannot be checked, so do not warn.
        if (gameRoot is null)
            return true;

        for (var b = type.BaseType; b is not null; b = b.BaseType)
        {
            var location = b.Assembly.Location;
            if (!string.IsNullOrEmpty(location)
                && Path.GetFullPath(location).StartsWith(gameRoot, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static string? NormaliseDir(string? dir)
    {
        if (string.IsNullOrEmpty(dir))
            return null;
        var full = Path.GetFullPath(dir);
        return full.EndsWith(Path.DirectorySeparatorChar) ? full : full + Path.DirectorySeparatorChar;
    }

    private static bool AttributeDeclaresInterfaces(CustomAttributeData attr)
    {
        foreach (var named in attr.NamedArguments)
            if (named.MemberName == "Interfaces"
                && named.TypedValue.Value is System.Collections.IList { Count: > 0 })
                return true;
        return false;
    }
}

public sealed class CodeBuildResult
{
    public bool Success { get; private init; }
    public string? Error { get; private init; }
    public IReadOnlyList<string> DllPaths { get; private init; } = Array.Empty<string>();
    public IReadOnlySet<string> JiangyuTypeNames { get; private init; } = new HashSet<string>();

    public static CodeBuildResult Built(IReadOnlyList<string> dlls, HashSet<string> typeNames)
        => new() { Success = true, DllPaths = dlls, JiangyuTypeNames = typeNames };

    public static CodeBuildResult Failed(string error)
        => new() { Success = false, Error = error };
}
