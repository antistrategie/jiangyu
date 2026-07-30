using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace Jiangyu.Mod
{
    public static class BuildMeshReplacementBundle
    {
        private const uint Magic = 0x4D455348; // "MESH"
        private const uint TextureMagic = 0x54585452; // "TXTR"

        private const string StagingRoot = "Assets/Jiangyu/Staging/MeshReplacement";
        private const string GeneratedDir = StagingRoot + "/Generated";
        private const string SpriteSourcesDir = StagingRoot + "/SpriteSources";
        private const string SpriteAdditionsDir = StagingRoot + "/SpriteAdditions";
        private const string AudioDir = StagingRoot + "/Audio";

        private static bool IsSpriteSourceExtension(string path)
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            return ext == ".png" || ext == ".jpg" || ext == ".jpeg";
        }

        public static void BuildAll()
        {
            var args = Environment.GetCommandLineArgs();
            var meshDataPath = GetArg(args, "-meshDataPath");
            var textureDataPath = GetArg(args, "-textureDataPath");
            var meshContractPath = GetArg(args, "-meshContractPath");
            var outputPath = GetArg(args, "-outputPath");
            var diagnosticsPath = GetArg(args, "-diagnosticsPath");
            var bundleName = GetArg(args, "-bundleName") ?? "meshes";
            var bundlePlanPath = GetArg(args, "-bundlePlanPath");
            var completionToken = GetArg(args, "-completionToken");
            var runPrefabs = string.Equals(GetArg(args, "-runPrefabs"), "true", StringComparison.OrdinalIgnoreCase);

            // Co-locating the prefab pass with the mesh-replacement pass in
            // one Unity batchmode session saves the second cold start (~5-8s
            // on a Linux Editor) when a mod has both. The compile pipeline
            // sets -runPrefabs true when both passes have work; the standalone
            // BuildBundles entry still exists for prefab-only builds.
            if (runPrefabs)
            {
                if (!BuildBundles.RunCore())
                {
                    EditorApplication.Exit(1);
                    return;
                }
            }

            if (string.IsNullOrEmpty(meshDataPath) || string.IsNullOrEmpty(outputPath) || string.IsNullOrEmpty(bundlePlanPath))
            {
                Debug.LogError("[Jiangyu] Missing required args: -meshDataPath, -outputPath and -bundlePlanPath");
                EditorApplication.Exit(1);
                return;
            }

            if (!File.Exists(meshDataPath))
            {
                Debug.LogError($"[Jiangyu] Mesh data file not found: {meshDataPath}");
                EditorApplication.Exit(1);
                return;
            }

            var meshes = ReadMeshData(meshDataPath);
            var textures = ReadTextureData(textureDataPath);
            var contracts = ReadMeshContracts(meshContractPath);
            var plan = BundlePlan.Load(bundlePlanPath);

            var projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            var modRoot = Path.GetFullPath(Path.Combine(projectRoot, ".."));
            var bakedStatePath = Path.Combine(modRoot, ".jiangyu", "generated_baked");
            // What the previous successful build materialised under Generated/, by input
            // hash. An unchanged input keeps its .asset (and with it the GUID Unity's
            // per-bundle hashing sees), so its bundle hashes as current and is skipped.
            // A changed one is deleted and recreated, which is exactly the signal that
            // rebuilds its bundle. The file is written only after a verified build, so a
            // failed bake re-bakes on the next run.
            var bakedState = LoadBakedState(bakedStatePath);
            var newBakedState = new Dictionary<string, string>();
            var expectedGenerated = new HashSet<string>();

            Directory.CreateDirectory(GeneratedDir);
            AssetDatabase.Refresh();

            // Every asset is collected into its planned bundle and shipped via the explicit
            // AssetBundleBuild map passed to BuildAssetBundles below. Nothing gets a
            // persistent assetBundleName: the prefab pass (BuildBundles) builds every
            // assignment in the project, so an assignment on a staged or generated asset
            // would fold the whole replacement set into that pass as well.
            var assetsByBundle = new Dictionary<string, List<string>>();
            var diagnostics = new StringBuilder();
            foreach (var meshData in meshes)
            {
                contracts.TryGetValue(meshData.Name, out var contract);
                var mesh = CreateMesh(meshData, contract);
                var assetPath = $"{GeneratedDir}/{mesh.name}.asset";
                // Meshes are always re-baked: contract stamping in the second pass mutates
                // them, so their input is not a pure function of meshdata.bin alone.
                if (File.Exists(assetPath))
                    AssetDatabase.DeleteAsset(assetPath);
                AssetDatabase.CreateAsset(mesh, assetPath);
                ApplyMeshContract(mesh, contract);
                diagnostics.AppendLine(BuildDiagnostics(mesh, contract));
                expectedGenerated.Add($"{mesh.name}.asset");
                AddToBundle(assetsByBundle, plan.MeshesBundle, assetPath);
            }

            foreach (var textureData in textures)
            {
                string bundle;
                if (!plan.TextureBundles.TryGetValue(textureData.Name, out bundle))
                    throw new InvalidDataException($"Bundle plan has no entry for texture '{textureData.Name}'");
                var assetPath = $"{GeneratedDir}/{textureData.Name}.asset";
                var hash = plan.TextureHashes[textureData.Name];
                var bakedKey = "texture:" + textureData.Name;
                string bakedHash;
                if (!(bakedState.TryGetValue(bakedKey, out bakedHash) && bakedHash == hash && File.Exists(assetPath)))
                {
                    if (File.Exists(assetPath))
                        AssetDatabase.DeleteAsset(assetPath);
                    var texture = CreateTexture(textureData);
                    AssetDatabase.CreateAsset(texture, assetPath);
                }
                newBakedState[bakedKey] = hash;
                expectedGenerated.Add($"{textureData.Name}.asset");
                AddToBundle(assetsByBundle, bundle, assetPath);
            }

            var spriteAssetCount = 0;
            // Decode the source PNG directly into an explicit RGBA32 Texture2D
            // instead of going through AssetDatabase.LoadAssetAtPath. The importer
            // path applies format auto-selection that strips the alpha channel for
            // solid-colour inputs, which then breaks the runtime mutation when the
            // destination game texture relies on alpha.
            if (Directory.Exists(SpriteSourcesDir))
            {
                // Both replacement and addition sprites stage flat in this
                // directory with the sprite_source__ prefix. The bundle stores
                // each sprite under the prefix-stripped stem. For additions the
                // C# pipeline pre-flattens slashes to `__` (see
                // AssetCategory.ToBundleAssetName) so the same prefix-strip
                // produces the bundle name the runtime resolver expects.
                foreach (var pngFile in Directory.GetFiles(SpriteSourcesDir, "*.*", SearchOption.TopDirectoryOnly))
                {
                    if (!IsSpriteSourceExtension(pngFile))
                        continue;

                    var spriteName = Path.GetFileNameWithoutExtension(pngFile);
                    const string prefix = "sprite_source__";
                    if (spriteName.StartsWith(prefix, StringComparison.Ordinal))
                        spriteName = spriteName.Substring(prefix.Length);

                    var textureAssetPath = $"{GeneratedDir}/{prefix}{spriteName}.asset";
                    var assetPath = $"{GeneratedDir}/{spriteName}.asset";
                    string sourceHash;
                    if (!plan.SpriteSourceHashes.TryGetValue(spriteName, out sourceHash))
                        throw new InvalidDataException($"Bundle plan has no entry for replacement sprite '{spriteName}'");
                    var bakedKey = "spritesource:" + spriteName;
                    string bakedHash;
                    // The sprite object and its backing texture are one unit: the sprite
                    // references the texture, so they are kept or recreated together.
                    if (!(bakedState.TryGetValue(bakedKey, out bakedHash) && bakedHash == sourceHash
                          && File.Exists(textureAssetPath) && File.Exists(assetPath)))
                    {
                        if (File.Exists(assetPath))
                            AssetDatabase.DeleteAsset(assetPath);
                        if (File.Exists(textureAssetPath))
                            AssetDatabase.DeleteAsset(textureAssetPath);

                        var sourceTexture = new Texture2D(2, 2, TextureFormat.RGBA32, mipChain: false, linear: false);
                        sourceTexture.name = prefix + spriteName;
                        if (!ImageConversion.LoadImage(sourceTexture, File.ReadAllBytes(pngFile), markNonReadable: false))
                            throw new InvalidDataException($"Failed to decode sprite source '{pngFile}'");
                        sourceTexture.wrapMode = TextureWrapMode.Clamp;
                        sourceTexture.filterMode = FilterMode.Bilinear;
                        sourceTexture.Apply(updateMipmaps: false, makeNoLongerReadable: false);
                        AssetDatabase.CreateAsset(sourceTexture, textureAssetPath);

                        var sprite = Sprite.Create(
                            sourceTexture,
                            new Rect(0f, 0f, sourceTexture.width, sourceTexture.height),
                            new Vector2(0.5f, 0.5f),
                            pixelsPerUnit: 100f);
                        sprite.name = spriteName;

                        // Use plain .asset (matching textures) rather than .sprite.asset.
                        // The .sprite.asset suffix bakes ".sprite" into the runtime
                        // sprite.name, which would break catalog lookups against the
                        // game's live sprite name.
                        AssetDatabase.CreateAsset(sprite, assetPath);
                    }
                    newBakedState[bakedKey] = sourceHash;
                    expectedGenerated.Add($"{prefix}{spriteName}.asset");
                    expectedGenerated.Add($"{spriteName}.asset");
                    AddToBundle(assetsByBundle, plan.SpritesBundle, textureAssetPath);
                    AddToBundle(assetsByBundle, plan.SpritesBundle, assetPath);
                    spriteAssetCount++;
                }
            }

            // Addition sprites go through Unity's standard TextureImporter path
            // because they're consumed as UI icons (the runtime-Texture2D path
            // above leaves m_RD.texture serialised as an unresolvable PPtr in the
            // bundle; the runtime ends up aliasing it with whatever asset shares
            // the fileID slot, which trashes the UI canvas when something tries
            // to render the sprite). The PNG sits as a project asset and Unity's
            // importer produces a properly-serialisable Texture2D + Sprite pair.
            if (Directory.Exists(SpriteAdditionsDir))
            {
                foreach (var pngFile in Directory.GetFiles(SpriteAdditionsDir, "*.*", SearchOption.TopDirectoryOnly))
                {
                    if (!IsSpriteSourceExtension(pngFile))
                        continue;

                    var importer = AssetImporter.GetAtPath(pngFile) as TextureImporter;
                    if (importer == null)
                    {
                        // A freshly staged file has no meta yet, so import once to
                        // materialise the importer before configuring it.
                        AssetDatabase.ImportAsset(pngFile, ImportAssetOptions.ForceSynchronousImport);
                        importer = AssetImporter.GetAtPath(pngFile) as TextureImporter;
                    }
                    if (importer == null)
                        throw new InvalidDataException($"Failed to acquire TextureImporter for '{pngFile}'");

                    // Reconfigure and reimport only when the saved settings differ.
                    // Staging keeps unchanged files and their metas in place across
                    // builds, so on a warm project this loop costs no imports.
                    if (importer.textureType != TextureImporterType.Sprite
                        || importer.spriteImportMode != SpriteImportMode.Single
                        || !importer.alphaIsTransparency
                        || importer.alphaSource != TextureImporterAlphaSource.FromInput
                        || importer.mipmapEnabled
                        || importer.filterMode != FilterMode.Bilinear
                        || importer.wrapMode != TextureWrapMode.Clamp
                        || importer.textureCompression != TextureImporterCompression.Uncompressed)
                    {
                        importer.textureType = TextureImporterType.Sprite;
                        importer.spriteImportMode = SpriteImportMode.Single;
                        importer.alphaIsTransparency = true;
                        importer.alphaSource = TextureImporterAlphaSource.FromInput;
                        importer.mipmapEnabled = false;
                        importer.filterMode = FilterMode.Bilinear;
                        importer.wrapMode = TextureWrapMode.Clamp;
                        importer.textureCompression = TextureImporterCompression.Uncompressed;
                        importer.SaveAndReimport();
                    }

                    // Persistent staging means this meta survives into prefab-pass runs,
                    // so scrub any bundle assignment it carries.
                    if (!string.IsNullOrEmpty(importer.assetBundleName))
                        importer.assetBundleName = string.Empty;

                    var importedSprite = AssetDatabase.LoadAssetAtPath<Sprite>(pngFile);
                    if (importedSprite == null)
                        throw new InvalidDataException($"Failed to import sprite '{pngFile}'");

                    AddToBundle(assetsByBundle, plan.SpritesBundle, pngFile);
                    spriteAssetCount++;
                }
            }

            var audioAssetCount = 0;
            var audioGuids = AssetDatabase.FindAssets("t:AudioClip", new[] { AudioDir });
            foreach (var guid in audioGuids)
            {
                var assetPath = AssetDatabase.GUIDToAssetPath(guid);
                if (string.IsNullOrEmpty(assetPath))
                    continue;

                var importer = AssetImporter.GetAtPath(assetPath);
                if (importer == null)
                    continue;

                // Force PCM + DecompressOnLoad: Vorbis transcoding smears
                // transients on percussive content (gunshots, impacts).
                if (importer is AudioImporter audioImporter)
                {
                    var settings = audioImporter.defaultSampleSettings;
                    if (settings.compressionFormat != AudioCompressionFormat.PCM
                        || settings.loadType != AudioClipLoadType.DecompressOnLoad)
                    {
                        settings.compressionFormat = AudioCompressionFormat.PCM;
                        settings.loadType = AudioClipLoadType.DecompressOnLoad;
                        audioImporter.defaultSampleSettings = settings;
                        audioImporter.SaveAndReimport();
                    }
                }

                // Persistent staging means this meta survives into prefab-pass runs,
                // so scrub any bundle assignment it carries.
                if (!string.IsNullOrEmpty(importer.assetBundleName))
                    importer.assetBundleName = string.Empty;
                var clipName = Path.GetFileNameWithoutExtension(assetPath);
                string audioBundle;
                if (!plan.AudioBundles.TryGetValue(clipName, out audioBundle))
                    throw new InvalidDataException($"Bundle plan has no entry for audio clip '{clipName}'");
                AddToBundle(assetsByBundle, audioBundle, assetPath);
                audioAssetCount++;
            }

            if (assetsByBundle.Count == 0)
            {
                Debug.LogError("[Jiangyu] No meshes, textures, sprites, or audio assets found to bundle");
                EditorApplication.Exit(1);
                return;
            }

            // Anything materialised by a previous plan but absent from this one is dead:
            // its input left the mod, so its asset (and meta) must not linger in Generated/
            // where a later bundle could still pull it in.
            foreach (var file in Directory.GetFiles(GeneratedDir, "*.asset", SearchOption.TopDirectoryOnly))
            {
                var fileName = Path.GetFileName(file);
                if (!expectedGenerated.Contains(fileName))
                    AssetDatabase.DeleteAsset($"{GeneratedDir}/{fileName}");
            }

            AssetDatabase.SaveAssets();

            if (!string.IsNullOrEmpty(diagnosticsPath) && diagnostics.Length > 0)
            {
                var diagnosticsDir = Path.GetDirectoryName(diagnosticsPath);
                if (!string.IsNullOrEmpty(diagnosticsDir))
                    Directory.CreateDirectory(diagnosticsDir);
                File.WriteAllText(diagnosticsPath, diagnostics.ToString());
            }

            var outputDir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(outputDir))
                Directory.CreateDirectory(outputDir);

            Debug.Log($"[Jiangyu] Prepared {meshes.Count} mesh(es), {textures.Count} texture(s), {spriteAssetCount} sprite(s), {audioAssetCount} audio clip(s) across {assetsByBundle.Count} bundle(s) for '{bundleName}'");

            var builds = assetsByBundle
                .OrderBy(kvp => kvp.Key, StringComparer.Ordinal)
                .Select(kvp => new AssetBundleBuild
                {
                    assetBundleName = kvp.Key,
                    assetNames = kvp.Value.ToArray(),
                })
                .ToArray();
            var expectedBundles = new List<string>(assetsByBundle.Keys);

            // LZ4 (chunk-based): compresses each bundle FILE on disk without changing the assets
            // inside. Additions are dominated by uncompressed RGBA32 textures (large
            // flat/transparent regions) and PCM audio, both of which LZ4 shrinks well, so the
            // shipped files are much smaller. LZ4 is block compression decoded on demand by
            // LoadFromFile, so load stays streamed and runtime memory is unchanged.
            //
            // Incremental first: with stable staged GUIDs and the baked-state skip above, an
            // unchanged group's bundle hashes as current and is not rebuilt, which is the whole
            // point of splitting. The stale-manifest hazard (Unity skipping a bundle yet writing
            // nothing while reporting success) is caught by the verify below and recovered with
            // one forced rebuild.
            var manifest = BuildPipeline.BuildAssetBundles(
                outputDir,
                builds,
                BuildAssetBundleOptions.ChunkBasedCompression,
                EditorUserBuildSettings.activeBuildTarget);

            if (manifest == null)
            {
                Debug.LogError("[Jiangyu] BuildAssetBundles returned null");
                EditorApplication.Exit(1);
                return;
            }

            if (!BundleBuildVerify.AllWritten(outputDir, expectedBundles, manifest, "[Jiangyu] (incremental)"))
            {
                Debug.LogWarning("[Jiangyu] incremental replacement build left expected bundle(s) unwritten, retrying with ForceRebuildAssetBundle.");
                manifest = BuildPipeline.BuildAssetBundles(
                    outputDir,
                    builds,
                    BuildAssetBundleOptions.ChunkBasedCompression | BuildAssetBundleOptions.ForceRebuildAssetBundle,
                    EditorUserBuildSettings.activeBuildTarget);
                if (manifest == null || !BundleBuildVerify.AllWritten(outputDir, expectedBundles, manifest, "[Jiangyu] (forced)"))
                {
                    EditorApplication.Exit(1);
                    return;
                }
            }

            SaveBakedState(bakedStatePath, newBakedState);
            // Written last: the compile pipeline treats a fresh marker carrying its token
            // as the only proof this script ran to completion, since the bundle files
            // themselves persist across compiles as incremental state.
            if (!string.IsNullOrEmpty(completionToken))
                File.WriteAllText(Path.Combine(modRoot, ".jiangyu", "unity_build_mesh.done"), completionToken);
            EditorApplication.Exit(0);
        }

        private static void AddToBundle(Dictionary<string, List<string>> assetsByBundle, string bundle, string assetPath)
        {
            if (string.IsNullOrEmpty(bundle))
                throw new InvalidDataException($"Bundle plan assigns no bundle for '{assetPath}'");
            List<string> assets;
            if (!assetsByBundle.TryGetValue(bundle, out assets))
            {
                assets = new List<string>();
                assetsByBundle[bundle] = assets;
            }
            assets.Add(assetPath);
        }

        private static Dictionary<string, string> LoadBakedState(string path)
        {
            var state = new Dictionary<string, string>();
            if (!File.Exists(path))
                return state;
            foreach (var line in File.ReadAllLines(path))
            {
                var parts = line.Split('\t');
                if (parts.Length == 2)
                    state[parts[0]] = parts[1];
            }
            return state;
        }

        private static void SaveBakedState(string path, Dictionary<string, string> state)
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            var lines = new List<string>(state.Count);
            foreach (var kvp in state)
                lines.Add(kvp.Key + "\t" + kvp.Value);
            lines.Sort(StringComparer.Ordinal);
            File.WriteAllLines(path, lines);
        }

        /// <summary>
        /// The compile-side bundle plan (see Jiangyu's ReplacementBundlePlan): which bundle
        /// each replacement asset ships in, plus input hashes for the Generated/ assets so
        /// unchanged ones can keep their files and GUIDs across builds.
        /// </summary>
        private sealed class BundlePlan
        {
            public readonly Dictionary<string, string> AudioBundles = new Dictionary<string, string>();
            public readonly Dictionary<string, string> TextureBundles = new Dictionary<string, string>();
            public readonly Dictionary<string, string> TextureHashes = new Dictionary<string, string>();
            public readonly Dictionary<string, string> SpriteSourceHashes = new Dictionary<string, string>();
            public string SpritesBundle;
            public string MeshesBundle;

            public static BundlePlan Load(string path)
            {
                var lines = File.ReadAllLines(path);
                if (lines.Length == 0 || lines[0] != "jiangyu-bundle-plan 1")
                    throw new InvalidDataException($"Unrecognised bundle plan at '{path}'");

                var plan = new BundlePlan();
                for (var i = 1; i < lines.Length; i++)
                {
                    if (string.IsNullOrWhiteSpace(lines[i]))
                        continue;
                    var parts = lines[i].Split('\t');
                    switch (parts[0])
                    {
                        case "audio":
                            plan.AudioBundles[parts[1]] = parts[2];
                            break;
                        case "sprites":
                            plan.SpritesBundle = parts[1];
                            break;
                        case "spritesource":
                            plan.SpriteSourceHashes[parts[1]] = parts[2];
                            break;
                        case "texture":
                            plan.TextureBundles[parts[1]] = parts[2];
                            plan.TextureHashes[parts[1]] = parts[3];
                            break;
                        case "meshes":
                            plan.MeshesBundle = parts[1];
                            break;
                        default:
                            throw new InvalidDataException($"Unrecognised bundle plan entry '{parts[0]}' at '{path}'");
                    }
                }
                return plan;
            }
        }

        private static Mesh CreateMesh(MeshData data, MeshContract? contract)
        {
            var mesh = new Mesh();
            mesh.name = data.Name;

            if (data.VertexCount > 65535)
                mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;

            mesh.vertices = data.Positions;
            if (data.Normals != null && data.Normals.Length > 0) mesh.normals = data.Normals;
            if (data.Tangents != null && data.Tangents.Length > 0) mesh.tangents = data.Tangents;
            if (data.UV0 != null) mesh.uv = data.UV0;
            if (data.UV1 != null) mesh.uv2 = data.UV1;
            if (data.Colors != null) mesh.colors32 = data.Colors;

            if (data.SubMeshes != null && data.SubMeshes.Length > 0)
            {
                mesh.subMeshCount = data.SubMeshes.Length;
                for (int i = 0; i < data.SubMeshes.Length; i++)
                {
                    var subMesh = data.SubMeshes[i];
                    var indices = new int[subMesh.IndexCount];
                    Array.Copy(data.Indices, subMesh.IndexStart, indices, 0, subMesh.IndexCount);
                    mesh.SetIndices(indices, MeshTopology.Triangles, i);
                }
            }
            else
            {
                mesh.triangles = data.Indices;
            }

            if (data.BoneWeights != null && data.BoneWeights.Length > 0)
                mesh.boneWeights = data.BoneWeights;
            var bindPoses = contract?.BindPoses is { Length: > 0 }
                ? contract.BindPoses
                : data.BindPoses;
            if (bindPoses != null && bindPoses.Length > 0)
                mesh.bindposes = bindPoses;

            mesh.RecalculateBounds();
            if (data.Normals == null || data.Normals.Length == 0)
                mesh.RecalculateNormals();
            if ((data.Tangents == null || data.Tangents.Length == 0) && data.UV0 != null)
                mesh.RecalculateTangents();

            return mesh;
        }

        private static void ApplyMeshContract(Mesh mesh, MeshContract? contract)
        {
            if (contract == null)
                return;

            var serialized = new SerializedObject(mesh);
            var boneHashes = serialized.FindProperty("m_BoneNameHashes");
            if (boneHashes != null && boneHashes.isArray)
            {
                boneHashes.arraySize = contract.BoneNameHashes.Length;
                for (int i = 0; i < contract.BoneNameHashes.Length; i++)
                {
                    boneHashes.GetArrayElementAtIndex(i).intValue = unchecked((int)contract.BoneNameHashes[i]);
                }
            }

            var rootBoneHash = serialized.FindProperty("m_RootBoneNameHash");
            if (rootBoneHash != null)
                rootBoneHash.longValue = contract.RootBoneNameHash;

            serialized.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(mesh);
        }

        private static Texture2D CreateTexture(TextureData data)
        {
            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, true, data.Linear);
            texture.name = data.Name;
            if (!ImageConversion.LoadImage(texture, data.Content, markNonReadable: false))
                throw new InvalidDataException($"Failed to decode texture '{data.Name}'");

            texture.wrapMode = TextureWrapMode.Repeat;
            texture.filterMode = FilterMode.Bilinear;
            texture.Apply(updateMipmaps: true, makeNoLongerReadable: false);
            return texture;
        }

        private static string BuildDiagnostics(Mesh mesh, MeshContract? contract)
        {
            var sb = new StringBuilder();
            var bounds = mesh.bounds;
            sb.AppendLine($"mesh={mesh.name}");
            sb.AppendLine($"vertexCount={mesh.vertexCount}");
            sb.AppendLine($"bindPoseCount={mesh.bindposes?.Length ?? 0}");
            sb.AppendLine($"boneWeightCount={mesh.boneWeights?.Length ?? 0}");
            sb.AppendLine($"contractApplied={(contract != null ? 1 : 0)}");
            sb.AppendLine($"contractBoneHashCount={contract?.BoneNameHashes.Length ?? 0}");
            sb.AppendLine($"contractRootBoneHash={(contract?.RootBoneNameHash ?? 0)}");
            sb.AppendLine($"boundsCenter={bounds.center.x:F4},{bounds.center.y:F4},{bounds.center.z:F4}");
            sb.AppendLine($"boundsSize={bounds.size.x:F4},{bounds.size.y:F4},{bounds.size.z:F4}");

            var boneWeights = mesh.boneWeights;
            if (boneWeights != null)
            {
                for (int i = 0; i < Math.Min(8, boneWeights.Length); i++)
                {
                    var bw = boneWeights[i];
                    sb.AppendLine(
                        $"sample[{i}] joints={bw.boneIndex0},{bw.boneIndex1},{bw.boneIndex2},{bw.boneIndex3} " +
                        $"weights={bw.weight0:F4},{bw.weight1:F4},{bw.weight2:F4},{bw.weight3:F4}");
                }
            }

            sb.AppendLine("--");
            return sb.ToString();
        }

        private static List<MeshData> ReadMeshData(string path)
        {
            var meshes = new List<MeshData>();

            using var fs = File.OpenRead(path);
            using var reader = new BinaryReader(fs);

            var magic = reader.ReadUInt32();
            if (magic != Magic)
                throw new InvalidDataException($"Invalid magic: 0x{magic:X8}");

            var version = reader.ReadInt32();
            if (version != 1)
                throw new InvalidDataException($"Unsupported version: {version}");

            var meshCount = reader.ReadInt32();
            for (int m = 0; m < meshCount; m++)
            {
                var data = new MeshData();

                var nameLen = reader.ReadInt32();
                data.Name = Encoding.UTF8.GetString(reader.ReadBytes(nameLen));

                data.VertexCount = reader.ReadInt32();
                var indexCount = reader.ReadInt32();
                var subMeshCount = reader.ReadInt32();
                var flags = reader.ReadByte();

                var hasNormals = (flags & 0x01) != 0;
                var hasTangents = (flags & 0x02) != 0;
                var hasUv0 = (flags & 0x04) != 0;
                var hasUv1 = (flags & 0x08) != 0;
                var hasColors = (flags & 0x10) != 0;
                var hasSkinning = (flags & 0x20) != 0;

                data.Positions = ReadVector3Array(reader, data.VertexCount);
                if (hasNormals) data.Normals = ReadVector3Array(reader, data.VertexCount);
                if (hasTangents) data.Tangents = ReadVector4Array(reader, data.VertexCount);
                if (hasUv0) data.UV0 = ReadVector2Array(reader, data.VertexCount);
                if (hasUv1) data.UV1 = ReadVector2Array(reader, data.VertexCount);

                if (hasColors)
                {
                    data.Colors = new Color32[data.VertexCount];
                    for (int i = 0; i < data.VertexCount; i++)
                        data.Colors[i] = new Color32(reader.ReadByte(), reader.ReadByte(), reader.ReadByte(), reader.ReadByte());
                }

                data.Indices = new int[indexCount];
                for (int i = 0; i < indexCount; i++)
                    data.Indices[i] = reader.ReadInt32();

                data.SubMeshes = new SubMeshData[subMeshCount];
                for (int i = 0; i < subMeshCount; i++)
                {
                    data.SubMeshes[i] = new SubMeshData
                    {
                        IndexStart = reader.ReadInt32(),
                        IndexCount = reader.ReadInt32(),
                    };
                    reader.ReadInt32();
                }

                if (hasSkinning)
                {
                    data.BoneWeights = new BoneWeight[data.VertexCount];
                    for (int i = 0; i < data.VertexCount; i++)
                    {
                        data.BoneWeights[i].weight0 = reader.ReadSingle();
                        data.BoneWeights[i].weight1 = reader.ReadSingle();
                        data.BoneWeights[i].weight2 = reader.ReadSingle();
                        data.BoneWeights[i].weight3 = reader.ReadSingle();
                    }

                    for (int i = 0; i < data.VertexCount; i++)
                    {
                        data.BoneWeights[i].boneIndex0 = reader.ReadInt32();
                        data.BoneWeights[i].boneIndex1 = reader.ReadInt32();
                        data.BoneWeights[i].boneIndex2 = reader.ReadInt32();
                        data.BoneWeights[i].boneIndex3 = reader.ReadInt32();
                    }

                    var bindPoseCount = reader.ReadInt32();
                    data.BindPoses = new Matrix4x4[bindPoseCount];
                    for (int i = 0; i < bindPoseCount; i++)
                    {
                        // Bind poses are serialised from System.Numerics.Matrix4x4 (row-major)
                        // and must be transposed when rebuilding UnityEngine.Matrix4x4.
                        var m11 = reader.ReadSingle(); var m12 = reader.ReadSingle(); var m13 = reader.ReadSingle(); var m14 = reader.ReadSingle();
                        var m21 = reader.ReadSingle(); var m22 = reader.ReadSingle(); var m23 = reader.ReadSingle(); var m24 = reader.ReadSingle();
                        var m31 = reader.ReadSingle(); var m32 = reader.ReadSingle(); var m33 = reader.ReadSingle(); var m34 = reader.ReadSingle();
                        var m41 = reader.ReadSingle(); var m42 = reader.ReadSingle(); var m43 = reader.ReadSingle(); var m44 = reader.ReadSingle();

                        var bindPose = new Matrix4x4();
                        bindPose.m00 = m11; bindPose.m01 = m21; bindPose.m02 = m31; bindPose.m03 = m41;
                        bindPose.m10 = m12; bindPose.m11 = m22; bindPose.m12 = m32; bindPose.m13 = m42;
                        bindPose.m20 = m13; bindPose.m21 = m23; bindPose.m22 = m33; bindPose.m23 = m43;
                        bindPose.m30 = m14; bindPose.m31 = m24; bindPose.m32 = m34; bindPose.m33 = m44;
                        data.BindPoses[i] = bindPose;
                    }
                }

                meshes.Add(data);
            }

            return meshes;
        }

        private static Vector2[] ReadVector2Array(BinaryReader reader, int count)
        {
            var result = new Vector2[count];
            for (int i = 0; i < count; i++)
                result[i] = new Vector2(reader.ReadSingle(), reader.ReadSingle());
            return result;
        }

        private static Vector3[] ReadVector3Array(BinaryReader reader, int count)
        {
            var result = new Vector3[count];
            for (int i = 0; i < count; i++)
                result[i] = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
            return result;
        }

        private static Vector4[] ReadVector4Array(BinaryReader reader, int count)
        {
            var result = new Vector4[count];
            for (int i = 0; i < count; i++)
                result[i] = new Vector4(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
            return result;
        }

        private static Dictionary<string, MeshContract> ReadMeshContracts(string? path)
        {
            var contracts = new Dictionary<string, MeshContract>(StringComparer.Ordinal);
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                return contracts;

            using var fs = File.OpenRead(path);
            using var reader = new BinaryReader(fs);

            var magic = reader.ReadUInt32();
            if (magic != 0x54435254)
                throw new InvalidDataException($"Invalid mesh contract magic: 0x{magic:X8}");

            var version = reader.ReadInt32();
            if (version != 1)
                throw new InvalidDataException($"Unsupported mesh contract version: {version}");

            var count = reader.ReadInt32();
            for (int i = 0; i < count; i++)
            {
                var nameLen = reader.ReadInt32();
                var name = Encoding.UTF8.GetString(reader.ReadBytes(nameLen));

                var boneHashCount = reader.ReadInt32();
                var boneHashes = new uint[boneHashCount];
                for (int j = 0; j < boneHashCount; j++)
                    boneHashes[j] = reader.ReadUInt32();

                var rootBoneHash = reader.ReadUInt32();

                var bindPoseCount = reader.ReadInt32();
                var bindPoses = new Matrix4x4[bindPoseCount];
                for (int j = 0; j < bindPoseCount; j++)
                {
                    var bindPose = new Matrix4x4();
                    bindPose.m00 = reader.ReadSingle(); bindPose.m01 = reader.ReadSingle(); bindPose.m02 = reader.ReadSingle(); bindPose.m03 = reader.ReadSingle();
                    bindPose.m10 = reader.ReadSingle(); bindPose.m11 = reader.ReadSingle(); bindPose.m12 = reader.ReadSingle(); bindPose.m13 = reader.ReadSingle();
                    bindPose.m20 = reader.ReadSingle(); bindPose.m21 = reader.ReadSingle(); bindPose.m22 = reader.ReadSingle(); bindPose.m23 = reader.ReadSingle();
                    bindPose.m30 = reader.ReadSingle(); bindPose.m31 = reader.ReadSingle(); bindPose.m32 = reader.ReadSingle(); bindPose.m33 = reader.ReadSingle();
                    bindPoses[j] = bindPose;
                }

                contracts[name] = new MeshContract
                {
                    Name = name,
                    BoneNameHashes = boneHashes,
                    RootBoneNameHash = rootBoneHash,
                    BindPoses = bindPoses,
                };
            }

            return contracts;
        }

        private static List<TextureData> ReadTextureData(string? path)
        {
            var textures = new List<TextureData>();
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                return textures;

            using var fs = File.OpenRead(path);
            using var reader = new BinaryReader(fs);

            var magic = reader.ReadUInt32();
            if (magic != TextureMagic)
                throw new InvalidDataException($"Invalid texture magic: 0x{magic:X8}");

            var version = reader.ReadInt32();
            if (version != 1)
                throw new InvalidDataException($"Unsupported texture version: {version}");

            var count = reader.ReadInt32();
            for (int i = 0; i < count; i++)
            {
                var nameLen = reader.ReadInt32();
                var name = Encoding.UTF8.GetString(reader.ReadBytes(nameLen));
                var linear = reader.ReadByte() != 0;
                var contentLen = reader.ReadInt32();
                var content = reader.ReadBytes(contentLen);

                textures.Add(new TextureData
                {
                    Name = name,
                    Linear = linear,
                    Content = content,
                });
            }

            return textures;
        }

        private static string? GetArg(string[] args, string name)
        {
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == name)
                    return args[i + 1];
            }

            return null;
        }

        private sealed class MeshData
        {
            public string Name = string.Empty;
            public int VertexCount;
            public Vector3[] Positions = Array.Empty<Vector3>();
            public Vector3[]? Normals;
            public Vector4[]? Tangents;
            public Vector2[]? UV0;
            public Vector2[]? UV1;
            public Color32[]? Colors;
            public int[] Indices = Array.Empty<int>();
            public SubMeshData[]? SubMeshes;
            public BoneWeight[]? BoneWeights;
            public Matrix4x4[]? BindPoses;
        }

        private sealed class MeshContract
        {
            public string Name = string.Empty;
            public uint[] BoneNameHashes = Array.Empty<uint>();
            public uint RootBoneNameHash;
            public Matrix4x4[] BindPoses = Array.Empty<Matrix4x4>();
        }

        private sealed class SubMeshData
        {
            public int IndexStart;
            public int IndexCount;
        }

        private sealed class TextureData
        {
            public string Name = string.Empty;
            public bool Linear;
            public byte[] Content = Array.Empty<byte>();
        }
    }
}
