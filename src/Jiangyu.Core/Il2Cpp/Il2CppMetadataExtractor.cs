using AsmResolver.DotNet;
using AsmResolver.DotNet.Signatures;
using Cpp2IL.Core.Api;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.OutputFormats;
using Cpp2IL.Core.ProcessingLayers;
using Jiangyu.Core.Abstractions;
using LibCpp2IL;
using AssetRipper.Primitives;
using Cpp2IlApi = Cpp2IL.Core.Cpp2IlApi;

namespace Jiangyu.Core.Il2Cpp;

/// <summary>
/// Reads attribute metadata directly from the game's IL2CPP files (which
/// Il2CppInterop wrappers strip on generation) and emits a serialisable
/// supplement. Currently surfaces <c>[NamedArray(typeof(T))]</c> pairings
/// on <c>DataTemplate</c> subtypes; extend by walking more attribute shapes
/// as new diagnostic needs appear.
///
/// Heavy operation — loads ~90 dummy assemblies into AsmResolver. Run once
/// during <c>jiangyu assets index</c>, cache the result via
/// <see cref="Il2CppMetadataCache"/>.
/// </summary>
public static class Il2CppMetadataExtractor
{
    private static readonly Lock StaticInitLock = new();
    private static bool _staticInitDone;

    public static Il2CppMetadataSupplement Extract(
        string gameAssemblyPath,
        string metadataPath,
        UnityVersion unityVersion,
        ILogSink log)
    {
        if (!File.Exists(gameAssemblyPath))
            throw new FileNotFoundException($"GameAssembly not found: {gameAssemblyPath}", gameAssemblyPath);
        if (!File.Exists(metadataPath))
            throw new FileNotFoundException($"global-metadata.dat not found: {metadataPath}", metadataPath);

        EnsureStaticInit();

        log.Info($"  IL2CPP: parsing {Path.GetFileName(gameAssemblyPath)} ({unityVersion})…");
        Cpp2IlApi.InitializeLibCpp2Il(gameAssemblyPath, metadataPath, unityVersion, false);

        var appContext = Cpp2IlApi.CurrentAppContext
            ?? throw new InvalidOperationException("Cpp2IL did not produce an application context.");

        var layers = new List<Cpp2IlProcessingLayer>
        {
            // AttributeAnalysisProcessingLayer recovers the attribute data
            // that Il2CppInterop drops. Without this, custom attributes are
            // invisible on the resulting AsmResolver assemblies.
            new AttributeAnalysisProcessingLayer(),
        };
        foreach (var layer in layers) layer.PreProcess(appContext, layers);
        foreach (var layer in layers) layer.Process(appContext);

        var assemblies = new AsmResolverDllOutputFormatDefault().BuildAssemblies(appContext);
        log.Info($"  IL2CPP: walking {assemblies.Count} assemblies for NamedArray pairings…");

        var supplement = new Il2CppMetadataSupplement
        {
            GeneratedAt = DateTimeOffset.UtcNow,
            GameAssemblyMtime = new FileInfo(gameAssemblyPath).LastWriteTimeUtc,
            MetadataMtime = new FileInfo(metadataPath).LastWriteTimeUtc,
        };

        foreach (var assembly in assemblies)
        {
            foreach (var module in assembly.Modules)
            {
                foreach (var type in module.GetAllTypes())
                {
                    // Concrete-class to interface-impl pairings: required for
                    // the catalog to discover Odin-routed polymorphic field
                    // subtypes (Il2CppInterop strips interface declarations
                    // from CIL, so reflection on the wrapper assembly can't
                    // see them). Walked here on every type, not just
                    // DataTemplate descendants, because interface impls
                    // (e.g. ITacticalCondition implementations) live across
                    // the type hierarchy.
                    CollectInterfaceImplementations(type, supplement);

                    if (!DescendsFromDataTemplate(type)) continue;
                    foreach (var field in type.Fields)
                    {
                        var pairing = TryExtractNamedArray(type, field);
                        if (pairing is not null) supplement.NamedArrays.Add(pairing);

                        var meta = TryExtractFieldMetadata(type, field);
                        if (meta is not null) supplement.Fields.Add(meta);
                    }
                }
            }
        }

        log.Info(
            $"  IL2CPP: extracted {supplement.NamedArrays.Count} NamedArray pairing(s), "
            + $"{supplement.Fields.Count} attributed field(s), "
            + $"{supplement.InterfaceImpls.Count} interface implementation(s).");
        return supplement;
    }

    /// <summary>
    /// Record (concrete class, implemented interface) pairs, walking up
    /// the inheritance chain so concrete subclasses inherit interface
    /// impls declared on abstract bases (the common MENACE pattern is
    /// <c>MoraleStateCondition : TacticalCondition</c> where the abstract
    /// <c>TacticalCondition</c> declares <c>: ITacticalCondition</c>).
    /// Skips interfaces themselves and abstract bases as the recorded
    /// "concrete" — those are non-instantiable and would only confuse the
    /// catalog's subtype-pick UI.
    /// </summary>
    private static void CollectInterfaceImplementations(
        TypeDefinition type,
        Il2CppMetadataSupplement supplement)
    {
        if (type.IsInterface) return;
        if (type.IsAbstract) return;

        var concreteFullName = type.FullName;
        if (string.IsNullOrEmpty(concreteFullName)) return;

        // Collect every interface declared at this type or any of its
        // ancestors. We deduplicate per-concrete-type because diamond
        // inheritance can otherwise produce repeats.
        var seenInterfaces = new HashSet<string>(StringComparer.Ordinal);
        var current = (TypeDefinition?)type;
        var depth = 0;
        const int maxDepth = 32;
        while (current is not null && depth++ < maxDepth)
        {
            foreach (var iface in current.Interfaces)
            {
                var ifaceType = iface.Interface?.ToTypeDefOrRef();
                var ifaceFullName = ifaceType?.FullName;
                if (string.IsNullOrEmpty(ifaceFullName)) continue;
                if (!seenInterfaces.Add(ifaceFullName)) continue;

                supplement.InterfaceImpls.Add(new InterfaceImplementation
                {
                    ConcreteFullName = concreteFullName,
                    InterfaceFullName = ifaceFullName,
                });
            }

            try
            {
                current = current.BaseType?.Resolve();
            }
            catch
            {
                break;
            }
        }
    }

    private static void EnsureStaticInit()
    {
        lock (StaticInitLock)
        {
            if (_staticInitDone) return;
            // AssetRipper may have already registered these instruction sets
            // (e.g. when the template index builds with Level2 before we run).
            try { InstructionSetRegistry.RegisterInstructionSet<X86InstructionSet>(DefaultInstructionSets.X86_32); } catch (ArgumentException) { }
            try { InstructionSetRegistry.RegisterInstructionSet<X86InstructionSet>(DefaultInstructionSets.X86_64); } catch (ArgumentException) { }
            try { InstructionSetRegistry.RegisterInstructionSet<WasmInstructionSet>(DefaultInstructionSets.WASM); } catch (ArgumentException) { }
            try { InstructionSetRegistry.RegisterInstructionSet<ArmV7InstructionSet>(DefaultInstructionSets.ARM_V7); } catch (ArgumentException) { }
            try { InstructionSetRegistry.RegisterInstructionSet<Arm64InstructionSet>(DefaultInstructionSets.ARM_V8); } catch (ArgumentException) { }
            LibCpp2IlBinaryRegistry.RegisterBuiltInBinarySupport();
            _staticInitDone = true;
        }
    }

    private static bool DescendsFromDataTemplate(TypeDefinition type)
    {
        var current = type.BaseType;
        var safety = 32;
        while (current is not null && safety-- > 0)
        {
            if (string.Equals(current.Name, "DataTemplate", StringComparison.Ordinal))
                return true;
            current = current.Resolve()?.BaseType;
        }
        return false;
    }

    /// <summary>
    /// Per-field attribute scan: pulls Range/Min/Tooltip/HideInInspector and
    /// the game's SoundID marker into a single record. Returns null when
    /// nothing of interest sits on the field — keeps the supplement sparse.
    /// </summary>
    private static FieldMetadata? TryExtractFieldMetadata(TypeDefinition type, FieldDefinition field)
    {
        if (field.CustomAttributes.Count == 0) return null;

        double? rangeMin = null;
        double? rangeMax = null;
        double? minValue = null;
        string? tooltip = null;
        bool hideInInspector = false;
        bool isSoundId = false;

        foreach (var attr in field.CustomAttributes)
        {
            var attrName = attr.Constructor?.DeclaringType?.FullName;
            if (attrName is null) continue;
            var sig = attr.Signature;

            switch (attrName)
            {
                case "UnityEngine.RangeAttribute":
                    if (sig is { FixedArguments.Count: >= 2 })
                    {
                        rangeMin = ToDouble(sig.FixedArguments[0].Element);
                        rangeMax = ToDouble(sig.FixedArguments[1].Element);
                    }
                    break;
                case "UnityEngine.MinAttribute":
                    if (sig is { FixedArguments.Count: >= 1 })
                        minValue = ToDouble(sig.FixedArguments[0].Element);
                    break;
                case "UnityEngine.TooltipAttribute":
                    if (sig is { FixedArguments.Count: >= 1 } && sig.FixedArguments[0].Element is { } tt)
                        tooltip = tt.ToString();
                    break;
                case "UnityEngine.HideInInspector":
                    hideInInspector = true;
                    break;
                case "Stem.SoundIDAttribute":
                    isSoundId = true;
                    break;
            }
        }

        if (rangeMin is null && rangeMax is null && minValue is null && tooltip is null && !hideInInspector && !isSoundId)
            return null;

        return new FieldMetadata
        {
            TemplateTypeShortName = type.Name?.ToString() ?? "",
            TemplateTypeFullName = type.FullName ?? "",
            FieldName = field.Name?.ToString() ?? "",
            RangeMin = rangeMin,
            RangeMax = rangeMax,
            MinValue = minValue,
            Tooltip = tooltip,
            HideInInspector = hideInInspector ? true : null,
            IsSoundId = isSoundId ? true : null,
        };
    }

    private static double? ToDouble(object? element)
    {
        return element switch
        {
            null => null,
            double d => d,
            float f => f,
            int i => i,
            long l => l,
            short s => s,
            byte b => b,
            uint u => u,
            ulong ul => ul,
            sbyte sb => sb,
            ushort us => us,
            _ => double.TryParse(element.ToString(), out var parsed) ? parsed : null,
        };
    }

    private static NamedArrayPairing? TryExtractNamedArray(TypeDefinition type, FieldDefinition field)
    {
        if (field.CustomAttributes.Count == 0) return null;

        // Element type of the array — for `T[]` it's T; we don't currently
        // descend into List<T>/Il2CppArray<T> since the game's NamedArray
        // pattern is on raw arrays.
        var elementTypeFullName = (field.Signature?.FieldType as SzArrayTypeSignature)?.BaseType?.FullName;

        foreach (var attr in field.CustomAttributes)
        {
            var sig = attr.Signature;
            if (sig is null) continue;
            foreach (var arg in sig.FixedArguments)
            {
                if (arg.Element is TypeSignature typeSig)
                {
                    var referenced = typeSig.Resolve();
                    if (referenced is not null && referenced.IsEnum && referenced.Name is not null)
                    {
                        return new NamedArrayPairing
                        {
                            TemplateTypeFullName = type.FullName ?? type.Name?.ToString() ?? "",
                            TemplateTypeShortName = type.Name?.ToString() ?? "",
                            FieldName = field.Name?.ToString() ?? "",
                            ElementTypeFullName = elementTypeFullName,
                            EnumTypeShortName = referenced.Name.ToString(),
                            EnumTypeFullName = referenced.FullName,
                            AttributeFullName = attr.Constructor?.DeclaringType?.FullName,
                        };
                    }
                }
            }
        }
        return null;
    }
}
