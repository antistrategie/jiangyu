using System.Reflection;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using Jiangyu.Loader.Templates;
using DataTemplateLoader = Il2CppMenace.Tools.DataTemplateLoader;
using DataTemplate = Il2CppMenace.Tools.DataTemplate;
using Il2CppDictionary = Il2CppSystem.Collections.Generic.Dictionary<string, Il2CppMenace.Tools.DataTemplate>;

namespace Jiangyu.Loader.Diagnostics;

/// <summary>
/// On-demand snapshot of every DataTemplate subtype registered in
/// <c>DataTemplateLoader.m_TemplateMaps</c>. Captures identity (m_ID, map
/// key, Unity name, hideFlags, native pointer), flags likely Jiangyu clones
/// via <c>hideFlags</c>, and snapshots each serialised member as a
/// classified summary (scalar / bytes / reference / collection / odinBlob /
/// null / unreadable). Driven by the <c>inspect.templates</c> bridge request
/// and returned to the caller; returns an <c>error</c> object when the loader's
/// <c>m_TemplateMaps</c> singleton or entries aren't ready yet.
/// </summary>
internal static class TemplateStateInspector
{
    private const int MaxByteArrayElements = 64;
    private const int MaxCollectionElementSummaries = 16;

    // Build the templates snapshot and return it for an on-demand bridge request. The
    // return type is object so the loader can hand it (or an error object) straight to
    // the JSON serialiser. Must run on the Unity main thread.
    internal static object Capture(string sceneName)
    {
        var singleton = DataTemplateLoader.GetSingleton();
        if (singleton == null)
            return new { error = "DataTemplateLoader.GetSingleton() returned null." };

        var templateMaps = singleton.m_TemplateMaps;
        if (templateMaps == null || templateMaps.Count == 0)
            return new { error = "DataTemplateLoader.m_TemplateMaps is null/empty (cache not ready)." };

        var dump = BuildTemplatesDump(sceneName, templateMaps);
        if (dump.TypeCount == 0)
            return new { error = "materialised 0 template types." };

        return dump;
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
                ? Il2CppReflectiveCast.GetTryCastMethod(managedType, throwIfMissing: false)
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

        // Odin / Sirenix serialised blob. Presence indicates this template
        // carries Odin-routed fields whose data lives in the blob at rest but
        // is accessible via normal Il2Cpp properties at runtime (Odin populates
        // the fields on load). Capture byte length when reachable.
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
        var typeName = Il2CppTypeName.Resolve(il2CppObject) ?? il2CppObject.GetType().Name;

        string identity = null;
        if (typeof(DataTemplate).IsAssignableFrom(il2CppObject.GetType()))
            identity = TemplateRuntimeAccess.ReadTemplateId(il2CppObject);
        else if (il2CppObject is UnityEngine.Object unityObject)
        {
            try { identity = unityObject.name; } catch { }
        }

        // Append native pointer for non-DataTemplate elements so collection
        // entries that share an underlying asset (e.g. PPtr-shared handler
        // in a cloned parent) can be distinguished by identity even when
        // their .name string collides. DataTemplates already carry a
        // unique m_ID; Unity-Object name alone isn't enough for handlers
        // because every Attack handler is named "Attack", every AddSkill
        // is named "AddSkill", etc.
        var nativeSuffix = string.Empty;
        if (!typeof(DataTemplate).IsAssignableFrom(il2CppObject.GetType()))
        {
            try { nativeSuffix = $" @ 0x{il2CppObject.Pointer.ToInt64():X}"; }
            catch { }
        }

        // Drop the redundant ":identity" segment when it just repeats the
        // type name (the vanilla naming convention for SerializedScriptable
        // handlers — every AddSkill instance is named "AddSkill"). Keep the
        // segment when it adds information, e.g. a uniquely-named asset.
        var head = string.IsNullOrWhiteSpace(identity) || string.Equals(identity, typeName, StringComparison.Ordinal)
            ? typeName
            : $"{typeName}:{identity}";
        return head + nativeSuffix;
    }

    private static IntPtr GetNativePointer(object instance)
    {
        if (instance is not Il2CppObjectBase il2CppObject)
            return IntPtr.Zero;

        return IL2CPP.Il2CppObjectBaseToPtr(il2CppObject);
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
