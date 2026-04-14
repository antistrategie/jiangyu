using AssetRipper.Assets;
using AssetRipper.Assets.Collections;
using AssetRipper.Assets.Metadata;
using AssetRipper.Assets.Traversal;
using AssetRipper.Import.Structure.Assembly.Serializable;
using AssetRipper.Primitives;
using Jiangyu.Core.Models;

namespace Jiangyu.Core.Assets;

public static class ObjectFieldInspector
{
    public static ObjectFieldInspection Inspect(IUnityObjectBase asset, int maxDepth, int maxArraySampleLength)
    {
        ArgumentNullException.ThrowIfNull(asset);
        if (maxDepth < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxDepth), "Max depth must be at least 1.");
        }
        if (maxArraySampleLength < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxArraySampleLength), "Max array sample length must be 0 or greater.");
        }

        var walker = new RawTreeWalker(asset.Collection);
        asset.WalkStandard(walker);

        bool anyTruncated = false;
        var fields = new List<InspectedFieldNode>(walker.Root.Fields.Count);
        foreach (var field in walker.Root.Fields)
        {
            fields.Add(Normalize(field, 1, maxDepth, maxArraySampleLength, ref anyTruncated));
        }

        return new ObjectFieldInspection
        {
            Fields = fields,
            Truncated = anyTruncated,
        };
    }

    private static InspectedFieldNode Normalize(
        RawFieldNode raw,
        int depth,
        int maxDepth,
        int maxArraySampleLength,
        ref bool anyTruncated)
    {
        return raw switch
        {
            RawScalarNode scalar => NormalizeScalar(scalar),
            RawReferenceNode reference => NormalizeReference(reference),
            RawArrayNode array => NormalizeArray(array, depth, maxDepth, maxArraySampleLength, ref anyTruncated),
            RawObjectNode obj => NormalizeObject(obj, depth, maxDepth, maxArraySampleLength, ref anyTruncated),
            _ => new InspectedFieldNode
            {
                Name = raw.Name,
                Kind = "unknown",
                FieldTypeName = raw.FieldTypeName,
                Reason = $"Unsupported raw node type: {raw.GetType().Name}",
            },
        };
    }

    private static InspectedFieldNode NormalizeScalar(RawScalarNode raw)
    {
        return new InspectedFieldNode
        {
            Name = raw.Name,
            Kind = raw.Kind,
            FieldTypeName = raw.FieldTypeName,
            Null = raw.IsNull ? true : null,
            Value = raw.Value,
            Reason = raw.Reason,
        };
    }

    private static InspectedFieldNode NormalizeReference(RawReferenceNode raw)
    {
        return new InspectedFieldNode
        {
            Name = raw.Name,
            Kind = "reference",
            FieldTypeName = raw.FieldTypeName,
            Null = raw.IsNull ? true : null,
            Reference = new InspectedReference
            {
                FileId = raw.FileId,
                PathId = raw.PathId,
                Name = raw.ReferenceName,
                ClassName = raw.ReferenceClassName,
            },
        };
    }

    private static InspectedFieldNode NormalizeArray(
        RawArrayNode raw,
        int depth,
        int maxDepth,
        int maxArraySampleLength,
        ref bool anyTruncated)
    {
        bool localTruncated = false;
        List<InspectedFieldNode>? elements = null;

        if (raw.Elements.Count > 0)
        {
            if (depth >= maxDepth)
            {
                localTruncated = true;
            }
            else
            {
                int sampleCount = Math.Min(raw.Elements.Count, maxArraySampleLength);
                if (sampleCount < raw.Elements.Count)
                {
                    localTruncated = true;
                }

                elements = new List<InspectedFieldNode>(sampleCount);
                for (int i = 0; i < sampleCount; i++)
                {
                    elements.Add(Normalize(raw.Elements[i], depth + 1, maxDepth, maxArraySampleLength, ref anyTruncated));
                }
            }
        }

        if (localTruncated)
        {
            anyTruncated = true;
        }

        return new InspectedFieldNode
        {
            Name = raw.Name,
            Kind = "array",
            FieldTypeName = raw.FieldTypeName,
            Count = raw.Count,
            Elements = elements,
            Truncated = localTruncated ? true : null,
        };
    }

    private static InspectedFieldNode NormalizeObject(
        RawObjectNode raw,
        int depth,
        int maxDepth,
        int maxArraySampleLength,
        ref bool anyTruncated)
    {
        bool localTruncated = false;
        List<InspectedFieldNode>? fields = null;

        if (raw.Fields.Count > 0)
        {
            if (depth >= maxDepth)
            {
                localTruncated = true;
            }
            else
            {
                fields = new List<InspectedFieldNode>(raw.Fields.Count);
                foreach (var field in raw.Fields)
                {
                    fields.Add(Normalize(field, depth + 1, maxDepth, maxArraySampleLength, ref anyTruncated));
                }
            }
        }

        if (localTruncated)
        {
            anyTruncated = true;
        }

        return new InspectedFieldNode
        {
            Name = raw.Name,
            Kind = "object",
            FieldTypeName = raw.FieldTypeName,
            Fields = fields,
            Truncated = localTruncated ? true : null,
        };
    }

    private abstract class RawFieldNode
    {
        public string? Name { get; set; }
        public string? FieldTypeName { get; set; }
    }

    private sealed class RawObjectNode : RawFieldNode
    {
        public List<RawFieldNode> Fields { get; } = [];
    }

    private sealed class RawArrayNode : RawFieldNode
    {
        public required int Count { get; init; }
        public List<RawFieldNode> Elements { get; } = [];
    }

    private sealed class RawReferenceNode : RawFieldNode
    {
        public required int FileId { get; init; }
        public required long PathId { get; init; }
        public required bool IsNull { get; init; }
        public string? ReferenceName { get; init; }
        public string? ReferenceClassName { get; init; }
    }

    private sealed class RawScalarNode : RawFieldNode
    {
        public required string Kind { get; init; }
        public object? Value { get; init; }
        public bool IsNull { get; init; }
        public string? Reason { get; init; }
    }

    private sealed class RawTreeWalker(AssetCollection collection) : AssetWalker
    {
        private readonly AssetCollection _collection = collection;
        private readonly Stack<ContainerContext> _containers = [];
        private bool _seenRootAsset;
        private string? _pendingFieldName;

        public RawObjectNode Root { get; } = new() { FieldTypeName = "Root" };

        public override bool EnterAsset(IUnityAssetBase asset)
        {
            if (!_seenRootAsset)
            {
                _seenRootAsset = true;
                Root.FieldTypeName = GetObjectTypeName(asset);
                _containers.Push(new ObjectContext(Root));
                return true;
            }

            var node = new RawObjectNode { FieldTypeName = GetObjectTypeName(asset) };
            AttachNode(node);
            _containers.Push(new ObjectContext(node));
            return true;
        }

        public override void ExitAsset(IUnityAssetBase asset)
        {
            if (_containers.Count > 0)
            {
                _containers.Pop();
            }
        }

        public override bool EnterField(IUnityAssetBase asset, string name)
        {
            _pendingFieldName = name;
            return true;
        }

        public override void ExitField(IUnityAssetBase asset, string name)
        {
            _pendingFieldName = null;
        }

        public override bool EnterList<T>(IReadOnlyList<T> list)
        {
            var node = new RawArrayNode
            {
                FieldTypeName = BuildListTypeName(typeof(T)),
                Count = list.Count,
            };

            AttachNode(node);
            _containers.Push(new ArrayContext(node));
            return true;
        }

        public override void ExitList<T>(IReadOnlyList<T> list)
        {
            if (_containers.Count > 0)
            {
                _containers.Pop();
            }
        }

        public override bool EnterDictionary<TKey, TValue>(IReadOnlyCollection<KeyValuePair<TKey, TValue>> dictionary)
        {
            var node = new RawArrayNode
            {
                FieldTypeName = $"Dictionary<{GetFriendlyTypeName(typeof(TKey))}, {GetFriendlyTypeName(typeof(TValue))}>",
                Count = dictionary.Count,
            };

            AttachNode(node);
            _containers.Push(new ArrayContext(node));
            return true;
        }

        public override void ExitDictionary<TKey, TValue>(IReadOnlyCollection<KeyValuePair<TKey, TValue>> dictionary)
        {
            if (_containers.Count > 0)
            {
                _containers.Pop();
            }
        }

        public override bool EnterPair<TKey, TValue>(KeyValuePair<TKey, TValue> pair)
        {
            return EnterPairObject("Pair");
        }

        public override bool EnterDictionaryPair<TKey, TValue>(KeyValuePair<TKey, TValue> pair)
        {
            return EnterPairObject("Pair");
        }

        public override void DividePair<TKey, TValue>(KeyValuePair<TKey, TValue> pair)
        {
            SwitchPairPhase();
        }

        public override void DivideDictionaryPair<TKey, TValue>(KeyValuePair<TKey, TValue> pair)
        {
            SwitchPairPhase();
        }

        public override void ExitPair<TKey, TValue>(KeyValuePair<TKey, TValue> pair)
        {
            if (_containers.Count > 0)
            {
                _containers.Pop();
            }
        }

        public override void ExitDictionaryPair<TKey, TValue>(KeyValuePair<TKey, TValue> pair)
        {
            if (_containers.Count > 0)
            {
                _containers.Pop();
            }
        }

        public override void VisitPrimitive<T>(T value)
        {
            var node = CreateScalarNode(value);
            AttachNode(node);
        }

        public override void VisitPPtr<TAsset>(PPtr<TAsset> pptr)
        {
            IUnityObjectBase? target = _collection.TryGetAsset(pptr);
            var node = new RawReferenceNode
            {
                FieldTypeName = GetReferenceTypeName(typeof(TAsset)),
                FileId = pptr.FileID,
                PathId = pptr.PathID,
                IsNull = pptr.FileID == 0 && pptr.PathID == 0,
                ReferenceName = target?.GetBestName(),
                ReferenceClassName = target?.ClassName,
            };

            AttachNode(node);
        }

        private bool EnterPairObject(string typeName)
        {
            var node = new RawObjectNode { FieldTypeName = typeName };
            AttachNode(node);
            _containers.Push(new PairContext(node));
            return true;
        }

        private void SwitchPairPhase()
        {
            if (_containers.TryPeek(out var container) && container is PairContext pair)
            {
                pair.CurrentSlot = "Value";
            }
        }

        private void AttachNode(RawFieldNode node)
        {
            if (!_containers.TryPeek(out var container))
            {
                throw new InvalidOperationException("No container available for attachment.");
            }

            switch (container)
            {
                case ObjectContext obj:
                    node.Name = ConsumeFieldName();
                    obj.Node.Fields.Add(node);
                    break;
                case ArrayContext array:
                    array.Node.Elements.Add(node);
                    break;
                case PairContext pair:
                    node.Name = pair.CurrentSlot;
                    pair.Node.Fields.Add(node);
                    break;
                default:
                    throw new InvalidOperationException($"Unsupported container context: {container.GetType().Name}");
            }
        }

        private string ConsumeFieldName()
        {
            if (string.IsNullOrEmpty(_pendingFieldName))
            {
                throw new InvalidOperationException("Expected a pending field name for object attachment.");
            }

            string fieldName = _pendingFieldName;
            _pendingFieldName = null;
            return fieldName;
        }
    }

    private abstract class ContainerContext;

    private sealed class ObjectContext(RawObjectNode node) : ContainerContext
    {
        public RawObjectNode Node { get; } = node;
    }

    private sealed class ArrayContext(RawArrayNode node) : ContainerContext
    {
        public RawArrayNode Node { get; } = node;
    }

    private sealed class PairContext(RawObjectNode node) : ContainerContext
    {
        public RawObjectNode Node { get; } = node;
        public string CurrentSlot { get; set; } = "Key";
    }

    private static RawScalarNode CreateScalarNode<T>(T value)
        where T : notnull
    {
        Type valueType = typeof(T);
        object? normalizedValue = value;
        string kind;

        if (value is Utf8String utf8)
        {
            kind = "string";
            normalizedValue = utf8.ToString();
        }
        else if (valueType == typeof(string) || valueType == typeof(char))
        {
            kind = "string";
            normalizedValue = value.ToString();
        }
        else if (valueType.IsEnum)
        {
            kind = "enum";
            normalizedValue = value.ToString();
        }
        else if (valueType == typeof(bool))
        {
            kind = "bool";
        }
        else if (valueType == typeof(float) || valueType == typeof(double))
        {
            kind = "float";
        }
        else if (IsIntegerType(valueType))
        {
            kind = "int";
        }
        else
        {
            kind = "unknown";
            normalizedValue = value.ToString();
        }

        return new RawScalarNode
        {
            Kind = kind,
            FieldTypeName = GetFriendlyTypeName(valueType),
            Value = normalizedValue,
            IsNull = false,
            Reason = kind == "unknown" ? $"Unhandled primitive type: {valueType.Name}" : null,
        };
    }

    private static bool IsIntegerType(Type type)
    {
        return type == typeof(byte)
            || type == typeof(sbyte)
            || type == typeof(short)
            || type == typeof(ushort)
            || type == typeof(int)
            || type == typeof(uint)
            || type == typeof(long)
            || type == typeof(ulong);
    }

    private static string BuildListTypeName(Type elementType)
    {
        return $"Array<{GetFriendlyTypeName(elementType)}>";
    }

    private static string GetReferenceTypeName(Type type)
    {
        if (type == typeof(IUnityObjectBase))
        {
            return "PPtr<Object>";
        }

        return $"PPtr<{GetFriendlyTypeName(type)}>";
    }

    private static string GetFriendlyTypeName(Type type)
    {
        if (type == typeof(Utf8String))
        {
            return "string";
        }

        if (type.IsGenericType)
        {
            string genericName = type.Name.Split('`')[0];
            string genericArgs = string.Join(", ", type.GetGenericArguments().Select(GetFriendlyTypeName));
            return $"{genericName}<{genericArgs}>";
        }

        return type.Name;
    }

    private static string GetObjectTypeName(IUnityAssetBase asset)
    {
        if (asset is IUnityObjectBase unityObject)
        {
            return unityObject.ClassName;
        }

        if (asset is SerializableStructure structure)
        {
            return structure.Type.Name;
        }

        return asset.GetType().Name;
    }
}

public sealed class ObjectFieldInspection
{
    public required List<InspectedFieldNode> Fields { get; init; }
    public required bool Truncated { get; init; }
}
