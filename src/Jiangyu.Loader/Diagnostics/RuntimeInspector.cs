using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using Jiangyu.Loader.Templates;
using DataTemplateLoader = Il2CppMenace.Tools.DataTemplateLoader;
using DataTemplate = Il2CppMenace.Tools.DataTemplate;
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
/// Enabled by a flag file in <c>&lt;UserData&gt;</c>:
/// <list type="bullet">
///   <item><c>jiangyu-inspect.flag</c> — unlimited retention (dumps accumulate forever).</item>
///   <item><c>jiangyu-inspect.&lt;N&gt;.flag</c> — rolling retention, keep at most N files
///     in <c>&lt;UserData&gt;/jiangyu-inspect/</c>; oldest are deleted after each write.</item>
/// </list>
/// File contents are ignored. Remove the flag to disable. Numbered flag wins if both exist.
///
/// Not a replacement mechanism - observation only. Dumps both scene-scoped
/// components and Resources.FindObjectsOfTypeAll asset lists, so atlas-packed
/// sprites and off-scene-cached assets are visible even though the main
/// replacement sweeps can't see the latter.
/// </summary>
internal static class RuntimeInspector
{
    private const string PlainFlagFileName = "jiangyu-inspect.flag";
    private const string NumberedFlagPattern = "jiangyu-inspect.*.flag";
    private const string OutputDirectoryName = "jiangyu-inspect";
    private const int MaxByteArrayElements = 64;
    private const int MaxCollectionElementSummaries = 16;
    private static int _templatesDumpSequence;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static bool IsEnabled() => ResolveFlag().Enabled;

    // Numbered flag wins when both are present. A non-positive or unparseable
    // N is silently ignored so a stray `jiangyu-inspect.ignored.flag` does not
    // disable the plain flag.
    private static FlagState ResolveFlag()
    {
        try
        {
            var userData = MelonEnvironment.UserDataDirectory;
            var numbered = Directory.GetFiles(userData, NumberedFlagPattern);
            foreach (var path in numbered)
            {
                var name = Path.GetFileName(path);
                if (string.Equals(name, PlainFlagFileName, StringComparison.Ordinal))
                    continue;
                if (TryParseRetention(name, out var cap))
                    return new FlagState(true, cap);
            }

            if (File.Exists(Path.Combine(userData, PlainFlagFileName)))
                return new FlagState(true, null);

            return new FlagState(false, null);
        }
        catch
        {
            return new FlagState(false, null);
        }
    }

    private static bool TryParseRetention(string fileName, out int cap)
    {
        cap = 0;
        const string prefix = "jiangyu-inspect.";
        const string suffix = ".flag";
        if (!fileName.StartsWith(prefix, StringComparison.Ordinal)
            || !fileName.EndsWith(suffix, StringComparison.Ordinal))
            return false;
        var middle = fileName.Substring(prefix.Length, fileName.Length - prefix.Length - suffix.Length);
        return int.TryParse(middle, out cap) && cap > 0;
    }

    private readonly struct FlagState
    {
        public FlagState(bool enabled, int? retentionCap)
        {
            Enabled = enabled;
            RetentionCap = retentionCap;
        }

        public bool Enabled { get; }
        public int? RetentionCap { get; }
    }

    // Per-kind LRU-by-name sweep. Files are bucketed by filename suffix so a
    // frequent kind (the periodic runtime sweep fires every 300 frames after
    // t=600) cannot evict a rare kind (templates dump, once per scene load).
    // Filenames are UTC timestamp-prefixed, so alphanumeric ordering within a
    // bucket equals chronological ordering. Called once per write, after the
    // new file lands, so cap=N means "at most N files per kind survive".
    private static void EnforceRetention(int? cap, MelonLogger.Instance log)
    {
        if (!cap.HasValue)
            return;

        try
        {
            var outDir = GetOutputDirectory();
            var files = new DirectoryInfo(outDir).GetFiles();
            if (files.Length == 0)
                return;

            var buckets = new Dictionary<string, List<FileInfo>>(StringComparer.Ordinal);
            foreach (var file in files)
            {
                var kind = ClassifyDumpKind(file.Name);
                if (!buckets.TryGetValue(kind, out var bucket))
                {
                    bucket = new List<FileInfo>();
                    buckets[kind] = bucket;
                }
                bucket.Add(file);
            }

            foreach (var bucket in buckets.Values)
            {
                if (bucket.Count <= cap.Value)
                    continue;

                bucket.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
                var toDelete = bucket.Count - cap.Value;
                for (var i = 0; i < toDelete; i++)
                {
                    try { bucket[i].Delete(); }
                    catch (Exception ex)
                    {
                        log.Warning($"[inspect] rotation: could not delete {bucket[i].Name}: {ex.Message}");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            log.Warning($"[inspect] rotation sweep failed: {ex.Message}");
        }
    }

    private static string ClassifyDumpKind(string fileName)
    {
        if (fileName.Contains("-templates-", StringComparison.Ordinal))
            return "templates";
        return "runtime";
    }

    public static void Dump(string sceneName, int buildIndex, MelonLogger.Instance log)
    {
        var flag = ResolveFlag();
        if (!flag.Enabled)
            return;

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

            EnforceRetention(flag.RetentionCap, log);
        }
        catch (Exception ex)
        {
            log.Error($"[inspect] dump failed: {ex}");
        }
    }

    /// <summary>
    /// Dumps the full state of every DataTemplate subtype registered in
    /// <c>DataTemplateLoader.m_TemplateMaps</c>. Captures identity (m_ID, map
    /// key, Unity name, hideFlags, native pointer), flags likely Jiangyu clones
    /// via <c>hideFlags</c>, and snapshots each serialised member as a
    /// classified summary (scalar / bytes / reference / collection / odinBlob /
    /// null / unreadable). One JSON file per call, named
    /// <c>&lt;timestamp&gt;-&lt;scene&gt;-templates-&lt;seq&gt;.json</c>.
    /// Returns <c>false</c> when the loader's <c>m_TemplateMaps</c> singleton or
    /// entries aren't ready yet (caller can retry later in-scene).
    /// </summary>
    public static bool TryDumpTemplatesFromLoader(string sceneName, MelonLogger.Instance log)
    {
        var flag = ResolveFlag();
        if (!flag.Enabled)
            return false;

        try
        {
            var singleton = DataTemplateLoader.GetSingleton();
            if (singleton == null)
            {
                log.Warning("[inspect] templates: DataTemplateLoader.GetSingleton() returned null.");
                return false;
            }

            var templateMaps = singleton.m_TemplateMaps;
            if (templateMaps == null || templateMaps.Count == 0)
            {
                log.Warning("[inspect] templates: DataTemplateLoader.m_TemplateMaps is null/empty (cache not ready).");
                return false;
            }

            var dump = BuildTemplatesDump(sceneName, templateMaps);
            if (dump.TypeCount == 0)
            {
                log.Warning("[inspect] templates: materialised 0 types.");
                return false;
            }

            var sequence = Interlocked.Increment(ref _templatesDumpSequence);
            var timestamp = DateTime.UtcNow.ToString("yyyy-MM-dd-HH-mm-ss-fff");
            var safeSceneName = SanitiseForFileName(sceneName);
            var filePath = Path.Combine(GetOutputDirectory(), $"{timestamp}-{safeSceneName}-templates-{sequence:D2}.json");

            File.WriteAllText(filePath, JsonSerializer.Serialize(dump, JsonOptions));

            log.Msg(
                $"[inspect] Wrote templates dump: {filePath}  " +
                $"(types={dump.TypeCount}  templates={dump.TemplateCount}  " +
                $"likelyClones={dump.LikelyCloneCount}  odinFields={dump.OdinFieldCount})");

            EnforceRetention(flag.RetentionCap, log);
            return true;
        }
        catch (Exception ex)
        {
            log.Error($"[inspect] templates dump failed: {ex}");
            return false;
        }
    }

    private static TemplatesDump BuildTemplatesDump(
        string sceneName,
        Il2CppSystem.Collections.Generic.Dictionary<Il2CppSystem.Type, Il2CppDictionary> templateMaps)
    {
        var dump = new TemplatesDump
        {
            Timestamp = DateTimeOffset.UtcNow,
            SceneName = sceneName,
        };

        foreach (var pair in templateMaps)
        {
            var il2CppType = pair.Key;
            var innerMap = pair.Value;
            if (il2CppType == null || innerMap == null)
                continue;

            // Dict values come out declared as DataTemplate, so template.GetType()
            // returns the base class and reflection misses the subtype-declared
            // members. Resolve the concrete managed wrapper for this map bucket
            // and TryCast each value to it so GetType() returns the real type.
            var managedType = TemplateRuntimeAccess.ResolveTemplateType(il2CppType.Name, out _);
            var tryCastGeneric = managedType != null
                ? BuildTryCastMethod(managedType)
                : null;

            var typeDump = new TemplateTypeDump
            {
                TypeFullName = il2CppType.FullName,
                TypeName = il2CppType.Name,
                MapEntryCount = innerMap.Count,
            };

            foreach (var entry in innerMap)
            {
                var template = CastToConcreteType(entry.Value, tryCastGeneric);
                var info = BuildTemplateStateInfo(entry.Key, template);
                if (info == null)
                    continue;

                typeDump.Templates.Add(info);
                if (info.IsLikelyClone)
                    dump.LikelyCloneCount++;
                dump.OdinFieldCount += info.OdinFieldCount;
            }

            typeDump.Templates.Sort((a, b) => string.CompareOrdinal(a.Id ?? a.MapKey, b.Id ?? b.MapKey));
            dump.Types.Add(typeDump);
            dump.TemplateCount += typeDump.Templates.Count;
        }

        dump.Types.Sort((a, b) => string.CompareOrdinal(a.TypeFullName, b.TypeFullName));
        dump.TypeCount = dump.Types.Count;
        return dump;
    }

    private static MethodInfo BuildTryCastMethod(Type concreteType)
    {
        try
        {
            var method = typeof(Il2CppObjectBase)
                .GetMethods(BindingFlags.Instance | BindingFlags.Public)
                .FirstOrDefault(m => m.Name == "TryCast"
                    && m.IsGenericMethodDefinition
                    && m.GetParameters().Length == 0);
            return method?.MakeGenericMethod(concreteType);
        }
        catch
        {
            return null;
        }
    }

    private static DataTemplate CastToConcreteType(DataTemplate template, MethodInfo tryCastGeneric)
    {
        if (template == null || tryCastGeneric == null)
            return template;

        try
        {
            return tryCastGeneric.Invoke(template, null) as DataTemplate ?? template;
        }
        catch
        {
            return template;
        }
    }

    private static TemplateStateInfo BuildTemplateStateInfo(string mapKey, DataTemplate template)
    {
        if (template == null)
            return null;

        var info = new TemplateStateInfo
        {
            MapKey = mapKey,
        };

        try
        {
            var runtimeType = template.GetType();
            info.RuntimeType = runtimeType.FullName;
            info.Id = TemplateRuntimeAccess.ReadTemplateId(template);

            try { info.Name = template.name; } catch { }
            try { info.HideFlags = template.hideFlags.ToString(); } catch { }
            info.IsLikelyClone = !string.IsNullOrEmpty(info.HideFlags)
                && info.HideFlags!.Contains("DontUnloadUnusedAsset", StringComparison.Ordinal);

            var pointer = GetNativePointer(template);
            info.NativePointer = pointer == IntPtr.Zero ? null : $"0x{pointer.ToInt64():X}";

            EnumerateMembers(template, runtimeType, info);
        }
        catch (Exception ex)
        {
            info.EnumerationError = ex.Message;
        }

        return info;
    }

    // Enumerate the flattened member surface of the runtime wrapper. Without
    // DeclaredOnly, managed reflection returns the full set including inherited
    // members, so we don't need to walk the hierarchy ourselves. Property reads
    // come first because that's how Il2CppInterop surfaces most serialised
    // Unity members (the `TryReadMember` path in TemplatePatchApplier, verified
    // in-game, tries property first, then field). Reads are guarded individually
    // so one throwing proxy getter doesn't abort the whole enumeration.
    private static void EnumerateMembers(object template, Type runtimeType, TemplateStateInfo info)
    {
        const BindingFlags flags = BindingFlags.Instance
            | BindingFlags.Public
            | BindingFlags.NonPublic
            | BindingFlags.FlattenHierarchy;

        var seenNames = new HashSet<string>(StringComparer.Ordinal);

        foreach (var property in runtimeType.GetProperties(flags))
        {
            if (property.GetIndexParameters().Length != 0)
                continue;
            if (!property.CanRead)
                continue;
            if (!seenNames.Add(property.Name))
                continue;
            if (property.Name == "m_ID")
                continue;

            info.Fields.Add(BuildMemberSnapshot(
                property.Name, property.PropertyType,
                () => property.GetValue(template), info));
        }

        // Instance fields do not honour FlattenHierarchy (that flag only
        // affects static members), so walk the hierarchy explicitly for
        // fields. Fields declared on inherited non-Il2CppObjectBase base
        // types still need to surface (e.g. DataTemplate-level m_IsInitialized).
        for (var current = runtimeType;
             current != null && current != typeof(Il2CppObjectBase) && current != typeof(object);
             current = current.BaseType)
        {
            foreach (var field in current.GetFields(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                if (!seenNames.Add(field.Name))
                    continue;
                if (field.Name == "m_ID")
                    continue;

                info.Fields.Add(BuildMemberSnapshot(
                    field.Name, field.FieldType,
                    () => field.GetValue(template), info));
            }
        }

        info.Fields.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
    }

    private static TemplateFieldSnapshot BuildMemberSnapshot(
        string memberName, Type memberType, Func<object> read, TemplateStateInfo info)
    {
        var snapshot = new TemplateFieldSnapshot
        {
            Name = memberName,
            DeclaredType = memberType.FullName ?? memberType.Name,
        };

        object value;
        try
        {
            value = read();
        }
        catch (Exception ex)
        {
            snapshot.Kind = "Unreadable";
            snapshot.Error = ex.Message;
            return snapshot;
        }

        if (value == null)
        {
            snapshot.Kind = "Null";
            return snapshot;
        }

        // Odin / Sirenix serialised blob. Presence alone answers "does this
        // template carry Odin-only fields that our reflection-based applier
        // won't touch." Capture byte length when we can reach a managed byte
        // array behind it without deep probing.
        if (IsOdinSerializationData(memberName, memberType))
        {
            snapshot.Kind = "OdinBlob";
            snapshot.OdinSerializedByteLength = TryReadOdinByteLength(value);
            info.OdinFieldCount++;
            return snapshot;
        }

        if (memberType == typeof(string))
        {
            snapshot.Kind = "Scalar";
            snapshot.ScalarValue = (string)value;
            return snapshot;
        }

        if (memberType.IsEnum)
        {
            snapshot.Kind = "Scalar";
            try { snapshot.ScalarValue = value.ToString(); } catch { }
            return snapshot;
        }

        if (memberType.IsPrimitive)
        {
            snapshot.Kind = "Scalar";
            try { snapshot.ScalarValue = Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture); } catch { }
            return snapshot;
        }

        if (TryAsByteArray(value, out var bytes))
        {
            snapshot.Kind = "Bytes";
            snapshot.ByteLength = bytes.Length;
            var sample = bytes.Length <= MaxByteArrayElements ? bytes : bytes[..MaxByteArrayElements];
            snapshot.Bytes = new List<byte>(sample);
            return snapshot;
        }

        if (TryAsCollection(value, out var shape, out var count, out var elementSummaries))
        {
            snapshot.Kind = "Collection";
            snapshot.CollectionShape = shape;
            snapshot.CollectionCount = count;
            if (elementSummaries != null)
                snapshot.ElementSummaries = elementSummaries;
            return snapshot;
        }

        if (value is Il2CppObjectBase il2CppObject)
        {
            snapshot.Kind = "Reference";
            snapshot.ReferenceSummary = BuildReferenceSummary(il2CppObject);
            return snapshot;
        }

        snapshot.Kind = "Other";
        try { snapshot.ScalarValue = value.ToString(); } catch { }
        return snapshot;
    }

    private static bool IsOdinSerializationData(string memberName, Type memberType)
    {
        if (string.Equals(memberName, "serializationData", StringComparison.Ordinal))
            return true;
        var typeName = memberType.FullName ?? string.Empty;
        return typeName.Contains("Sirenix.Serialization.SerializationData", StringComparison.Ordinal)
            || typeName.Contains("SerializationData", StringComparison.Ordinal);
    }

    private static int? TryReadOdinByteLength(object serializationData)
    {
        try
        {
            var type = serializationData.GetType();
            for (var current = type; current != null; current = current.BaseType)
            {
                var bytesField = current.GetField(
                    "SerializedBytes",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                if (bytesField != null)
                {
                    var bytes = bytesField.GetValue(serializationData);
                    if (bytes == null)
                        return 0;
                    if (TryAsByteArray(bytes, out var managed))
                        return managed.Length;
                    return null;
                }
            }
        }
        catch
        {
        }

        return null;
    }

    private static bool TryAsByteArray(object value, out byte[] bytes)
    {
        bytes = null;
        if (value == null)
            return false;

        if (value is byte[] direct)
        {
            bytes = direct;
            return true;
        }

        var type = value.GetType();
        if (!type.IsGenericType)
            return false;

        var openName = type.GetGenericTypeDefinition().FullName;
        if (!string.Equals(openName, "Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray`1", StringComparison.Ordinal))
            return false;

        if (type.GetGenericArguments()[0] != typeof(byte))
            return false;

        try
        {
            var lengthProperty = type.GetProperty("Length", BindingFlags.Instance | BindingFlags.Public);
            var indexer = type.GetProperties(BindingFlags.Instance | BindingFlags.Public)
                .FirstOrDefault(p =>
                {
                    var parameters = p.GetIndexParameters();
                    return parameters.Length == 1 && parameters[0].ParameterType == typeof(int);
                });
            if (lengthProperty == null || indexer == null)
                return false;

            var length = (int)lengthProperty.GetValue(value)!;
            var copy = new byte[length];
            var args = new object[1];
            for (var i = 0; i < length; i++)
            {
                args[0] = i;
                copy[i] = (byte)indexer.GetValue(value, args)!;
            }
            bytes = copy;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryAsCollection(
        object value, out string shape, out int count, out List<string> elementSummaries)
    {
        shape = null;
        count = 0;
        elementSummaries = null;

        var type = value.GetType();

        if (value is Array managedArray)
        {
            shape = "ManagedArray";
            count = managedArray.Length;
            elementSummaries = SummariseElements(i => managedArray.GetValue(i), count);
            return true;
        }

        if (type.IsGenericType)
        {
            var openName = type.GetGenericTypeDefinition().FullName ?? string.Empty;
            if (openName == "Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray`1"
                || openName == "Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray`1")
            {
                if (TryReadCollection(value, type, "Length", out count, out elementSummaries))
                {
                    shape = openName.Contains("Il2CppReferenceArray", StringComparison.Ordinal)
                        ? "Il2CppReferenceArray"
                        : "Il2CppStructArray";
                    return true;
                }
            }
        }

        // Generic List (Il2Cpp-generated or managed): has Count + Item[int].
        var countProperty = type.GetProperty(
            "Count", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (countProperty != null && countProperty.GetIndexParameters().Length == 0)
        {
            if (TryReadCollection(value, type, "Count", out count, out elementSummaries))
            {
                shape = "List";
                return true;
            }
        }

        return false;
    }

    private static bool TryReadCollection(
        object collection, Type type, string lengthPropertyName,
        out int count, out List<string> elementSummaries)
    {
        count = 0;
        elementSummaries = null;

        try
        {
            var lengthProperty = type.GetProperty(
                lengthPropertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            var indexer = type.GetProperties(
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(p =>
                {
                    var parameters = p.GetIndexParameters();
                    return parameters.Length == 1 && parameters[0].ParameterType == typeof(int) && p.CanRead;
                });
            if (lengthProperty == null || indexer == null)
                return false;

            count = (int)lengthProperty.GetValue(collection)!;
            var args = new object[1];
            elementSummaries = SummariseElements(
                i =>
                {
                    args[0] = i;
                    return indexer.GetValue(collection, args);
                },
                count);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static List<string> SummariseElements(Func<int, object> reader, int count)
    {
        var take = Math.Min(count, MaxCollectionElementSummaries);
        var list = new List<string>(take);
        for (var i = 0; i < take; i++)
        {
            try
            {
                var element = reader(i);
                list.Add(element == null ? "null" : SummariseElement(element));
            }
            catch (Exception ex)
            {
                list.Add($"<error: {ex.Message}>");
            }
        }
        return list;
    }

    private static string SummariseElement(object element)
    {
        if (element is Il2CppObjectBase il2Cpp)
            return BuildReferenceSummary(il2Cpp);
        if (element is string s)
            return s;
        try { return Convert.ToString(element, System.Globalization.CultureInfo.InvariantCulture); }
        catch { return element.GetType().Name; }
    }

    private static string BuildReferenceSummary(Il2CppObjectBase il2CppObject)
    {
        var typeName = il2CppObject.GetType().Name;

        string identity = null;
        if (typeof(DataTemplate).IsAssignableFrom(il2CppObject.GetType()))
            identity = TemplateRuntimeAccess.ReadTemplateId(il2CppObject);
        else if (il2CppObject is UnityEngine.Object unityObject)
        {
            try { identity = unityObject.name; } catch { }
        }

        return string.IsNullOrWhiteSpace(identity) ? typeName : $"{typeName}:{identity}";
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

    private sealed class TemplatesDump
    {
        public DateTimeOffset Timestamp { get; set; }
        public string SceneName { get; set; }
        public int TypeCount { get; set; }
        public int TemplateCount { get; set; }
        public int LikelyCloneCount { get; set; }
        public int OdinFieldCount { get; set; }
        public List<TemplateTypeDump> Types { get; } = new();
    }

    private sealed class TemplateTypeDump
    {
        public string TypeFullName { get; set; }
        public string TypeName { get; set; }
        public int MapEntryCount { get; set; }
        public List<TemplateStateInfo> Templates { get; } = new();
    }

    private sealed class TemplateStateInfo
    {
        public string MapKey { get; set; }
        public string Id { get; set; }
        public string Name { get; set; }
        public string RuntimeType { get; set; }
        public string NativePointer { get; set; }
        public string HideFlags { get; set; }
        public bool IsLikelyClone { get; set; }
        public int OdinFieldCount { get; set; }
        public string EnumerationError { get; set; }
        public List<TemplateFieldSnapshot> Fields { get; } = new();
    }

    private sealed class TemplateFieldSnapshot
    {
        public string Name { get; set; }
        public string DeclaredType { get; set; }
        public string Kind { get; set; }
        public string ScalarValue { get; set; }
        public int? ByteLength { get; set; }
        public List<byte> Bytes { get; set; }
        public string CollectionShape { get; set; }
        public int? CollectionCount { get; set; }
        public List<string> ElementSummaries { get; set; }
        public string ReferenceSummary { get; set; }
        public int? OdinSerializedByteLength { get; set; }
        public string Error { get; set; }
    }
}
