using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using DataTemplateLoader = Il2CppMenace.Tools.DataTemplateLoader;
using DataTemplate = Il2CppMenace.Tools.DataTemplate;
using EntityTemplate = Il2CppMenace.Tactical.EntityTemplate;
using Il2CppEnumerable = Il2CppSystem.Collections.IEnumerable;
using Il2CppEnumerator = Il2CppSystem.Collections.IEnumerator;
using Il2CppDictionary = Il2CppSystem.Collections.Generic.Dictionary<string, Il2CppMenace.Tools.DataTemplate>;
using MelonLoader;
using MelonLoader.Utils;
using UnityEngine;
using UnityEngine.UI;

namespace Jiangyu.Loader.Diagnostics;

/// <summary>
/// Opt-in runtime inspection that dumps live scene identity plus selected
/// template snapshots to disk for investigation of why a replacement or future
/// template patch is or isn't landing.
///
/// Enabled by the presence of <c>&lt;UserData&gt;/jiangyu-inspect.flag</c>
/// (any contents; empty is fine). Remove the flag to disable. While enabled,
/// a JSON dump is written to
/// <c>&lt;UserData&gt;/jiangyu-inspect/&lt;timestamp&gt;-&lt;scene&gt;.json</c>
/// on each scene load, and template probes may emit additional JSON files into
/// the same directory.
///
/// Not a replacement mechanism - observation only. Dumps both scene-scoped
/// components and Resources.FindObjectsOfTypeAll asset lists, so atlas-packed
/// sprites and off-scene-cached assets are visible even though the main
/// replacement sweeps can't see the latter.
/// </summary>
internal static class RuntimeInspector
{
    private const string FlagFileName = "jiangyu-inspect.flag";
    private const string OutputDirectoryName = "jiangyu-inspect";
    private static int _entityTemplateDumpSequence;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static bool IsEnabled()
    {
        try
        {
            return File.Exists(Path.Combine(MelonEnvironment.UserDataDirectory, FlagFileName));
        }
        catch
        {
            return false;
        }
    }

    public static void Dump(string sceneName, int buildIndex, MelonLogger.Instance log)
    {
        try
        {
            var timestamp = DateTime.UtcNow.ToString("yyyy-MM-dd-HH-mm-ss");
            var safeSceneName = SanitiseForFileName(sceneName);
            var filePath = Path.Combine(GetOutputDirectory(), $"{timestamp}-{safeSceneName}.json");

            var dump = BuildDump(sceneName, buildIndex);
            File.WriteAllText(filePath, JsonSerializer.Serialize(dump, JsonOptions));

            log.Msg(
                $"[inspect] Wrote runtime dump: {filePath}  " +
                $"(sprite renderers={dump.SpriteRenderers.Count}  " +
                $"ui images={dump.UiImages.Count}  " +
                $"sprite assets={dump.SpriteAssets.Count}  " +
                $"audio sources={dump.AudioSources.Count}  " +
                $"audio clips={dump.AudioClipAssets.Count})");
        }
        catch (Exception ex)
        {
            log.Error($"[inspect] dump failed: {ex}");
        }
    }

    public static bool TryDumpEntityTemplatesFromLoader(string sceneName, MelonLogger.Instance log)
    {
        if (!IsEnabled())
            return false;

        try
        {
            var templateCollection = GetAllEntityTemplateCollection();
            if (templateCollection == null)
            {
                log.Warning("[inspect] EntityTemplate probe: DataTemplateLoader.GetAll<EntityTemplate>() returned null.");
                return false;
            }

            var materialisedTemplates = MaterialiseEntityTemplates(templateCollection);

            if (materialisedTemplates.Count == 0)
            {
                var reportedCount = TryReadCollectionCount(templateCollection);
                log.Warning(
                    $"[inspect] EntityTemplate probe: DataTemplateLoader.GetAll<EntityTemplate>() materialised 0 templates (reported count={reportedCount?.ToString() ?? "unknown"}).");
                return false;
            }

            var sequence = Interlocked.Increment(ref _entityTemplateDumpSequence);
            var timestamp = DateTime.UtcNow.ToString("yyyy-MM-dd-HH-mm-ss-fff");
            var safeSceneName = SanitiseForFileName(sceneName);
            var filePath = Path.Combine(GetOutputDirectory(), $"{timestamp}-{safeSceneName}-entity-templates-{sequence:D2}.json");

            var dump = BuildEntityTemplateDump(sceneName, materialisedTemplates, null);
            File.WriteAllText(filePath, JsonSerializer.Serialize(dump, JsonOptions));

            log.Msg(
                $"[inspect] Wrote entity-template dump: {filePath}  " +
                $"(templates={dump.TemplateCount}  mapEntries={dump.MapCount}  withId={dump.TemplatesWithId}  " +
                $"withCollection={dump.TemplatesWithCollection}  withPathId={dump.TemplatesWithPathId})");

            return true;
        }
        catch (Exception ex)
        {
            log.Error($"[inspect] entity-template dump failed: {ex}");
            return false;
        }
    }

    private static object GetAllEntityTemplateCollection()
    {
        var loaderType = typeof(DataTemplateLoader);
        var entityTemplateType = loaderType.Assembly.GetTypes()
            .FirstOrDefault(type => type.Name == nameof(EntityTemplate) && !type.IsAbstract) ?? typeof(EntityTemplate);

        var getAllMethod = loaderType
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Single(method => method.Name == "GetAll" &&
                              method.IsGenericMethodDefinition &&
                              method.GetParameters().Length == 0);

        return getAllMethod.MakeGenericMethod(entityTemplateType).Invoke(null, null);
    }

    private static List<EntityTemplate> MaterialiseEntityTemplates(object templateCollection)
    {
        var results = new List<EntityTemplate>();

        if (templateCollection is Il2CppObjectBase il2CppCollection)
        {
            try
            {
                var il2CppEnumerable = il2CppCollection.TryCast<Il2CppEnumerable>();
                if (il2CppEnumerable != null)
                {
                    MaterialiseEntityTemplates(il2CppEnumerable.GetEnumerator(), results);
                    if (results.Count > 0)
                        return results;
                }
            }
            catch
            {
            }
        }

        try
        {
            var getEnumeratorMethod = templateCollection.GetType()
                .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(method => method.Name == "GetEnumerator" && method.GetParameters().Length == 0);

            if (getEnumeratorMethod != null)
            {
                var enumerator = getEnumeratorMethod.Invoke(templateCollection, null);
                if (enumerator != null)
                {
                    var enumeratorType = enumerator.GetType();
                    var moveNextMethod = enumeratorType.GetMethod("MoveNext", BindingFlags.Public | BindingFlags.Instance);
                    var currentProperty = enumeratorType.GetProperty("Current", BindingFlags.Public | BindingFlags.Instance);

                    if (moveNextMethod != null && currentProperty != null)
                    {
                        while ((bool)moveNextMethod.Invoke(enumerator, null))
                        {
                            AddAsEntityTemplate(results, currentProperty.GetValue(enumerator));
                        }

                        if (results.Count > 0)
                            return results;
                    }
                }
            }
        }
        catch
        {
        }

        try
        {
            if (templateCollection is System.Collections.IEnumerable managedEnumerable)
            {
                foreach (var item in managedEnumerable)
                {
                    AddAsEntityTemplate(results, item);
                }

                if (results.Count > 0)
                    return results;
            }
        }
        catch
        {
        }

        return results;
    }

    private static void AddAsEntityTemplate(List<EntityTemplate> destination, object item)
    {
        if (item == null)
            return;

        if (item is EntityTemplate template)
        {
            destination.Add(template);
            return;
        }

        if (item is Il2CppObjectBase il2CppItem)
        {
            var cast = il2CppItem.TryCast<EntityTemplate>();
            if (cast != null)
                destination.Add(cast);
        }
    }

    private static int? TryReadCollectionCount(object collection)
    {
        if (collection == null)
            return null;

        try
        {
            var countProperty = collection.GetType().GetProperty("Count", BindingFlags.Public | BindingFlags.Instance);
            if (countProperty == null)
                return null;

            var countValue = countProperty.GetValue(collection);
            return countValue == null ? null : Convert.ToInt32(countValue);
        }
        catch
        {
            return null;
        }
    }

    private static void MaterialiseEntityTemplates(Il2CppEnumerator enumerator, List<EntityTemplate> destination)
    {
        if (enumerator == null)
            return;

        while (enumerator.MoveNext())
        {
            var current = enumerator.Current;
            var template = current?.TryCast<EntityTemplate>();
            if (template != null)
                destination.Add(template);
        }
    }

    private static RuntimeDump BuildDump(string sceneName, int buildIndex)
    {
        var dump = new RuntimeDump
        {
            Timestamp = DateTimeOffset.UtcNow,
            SceneName = sceneName,
            SceneBuildIndex = buildIndex,
        };

        foreach (var obj in UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<SpriteRenderer>(), true))
        {
            var renderer = obj.Cast<SpriteRenderer>();
            dump.SpriteRenderers.Add(new SpriteRendererInfo
            {
                GameObjectPath = GameObjectPath(renderer?.gameObject),
                SpriteName = renderer?.sprite?.name,
                Enabled = renderer?.enabled ?? false,
            });
        }

        foreach (var obj in UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<Image>(), true))
        {
            var image = obj.Cast<Image>();
            dump.UiImages.Add(new UiImageInfo
            {
                GameObjectPath = GameObjectPath(image?.gameObject),
                SpriteName = image?.sprite?.name,
                Enabled = image?.enabled ?? false,
            });
        }

        // Diagnostic counts for why UiImages / SpriteRenderers may come back empty.
        // If Canvas > 0 but Graphic == 0, UI roots exist but Graphic components
        // haven't awoken at scene-load (timing). If Canvas == 0, UI isn't loaded
        // into the scene yet. If Graphic > 0 but Image == 0, Image type resolution
        // is broken.
        dump.Counts.Canvases = CountOfType(Il2CppType.Of<Canvas>());
        dump.Counts.Graphics = CountOfType(Il2CppType.Of<Graphic>());
        dump.Counts.RawImages = CountOfType(Il2CppType.Of<RawImage>());
        dump.Counts.UiDocuments = CountOfType(Il2CppType.Of<UnityEngine.UIElements.UIDocument>());
        dump.Counts.TotalGameObjects = CountOfType(Il2CppType.Of<GameObject>());

        foreach (var obj in Resources.FindObjectsOfTypeAll(Il2CppType.Of<Sprite>()))
        {
            var sprite = obj.Cast<Sprite>();
            if (sprite == null)
                continue;
            dump.SpriteAssets.Add(new SpriteAssetInfo
            {
                Name = sprite.name,
                TextureName = sprite.texture?.name,
            });
        }

        foreach (var obj in Resources.FindObjectsOfTypeAll(Il2CppType.Of<Texture2D>()))
        {
            var texture = obj?.TryCast<Texture2D>();
            if (texture == null)
                continue;
            dump.TextureAssets.Add(new TextureAssetInfo
            {
                Name = texture.name,
                Width = texture.width,
                Height = texture.height,
                Format = texture.format.ToString(),
                HideFlags = texture.hideFlags.ToString(),
            });
        }

        foreach (var obj in UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<SkinnedMeshRenderer>(), true))
        {
            var smr = obj?.TryCast<SkinnedMeshRenderer>();
            if (smr == null)
                continue;
            dump.SkinnedMeshRenderers.Add(new SkinnedMeshRendererInfo
            {
                GameObjectPath = GameObjectPath(smr.gameObject),
                MeshName = smr.sharedMesh?.name,
                BoneCount = smr.bones?.Length ?? 0,
                RootBoneName = smr.rootBone?.name,
                HideFlags = smr.hideFlags.ToString(),
                SceneLoaded = smr.gameObject != null && smr.gameObject.scene.isLoaded,
            });
        }

        foreach (var obj in UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<AudioSource>(), true))
        {
            var source = obj.Cast<AudioSource>();
            dump.AudioSources.Add(new AudioSourceInfo
            {
                GameObjectPath = GameObjectPath(source?.gameObject),
                ClipName = source?.clip?.name,
                PlayOnAwake = source?.playOnAwake ?? false,
            });
        }

        foreach (var obj in Resources.FindObjectsOfTypeAll(Il2CppType.Of<AudioClip>()))
        {
            var clip = obj.Cast<AudioClip>();
            if (clip == null)
                continue;
            dump.AudioClipAssets.Add(new AudioClipAssetInfo
            {
                Name = clip.name,
                LengthSeconds = clip.length,
            });
        }

        return dump;
    }

    private static EntityTemplateDump BuildEntityTemplateDump(
        string sceneName,
        IReadOnlyCollection<EntityTemplate> templates,
        Il2CppDictionary templateMap)
    {
        var dump = new EntityTemplateDump
        {
            Timestamp = DateTimeOffset.UtcNow,
            SceneName = sceneName,
            TemplateCount = templates?.Count ?? 0,
            MapCount = templateMap?.Count ?? 0,
        };

        var keysByPointer = BuildMapKeysByPointer(templateMap);
        if (templates == null)
            return dump;

        var seenTypes = new HashSet<Type>();

        foreach (var template in templates)
        {
            if (template == null)
                continue;

            var templateType = template.GetType();
            if (seenTypes.Add(templateType))
                dump.RuntimeTypes.Add(BuildRuntimeTypeShape(templateType));

            var nativePointer = GetNativePointer(template);
            var info = new EntityTemplateInfo
            {
                NativePointer = nativePointer == IntPtr.Zero ? null : $"0x{nativePointer.ToInt64():X}",
                RuntimeType = templateType.FullName,
                Id = TryReadStringMember(template, out var idMember, "ID", "m_ID", "Id", "id"),
                IdMember = idMember,
                Name = TryReadStringMember(template, out var nameMember, "Name", "m_Name", "name"),
                NameMember = nameMember,
                EntityType = template.Type.ToString(),
                ActorType = template.ActorType.ToString(),
                StructureType = template.StructureType.ToString(),
                Collection = TryReadStringMember(template, out var collectionMember, "Collection", "m_Collection", "collection"),
                CollectionMember = collectionMember,
                PathId = TryReadLongMember(template, out var pathIdMember, "PathId", "m_PathId", "pathId"),
                PathIdMember = pathIdMember,
            };

            if (nativePointer != IntPtr.Zero && keysByPointer.TryGetValue(nativePointer, out var keys))
            {
                info.MapKeys.AddRange(keys);
            }

            if (!string.IsNullOrWhiteSpace(info.Id))
                dump.TemplatesWithId++;
            if (!string.IsNullOrWhiteSpace(info.Collection))
                dump.TemplatesWithCollection++;
            if (info.PathId.HasValue)
                dump.TemplatesWithPathId++;
            if (!string.IsNullOrWhiteSpace(info.Name))
                dump.TemplatesWithName++;
            if (info.MapKeys.Count > 0)
                dump.TemplatesWithMapKeyMatch++;

            dump.Templates.Add(info);
        }

        return dump;
    }

    // Shape-only (no value reads) so we don't trip Il2Cpp proxy accessors. Walks
    // the runtime wrapper up to (but not including) Il2CppObjectBase, which
    // filters interop-base members like `Pointer`/`WasCollected` out of the
    // modder-facing view while keeping the actual MENACE template surface.
    private static RuntimeTypeShape BuildRuntimeTypeShape(Type templateType)
    {
        var shape = new RuntimeTypeShape
        {
            TypeFullName = templateType.FullName,
        };

        var seenMembers = new HashSet<string>(StringComparer.Ordinal);
        const BindingFlags flags = BindingFlags.Instance
            | BindingFlags.Public
            | BindingFlags.NonPublic
            | BindingFlags.DeclaredOnly;

        for (var current = templateType; current != null && current != typeof(Il2CppObjectBase) && current != typeof(object); current = current.BaseType)
        {
            foreach (var property in current.GetProperties(flags))
            {
                if (property.GetIndexParameters().Length != 0)
                    continue;

                var key = "P:" + property.Name;
                if (!seenMembers.Add(key))
                    continue;

                shape.Members.Add(new RuntimeTypeMemberShape
                {
                    Name = property.Name,
                    Kind = "Property",
                    TypeName = property.PropertyType.FullName ?? property.PropertyType.Name,
                    IsWritable = property.CanWrite,
                });
            }

            foreach (var field in current.GetFields(flags))
            {
                var key = "F:" + field.Name;
                if (!seenMembers.Add(key))
                    continue;

                shape.Members.Add(new RuntimeTypeMemberShape
                {
                    Name = field.Name,
                    Kind = "Field",
                    TypeName = field.FieldType.FullName ?? field.FieldType.Name,
                    IsWritable = !field.IsInitOnly,
                });
            }
        }

        shape.Members.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        return shape;
    }

    private static Dictionary<IntPtr, List<string>> BuildMapKeysByPointer(Il2CppDictionary templateMap)
    {
        var result = new Dictionary<IntPtr, List<string>>();
        if (templateMap == null)
            return result;

        foreach (var pair in templateMap)
        {
            if (pair.Value == null)
                continue;

            var pointer = GetNativePointer(pair.Value);
            if (pointer == IntPtr.Zero)
                continue;

            if (!result.TryGetValue(pointer, out var keys))
            {
                keys = new List<string>();
                result[pointer] = keys;
            }

            keys.Add(pair.Key);
        }

        return result;
    }

    private static string TryReadStringMember(object instance, out string memberName, params string[] candidates)
    {
        var value = TryReadMemberValue(instance, out memberName, candidates);
        return value?.ToString();
    }

    private static long? TryReadLongMember(object instance, out string memberName, params string[] candidates)
    {
        var value = TryReadMemberValue(instance, out memberName, candidates);
        if (value == null)
            return null;

        try
        {
            return Convert.ToInt64(value);
        }
        catch
        {
            return null;
        }
    }

    private static object TryReadMemberValue(object instance, out string memberName, params string[] candidates)
    {
        memberName = null;
        if (instance == null || candidates == null || candidates.Length == 0)
            return null;

        var type = instance.GetType();
        foreach (var candidate in candidates)
        {
            for (var current = type; current != null; current = current.BaseType)
            {
                var property = current.GetProperty(
                    candidate,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                if (property != null && property.GetIndexParameters().Length == 0)
                {
                    try
                    {
                        memberName = property.Name;
                        return property.GetValue(instance);
                    }
                    catch
                    {
                        return null;
                    }
                }

                var field = current.GetField(
                    candidate,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                if (field != null)
                {
                    try
                    {
                        memberName = field.Name;
                        return field.GetValue(instance);
                    }
                    catch
                    {
                        return null;
                    }
                }
            }
        }

        return null;
    }

    private static IntPtr GetNativePointer(object instance)
    {
        if (instance is not Il2CppObjectBase il2CppObject)
            return IntPtr.Zero;

        return IL2CPP.Il2CppObjectBaseToPtr(il2CppObject);
    }

    private static int CountOfType(Il2CppSystem.Type type)
    {
        var count = 0;
        foreach (var _ in UnityEngine.Object.FindObjectsOfType(type, true))
        {
            count++;
        }
        return count;
    }

    private static string GameObjectPath(GameObject gameObject)
    {
        if (gameObject == null)
            return null;

        var parts = new List<string>();
        var transform = gameObject.transform;
        while (transform != null)
        {
            parts.Add(transform.name);
            transform = transform.parent;
        }
        parts.Reverse();
        return "/" + string.Join("/", parts);
    }

    private static string SanitiseForFileName(string name)
    {
        if (string.IsNullOrEmpty(name))
            return "unknown";
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new System.Text.StringBuilder(name.Length);
        foreach (var c in name)
        {
            builder.Append(Array.IndexOf(invalid, c) >= 0 || char.IsWhiteSpace(c) ? '_' : c);
        }
        return builder.ToString();
    }

    private static string GetOutputDirectory()
    {
        var outDir = Path.Combine(MelonEnvironment.UserDataDirectory, OutputDirectoryName);
        Directory.CreateDirectory(outDir);
        return outDir;
    }

    private sealed class RuntimeDump
    {
        public DateTimeOffset Timestamp { get; set; }
        public string SceneName { get; set; }
        public int SceneBuildIndex { get; set; }
        public DiagnosticCounts Counts { get; } = new();
        public List<SpriteRendererInfo> SpriteRenderers { get; } = new();
        public List<UiImageInfo> UiImages { get; } = new();
        public List<SpriteAssetInfo> SpriteAssets { get; } = new();
        public List<TextureAssetInfo> TextureAssets { get; } = new();
        public List<SkinnedMeshRendererInfo> SkinnedMeshRenderers { get; } = new();
        public List<AudioSourceInfo> AudioSources { get; } = new();
        public List<AudioClipAssetInfo> AudioClipAssets { get; } = new();
    }

    private sealed class EntityTemplateDump
    {
        public DateTimeOffset Timestamp { get; set; }
        public string SceneName { get; set; }
        public int TemplateCount { get; set; }
        public int MapCount { get; set; }
        public int TemplatesWithId { get; set; }
        public int TemplatesWithName { get; set; }
        public int TemplatesWithCollection { get; set; }
        public int TemplatesWithPathId { get; set; }
        public int TemplatesWithMapKeyMatch { get; set; }
        public List<RuntimeTypeShape> RuntimeTypes { get; } = new();
        public List<EntityTemplateInfo> Templates { get; } = new();
    }

    private sealed class RuntimeTypeShape
    {
        public string TypeFullName { get; set; }
        public List<RuntimeTypeMemberShape> Members { get; } = new();
    }

    private sealed class RuntimeTypeMemberShape
    {
        public string Name { get; set; }
        public string Kind { get; set; }
        public string TypeName { get; set; }
        public bool IsWritable { get; set; }
    }

    private sealed class EntityTemplateInfo
    {
        public string RuntimeType { get; set; }
        public string NativePointer { get; set; }
        public string Id { get; set; }
        public string IdMember { get; set; }
        public string Name { get; set; }
        public string NameMember { get; set; }
        public string EntityType { get; set; }
        public string ActorType { get; set; }
        public string StructureType { get; set; }
        public string Collection { get; set; }
        public string CollectionMember { get; set; }
        public long? PathId { get; set; }
        public string PathIdMember { get; set; }
        public List<string> MapKeys { get; } = new();
    }

    private sealed class SkinnedMeshRendererInfo
    {
        public string GameObjectPath { get; set; }
        public string MeshName { get; set; }
        public int BoneCount { get; set; }
        public string RootBoneName { get; set; }
        public string HideFlags { get; set; }
        public bool SceneLoaded { get; set; }
    }

    private sealed class TextureAssetInfo
    {
        public string Name { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public string Format { get; set; }
        public string HideFlags { get; set; }
    }

    private sealed class DiagnosticCounts
    {
        public int Canvases { get; set; }
        public int Graphics { get; set; }
        public int RawImages { get; set; }
        public int UiDocuments { get; set; }
        public int TotalGameObjects { get; set; }
    }

    private sealed class SpriteRendererInfo
    {
        public string GameObjectPath { get; set; }
        public string SpriteName { get; set; }
        public bool Enabled { get; set; }
    }

    private sealed class UiImageInfo
    {
        public string GameObjectPath { get; set; }
        public string SpriteName { get; set; }
        public bool Enabled { get; set; }
    }

    private sealed class SpriteAssetInfo
    {
        public string Name { get; set; }
        public string TextureName { get; set; }
    }

    private sealed class AudioSourceInfo
    {
        public string GameObjectPath { get; set; }
        public string ClipName { get; set; }
        public bool PlayOnAwake { get; set; }
    }

    private sealed class AudioClipAssetInfo
    {
        public string Name { get; set; }
        public float LengthSeconds { get; set; }
    }
}
