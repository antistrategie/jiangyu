using AssetsTools.NET;
using AssetsTools.NET.Extra;
using Jiangyu.Core.Abstractions;

namespace Jiangyu.Core.Compile;

/// <summary>
/// Restores hollow AnimationClips inside a staged addition-prefab bundle
/// from the game's own assets.
///
/// AssetRipper rips of the game's clips play fine in the ENGINE, but their
/// curve payload is opaque to the Unity editor's serialisation model, so a
/// bundle built from a ripped controller ships every vanilla clip as an
/// empty shell: correct settings, zero curves. Any state driven by one
/// freezes at rest pose in-game (a crouch drops the character to the root
/// baseline, one-shots never play). The modder cannot fix this from the
/// editor side: reading the ripped curves via AnimationUtility returns
/// nothing, and any editor write re-serialises the clip and discards them.
///
/// The restoration walks the staged bundle, finds AnimationClips whose curve
/// payload is empty, and replaces each object's data, in place, with the raw
/// bytes of the identically named clip from the game files. The game and the
/// mod bundle target the same engine version, so the binary layout matches,
/// and in-place replacement preserves every reference (the controller's clip
/// pointers). The originals carry the true curves, animation events
/// (footsteps, vault completion), and root-motion settings.
/// </summary>
internal static class AnimationClipRestoration
{
    private const int ClassIdAnimationClip = 74;

    /// <summary>
    /// Lazily built name-to-raw-bytes index of the game's AnimationClips.
    /// Built once per compile, shared across staged bundles. Matching is by
    /// NAME, which is weaker than the reference identity a Unity build
    /// carries, so names that map to more than one distinct clip payload in
    /// the game files are tracked and warned about on use.
    /// </summary>
    public sealed class GameClipIndex(string gameDataPath)
    {
        private Dictionary<string, byte[]>? _clips;
        private HashSet<string>? _ambiguous;

        public IReadOnlyDictionary<string, byte[]> Clips
        {
            get
            {
                EnsureBuilt();
                return _clips!;
            }
        }

        /// <summary>Names carried by multiple game clips with DIFFERENT payloads.</summary>
        public IReadOnlySet<string> AmbiguousNames
        {
            get
            {
                EnsureBuilt();
                return _ambiguous!;
            }
        }

        private void EnsureBuilt()
        {
            if (_clips != null)
                return;
            var clips = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            var ambiguous = new HashSet<string>(StringComparer.Ordinal);
            var am = new AssetsManager();
            try
            {
                foreach (var path in Directory.EnumerateFiles(gameDataPath, "*.assets")
                             .OrderBy(p => p, StringComparer.Ordinal))
                {
                    AssetsFileInstance? inst;
                    try
                    {
                        inst = am.LoadAssetsFile(path, loadDeps: false);
                    }
                    catch
                    {
                        continue;
                    }
                    if (inst?.file == null)
                        continue;

                    var reader = inst.file.Reader;
                    foreach (var info in inst.file.AssetInfos)
                    {
                        if (info.TypeId != ClassIdAnimationClip)
                            continue;
                        // m_Name is the first field of any NamedObject:
                        // int32 length + UTF-8 bytes. Game files carry no
                        // typetrees, so it is read directly.
                        reader.Position = info.GetAbsoluteByteOffset(inst.file);
                        var name = reader.ReadCountStringInt32();
                        if (string.IsNullOrEmpty(name))
                            continue;
                        reader.Position = info.GetAbsoluteByteOffset(inst.file);
                        var data = reader.ReadBytes((int)info.ByteSize);
                        if (clips.TryGetValue(name, out var existing))
                        {
                            if (!existing.AsSpan().SequenceEqual(data))
                                ambiguous.Add(name);
                            continue;
                        }
                        clips[name] = data;
                    }
                }
            }
            finally
            {
                am.UnloadAll();
            }
            _clips = clips;
            _ambiguous = ambiguous;
        }
    }

    /// <summary>
    /// Scans one staged bundle and swaps hollow clips for game originals.
    /// No-ops (and leaves the file byte-identical) when the bundle contains
    /// no hollow clips, so mods without ripped controllers pay one cheap
    /// scan and nothing else. Never fails the compile: any error leaves the
    /// bundle as built and logs a warning.
    /// </summary>
    public static void RestoreStagedBundle(string bundlePath, GameClipIndex gameClips, ILogSink log)
    {
        var restored = 0;
        var missing = new List<string>();
        var ambiguous = new List<string>();
        try
        {
            restored = Restore(bundlePath, gameClips, missing, ambiguous);
        }
        catch (Exception e)
        {
            log.Warning(
                $"  Animation clip restoration failed for {Path.GetFileName(bundlePath)}: "
                + $"{e.GetType().Name}: {e.Message}. The bundle ships as built.");
        }
        finally
        {
            // never leave write-phase temp files behind
            TryDelete(bundlePath + ".restore-raw");
            TryDelete(bundlePath + ".restore-packed");
        }

        if (restored > 0)
            log.Info($"  Restored {restored} hollow animation clip(s) in {Path.GetFileName(bundlePath)} from game assets.");
        foreach (var name in missing.Distinct().Take(5))
            log.Warning($"  Hollow animation clip '{name}' has no matching game clip; it will play empty.");
        foreach (var name in ambiguous.Distinct().Take(5))
            log.Warning($"  Hollow animation clip '{name}' matches multiple distinct game clips; restored from the first by file order.");
    }

    private static int Restore(string bundlePath, GameClipIndex gameClips, List<string> missing, List<string> ambiguous)
    {
        var am = new AssetsManager
        {
            UseQuickLookup = true,
            UseTemplateFieldCache = true,
        };

        var restored = 0;
        try
        {
            // Open the stream OURSELVES: LoadBundleFile(path) opens a
            // FileStream internally and leaks it when parsing throws before
            // the instance registers with the manager (hand-shipped
            // escape-hatch files are not necessarily real bundles).
            var stream = File.OpenRead(bundlePath);
            BundleFileInstance bundle;
            try
            {
                bundle = am.LoadBundleFile(stream, unpackIfPacked: true);
            }
            catch
            {
                stream.Dispose();
                return 0; // not a readable bundle: nothing to restore
            }

            var dirInfos = bundle.file.BlockAndDirInfo.DirectoryInfos;
            var modified = false;

            for (var i = 0; i < dirInfos.Count; i++)
            {
                AssetsFileInstance? inst;
                try
                {
                    inst = am.LoadAssetsFileFromBundle(bundle, i, loadDeps: false);
                }
                catch
                {
                    continue; // .resS and other non-assets entries
                }
                if (inst?.file == null || !inst.file.Metadata.TypeTreeEnabled)
                    continue;

                var fileModified = false;
                foreach (var info in inst.file.AssetInfos)
                {
                    if (info.TypeId != ClassIdAnimationClip)
                        continue;
                    var baseField = am.GetBaseField(inst, info);
                    if (baseField == null || !IsHollow(baseField))
                        continue;

                    var name = baseField["m_Name"].AsString;
                    if (!gameClips.Clips.TryGetValue(name, out var original))
                    {
                        missing.Add(name);
                        continue;
                    }
                    if (gameClips.AmbiguousNames.Contains(name))
                        ambiguous.Add(name);
                    info.SetNewData(original);
                    restored++;
                    fileModified = true;
                }

                if (fileModified)
                {
                    dirInfos[i].SetNewData(inst.file);
                    modified = true;
                }
            }

            if (!modified)
                return restored;

            // Write honours the SetNewData replacements but emits an
            // uncompressed bundle; Pack compresses but serialises from the
            // ORIGINAL blocks. So: write the modified content first, reload
            // it, then pack.
            var rawPath = bundlePath + ".restore-raw";
            var packedPath = bundlePath + ".restore-packed";
            using (var writer = new AssetsFileWriter(rawPath))
            {
                bundle.file.Write(writer);
            }
            am.UnloadAll();

            var rawBundle = new AssetBundleFile();
            using (var rawReader = new AssetsFileReader(File.OpenRead(rawPath)))
            {
                rawBundle.Read(rawReader);
                using var packedWriter = new AssetsFileWriter(packedPath);
                rawBundle.Pack(packedWriter, AssetBundleCompressionType.LZ4);
            }
            File.Delete(rawPath);
            File.Move(packedPath, bundlePath, overwrite: true);
            return restored;
        }
        finally
        {
            am.UnloadAll();
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // best-effort cleanup
        }
    }

    /// <summary>
    /// A humanoid clip whose streamed, dense, and constant curve blocks are
    /// all empty carries no animation at all: the ripped-clip signature.
    /// Healthy clips (including generic ones) always carry at least one.
    /// </summary>
    private static bool IsHollow(AssetTypeValueField clip)
    {
        var muscle = clip["m_MuscleClip"];
        if (muscle.IsDummy)
            return false;
        var data = muscle["m_Clip"]["data"];
        if (data.IsDummy)
            return false;
        // vector fields carry a single "Array" child that holds the items
        var streamed = data["m_StreamedClip"]["data"]["Array"];
        var denseCount = data["m_DenseClip"]["m_CurveCount"];
        var constant = data["m_ConstantClip"]["data"]["Array"];
        return (streamed.IsDummy || streamed.Children.Count == 0)
            && (denseCount.IsDummy || denseCount.AsUInt == 0)
            && (constant.IsDummy || constant.Children.Count == 0);
    }
}
