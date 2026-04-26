using System.Collections.Immutable;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Jiangyu.Rpc.Generators;

[Generator]
public sealed class RpcTypeGenerator : IIncrementalGenerator
{
    private const string AttributeFullName = "Jiangyu.Shared.RpcTypeAttribute";

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        // Collect types from source in the compiling project.
        var sourceTypes = context.SyntaxProvider
            .CreateSyntaxProvider(
                predicate: static (node, _) => node is ClassDeclarationSyntax or StructDeclarationSyntax,
                transform: static (ctx, ct) => TransformFromSource(ctx, ct))
            .Where(static t => t is not null);

        // Collect types from referenced assemblies (e.g. Jiangyu.Core).
        var refTypes = context.CompilationProvider
            .SelectMany(static (compilation, ct) => CollectFromReferences(compilation, ct));

        // Merge and deduplicate by type name.
        var allTypes = sourceTypes.Collect().Combine(refTypes.Collect())
            .Select(static (pair, _) =>
            {
                var (src, ext) = pair;
                var seen = new HashSet<string>();
                var result = ImmutableArray.CreateBuilder<RpcTypeInfo>();
                foreach (var t in src)
                {
                    if (t is not null && seen.Add(t.Name))
                        result.Add(t);
                }
                foreach (var t in ext)
                {
                    if (seen.Add(t.Name))
                        result.Add(t);
                }
                return result.ToImmutable();
            });

        var outputPath = context.AnalyzerConfigOptionsProvider
            .Select(static (provider, _) =>
                provider.GlobalOptions.TryGetValue("build_property.RpcTypesOutputPath", out var path)
                    ? path
                    : null);

        context.RegisterImplementationSourceOutput(allTypes.Combine(outputPath), static (ctx, pair) =>
        {
            var (types, path) = pair;
            if (path is null || types.Length == 0) return;

            var ts = GenerateTypeScript(types);
            try
            {
                var dir = Path.GetDirectoryName(path);
                if (dir is not null)
                    Directory.CreateDirectory(dir);
                File.WriteAllText(path, ts, Encoding.UTF8);
            }
            catch
            {
                // Don't break the build on I/O errors.
            }
        });
    }

    private static RpcTypeInfo? TransformFromSource(GeneratorSyntaxContext ctx, CancellationToken ct)
    {
        var typeDecl = (TypeDeclarationSyntax)ctx.Node;
        if (typeDecl.AttributeLists.Count == 0) return null;

        var symbol = ctx.SemanticModel.GetDeclaredSymbol(typeDecl, ct) as INamedTypeSymbol;
        return symbol is not null && HasRpcTypeAttribute(symbol) ? BuildTypeInfo(symbol) : null;
    }

    private static IEnumerable<RpcTypeInfo> CollectFromReferences(Compilation compilation, CancellationToken ct)
    {
        // Walk all referenced assemblies (not the compiling project itself —
        // those are handled by the SyntaxProvider).
        var results = new List<RpcTypeInfo>();
        foreach (var refAssembly in compilation.SourceModule.ReferencedAssemblySymbols)
        {
            ct.ThrowIfCancellationRequested();
            WalkNamespaceForTypes(refAssembly.GlobalNamespace, results);
        }
        return results;
    }

    private static void WalkNamespaceForTypes(INamespaceSymbol ns, List<RpcTypeInfo> results)
    {
        foreach (var member in ns.GetMembers())
        {
            if (member is INamespaceSymbol childNs)
            {
                WalkNamespaceForTypes(childNs, results);
            }
            else if (member is INamedTypeSymbol type)
            {
                CollectTypeRecursive(type, results);
            }
        }
    }

    private static void CollectTypeRecursive(INamedTypeSymbol type, List<RpcTypeInfo> results)
    {
        if (HasRpcTypeAttribute(type))
            results.Add(BuildTypeInfo(type));

        foreach (var nested in type.GetTypeMembers())
            CollectTypeRecursive(nested, results);
    }

    private static bool HasRpcTypeAttribute(INamedTypeSymbol symbol)
    {
        foreach (var attr in symbol.GetAttributes())
        {
            if (attr.AttributeClass?.ToDisplayString() == AttributeFullName)
                return true;
        }
        return false;
    }

    private static RpcTypeInfo BuildTypeInfo(INamedTypeSymbol symbol)
    {
        var properties = new List<PropertyInfo>();
        foreach (var member in symbol.GetMembers())
        {
            if (member is not IPropertySymbol prop) continue;
            if (prop.IsStatic) continue;

            var tsName = GetJsonPropertyName(prop) ?? CamelCase(prop.Name);
            var tsType = MapType(prop.Type);
            var isOptional = IsOptionalProperty(prop);

            properties.Add(new PropertyInfo(tsName, tsType, isOptional));
        }

        return new RpcTypeInfo(symbol.Name, properties);
    }

    private static string GenerateTypeScript(ImmutableArray<RpcTypeInfo> types)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// Auto-generated by Jiangyu.Rpc.Generators. Do not edit.");
        sb.AppendLine();

        foreach (var type in types)
        {
            if (type.Properties.Count == 0)
            {
                // Placeholder from reference assembly — we only had the name,
                // not the properties. Skip.
                continue;
            }

            sb.AppendLine($"export interface {type.Name} {{");
            foreach (var prop in type.Properties)
            {
                var optional = prop.IsOptional ? "?" : "";
                sb.AppendLine($"  {prop.Name}{optional}: {prop.TsType};");
            }
            sb.AppendLine("}");
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static string? GetJsonPropertyName(IPropertySymbol prop)
    {
        foreach (var attr in prop.GetAttributes())
        {
            if (attr.AttributeClass?.Name is "JsonPropertyNameAttribute" or "JsonPropertyName"
                && attr.ConstructorArguments.Length == 1
                && attr.ConstructorArguments[0].Value is string name)
            {
                return name;
            }
        }
        return null;
    }

    private static string MapType(ITypeSymbol type)
    {
        // Nullable<T> — unwrap and add | null.
        if (type is INamedTypeSymbol named
            && named.IsGenericType
            && named.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T)
        {
            var inner = MapType(named.TypeArguments[0]);
            return inner == "unknown" ? inner : $"{inner} | null";
        }

        // Nullable reference type via annotation.
        if (type.NullableAnnotation == NullableAnnotation.Annotated)
        {
            var inner = MapTypeCore(type.WithNullableAnnotation(NullableAnnotation.NotAnnotated));
            return inner == "unknown" ? inner : $"{inner} | null";
        }

        return MapTypeCore(type);
    }

    private static string MapTypeCore(ITypeSymbol type)
    {
        // Special types.
        switch (type.SpecialType)
        {
            case SpecialType.System_String:
                return "string";
            case SpecialType.System_Boolean:
                return "boolean";
            case SpecialType.System_Int32:
            case SpecialType.System_Int64:
            case SpecialType.System_Single:
            case SpecialType.System_Double:
            case SpecialType.System_Int16:
            case SpecialType.System_Byte:
            case SpecialType.System_Decimal:
            case SpecialType.System_UInt32:
            case SpecialType.System_UInt64:
            case SpecialType.System_UInt16:
            case SpecialType.System_SByte:
                return "number";
            case SpecialType.System_DateTime:
                return "string";
        }

        if (type.ContainingNamespace?.ToDisplayString() == "System" && type.MetadataName == "DateTimeOffset")
            return "string";

        // object → unknown.
        if (type.SpecialType == SpecialType.System_Object)
            return "unknown";

        // Generics.
        if (type is INamedTypeSymbol named && named.IsGenericType)
        {
            var original = named.OriginalDefinition;
            var originalFull = original.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

            if (originalFull.Contains("System.Collections.Generic.List")
                || originalFull.Contains("System.Collections.Generic.IReadOnlyList")
                || originalFull.Contains("System.Collections.Generic.IList"))
            {
                var elem = MapType(named.TypeArguments[0]);
                return $"{elem}[]";
            }

            if (originalFull.Contains("System.Collections.Generic.Dictionary"))
            {
                var val = MapType(named.TypeArguments[1]);
                return $"Record<string, {val}>";
            }
        }

        // Enum → string union.
        if (type.TypeKind == TypeKind.Enum)
        {
            var members = new List<string>();
            foreach (var member in type.GetMembers())
            {
                if (member is IFieldSymbol field && field.HasConstantValue)
                    members.Add($"\"{field.Name}\"");
            }
            if (members.Count > 0)
                return string.Join(" | ", members);
            return "string";
        }

        // Fall back to the type name (another [RpcType] interface).
        return type.Name;
    }

    private static bool IsOptionalProperty(IPropertySymbol prop)
    {
        if (prop.IsRequired) return false;

        // Non-nullable value types are always present (they have defaults).
        if (prop.Type.IsValueType && prop.Type.NullableAnnotation != NullableAnnotation.Annotated)
            return false;

        return true;
    }

    private static string CamelCase(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;
        return char.ToLowerInvariant(name[0]) + name.Substring(1);
    }

    private sealed class RpcTypeInfo
    {
        public string Name { get; }
        public List<PropertyInfo> Properties { get; }
        public RpcTypeInfo(string name, List<PropertyInfo> properties)
        {
            Name = name;
            Properties = properties;
        }
    }

    private sealed class PropertyInfo
    {
        public string Name { get; }
        public string TsType { get; }
        public bool IsOptional { get; }
        public PropertyInfo(string name, string tsType, bool isOptional)
        {
            Name = name;
            TsType = tsType;
            IsOptional = isOptional;
        }
    }
}
