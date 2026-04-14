using AssetsTools.NET;
using AssetsTools.NET.Extra;

namespace Jiangyu.Core.Assets;

public sealed class AssetInspectionService
{
    private const int ClassIdGameObject = 1;
    private const int ClassIdTransform = 4;
    private const int ClassIdMeshRenderer = 23;
    private const int ClassIdMeshFilter = 33;
    private const int ClassIdMesh = 43;
    private const int ClassIdAnimator = 95;
    private const int ClassIdSkinnedMeshRenderer = 137;
    private const int ClassIdLODGroup = 205;

    /// <summary>
    /// Inspects a bundle file and game data directory, returning a structured report
    /// of all matching GameObjects and their components.
    /// </summary>
    public static InspectionReport InspectBundles(
        string bundlePath,
        string gameDataPath,
        string gameFilter = "basic_soldier",
        string? bundleFilter = null)
    {
        bundleFilter ??= gameFilter;

        var am = new AssetsManager
        {
            UseQuickLookup = true,
            UseTemplateFieldCache = true,
            UseMonoTemplateFieldCache = true,
        };

        var bundle = am.LoadBundleFile(bundlePath);
        var bundleAssetFiles = new List<AssetsFileInstance>();
        for (var i = 0; i < bundle.file.BlockAndDirInfo.DirectoryInfos.Count; i++)
        {
            var inst = am.LoadAssetsFileFromBundle(bundle, i, loadDeps: false);
            if (inst?.file != null)
                bundleAssetFiles.Add(inst);
        }

        if (bundleAssetFiles.Count == 0)
            throw new InvalidOperationException("No assets files found inside bundle.");

        var templateSource = bundleAssetFiles.FirstOrDefault(f => f.file.Metadata.TypeTreeEnabled)
            ?? throw new InvalidOperationException("Bundle assets do not contain typetrees for standard Unity classes.");

        var templates = BuildTemplates(am, templateSource);

        var gameFiles = EnumerateGameAssetFiles(gameDataPath)
            .Select(path => am.LoadAssetsFile(path, loadDeps: false))
            .Where(inst => inst?.file != null)
            .ToList();

        return new InspectionReport
        {
            BundlePath = bundlePath,
            GameDataPath = gameDataPath,
            GameFilter = gameFilter,
            BundleFilter = bundleFilter,
            BundleFiles = [.. bundleAssetFiles.Select(inst => InspectFile(inst, templates, bundleFilter))],
            GameFiles = [.. gameFiles.Select(inst => InspectFile(inst, templates, gameFilter))],
        };
    }

    private static Dictionary<int, AssetTypeTemplateField> BuildTemplates(AssetsManager am, AssetsFileInstance templateSource)
    {
        int[] typeIds =
        [
            ClassIdGameObject,
            ClassIdTransform,
            ClassIdMeshRenderer,
            ClassIdMeshFilter,
            ClassIdAnimator,
            ClassIdSkinnedMeshRenderer,
            ClassIdLODGroup,
            ClassIdMesh,
        ];

        var result = new Dictionary<int, AssetTypeTemplateField>();
        foreach (var typeId in typeIds)
        {
            var info = templateSource.file.AssetInfos.FirstOrDefault(i => i.TypeId == typeId);
            if (info == null)
                continue;

            var template = am.GetTemplateBaseField(templateSource, info);
            if (template != null)
                result[typeId] = template;
        }

        return result;
    }

    private static AssetFileReport InspectFile(
        AssetsFileInstance inst,
        IReadOnlyDictionary<int, AssetTypeTemplateField> templates,
        string nameFilter)
    {
        var ctx = new FileContext(inst, templates);
        var matchingPathIds = new HashSet<long>();

        foreach (var info in inst.file.AssetInfos.Where(i => i.TypeId == ClassIdGameObject))
        {
            var baseField = ctx.Read(info, ClassIdGameObject);
            if (baseField == null)
                continue;

            var name = baseField["m_Name"].AsString;
            if (string.IsNullOrEmpty(name) ||
                name.IndexOf(nameFilter, StringComparison.OrdinalIgnoreCase) < 0)
                continue;

            ctx.CollectGameObjectAndDescendants(info.PathId, matchingPathIds);
        }

        var matching = matchingPathIds
            .Select(ctx.BuildGameObject)
            .OrderBy(g => g.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new AssetFileReport
        {
            Path = inst.path,
            HasTypeTree = inst.file.Metadata.TypeTreeEnabled,
            UnityVersion = inst.file.Metadata.UnityVersion,
            GameObjects = [.. matching.OrderBy(g => g.Path, StringComparer.OrdinalIgnoreCase)],
        };
    }

    private static IEnumerable<string> EnumerateGameAssetFiles(string gameDataPath)
    {
        foreach (var path in Directory.EnumerateFiles(gameDataPath, "*", SearchOption.TopDirectoryOnly))
        {
            var fileName = System.IO.Path.GetFileName(path);
            if (fileName.Equals("globalgamemanagers.assets", StringComparison.OrdinalIgnoreCase) ||
                fileName.Equals("resources.assets", StringComparison.OrdinalIgnoreCase) ||
                fileName.StartsWith("sharedassets", StringComparison.OrdinalIgnoreCase) && fileName.EndsWith(".assets", StringComparison.OrdinalIgnoreCase) ||
                fileName.StartsWith("level", StringComparison.OrdinalIgnoreCase))
            {
                yield return path;
            }
        }
    }

    internal static string GetClassName(int typeId) => typeId switch
    {
        ClassIdGameObject => nameof(GameObjectInfo),
        ClassIdTransform => "Transform",
        ClassIdMeshRenderer => "MeshRenderer",
        ClassIdMeshFilter => "MeshFilter",
        ClassIdMesh => "Mesh",
        ClassIdAnimator => "Animator",
        ClassIdSkinnedMeshRenderer => "SkinnedMeshRenderer",
        ClassIdLODGroup => "LODGroup",
        _ => $"ClassID:{typeId}",
    };

    private sealed class FileContext(AssetsFileInstance inst, IReadOnlyDictionary<int, AssetTypeTemplateField> templates)
    {
        private readonly AssetsFileInstance _inst = inst;
        private readonly IReadOnlyDictionary<int, AssetTypeTemplateField> _templates = templates;
        private readonly Dictionary<long, GameObjectInfo> _gameObjectCache = [];
        private readonly Dictionary<long, AssetTypeValueField?> _fieldCache = [];

        public GameObjectInfo BuildGameObject(long pathId)
        {
            if (_gameObjectCache.TryGetValue(pathId, out var cached))
                return cached;

            var info = _inst.file.GetAssetInfo(pathId);
            var field = Read(info, ClassIdGameObject) ?? throw new InvalidOperationException($"Failed to read GameObject {pathId}");
            var name = field["m_Name"].AsString;
            var componentRefs = ReadComponentRefs(field);

            var transformRef = componentRefs.FirstOrDefault(c => c.TypeId == ClassIdTransform);
            var path = !transformRef.Equals(default)
                ? BuildTransformPath(transformRef.PathId)
                : name;

            var result = new GameObjectInfo
            {
                PathId = pathId,
                Name = name,
                Path = path,
                Components = [.. componentRefs.Select(c => new ComponentInfo
                {
                    PathId = c.PathId,
                    TypeId = c.TypeId,
                    TypeName = GetClassName(c.TypeId),
                })],
                Transform = transformRef.Equals(default) ? null : ReadTransformInfo(transformRef.PathId),
            };

            foreach (var comp in componentRefs)
            {
                switch (comp.TypeId)
                {
                    case ClassIdSkinnedMeshRenderer:
                        result.SkinnedMeshRenderers.Add(ReadSkinnedMeshRenderer(comp.PathId));
                        break;
                    case ClassIdAnimator:
                        result.Animators.Add(ReadAnimator(comp.PathId));
                        break;
                    case ClassIdLODGroup:
                        result.LodGroups.Add(ReadLodGroup(comp.PathId));
                        break;
                }
            }

            _gameObjectCache[pathId] = result;
            return result;
        }

        public AssetTypeValueField? Read(AssetFileInfo info, int expectedTypeId)
        {
            if (_fieldCache.TryGetValue(info.PathId, out var cached))
                return cached;

            if (!_templates.TryGetValue(expectedTypeId, out var template))
            {
                _fieldCache[info.PathId] = null;
                return null;
            }

            AssetTypeValueField? value = null;
            lock (_inst.LockReader)
            {
                var reader = _inst.file.Reader;
                value = template.MakeValue(reader, info.GetAbsoluteByteOffset(_inst.file));
            }

            _fieldCache[info.PathId] = value;
            return value;
        }

        private string BuildTransformPath(long transformPathId)
        {
            var parts = new List<string>();
            var seen = new HashSet<long>();
            var current = transformPathId;

            while (current != 0 && seen.Add(current))
            {
                var transformInfo = _inst.file.GetAssetInfo(current);
                var transformField = Read(transformInfo, ClassIdTransform);
                if (transformField == null)
                    break;

                var gameObjectRef = ReadPPtr(transformField["m_GameObject"]);
                if (gameObjectRef.PathId != 0)
                {
                    var goInfo = _inst.file.GetAssetInfo(gameObjectRef.PathId);
                    var goField = Read(goInfo, ClassIdGameObject);
                    var goName = goField?["m_Name"].AsString;
                    if (!string.IsNullOrEmpty(goName))
                        parts.Add(goName);
                }

                current = ReadPPtr(transformField["m_Father"]).PathId;
            }

            parts.Reverse();
            return string.Join("/", parts);
        }

        public void CollectGameObjectAndDescendants(long gameObjectPathId, HashSet<long> sink)
        {
            if (!sink.Add(gameObjectPathId))
                return;

            var transformPathId = GetTransformPathId(gameObjectPathId);
            if (transformPathId == 0)
                return;

            var transformInfo = _inst.file.GetAssetInfo(transformPathId);
            var transformField = transformInfo == null ? null : Read(transformInfo, ClassIdTransform);
            if (transformField == null)
                return;

            foreach (var childTransform in GetArrayElements(transformField["m_Children"]).Select(ReadPPtr))
            {
                if (childTransform.PathId == 0)
                    continue;

                var childGameObjectPathId = GetGameObjectPathIdFromTransform(childTransform.PathId);
                if (childGameObjectPathId != 0)
                    CollectGameObjectAndDescendants(childGameObjectPathId, sink);
            }
        }

        private List<ComponentRef> ReadComponentRefs(AssetTypeValueField gameObjectField)
        {
            var list = new List<ComponentRef>();
            foreach (var element in GetArrayElements(gameObjectField["m_Component"]))
            {
                var component = element["component"];
                var pptr = ReadPPtr(component);
                if (pptr.PathId == 0)
                    continue;
                var info = _inst.file.GetAssetInfo(pptr.PathId);
                if (info == null)
                    continue;

                list.Add(new ComponentRef(pptr.PathId, info.TypeId));
            }
            return list;
        }

        private SkinnedMeshRendererInfo ReadSkinnedMeshRenderer(long pathId)
        {
            var info = _inst.file.GetAssetInfo(pathId);
            var field = Read(info, ClassIdSkinnedMeshRenderer);
            if (field == null)
            {
                return new SkinnedMeshRendererInfo
                {
                    PathId = pathId,
                };
            }
            var rootBone = ReadPPtr(field["m_RootBone"]);
            var mesh = ReadPPtr(field["m_Mesh"]);
            var materials = GetArrayElements(field["m_Materials"]).Select(ReadPPtr).ToList();
            var bones = GetArrayElements(field["m_Bones"]).Select(ReadPPtr).ToList();

            return new SkinnedMeshRendererInfo
            {
                PathId = pathId,
                MeshPathId = mesh.PathId == 0 ? null : mesh.PathId,
                MeshName = mesh.PathId == 0 ? null : ReadObjectName(mesh.PathId, ClassIdMesh),
                RootBonePathId = rootBone.PathId == 0 ? null : rootBone.PathId,
                RootBoneName = rootBone.PathId == 0 ? null : ReadTransformGameObjectName(rootBone.PathId),
                RootBonePath = rootBone.PathId == 0 ? null : BuildTransformPath(rootBone.PathId),
                BoneNames = [.. bones.Select(b => ReadTransformGameObjectName(b.PathId) ?? $"<{b.PathId}>")],
                MaterialCount = materials.Count(m => m.PathId != 0),
            };
        }

        private AnimatorInfo ReadAnimator(long pathId)
        {
            var info = _inst.file.GetAssetInfo(pathId);
            var field = Read(info, ClassIdAnimator);
            if (field == null)
            {
                return new AnimatorInfo
                {
                    PathId = pathId,
                };
            }
            var avatar = ReadPPtr(field["m_Avatar"]);
            var controller = ReadPPtr(field["m_Controller"]);

            return new AnimatorInfo
            {
                PathId = pathId,
                AvatarPathId = avatar.PathId == 0 ? null : avatar.PathId,
                ControllerPathId = controller.PathId == 0 ? null : controller.PathId,
                CullingMode = field["m_CullingMode"].IsDummy ? null : field["m_CullingMode"].AsInt,
            };
        }

        private LodGroupInfo ReadLodGroup(long pathId)
        {
            var info = _inst.file.GetAssetInfo(pathId);
            var field = Read(info, ClassIdLODGroup);
            if (field == null)
            {
                return new LodGroupInfo
                {
                    PathId = pathId,
                };
            }
            var lods = new List<LodInfo>();
            foreach (var lodField in GetArrayElements(field["m_LODs"]))
            {
                var renderers = GetArrayElements(lodField["renderers"])
                    .Select(ReadPPtr)
                    .Where(p => p.PathId != 0)
                    .Select(p => p.PathId)
                    .ToList();
                lods.Add(new LodInfo
                {
                    ScreenRelativeHeight = lodField["screenRelativeHeight"].IsDummy ? null : lodField["screenRelativeHeight"].AsFloat,
                    RendererPathIds = renderers,
                });
            }

            return new LodGroupInfo
            {
                PathId = pathId,
                Lods = lods,
            };
        }

        private string? ReadTransformGameObjectName(long transformPathId)
        {
            if (transformPathId == 0)
                return null;

            var transformInfo = _inst.file.GetAssetInfo(transformPathId);
            if (transformInfo == null)
                return null;

            var transformField = Read(transformInfo, ClassIdTransform);
            if (transformField == null)
                return null;

            var gameObject = ReadPPtr(transformField["m_GameObject"]);
            return gameObject.PathId == 0 ? null : ReadObjectName(gameObject.PathId, ClassIdGameObject);
        }

        private string? ReadObjectName(long pathId, int expectedTypeId)
        {
            var info = _inst.file.GetAssetInfo(pathId);
            if (info == null)
                return null;

            var field = Read(info, expectedTypeId);
            if (field == null)
                return null;

            return expectedTypeId switch
            {
                ClassIdGameObject => field["m_Name"].AsString,
                ClassIdMesh => field["m_Name"].AsString,
                _ => null,
            };
        }

        private TransformInfo? ReadTransformInfo(long transformPathId)
        {
            var transformInfo = _inst.file.GetAssetInfo(transformPathId);
            if (transformInfo == null)
                return null;

            var field = Read(transformInfo, ClassIdTransform);
            if (field == null)
                return null;

            return new TransformInfo
            {
                PathId = transformPathId,
                LocalPosition = ReadVector3(field["m_LocalPosition"]),
                LocalScale = ReadVector3(field["m_LocalScale"]),
                ChildNames = [.. GetArrayElements(field["m_Children"])
                    .Select(ReadPPtr)
                    .Where(p => p.PathId != 0)
                    .Select(p => ReadTransformGameObjectName(p.PathId) ?? $"<{p.PathId}>")],
            };
        }

        private long GetTransformPathId(long gameObjectPathId)
        {
            var gameObject = BuildGameObjectSkeleton(gameObjectPathId);
            return gameObject.TransformPathId;
        }

        private long GetGameObjectPathIdFromTransform(long transformPathId)
        {
            var transformInfo = _inst.file.GetAssetInfo(transformPathId);
            if (transformInfo == null)
                return 0;

            var field = Read(transformInfo, ClassIdTransform);
            if (field == null)
                return 0;

            return ReadPPtr(field["m_GameObject"]).PathId;
        }

        private GameObjectSkeleton BuildGameObjectSkeleton(long pathId)
        {
            var info = _inst.file.GetAssetInfo(pathId);
            var field = Read(info, ClassIdGameObject) ?? throw new InvalidOperationException($"Failed to read GameObject {pathId}");
            var name = field["m_Name"].AsString;
            var components = ReadComponentRefs(field);
            var transformPathId = components.FirstOrDefault(c => c.TypeId == ClassIdTransform).PathId;
            return new GameObjectSkeleton(pathId, name, transformPathId, components);
        }

        private static List<AssetTypeValueField> GetArrayElements(AssetTypeValueField field)
        {
            if (field.IsDummy)
                return [];

            var arrayField = field["Array"];
            if (arrayField.IsDummy)
                return [];

            return arrayField.Children;
        }

        private static PPtr ReadPPtr(AssetTypeValueField field)
        {
            if (field.IsDummy)
                return default;

            var fileIdField = field["m_FileID"];
            var pathIdField = field["m_PathID"];
            if (fileIdField.IsDummy || pathIdField.IsDummy)
                return default;

            return new PPtr(fileIdField.AsInt, pathIdField.AsLong);
        }

        private static float[] ReadVector3(AssetTypeValueField field)
        {
            if (field.IsDummy)
                return [0f, 0f, 0f];

            return
            [
                field["x"].IsDummy ? 0f : field["x"].AsFloat,
                field["y"].IsDummy ? 0f : field["y"].AsFloat,
                field["z"].IsDummy ? 0f : field["z"].AsFloat,
            ];
        }
    }

    private readonly record struct ComponentRef(long PathId, int TypeId);
    private readonly record struct PPtr(int FileId, long PathId);
    private readonly record struct GameObjectSkeleton(long PathId, string Name, long TransformPathId, List<ComponentRef> Components);
}

// --- Report types ---

public sealed class InspectionReport
{
    public string BundlePath { get; set; } = string.Empty;
    public string GameDataPath { get; set; } = string.Empty;
    public string GameFilter { get; set; } = string.Empty;
    public string BundleFilter { get; set; } = string.Empty;
    public List<AssetFileReport> BundleFiles { get; set; } = [];
    public List<AssetFileReport> GameFiles { get; set; } = [];
}

public sealed class AssetFileReport
{
    public string Path { get; set; } = string.Empty;
    public bool HasTypeTree { get; set; }
    public string UnityVersion { get; set; } = string.Empty;
    public List<GameObjectInfo> GameObjects { get; set; } = [];
}

public sealed class GameObjectInfo
{
    public long PathId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public List<ComponentInfo> Components { get; set; } = [];
    public TransformInfo? Transform { get; set; }
    public List<SkinnedMeshRendererInfo> SkinnedMeshRenderers { get; set; } = [];
    public List<AnimatorInfo> Animators { get; set; } = [];
    public List<LodGroupInfo> LodGroups { get; set; } = [];
}

public sealed class ComponentInfo
{
    public long PathId { get; set; }
    public int TypeId { get; set; }
    public string TypeName { get; set; } = string.Empty;
}

public sealed class SkinnedMeshRendererInfo
{
    public long PathId { get; set; }
    public long? MeshPathId { get; set; }
    public string? MeshName { get; set; }
    public long? RootBonePathId { get; set; }
    public string? RootBoneName { get; set; }
    public string? RootBonePath { get; set; }
    public List<string> BoneNames { get; set; } = [];
    public int MaterialCount { get; set; }
}

public sealed class TransformInfo
{
    public long PathId { get; set; }
    public float[] LocalPosition { get; set; } = [0f, 0f, 0f];
    public float[] LocalScale { get; set; } = [1f, 1f, 1f];
    public List<string> ChildNames { get; set; } = [];
}

public sealed class AnimatorInfo
{
    public long PathId { get; set; }
    public long? AvatarPathId { get; set; }
    public long? ControllerPathId { get; set; }
    public int? CullingMode { get; set; }
}

public sealed class LodGroupInfo
{
    public long PathId { get; set; }
    public List<LodInfo> Lods { get; set; } = [];
}

public sealed class LodInfo
{
    public float? ScreenRelativeHeight { get; set; }
    public List<long> RendererPathIds { get; set; } = [];
}
