using System.Text.Json;
using AssetsTools.NET;
using AssetsTools.NET.Extra;

namespace Jiangyu.Compiler.Commands;

public static class InspectCommand
{
    private const string DefaultOutputFileName = "jiangyu-inspect.json";
    private static readonly JsonSerializerOptions PrettyJsonOptions = new()
    {
        WriteIndented = true,
    };
    private const int ClassIdGameObject = 1;
    private const int ClassIdTransform = 4;
    private const int ClassIdMeshRenderer = 23;
    private const int ClassIdMeshFilter = 33;
    private const int ClassIdMesh = 43;
    private const int ClassIdAnimator = 95;
    private const int ClassIdSkinnedMeshRenderer = 137;
    private const int ClassIdLODGroup = 205;

    public static Task<int> RunAsync(string[] args)
    {
        var options = ParseArgs(args);
        if (options is null)
        {
            PrintUsage();
            return Task.FromResult(1);
        }

        var resolvedOptions = options.Value;

        if (!File.Exists(resolvedOptions.BundlePath))
        {
            Console.Error.WriteLine($"Error: bundle not found: {resolvedOptions.BundlePath}");
            return Task.FromResult(1);
        }

        if (!Directory.Exists(resolvedOptions.GameDataPath))
        {
            Console.Error.WriteLine($"Error: game data directory not found: {resolvedOptions.GameDataPath}");
            return Task.FromResult(1);
        }

        try
        {
            Run(resolvedOptions);
            return Task.FromResult(0);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: inspect failed: {ex}");
            return Task.FromResult(1);
        }
    }

    private static void Run(InspectOptions options)
    {
        var am = new AssetsManager
        {
            UseQuickLookup = true,
            UseTemplateFieldCache = true,
            UseMonoTemplateFieldCache = true,
        };

        var bundle = am.LoadBundleFile(options.BundlePath);
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

        var gameFiles = EnumerateGameAssetFiles(options.GameDataPath)
            .Select(path => am.LoadAssetsFile(path, loadDeps: false))
            .Where(inst => inst?.file != null)
            .ToList();

        var report = new InspectionReport
        {
            BundlePath = options.BundlePath,
            GameDataPath = options.GameDataPath,
            GameFilter = options.GameFilter,
            BundleFilter = options.BundleFilter,
            BundleFiles = [.. bundleAssetFiles.Select(inst => InspectFile(inst, templates, options.BundleFilter))],
            GameFiles = [.. gameFiles.Select(inst => InspectFile(inst, templates, options.GameFilter))],
        };

        Directory.CreateDirectory(Path.GetDirectoryName(options.OutputPath)!);
        File.WriteAllText(options.OutputPath, JsonSerializer.Serialize(report, PrettyJsonOptions));

        var bundleMatches = report.BundleFiles.Sum(f => f.GameObjects.Count);
        var gameMatches = report.GameFiles.Sum(f => f.GameObjects.Count);
        Console.WriteLine($"Wrote inspection report to {options.OutputPath}");
        Console.WriteLine($"  bundle matches: {bundleMatches}");
        Console.WriteLine($"  game matches:   {gameMatches}");

        foreach (var file in report.GameFiles.Where(f => f.GameObjects.Count > 0))
        {
            Console.WriteLine($"[game] {Path.GetFileName(file.Path)}");
            foreach (var go in file.GameObjects.Take(8))
                PrintGameObjectSummary(go);
        }

        foreach (var file in report.BundleFiles.Where(f => f.GameObjects.Count > 0))
        {
            Console.WriteLine($"[bundle] {Path.GetFileName(file.Path)}");
            foreach (var go in file.GameObjects.Take(8))
                PrintGameObjectSummary(go);
        }
    }

    private static void PrintGameObjectSummary(GameObjectInfo go)
    {
        var compSummary = string.Join(", ", go.Components.Select(c => c.TypeName));
        Console.WriteLine($"  {go.Path} [{compSummary}]");
        if (go.Transform is not null)
        {
            Console.WriteLine($"    Transform pos={FormatVector(go.Transform.LocalPosition)} scale={FormatVector(go.Transform.LocalScale)} children={go.Transform.ChildNames.Count}");
        }
        foreach (var smr in go.SkinnedMeshRenderers)
        {
            Console.WriteLine($"    SMR mesh={smr.MeshName ?? smr.MeshPathId?.ToString() ?? "null"} root={smr.RootBonePath ?? smr.RootBoneName ?? smr.RootBonePathId?.ToString() ?? "null"} bones={smr.BoneNames.Count} mats={smr.MaterialCount}");
        }
        foreach (var animator in go.Animators)
        {
            Console.WriteLine($"    Animator avatar={animator.AvatarPathId?.ToString() ?? "null"} controller={animator.ControllerPathId?.ToString() ?? "null"} culling={animator.CullingMode}");
        }
        foreach (var lod in go.LodGroups)
        {
            Console.WriteLine($"    LODGroup lods={lod.Lods.Count}");
        }
    }

    private static string FormatVector(float[] values)
        => $"({string.Join(", ", values.Select(v => v.ToString("0.####")))})";

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
            var fileName = Path.GetFileName(path);
            if (fileName.Equals("globalgamemanagers.assets", StringComparison.OrdinalIgnoreCase) ||
                fileName.Equals("resources.assets", StringComparison.OrdinalIgnoreCase) ||
                fileName.StartsWith("sharedassets", StringComparison.OrdinalIgnoreCase) && fileName.EndsWith(".assets", StringComparison.OrdinalIgnoreCase) ||
                fileName.StartsWith("level", StringComparison.OrdinalIgnoreCase))
            {
                yield return path;
            }
        }
    }

    private static InspectOptions? ParseArgs(string[] args)
    {
        string? bundlePath = null;
        string? gameDataPath = null;
        var gameFilter = "basic_soldier";
        string? bundleFilter = null;
        var outputPath = GetDefaultOutputPath();

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--bundle":
                    bundlePath = args[++i];
                    break;
                case "--game-data":
                    gameDataPath = args[++i];
                    break;
                case "--filter":
                    gameFilter = args[++i];
                    break;
                case "--bundle-filter":
                    bundleFilter = args[++i];
                    break;
                case "--out":
                    outputPath = args[++i];
                    break;
                default:
                    return null;
            }
        }

        if (bundlePath is null || gameDataPath is null)
            return null;

        return new InspectOptions(
            Path.GetFullPath(bundlePath),
            Path.GetFullPath(gameDataPath),
            gameFilter,
            bundleFilter ?? gameFilter,
            Path.GetFullPath(outputPath));
    }

    private static string GetDefaultOutputPath()
        => Path.Combine(Path.GetTempPath(), DefaultOutputFileName);

    private static void PrintUsage()
    {
        Console.Error.WriteLine("Usage: jiangyu inspect --bundle <bundle> --game-data <Menace_Data> [--filter <name>] [--bundle-filter <name>] [--out <json>]");
    }

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

    private static string GetClassName(int typeId) => typeId switch
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

    private readonly record struct InspectOptions(
        string BundlePath,
        string GameDataPath,
        string GameFilter,
        string BundleFilter,
        string OutputPath);

    private readonly record struct ComponentRef(long PathId, int TypeId);
    private readonly record struct PPtr(int FileId, long PathId);
    private readonly record struct GameObjectSkeleton(long PathId, string Name, long TransformPathId, List<ComponentRef> Components);

    private sealed class InspectionReport
    {
        public string BundlePath { get; set; } = string.Empty;
        public string GameDataPath { get; set; } = string.Empty;
        public string GameFilter { get; set; } = string.Empty;
        public string BundleFilter { get; set; } = string.Empty;
        public List<AssetFileReport> BundleFiles { get; set; } = [];
        public List<AssetFileReport> GameFiles { get; set; } = [];
    }

    private sealed class AssetFileReport
    {
        public string Path { get; set; } = string.Empty;
        public bool HasTypeTree { get; set; }
        public string UnityVersion { get; set; } = string.Empty;
        public List<GameObjectInfo> GameObjects { get; set; } = [];
    }

    private sealed class GameObjectInfo
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

    private sealed class ComponentInfo
    {
        public long PathId { get; set; }
        public int TypeId { get; set; }
        public string TypeName { get; set; } = string.Empty;
    }

    private sealed class SkinnedMeshRendererInfo
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

    private sealed class TransformInfo
    {
        public long PathId { get; set; }
        public float[] LocalPosition { get; set; } = [0f, 0f, 0f];
        public float[] LocalScale { get; set; } = [1f, 1f, 1f];
        public List<string> ChildNames { get; set; } = [];
    }

    private sealed class AnimatorInfo
    {
        public long PathId { get; set; }
        public long? AvatarPathId { get; set; }
        public long? ControllerPathId { get; set; }
        public int? CullingMode { get; set; }
    }

    private sealed class LodGroupInfo
    {
        public long PathId { get; set; }
        public List<LodInfo> Lods { get; set; } = [];
    }

    private sealed class LodInfo
    {
        public float? ScreenRelativeHeight { get; set; }
        public List<long> RendererPathIds { get; set; } = [];
    }
}
