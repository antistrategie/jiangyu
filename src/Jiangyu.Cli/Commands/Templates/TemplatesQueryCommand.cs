using System.CommandLine;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Jiangyu.Core.Config;
using Jiangyu.Core.Templates;
using Jiangyu.Shared.Templates;

namespace Jiangyu.Cli.Commands.Templates;

public static class TemplatesQueryCommand
{
    private const string DefaultAssemblyRelativePath = "MelonLoader/Il2CppAssemblies/Assembly-CSharp.dll";
    private const string MelonLoaderNet6RelativePath = "MelonLoader/net6";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static Command Create()
    {
        var pathArgument = new Argument<string>("path")
        {
            Description = "jq-like path: TypeName, TypeName.Member, TypeName.Member[0], etc.",
        };
        var assemblyOption = new Option<string?>("--assembly")
        {
            Description = "Override path to Assembly-CSharp.dll (defaults to MelonLoader/Il2CppAssemblies under the game)",
        };
        var jsonOption = new Option<bool>("--json")
        {
            Description = "Emit machine-readable JSON instead of the human listing",
        };
        var includeReadOnlyOption = new Option<bool>("--include-readonly")
        {
            Description = "Include read-only members (not patchable)",
        };

        var command = new Command("query", "Navigate the template type tree offline from Assembly-CSharp.dll")
        {
            pathArgument,
            assemblyOption,
            jsonOption,
            includeReadOnlyOption,
        };

        command.SetAction(parseResult =>
        {
            var path = parseResult.GetValue(pathArgument)!;
            var assemblyOverride = parseResult.GetValue(assemblyOption);
            var emitJson = parseResult.GetValue(jsonOption);
            var includeReadOnly = parseResult.GetValue(includeReadOnlyOption);

            string? assemblyPath = assemblyOverride;
            string? gamePath = null;
            if (string.IsNullOrWhiteSpace(assemblyPath))
            {
                var resolution = EnvironmentContext.ResolveFromGlobalConfig();
                if (!resolution.Success)
                {
                    Console.Error.WriteLine(resolution.Error);
                    return 1;
                }

                gamePath = Path.GetDirectoryName(resolution.Context!.GameDataPath);
                if (string.IsNullOrEmpty(gamePath))
                {
                    Console.Error.WriteLine("Error: could not derive game directory from data path.");
                    return 1;
                }

                assemblyPath = Path.Combine(gamePath, DefaultAssemblyRelativePath);
            }
            else
            {
                // When --assembly is explicit, infer the game root by walking up to
                // the MelonLoader directory so we can still find Il2CppInterop.Runtime.
                var current = Path.GetDirectoryName(assemblyPath);
                while (!string.IsNullOrEmpty(current))
                {
                    if (Directory.Exists(Path.Combine(current, "MelonLoader")))
                    {
                        gamePath = current;
                        break;
                    }
                    current = Path.GetDirectoryName(current);
                }
            }

            if (!File.Exists(assemblyPath))
            {
                Console.Error.WriteLine($"Error: assembly not found: {assemblyPath}");
                return 1;
            }

            var additionalSearchDirectories = new List<string>();
            if (!string.IsNullOrEmpty(gamePath))
            {
                var melonLoaderNet6 = Path.Combine(gamePath, MelonLoaderNet6RelativePath);
                if (Directory.Exists(melonLoaderNet6))
                    additionalSearchDirectories.Add(melonLoaderNet6);
            }

            try
            {
                using var catalog = TemplateTypeCatalog.Load(assemblyPath, additionalSearchDirectories);
                var result = TemplateMemberQuery.Run(catalog, path);

                return result.Kind switch
                {
                    QueryResultKind.Error => WriteError(result.ErrorMessage ?? "unknown error"),
                    QueryResultKind.TypeNode when emitJson => WriteTypeNodeJson(catalog, result, includeReadOnly),
                    QueryResultKind.TypeNode => WriteTypeNodeText(catalog, result, includeReadOnly),
                    QueryResultKind.Leaf when emitJson => WriteLeafJson(result),
                    QueryResultKind.Leaf => WriteLeafText(catalog, result),
                    _ => WriteError($"unexpected result kind: {result.Kind}"),
                };
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error: query failed: {ex.Message}");
                return 1;
            }
        });

        return command;
    }

    private static int WriteError(string message)
    {
        Console.Error.WriteLine($"Error: {message}");
        return 1;
    }

    private static int WriteTypeNodeText(TemplateTypeCatalog catalog, QueryResult result, bool includeReadOnly)
    {
        var type = result.CurrentType!;
        var members = includeReadOnly
            ? TemplateTypeCatalog.GetMembers(type, includeReadOnly: true)
            : result.Members!;

        Console.WriteLine($"{catalog.FriendlyName(type)}  ({type.FullName})");
        if (!string.IsNullOrEmpty(result.ResolvedPath) && result.ResolvedPath != catalog.FriendlyName(type))
            Console.WriteLine($"  path: {result.ResolvedPath}");
        if (result.UnwrappedFrom != null)
            Console.WriteLine($"  (unwrapped element type of {catalog.FriendlyName(result.UnwrappedFrom)})");
        if (result.PatchScalarKind == CompiledTemplateValueKind.TemplateReference)
            Console.WriteLine($"  patch value: TemplateReference ({result.ReferenceTargetTypeName})");
        if (result.IsLikelyOdinOnly)
            Console.WriteLine("  warning: likely Odin-only; Jiangyu's current template applier will not write this member.");
        Console.WriteLine();

        if (members.Count == 0)
        {
            Console.WriteLine("  (no writable members)");
            return 0;
        }

        var nameWidth = members.Max(m => m.Name.Length);
        foreach (var member in members)
        {
            var typeDisplay = catalog.FriendlyName(member.MemberType);
            var tags = new List<string>();
            if (member.IsInherited)
                tags.Add("inherited");
            if (!member.IsWritable)
                tags.Add("read-only");
            if (member.IsLikelyOdinOnly)
                tags.Add("odin-only");

            var suffix = tags.Count == 0 ? "" : "  [" + string.Join(", ", tags) + "]";
            Console.WriteLine($"  {member.Name.PadRight(nameWidth)}  {typeDisplay}{suffix}");
        }

        Console.WriteLine();
        var writableCount = members.Count(m => m.IsWritable);
        if (includeReadOnly && writableCount != members.Count)
            Console.WriteLine($"{members.Count} members ({writableCount} writable).");
        else
            Console.WriteLine($"{members.Count} writable members.");

        return 0;
    }

    private static int WriteTypeNodeJson(TemplateTypeCatalog catalog, QueryResult result, bool includeReadOnly)
    {
        var type = result.CurrentType!;
        var members = includeReadOnly
            ? TemplateTypeCatalog.GetMembers(type, includeReadOnly: true)
            : result.Members!;

        var payload = new
        {
            kind = "typeNode",
            resolvedPath = result.ResolvedPath,
            type = new
            {
                name = type.Name,
                fullName = type.FullName,
            },
            members = members.Select(m => new
            {
                name = m.Name,
                kind = m.Kind.ToString(),
                typeName = catalog.FriendlyName(m.MemberType),
                typeFullName = m.MemberType.FullName,
                declaringTypeFullName = m.DeclaringTypeFullName,
                isInherited = m.IsInherited,
                isWritable = m.IsWritable,
                isLikelyOdinOnly = m.IsLikelyOdinOnly,
            }),
            patchValueKind = result.PatchScalarKind?.ToString(),
            referenceTargetTypeName = result.ReferenceTargetTypeName,
            isLikelyOdinOnly = result.IsLikelyOdinOnly,
        };
        Console.WriteLine(JsonSerializer.Serialize(payload, JsonOptions));
        return 0;
    }

    private static int WriteLeafText(TemplateTypeCatalog catalog, QueryResult result)
    {
        var type = result.CurrentType!;
        Console.WriteLine($"{result.ResolvedPath}");
        Console.WriteLine($"  type:      {catalog.FriendlyName(type)}");
        Console.WriteLine($"  writable:  {(result.IsWritable ? "yes" : "no")}");
        if (result.PatchScalarKind != null)
            Console.WriteLine($"  value:     {result.PatchScalarKind}");
        if (!string.IsNullOrWhiteSpace(result.ReferenceTargetTypeName))
            Console.WriteLine($"  target:    {result.ReferenceTargetTypeName}");
        if (result.EnumMemberNames.Count > 0)
            Console.WriteLine($"  enum:      {string.Join(", ", result.EnumMemberNames)}");
        if (result.IsLikelyOdinOnly)
            Console.WriteLine("  warning:   likely Odin-only; Jiangyu's current template applier will not write this member.");

        var example = BuildPatchExample(result);
        if (example != null)
        {
            Console.WriteLine();
            Console.WriteLine("  Example patch (compiled jiangyu.json shape):");
            Console.WriteLine();
            foreach (var line in example.Split('\n'))
                Console.WriteLine("    " + line);
        }

        return 0;
    }

    private static int WriteLeafJson(QueryResult result)
    {
        var type = result.CurrentType!;
        var payload = new
        {
            kind = "leaf",
            resolvedPath = result.ResolvedPath,
            type = new
            {
                name = type.Name,
                fullName = type.FullName,
            },
            isWritable = result.IsWritable,
            patchScalarKind = result.PatchScalarKind?.ToString(),
            enumMemberNames = result.EnumMemberNames,
            referenceTargetTypeName = result.ReferenceTargetTypeName,
            isLikelyOdinOnly = result.IsLikelyOdinOnly,
        };
        Console.WriteLine(JsonSerializer.Serialize(payload, JsonOptions));
        return 0;
    }

    private static string? BuildPatchExample(QueryResult result)
    {
        if (result.PatchScalarKind == null || string.IsNullOrEmpty(result.ResolvedPath))
            return null;

        var pathParts = result.ResolvedPath.Split('.', 2);
        if (pathParts.Length < 2)
            return null;

        var typeName = pathParts[0].Split('.').Last(); // short name for stability
        var fieldPath = pathParts[1];

        var sampleValue = SampleValueFor(result.PatchScalarKind.Value);
        var valueJson = result.PatchScalarKind.Value switch
        {
            CompiledTemplateValueKind.Boolean => $"{{ \"kind\": \"Boolean\", \"boolean\": {sampleValue} }}",
            CompiledTemplateValueKind.Byte => $"{{ \"kind\": \"Byte\", \"byte\": {sampleValue} }}",
            CompiledTemplateValueKind.Int32 => $"{{ \"kind\": \"Int32\", \"int32\": {sampleValue} }}",
            CompiledTemplateValueKind.Single => $"{{ \"kind\": \"Single\", \"single\": {sampleValue} }}",
            CompiledTemplateValueKind.String => $"{{ \"kind\": \"String\", \"string\": {sampleValue} }}",
            CompiledTemplateValueKind.Enum => $"{{ \"kind\": \"Enum\", \"enumValue\": {sampleValue} }}",
            CompiledTemplateValueKind.TemplateReference => $"{{ \"kind\": \"TemplateReference\", \"reference\": {{ \"templateType\": \"{result.ReferenceTargetTypeName ?? "TemplateType"}\", \"templateId\": \"<target-id>\" }} }}",
            _ => null,
        };
        if (valueJson == null)
            return null;

        var sb = new StringBuilder();
        sb.AppendLine("\"templatePatches\": [");
        sb.AppendLine("  {");
        sb.AppendLine($"    \"templateType\": \"{typeName}\",");
        sb.AppendLine("    \"templateId\":   \"<id>\",");
        sb.AppendLine("    \"set\": [");
        sb.AppendLine($"      {{ \"fieldPath\": \"{fieldPath}\", \"value\": {valueJson} }}");
        sb.AppendLine("    ]");
        sb.AppendLine("  }");
        sb.Append(']');
        return sb.ToString();
    }

    private static string SampleValueFor(CompiledTemplateValueKind kind) => kind switch
    {
        CompiledTemplateValueKind.Boolean => "true",
        CompiledTemplateValueKind.Byte => "50",
        CompiledTemplateValueKind.Int32 => "100",
        CompiledTemplateValueKind.Single => "1.0",
        CompiledTemplateValueKind.String => "\"example\"",
        CompiledTemplateValueKind.Enum => "\"EnumMember\"",
        _ => "null",
    };
}
