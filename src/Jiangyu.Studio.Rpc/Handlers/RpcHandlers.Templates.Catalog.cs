using System.Text.Json;
using System.Text.Json.Serialization;
using Jiangyu.Core.Abstractions;
using Jiangyu.Core.Assets;
using Jiangyu.Core.Config;
using Jiangyu.Core.Il2Cpp;
using Jiangyu.Core.Models;
using Jiangyu.Core.Templates;
using Jiangyu.Core.Rpc;
using static Jiangyu.Studio.Rpc.RpcHelpers;

namespace Jiangyu.Studio.Rpc;

public static partial class RpcHandlers
{
    [McpTool("jiangyu_templates_clone_sources",
        "Return the available source identifiers for cloning a template of the given type. Covers both DataTemplate-derived types (via the template index) and non-DataTemplate types like SoundBank and ConversationTemplate (via the asset index).")]
    [McpParam("templateType", "string", "Template type short name (e.g. \"SoundBank\", \"ConversationTemplate\", \"EntityTemplate\").", Required = true)]
    internal static JsonElement TemplatesCloneSources(JsonElement? parameters)
    {
        var templateType = RequireString(parameters, "templateType");
        var items = new List<TemplateCloneSource>();

        var resolution = EnvironmentContext.ResolveFromGlobalConfig();
        if (resolution.Success)
        {
            // SoundBank and ConversationTemplate are not DataTemplate
            // subclasses, so the template-index path
            // (templates_index/list) misses them. Surface them through
            // the asset index instead. For ConversationTemplate the
            // unique identifier is the Path field (asset names are
            // non-unique); for SoundBank the asset name itself is
            // unique.
            if (string.Equals(templateType, "SoundBank", StringComparison.Ordinal)
        || string.Equals(templateType, "ConversationTemplate", StringComparison.Ordinal))
            {
                var pipeline = RpcHelpers.RequireEnvironment().CreateAssetPipelineService(NullProgressSink.Instance, NullLogSink.Instance);
                var assetIndex = pipeline.LoadIndex();
                if (assetIndex?.Assets is { } assets)
                {
                    foreach (var asset in assets)
                    {
                        if (templateType == "SoundBank")
                        {
                            if (asset.SoundBank?.BankId is null || string.IsNullOrEmpty(asset.Name)) continue;
                            items.Add(new TemplateCloneSource { Id = asset.Name, Label = asset.Name });
                        }
                        else
                        {
                            // ConversationTemplate: prefer the Path field
                            // for uniqueness; fall back to bare asset
                            // name when Path isn't indexed.
                            var conv = asset.Conversation;
                            if (conv?.Roles is null) continue;
                            var id = !string.IsNullOrEmpty(conv.Path) ? conv.Path : asset.Name;
                            if (string.IsNullOrEmpty(id)) continue;
                            items.Add(new TemplateCloneSource { Id = id, Label = id });
                        }
                    }
                }
            }
        }

        return JsonSerializer.SerializeToElement(new TemplateCloneSourcesResult { Sources = items });
    }

    [McpTool("jiangyu_templates_conversation_roles",
        "Return the Roles (name + Guid) of a ConversationTemplate. Used by Studio's role combobox when authoring SAY/CHOICE nodes. For a cloned conversation, pass the source's templateId.")]
    [McpParam("templateId", "string", "The ConversationTemplate's identifier (asset name, e.g. \"click_bark\").")]
    internal static JsonElement TemplatesConversationRoles(JsonElement? parameters)
    {
        var templateId = RequireString(parameters, "templateId");

        var pipeline = RpcHelpers.RequireEnvironment().CreateAssetPipelineService(NullProgressSink.Instance, NullLogSink.Instance);
        var index = pipeline.LoadIndex();
        var roles = new List<TemplateConversationRole>();
        if (index?.Assets is { } assets)
        {
            // Match on Path FIRST. ConversationTemplate asset names
            // are non-unique (every speaker has a click_bark, a
            // response_death, ...); the unique identifier is Path
            // ("JeanSy/click_bark"). Fall back to bare-name match
            // only when Path isn't indexed.
            var slash = templateId.LastIndexOf('/');
            var bareName = slash >= 0 && slash < templateId.Length - 1
        ? templateId[(slash + 1)..]
        : templateId;
            Jiangyu.Core.Models.AssetEntry? pathMatch = null;
            Jiangyu.Core.Models.AssetEntry? nameMatch = null;

            foreach (var asset in assets)
            {
                var conv = asset.Conversation;
                if (conv?.Roles is null || conv.Roles.Count == 0) continue;
                if (pathMatch is null && !string.IsNullOrEmpty(conv.Path)
                    && string.Equals(conv.Path, templateId, StringComparison.Ordinal))
                {
                    pathMatch = asset;
                    break;
                }
                if (nameMatch is null && string.Equals(asset.Name, bareName, StringComparison.Ordinal))
                    nameMatch = asset;
            }

            var picked = pathMatch ?? nameMatch;
            if (picked?.Conversation?.Roles is { } pickedRoles)
            {
                foreach (var role in pickedRoles)
                    roles.Add(new TemplateConversationRole { Name = role.Name, Guid = role.Guid });
            }
        }

        return JsonSerializer.SerializeToElement(new TemplateConversationRolesResult { Roles = roles });
    }

    // Catalog loads are expensive (reflection over Assembly-CSharp.dll), and the
    // source-view parse RPC fires on every debounced keystroke. Cache the
    // catalog per assembly path so repeated parses reuse one instance; reload
    // only when the game path changes.
    private static TemplateTypeCatalog? _cachedCatalog;
    private static string? _cachedCatalogPath;
    private static readonly Lock _catalogLock = new();

    private static TemplateTypeCatalog? TryGetCachedCatalog()
    {
        var resolution = EnvironmentContext.ResolveFromGlobalConfig();
        if (!resolution.Success) return null;

        var gamePath = Path.GetDirectoryName(RpcHelpers.RequireEnvironment().GameDataPath);
        if (gamePath is null) return null;

        var assemblyPath = Path.Combine(gamePath, DefaultAssemblyRelativePath);
        if (!File.Exists(assemblyPath)) return null;

        lock (_catalogLock)
        {
            if (_cachedCatalog != null && _cachedCatalogPath == assemblyPath)
                return _cachedCatalog;

            _cachedCatalog?.Dispose();
            _cachedCatalog = null;
            _cachedCatalogPath = null;

            var additionalSearchDirs = new List<string>();
            var melonNet6 = Path.Combine(gamePath, MelonLoaderNet6RelativePath);
            if (Directory.Exists(melonNet6))
                additionalSearchDirs.Add(melonNet6);

            try
            {
                var supplement = Il2CppMetadataCache.LoadIfPresent(RpcHelpers.RequireEnvironment().CachePath);
                _cachedCatalog = TemplateTypeCatalog.Load(assemblyPath, additionalSearchDirs, supplement);
                _cachedCatalogPath = assemblyPath;
                return _cachedCatalog;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"templatesParse: failed to load template catalog: {ex.Message}");
                return null;
            }
        }
    }
    [McpTool("jiangyu_templates_project_clones",
        "List template clones already defined in the current project's templates/*.kdl files. Returns {\"clones\": [{\"templateType\", \"id\", \"file\"}, ...]}.")]
    internal static JsonElement TemplatesProjectClones(JsonElement? __)
    {
        return JsonSerializer.SerializeToElement(new { clones = EnumerateProjectClones() });
    }

    // Per-file clone cache for EnumerateProjectClones. Keyed by absolute
    // path → (mtime, parsed clones from that file). Each call stats every
    // KDL file under templates/ and only re-parses the files whose mtime
    // has shifted. Cuts the per-RPC cost from "parse every .kdl on every
    // keystroke" down to "stat every .kdl, parse only what changed".
    //
    // Concurrency: single lock around dict ops + parses. The dispatch
    // path is already single-threaded per WebView, and parses are short
    // relative to the editor's debounce, so contention is low. If
    // multiple Mcp clients ever hammer this in parallel, split the lock
    // (identify stale paths under lock, parse outside, reinsert under
    // lock with a TOCTOU re-check).
    private static readonly Dictionary<string, (DateTime MTime, List<ProjectCloneEntry> Entries)>
        _projectCloneCache = new(StringComparer.Ordinal);
    private static readonly object _projectCloneCacheLock = new();
    private static string? _projectCloneCacheRoot;

    /// <summary>
    /// Scan the active project's <c>templates/*.kdl</c> tree and surface every
    /// clone declaration as a <see cref="ProjectCloneEntry"/>. Returns an empty
    /// list when no project is open or no templates directory exists. Used by
    /// <see cref="TemplatesProjectClones"/> to feed the FieldAdder's clone
    /// dropdown and by <see cref="TemplatesParse"/> to broaden the BankId
    /// resolver beyond the document being edited so cross-file references like
    /// a squad_leader patch's <c>Stem.ID.bankId="tactical_barks_voymastina_va"</c>
    /// resolve against the voicelines.kdl clone that defines that bank.
    ///
    /// Caches per-file by mtime; opening a different project clears the
    /// cache entirely, deletions evict stale entries each scan.
    /// </summary>
    private static List<ProjectCloneEntry> EnumerateProjectClones()
    {
        var root = RpcContext.ProjectRoot;
        if (root is null) return new List<ProjectCloneEntry>();

        var templatesDir = Path.Combine(root, "templates");
        if (!Directory.Exists(templatesDir)) return new List<ProjectCloneEntry>();

        var results = new List<ProjectCloneEntry>();
        var seenPaths = new HashSet<string>(StringComparer.Ordinal);

        lock (_projectCloneCacheLock)
        {
            // Project switched: drop all cached parses. Paths from the
            // previous project would never match the new templates/
            // scan but would still occupy memory if we kept them.
            if (!string.Equals(_projectCloneCacheRoot, root, StringComparison.Ordinal))
            {
                _projectCloneCache.Clear();
                _projectCloneCacheRoot = root;
            }

            foreach (var file in Directory.EnumerateFiles(templatesDir, "*.kdl", SearchOption.AllDirectories))
            {
                seenPaths.Add(file);
                DateTime mtime;
                try { mtime = File.GetLastWriteTimeUtc(file); }
                catch { continue; }

                if (_projectCloneCache.TryGetValue(file, out var cached) && cached.MTime == mtime)
                {
                    results.AddRange(cached.Entries);
                    continue;
                }

                var entries = new List<ProjectCloneEntry>();
                try
                {
                    var text = File.ReadAllText(file);
                    var doc = KdlTemplateParser.ParseText(text);
                    var relativePath = Path.GetRelativePath(root, file).Replace('\\', '/');
                    foreach (var node in doc.Nodes)
                    {
                        if (node.Kind == KdlEditorNodeKind.Clone && !string.IsNullOrEmpty(node.CloneId))
                        {
                            entries.Add(new ProjectCloneEntry
                            {
                                TemplateType = node.TemplateType,
                                Id = node.CloneId,
                                File = relativePath,
                            });
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"EnumerateProjectClones: failed to parse '{file}': {ex.Message}");
                }

                _projectCloneCache[file] = (mtime, entries);
                results.AddRange(entries);
            }

            // Evict cache entries for files that no longer exist in the
            // scan (deleted, renamed). Keeps the dict bounded by the
            // actual templates/ tree rather than monotonically growing.
            if (_projectCloneCache.Count > seenPaths.Count)
            {
                var stale = _projectCloneCache.Keys.Where(k => !seenPaths.Contains(k)).ToList();
                foreach (var path in stale) _projectCloneCache.Remove(path);
            }
        }

        return results;
    }

    [McpTool("jiangyu_templates_enum_members",
        "Direct enum-member lookup by exact type name. Returns {\"members\": [{\"name\": \"Basic\", \"value\": 0}, ...]}. PREFER jiangyu_templates_query first — its response inlines enumMembers for any enum or NamedArray field, in one call, with no need to know the enum's name in advance. Only fall back to this tool when you already have the exact enum type name in hand (e.g. from a prior query response) and don't need the surrounding schema.")]
    [McpParam("typeName", "string", "Fully qualified or short enum type name (e.g. \"PerkTier\"). Must match the assembly exactly — do not guess. Get the correct name from jiangyu_templates_query's namedArrayEnumTypeName / enumTypeName fields.", Required = true)]
    internal static JsonElement TemplatesEnumMembers(JsonElement? parameters)
    {
        var typeName = RequireString(parameters, "typeName");

        var gamePath = Path.GetDirectoryName(RpcHelpers.RequireEnvironment().GameDataPath)
            ?? throw new InvalidOperationException("Could not derive game directory.");

        var assemblyPath = Path.Combine(gamePath, DefaultAssemblyRelativePath);
        if (!File.Exists(assemblyPath))
            throw new InvalidOperationException($"Assembly-CSharp.dll not found at: {assemblyPath}");

        var additionalSearchDirs = new List<string>();
        var melonNet6 = Path.Combine(gamePath, MelonLoaderNet6RelativePath);
        if (Directory.Exists(melonNet6))
            additionalSearchDirs.Add(melonNet6);

        using var catalog = TemplateTypeCatalog.Load(assemblyPath, additionalSearchDirs);
        var type = catalog.ResolveType(typeName, out IReadOnlyList<Type> _, out var error);
        if (type == null || !type.IsEnum)
            throw new InvalidOperationException(error ?? $"'{typeName}' is not a known enum type.");

        var members = type
            .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Select(f => new EnumMemberEntry
            {
                Name = f.Name,
                Value = Convert.ToInt32(f.GetRawConstantValue(), System.Globalization.CultureInfo.InvariantCulture),
            })
            .OrderBy(e => e.Value)
            .ToList();
        return JsonSerializer.SerializeToElement(new EnumMembersResult { Members = members });
    }

    [RpcType]
    internal sealed class EnumMembersResult
    {
        [JsonPropertyName("members")]
        public required List<EnumMemberEntry> Members { get; set; }
    }

    // Cached per-composite-type candidate-name lookups. Cleared whenever the
    // values cache is rebuilt (sound names are sourced from the bank
    // inspection cache, so the invariant is the same).
    private static readonly Dictionary<string, List<string>> _prototypeCandidatesCache = new(StringComparer.Ordinal);

    // Registry of composite types that have a working `from=` prototype
    // lookup. Maps the canonical short name to a callback that produces the
    // candidate names. The Studio UI queries this set to decide whether to
    // render the `from=` input at all; types not in the registry get no
    // input, since the candidate list would always be empty.
    //
    // Each entry's name aliases (Il2Cpp/Stem prefixes) are handled by the
    // normalisation step in TemplatesPrototypeCandidates / IsSupported;
    // only the short name appears here.
    private static readonly Dictionary<string, Func<List<string>>> _prototypeProviders = new(StringComparer.Ordinal)
    {
        { "Sound", CollectSoundCandidates },
    };

    private static string NormaliseCompositeTypeShortName(string compositeType)
    {
        if (string.IsNullOrEmpty(compositeType)) return string.Empty;
        var normalised = compositeType;
        if (normalised.StartsWith("Il2Cpp", StringComparison.Ordinal))
            normalised = normalised.Substring(6);
        return normalised.Contains('.') ? normalised.Substring(normalised.LastIndexOf('.') + 1) : normalised;
    }

    [McpTool("jiangyu_templates_prototype_supported_types",
        "List composite type short names that support `from=` prototype-source lookups. The Studio UI queries this set on first load to decide whether to render the `from=` input for a given composite; types absent from this list have no candidates and the input is suppressed. Aliases (Il2Cpp/Stem prefixes) normalise to the short names returned here.")]
    internal static JsonElement TemplatesPrototypeSupportedTypes(JsonElement? __)
    {
        return JsonSerializer.SerializeToElement(new { types = _prototypeProviders.Keys.ToArray() });
    }

    [McpTool("jiangyu_templates_prototype_candidates",
        "List candidate prototype-source names for a constructed type. Used by the visual editor's `from=` autocomplete on type= construction values; returns the names a modder could write as `from=\"X\"` to seed a fresh element from an existing one. Sound is the only constructed type with semantically-named elements (`sounds[].name` inside each SoundBank); other constructed types in MENACE are either nameless structs (SoundVariation, ID), ref-keyed elements that would need a per-type lookup (Perk, EntityLootEntry), or top-level templates that go through `clone` rather than `type=`. Empty list for those.")]
    [McpParam("compositeType", "string", "Constructed type name (e.g. \"Sound\", \"Stem.Sound\", or \"Il2CppStem.Sound\").", Required = true)]
    internal static JsonElement TemplatesPrototypeCandidates(JsonElement? parameters)
    {
        var compositeType = RequireString(parameters, "compositeType");
        var shortName = NormaliseCompositeTypeShortName(compositeType);

        if (_prototypeCandidatesCache.TryGetValue(shortName, out var cached))
            return JsonSerializer.SerializeToElement(new { candidates = cached });

        var candidates = _prototypeProviders.TryGetValue(shortName, out var provider)
            ? provider()
            : new List<string>();

        _prototypeCandidatesCache[shortName] = candidates;
        return JsonSerializer.SerializeToElement(new { candidates });
    }

    // Read pre-built sound names from the asset index. The asset-index
    // build walks each SoundBank's sounds[] array once and stores the
    // names on AssetEntry.SoundBank.NamedChildren, so this is a flat
    // dictionary lookup at RPC time, no live AssetRipper inspection, no
    // host-thread freeze, no per-call cost beyond reading the cached index.
    private static List<string> CollectSoundCandidates()
    {
        var names = new List<string>();
        var resolution = EnvironmentContext.ResolveFromGlobalConfig();
        if (!resolution.Success) return names;

        var pipeline = RpcHelpers.RequireEnvironment().CreateAssetPipelineService(NullProgressSink.Instance, NullLogSink.Instance);
        var index = pipeline.LoadIndex();
        if (index?.Assets is null) return names;

        foreach (var entry in index.Assets)
        {
            var soundBank = entry.SoundBank;
            if (soundBank?.NamedChildren is null || soundBank.NamedChildren.Count == 0)
                continue;
            if (!string.Equals(entry.ClassName, "MonoBehaviour", StringComparison.Ordinal))
                continue;
            if (entry.Name is null || !entry.Name.EndsWith("_soundbank", StringComparison.Ordinal))
                continue;

            foreach (var name in soundBank.NamedChildren)
            {
                if (!string.IsNullOrEmpty(name))
                    names.Add(name);
            }
        }

        return names;
    }

    [RpcType]
    internal sealed class TemplateCloneSourcesResult
    {
        [JsonPropertyName("sources")]
        public required List<TemplateCloneSource> Sources { get; set; }
    }

    [RpcType]
    internal sealed class TemplateCloneSource
    {
        [JsonPropertyName("id")]
        public required string Id { get; set; }

        [JsonPropertyName("label")]
        public required string Label { get; set; }
    }

    [RpcType]
    internal sealed class TemplateConversationRolesResult
    {
        [JsonPropertyName("roles")]
        public required List<TemplateConversationRole> Roles { get; set; }
    }

    [RpcType]
    internal sealed class TemplateConversationRole
    {
        [JsonPropertyName("name")]
        public required string Name { get; set; }

        [JsonPropertyName("guid")]
        public required int Guid { get; set; }
    }

    [RpcType]
    internal sealed class EnumMemberEntry
    {
        [JsonPropertyName("name")]
        public required string Name { get; set; }

        [JsonPropertyName("value")]
        public required int Value { get; set; }
    }
    private static string? ComputeMemberPatchScalarKind(TemplateTypeCatalog catalog, MemberShape m)
    {
        // Single source of truth: TemplateMemberQuery.MapScalarKind
        var memberType = m.MemberType;
        var elementType = TemplateTypeCatalog.GetElementType(memberType);
        var leafType = elementType ?? memberType;

        if (TemplateTypeCatalog.IsScalar(leafType))
            return TemplateMemberQuery.MapScalarKind(leafType)?.ToString();

        // ScriptableObject leaves split two ways:
        //  - Resolvable by name (DataTemplate descendants via DataTemplateLoader
        //    + m_ID; concrete non-DataTemplate ScriptableObjects with no
        //    subtypes via Resources.FindObjectsOfTypeAll + Object.name like
        //    PerkTreeTemplate). Both surface as TemplateReference for the
        //    modder — same authoring shape, different runtime lookup path.
        //  - Constructed inline as TypeConstruction when the leaf is an
        //    abstract base with concrete subtypes (the owned-element shape
        //    of EventHandlers, surfaced via ElementSubtypes). Returning null
        //    here routes the visual editor to the composite/handler picker.
        if (TemplateTypeCatalog.IsTemplateReferenceTarget(leafType)
            && IsByNameResolvableReference(catalog, leafType))
        {
            return "TemplateReference";
        }

        // Unity asset leaves (Sprite/Texture2D/AudioClip/Material) patch via
        // an asset reference: a single name string the loader resolves
        // against the mod-bundle catalog or the live game-asset registry.
        if (Jiangyu.Shared.Replacements.AssetCategory.IsSupported(leafType.Name))
        {
            return "AssetReference";
        }

        return null;
    }

    /// <summary>
    /// True when <paramref name="leafType"/> can be patched as a
    /// TemplateReference value: either a DataTemplate descendant (looked up
    /// by m_ID through DataTemplateLoader) or a non-abstract ScriptableObject
    /// with no concrete polymorphic descendants in the catalog (looked up by
    /// Object.name through Resources.FindObjectsOfTypeAll, e.g.
    /// PerkTreeTemplate or SpeakerTemplate). Excludes abstract bases like
    /// SkillEventHandlerTemplate whose authoring shape is construction-style
    /// (type="X" { ... }), not by-name reference.
    /// </summary>
    private static bool IsByNameResolvableReference(TemplateTypeCatalog catalog, Type leafType)
    {
        if (TemplateTypeCatalog.IsDataTemplateType(leafType)) return true;
        // Construction-style polymorphic destinations have descendants and
        // ride the ElementSubtypes / type= path instead — keep them out
        // of the ref-combobox UX so the two pickers don't both fire.
        return !catalog.HasReferenceSubtype(leafType);
    }

    private static string? ComputeElementTypeName(TemplateTypeCatalog catalog, MemberShape m)
    {
        var elementType = TemplateTypeCatalog.GetElementType(m.MemberType);
        // ResolvableName, not FriendlyName: the editor stores this as a
        // construction type= value and queries members against it, so a short
        // name that collides across namespaces (Il2CppStem.ID) must come back
        // as the full name that resolves unambiguously.
        return elementType != null ? catalog.ResolvableName(elementType) : null;
    }

    private static string? ComputeEnumTypeName(TemplateTypeCatalog catalog, MemberShape m)
    {
        var memberType = m.MemberType;
        var elementType = TemplateTypeCatalog.GetElementType(memberType);
        var leafType = elementType ?? memberType;
        return leafType.IsEnum ? catalog.FriendlyName(leafType) : null;
    }

    /// <summary>
    /// Resolves the enum paired with this member (either as a regular enum
    /// scalar/element or via <c>[NamedArray(typeof(T))]</c>) and returns its
    /// members as <see cref="EnumMemberEntry"/> pairs. Inlined on member
    /// schema so the visual editor doesn't need a follow-up
    /// <c>templatesEnumMembers</c> RPC for either dropdown surface.
    /// </summary>
    private static List<EnumMemberEntry>? ComputeEnumMembers(TemplateTypeCatalog catalog, MemberShape m)
    {
        var memberType = m.MemberType;
        var elementType = TemplateTypeCatalog.GetElementType(memberType);
        var leafType = elementType ?? memberType;
        Type? enumType = leafType.IsEnum ? leafType : null;

        if (enumType is null && m.NamedArrayEnumTypeName is not null)
        {
            enumType = catalog.ResolveType(m.NamedArrayEnumTypeName, out _, out _);
        }

        return enumType is { IsEnum: true } ? BuildEnumMembers(enumType) : null;
    }

    private static List<EnumMemberEntry> BuildEnumMembers(Type enumType)
    {
        return [.. enumType
            .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Select(f => new EnumMemberEntry
            {
        Name = f.Name,
        Value = Convert.ToInt32(f.GetRawConstantValue(), System.Globalization.CultureInfo.InvariantCulture),
            })
            .OrderBy(e => e.Value)];
    }

    private static string? ComputeReferenceTypeName(TemplateTypeCatalog catalog, MemberShape m)
    {
        var memberType = m.MemberType;
        var elementType = TemplateTypeCatalog.GetElementType(memberType);
        var leafType = elementType ?? memberType;
        // Mirror ComputeMemberPatchScalarKind. Both DataTemplate-backed and
        // concrete non-DataTemplate ScriptableObject refs get a name so the
        // visual editor can hide the redundant Type combobox on monomorphic
        // destinations. Owned ScriptableObject collections (EventHandlers)
        // route through ElementSubtypes instead, surfaced separately.
        return TemplateTypeCatalog.IsTemplateReferenceTarget(leafType)
            && IsByNameResolvableReference(catalog, leafType)
            ? catalog.FriendlyName(leafType) : null;
    }

    /// <summary>
    /// True when the destination's reference target has descendant reference
    /// types in the assembly, so the modder needs to disambiguate with an
    /// explicit concrete type. The editor uses this to keep the ref-type
    /// combobox visible. Structural check (any strict ref-target descendant)
    /// rather than <see cref="Type.IsAbstract"/> because Il2CppInterop
    /// wrappers strip the abstract bit on generation. Null when the field is
    /// not a reference target or the leaf type is the only ref target in its
    /// branch (selector hidden).
    /// </summary>
    private static bool? ComputeReferenceTypeIsPolymorphic(
        TemplateTypeCatalog catalog,
        MemberShape m,
        IReadOnlySet<string> instantiatedClassNames)
    {
        var memberType = m.MemberType;
        var elementType = TemplateTypeCatalog.GetElementType(memberType);
        var leafType = elementType ?? memberType;
        if (!TemplateTypeCatalog.IsTemplateReferenceTarget(leafType))
            return null;
        // Construction-style polymorphism (owned ScriptableObject lists like
        // EventHandlers) is reported via ElementSubtypes; the ref-polymorphism
        // flag is reserved for DataTemplate-backed lookups so the visual
        // editor's two pickers don't both fire on the same member.
        if (!TemplateTypeCatalog.IsDataTemplateType(leafType))
            return null;
        if (!catalog.HasReferenceSubtype(leafType))
            return null;
        // Refine: structural subtype existence alone over-flags concrete
        // first-class types whose descendants happen to inherit from them
        // (e.g. PerkTemplate : SkillTemplate, even though SkillTemplate has
        // its own 500+ instances and modders don't pick a subtype here).
        // Only treat as polymorphic when the leaf type has zero direct
        // instances in the index, the same shape an abstract base like
        // BaseItemTemplate has.
        var leafFriendlyName = catalog.FriendlyName(leafType);
        if (instantiatedClassNames.Contains(leafFriendlyName))
            return null;
        return true;
    }

    /// <summary>
    /// Discriminator strings the visual editor's combobox shows for a
    /// tagged-string field. Drawn from the indexed allowlist
    /// (<see cref="TaggedDiscriminatorIndex"/>) so the picker only
    /// surfaces forms vanilla's runtime accepts.
    /// </summary>
    private static List<string>? ComputeTaggedDiscriminators(MemberShape m)
    {
        if (m.TaggedPolymorphicBase is not { } baseType) return null;
        var fqn = baseType.FullName ?? baseType.Name;
        var allowed = TaggedDiscriminatorIndex.GetAllowed(fqn);
        if (allowed is null || allowed.Count == 0) return null;
        return [.. allowed.OrderBy(s => s, StringComparer.Ordinal)];
    }

    /// <summary>
    /// Concrete subtypes the modder can pick when appending to a polymorphic
    /// owned-element collection (e.g. EventHandlers). Populated only for
    /// collections whose element type is a non-DataTemplate ScriptableObject
    /// base with strict descendants — this is the construction-style
    /// polymorphism (the modder builds a fresh subordinate object), distinct
    /// from ref-style polymorphism where the modder picks an existing
    /// DataTemplateLoader instance. Null otherwise so the visual editor falls
    /// through to the standard composite/ref flow.
    /// </summary>
    private static List<string>? ComputeElementSubtypes(TemplateTypeCatalog catalog, MemberShape m)
    {
        var elementType = TemplateTypeCatalog.GetElementType(m.MemberType);
        if (elementType is null) return null;
        if (!TemplateTypeCatalog.IsTemplateReferenceTarget(elementType)) return null;
        if (TemplateTypeCatalog.IsDataTemplateType(elementType)) return null;
        if (!catalog.HasReferenceSubtype(elementType)) return null;

        var subtypes = catalog.EnumerateConcreteSubtypes(elementType);
        if (subtypes.Count == 0) return null;
        return [.. subtypes
            .Select(catalog.FriendlyName)
            .OrderBy(n => n, StringComparer.Ordinal)];
    }

    /// <summary>
    /// Concrete subtype choices for a polymorphic scalar field (declared
    /// type is itself an interface or abstract base with concrete
    /// descendants, but isn't a Unity-asset reference target — that case
    /// goes through ref= picking instead). The visual editor surfaces these
    /// as a "Pick handler" combobox the same way it does for collection
    /// elements; the resulting patch is a Set with a TypeConstruction
    /// value. Used for Odin-routed scalar fields like
    /// <c>Attack.DamageFilterCondition: ITacticalCondition</c>.
    /// </summary>
    private static List<string>? ComputeScalarSubtypes(TemplateTypeCatalog catalog, MemberShape m)
    {
        var memberType = m.MemberType;
        if (TemplateTypeCatalog.GetElementType(memberType) != null) return null;
        if (TemplateTypeCatalog.IsScalar(memberType)) return null;
        // ScriptableObject / DataTemplate-typed scalars are picked via ref=,
        // not constructed via type=. EnumerateConcreteSubtypes on its own
        // doesn't filter those out, so we exclude them explicitly.
        if (TemplateTypeCatalog.IsTemplateReferenceTarget(memberType)) return null;
        if (TemplateTypeCatalog.IsDataTemplateType(memberType)) return null;
        // Catch-all wrapper types describe "any IL2CPP object" — every game
        // class is a "subtype" of these in the catalogue's view, which is
        // both useless to a modder (thousands of irrelevant choices) and a
        // wrong signal that the field is constructible. Multi-dimensional
        // arrays land here too because GetElementType only recognises 1D
        // collections; a richer editor for those is a separate task.
        if (IsCatchAllRuntimeType(memberType)) return null;

        // Use the broad concrete-descendant set rather than HasReferenceSubtype:
        // Odin-routed interface fields (e.g. ITacticalCondition) typically
        // have plain-managed-class concrete impls, not ScriptableObjects.
        var subtypes = catalog.EnumerateConcreteSubtypes(memberType);
        if (subtypes.Count == 0) return null;
        return [.. subtypes
            .Select(catalog.FriendlyName)
            .OrderBy(n => n, StringComparer.Ordinal)];
    }

    /// <summary>
    /// True for runtime "any-object" wrapper types whose subtypes span the
    /// entire game-type graph and so don't represent a meaningful authoring
    /// choice. Surfacing them as polymorphic-construct destinations would
    /// dump the modder into a thousands-long picker that doesn't compose
    /// to a usable value.
}
