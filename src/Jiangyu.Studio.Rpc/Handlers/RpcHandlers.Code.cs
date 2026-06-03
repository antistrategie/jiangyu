using System.Text.Json;
using System.Text.Json.Serialization;
using Jiangyu.Core.Abstractions;
using Jiangyu.Core.Code;
using Jiangyu.Core.Config;
using Jiangyu.Core.Models;
using Jiangyu.Core.Rpc;
using Jiangyu.Shared.Templates;

namespace Jiangyu.Studio.Rpc;

public static partial class RpcHandlers
{
    private static readonly JsonSerializerOptions CodeJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    [McpTool("jiangyu_code_sync",
        "Scaffold or refresh code/ for C# modding under the open project. Idempotent: creates the project on first run, refreshes the Jiangyu-managed build files and local.props on re-run, and preserves your .csproj and source. Returns {createdCount, updatedCount, preservedCount, sdkResolved}.")]
    internal static JsonElement CodeSync(JsonElement? __)
    {
        var projectRoot = RpcContext.ProjectRoot ?? throw new InvalidOperationException("No project open.");

        var config = GlobalConfig.Load();
        var (gameDir, _) = GlobalConfig.ResolveGamePath(config);
        var (sdkDir, _) = GlobalConfig.ResolveSdkDir(config);

        var result = new CodeProjectScaffolder(NullLogSink.Instance).Init(projectRoot, gameDir, sdkDir);

        return JsonSerializer.SerializeToElement(new CodeSyncResult
        {
            CreatedCount = result.CreatedFiles.Count,
            UpdatedCount = result.OverwrittenFiles.Count,
            PreservedCount = result.PreservedFiles.Count,
            SdkResolved = sdkDir is not null,
        });
    }

    [McpTool("jiangyu_code_types",
        "List the [JiangyuType] types the current project defines, with their base type and fields. Reads the built code DLLs under compiled/code, so run jiangyu_compile first. Use a returned name in a template type=\"<modId>:<name>\" reference.")]
    internal static JsonElement CodeTypes(JsonElement? __)
    {
        var projectRoot = RpcContext.ProjectRoot ?? throw new InvalidOperationException("No project open.");

        var codeDir = Path.Combine(projectRoot, "compiled", "code");
        var dlls = Directory.Exists(codeDir) ? Directory.GetFiles(codeDir, "*.dll") : [];
        if (dlls.Length == 0)
            return JsonSerializer.SerializeToElement(
                new { types = Array.Empty<JiangyuTypeInfo>(), message = "No built code DLL under compiled/code. Run jiangyu_compile first." },
                CodeJsonOptions);

        var config = GlobalConfig.Load();
        var (gameDir, _) = GlobalConfig.ResolveGamePath(config);
        var (sdkDir, _) = GlobalConfig.ResolveSdkDir(config);

        var types = CodeTypeInspector.Inspect(dlls, gameDir, sdkDir);
        return JsonSerializer.SerializeToElement(new { types }, CodeJsonOptions);
    }

    [McpTool("jiangyu_xref_type",
        "Find template directives that reference a mod [JiangyuType] by name. Pass the bare name ('DeathDefying') or the qualified one ('WOMENACE:DeathDefying'). Reads the compiled manifest, so run jiangyu_compile first.")]
    [McpParam("name", "string", "The [JiangyuType] name to find references to.", Required = true)]
    internal static JsonElement XrefType(JsonElement? parameters)
    {
        var name = RpcHelpers.RequireString(parameters, "name");
        var rows = LoadCompiledRefs();
        if (rows is null)
            return CompileFirst();

        var matches = rows.Where(r => r.Kind == "type"
            && (string.Equals(r.Value, name, StringComparison.Ordinal) || string.Equals(CodeTypeResolver.BareName(r.Value), name, StringComparison.Ordinal)));
        return JsonSerializer.SerializeToElement(new { references = matches }, CodeJsonOptions);
    }

    [McpTool("jiangyu_xref_asset",
        "Find template directives that reference a bundled asset by its logical name (the path under assets/additions/<category>/ without extension). Reads the compiled manifest, so run jiangyu_compile first.")]
    [McpParam("name", "string", "The asset's logical name.", Required = true)]
    internal static JsonElement XrefAsset(JsonElement? parameters)
    {
        var name = RpcHelpers.RequireString(parameters, "name");
        var rows = LoadCompiledRefs();
        if (rows is null)
            return CompileFirst();

        var matches = rows.Where(r => r.Kind == "asset" && string.Equals(r.Value, name, StringComparison.Ordinal));
        return JsonSerializer.SerializeToElement(new { references = matches }, CodeJsonOptions);
    }

    private static JsonElement CompileFirst()
        => JsonSerializer.SerializeToElement(
            new { references = Array.Empty<TemplateRefRow>(), message = "No compiled manifest under compiled/. Run jiangyu_compile first." },
            CodeJsonOptions);

    // Every type= and asset= reference in the compiled template patches, tagged with
    // the template directive that holds it. Null when the project has no compiled
    // manifest yet (never compiled).
    private static List<TemplateRefRow>? LoadCompiledRefs()
    {
        var projectRoot = RpcContext.ProjectRoot ?? throw new InvalidOperationException("No project open.");
        var manifest = ModManifest.TryLoad(Path.Combine(projectRoot, "compiled"));
        if (manifest is null)
            return null;

        var rows = new List<TemplateRefRow>();
        foreach (var patch in manifest.TemplatePatches ?? [])
            foreach (var (kind, value, fieldPath) in CompiledTemplateReferences.Enumerate(patch))
                rows.Add(new TemplateRefRow(patch.TemplateType ?? "EntityTemplate", patch.TemplateId, fieldPath, kind, value));
        return rows;
    }

    private sealed record TemplateRefRow(string TemplateType, string TemplateId, string FieldPath, string Kind, string Value);

    [RpcType]
    internal sealed class CodeSyncResult
    {
        [JsonPropertyName("createdCount")]
        public required int CreatedCount { get; set; }

        [JsonPropertyName("updatedCount")]
        public required int UpdatedCount { get; set; }

        [JsonPropertyName("preservedCount")]
        public required int PreservedCount { get; set; }

        [JsonPropertyName("sdkResolved")]
        public required bool SdkResolved { get; set; }
    }
}
