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
        private Dictionary<string, string>? _aliases;

        /// <summary>Test seam: build from an in-memory clip set instead of game files.</summary>
        internal GameClipIndex(Dictionary<string, byte[]> clips) : this(string.Empty)
        {
            _clips = clips;
            _ambiguous = new HashSet<string>(StringComparer.Ordinal);
            _aliases = BuildAliases(clips, _ambiguous);
        }

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

        /// <summary>
        /// Exact-name lookup with a ripped-name fallback: an AssetRipper rip
        /// of an FBX sub-asset clip ("model|clip") sanitises the characters a
        /// file name cannot carry to underscores, so the bundle-side name no
        /// longer matches the game-side one. The alias index maps each game
        /// name's sanitised form back to the original.
        /// </summary>
        public bool TryGetClip(string name, out byte[] data, out string matchedName)
        {
            EnsureBuilt();
            if (_clips!.TryGetValue(name, out data!))
            {
                matchedName = name;
                return true;
            }
            if (_aliases!.TryGetValue(name, out var canonical) && _clips.TryGetValue(canonical, out data!))
            {
                matchedName = canonical;
                return true;
            }
            data = null!;
            matchedName = name;
            return false;
        }

        private static Dictionary<string, string> BuildAliases(
            Dictionary<string, byte[]> clips, HashSet<string> ambiguous)
        {
            var aliases = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var name in clips.Keys)
            {
                var alias = SanitiseAsRippedFileName(name);
                if (alias == name || clips.ContainsKey(alias))
                    continue;
                if (aliases.TryGetValue(alias, out var existing))
                {
                    // two distinct game clips collapse onto one ripped name:
                    // the pick is arbitrary, surface it like any other
                    // ambiguous match
                    if (!string.Equals(existing, name, StringComparison.Ordinal))
                        ambiguous.Add(alias);
                    continue;
                }
                aliases[alias] = name;
            }
            return aliases;
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
            _aliases = BuildAliases(clips, ambiguous);
        }
    }

    /// <summary>
    /// The name a game clip gets when AssetRipper writes it as a .anim file:
    /// every character a file name cannot carry becomes an underscore. The
    /// pipe of the FBX sub-asset convention ("model|clip") is the case that
    /// actually occurs in the game's clips; the rest are covered alongside.
    /// </summary>
    internal static string SanitiseAsRippedFileName(string name)
    {
        var chars = name.ToCharArray();
        var changed = false;
        for (var i = 0; i < chars.Length; i++)
        {
            if (chars[i] is '|' or '/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>')
            {
                chars[i] = '_';
                changed = true;
            }
        }
        return changed ? new string(chars) : name;
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
        var aliased = new List<(string Hollow, string Game)>();
        try
        {
            restored = Restore(bundlePath, gameClips, missing, ambiguous, aliased);
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
        // An alias restore is not name-exact: the hollow clip was matched to a
        // game clip only after sanitising the game name (e.g. FBX 'model|clip'
        // -> 'model_clip'). Surface it so a coincidental collision with an
        // unrelated clip of the same sanitised name is visible, not silent.
        foreach (var (hollow, game) in aliased.Take(10))
            log.Info($"  Hollow animation clip '{hollow}' restored from game clip '{game}' via a sanitised-name alias.");
    }

    private static int Restore(string bundlePath, GameClipIndex gameClips, List<string> missing, List<string> ambiguous, List<(string Hollow, string Game)> aliased)
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
                    if (!gameClips.TryGetClip(name, out var original, out var matchedName))
                    {
                        missing.Add(name);
                        continue;
                    }
                    if (gameClips.AmbiguousNames.Contains(name) || gameClips.AmbiguousNames.Contains(matchedName))
                        ambiguous.Add(name);
                    if (!string.Equals(matchedName, name, StringComparison.Ordinal))
                        aliased.Add((name, matchedName));
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
