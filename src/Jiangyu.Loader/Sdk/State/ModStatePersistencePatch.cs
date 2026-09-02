using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HarmonyLib;
using Il2CppMenace.Strategy;
using Jiangyu.Loader.Logging;
using Jiangyu.Loader.Runtime.Patching;
using Jiangyu.Shared.State;
using MelonLoader;

namespace Jiangyu.Loader.Sdk.State;

/// <summary>
/// Persists mod state across save/load by Harmony-patching the game's save system:
/// after a save, each mod's state is written to a sidecar beside the new save file;
/// after a load, each mod's state is read back from the sidecar. The store is created
/// after these patches install, so it is handed in via the static <see cref="Store"/>.
/// </summary>
internal sealed class ModStatePersistencePatch : IHarmonyPatchModule
{
    internal static ModStateStore Store;
    private static MelonLogger.Instance _log;

    public void Install(HarmonyLib.Harmony harmony, LoaderHarmonyPatchContext context)
    {
        _log = context.Log;
        Patch(harmony, "Il2CppMenace.Strategy.SaveSystem", "Save", nameof(SavePostfix));
        Patch(harmony, "Il2CppMenace.Strategy.SaveSystem", "ExecLoad", nameof(ExecLoadPostfix));
        Patch(harmony, "Il2CppMenace.Strategy.SaveSystem", "Delete", nameof(DeletePostfix));
        Patch(harmony, "Il2CppMenace.States.StrategyState", "CreateNewGame", nameof(CreateNewGamePostfix));
    }

    private static void Patch(HarmonyLib.Harmony harmony, string typeName, string method, string postfix)
        => HarmonyPatching.TryPostfix(harmony, typeName, method, typeof(ModStatePersistencePatch), postfix, _log, "mod state");

    private static void SavePostfix(string __1, string __2)
    {
        var store = Store;
        if (store == null)
            return;
        try
        {
            ModStateSidecarRepair.RunOnce(_log);
            var latest = SaveSystem.GetLatestSaveFilePath();
            // The game moves the finished save into place before this postfix runs, so File.Exists
            // is what tells the resolver a derived path missed the file that was actually written.
            string derived = null;
            string Derive(string name) => derived = SaveNameFold.PathForName(name);
            var slot = SaveSlotResolver.Resolve(__1, __2, Derive, File.Exists);
            // An explicit path is the save the caller meant, hit or miss. With no file behind it the
            // write did not land, and the guess below would hand this state to an unrelated slot.
            bool explicitPath = !string.IsNullOrEmpty(__1);
            // A name that folds away to nothing is the game's own manual_<timestamp> case and the
            // recovery handles it. A path we did name and cannot find is the fold drifting from the
            // game, which the recovery papers over silently, so say so here.
            if (slot == null && (explicitPath || derived != null))
                _log.Warning($"mod state: no save file at {derived ?? __1}");
            // Autosaves, quicksaves and save names that fold away to nothing leave the file the game
            // just wrote underivable, so recover it by content. A slot we did resolve is exact, so
            // skip the guess there: it could otherwise grab a concurrent autosave that happens to be
            // newer and attach the sidecar to the wrong save.
            var targets = new[]
            {
                slot,
                latest,
                slot == null && !explicitPath ? GetJustWrittenSavePath(latest) : null,
            };
            var written = new HashSet<string>(StringComparer.Ordinal);
            foreach (var path in targets)
            {
                if (string.IsNullOrEmpty(path) || !written.Add(path))
                    continue;
                LoaderDebug.Write(_log, $"mod state: save -> {path}");
                store.WriteAll(path);
            }
        }
        catch (Exception ex)
        {
            _log.Error($"mod state: save postfix failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    // The file the game just wrote, for the saves whose path Save derives internally: autosaves,
    // quicksaves, and the manual_<timestamp> fallback a save name folds to when nothing survives.
    // Matching the latest alias byte for byte identifies it outright; the newest file by mtime is
    // the fallback when the alias cannot be read or nothing matches it.
    private static string GetJustWrittenSavePath(string latest)
    {
        try
        {
            var paths = SaveSystem.GetSaveFilePaths();
            if (paths == null)
                return null;
            // The two calls need not spell the same file the same way, so the folder is flat and
            // the file name is what tells the alias apart from the saves it aliases.
            var aliasName = FileNameOrNull(latest);
            var aliasLength = LengthOrMissing(latest);
            string newest = null;
            var newestTime = DateTime.MinValue;
            for (int i = 0; i < paths.Length; i++)
            {
                var p = paths[i];
                if (string.IsNullOrEmpty(p) || (aliasName != null && FileNameOrNull(p) == aliasName))
                    continue;
                DateTime t;
                long length;
                try
                {
                    if (!File.Exists(p))
                        continue;
                    t = File.GetLastWriteTimeUtc(p);
                    length = LengthOrMissing(p);
                }
                catch { continue; }
                // A read that fails here must not cost this candidate its place in the mtime
                // comparison, so the content match answers false rather than throwing out of it.
                if (aliasLength >= 0 && length == aliasLength && ContentsMatch(p, latest))
                    return p;
                if (t > newestTime)
                {
                    newestTime = t;
                    newest = p;
                }
            }
            return newest;
        }
        catch
        {
            return null;
        }
    }

    private static string FileNameOrNull(string path)
        => string.IsNullOrEmpty(path) ? null : Path.GetFileName(path);

    // The size of a file, or -1 when there is nothing readable there.
    private static long LengthOrMissing(string path)
    {
        try
        {
            if (string.IsNullOrEmpty(path))
                return -1;
            var info = new FileInfo(path);
            return info.Exists ? info.Length : -1;
        }
        catch
        {
            return -1;
        }
    }

    // JIANGYU-CONTRACT: Save writes one temporary file, copies it onto the latest alias and then
    // moves it to the real slot, so the alias and the file just written are byte identical. That
    // makes the alias an exact fingerprint for the save being written, where mtime alone can be
    // beaten by a concurrent autosave. Valid for the current MENACE save system; verified by
    // disassembling Menace.Strategy.SaveSystem.Save and comparing latest.save against the save it
    // aliases on disk, see docs/research/investigations/2026-08-04-save-file-name-derivation.md.
    // Saves run to megabytes and this sits in a save postfix on the game thread, so the comparison
    // streams both files through fixed buffers instead of holding either one whole.
    private static bool ContentsMatch(string left, string right)
    {
        try
        {
            using var leftStream = File.OpenRead(left);
            using var rightStream = File.OpenRead(right);
            var leftBuffer = new byte[CompareBufferBytes];
            var rightBuffer = new byte[CompareBufferBytes];
            while (true)
            {
                var read = FillBuffer(leftStream, leftBuffer);
                if (read != FillBuffer(rightStream, rightBuffer))
                    return false;
                if (read == 0)
                    return true;
                if (!leftBuffer.AsSpan(0, read).SequenceEqual(rightBuffer.AsSpan(0, read)))
                    return false;
            }
        }
        catch
        {
            return false;
        }
    }

    private const int CompareBufferBytes = 64 * 1024;

    // Reads until the buffer is full or the stream ends, so a short read cannot desynchronise the
    // two sides of the comparison.
    private static int FillBuffer(Stream stream, byte[] buffer)
    {
        var filled = 0;
        while (filled < buffer.Length)
        {
            var read = stream.Read(buffer, filled, buffer.Length - filled);
            if (read == 0)
                break;
            filled += read;
        }
        return filled;
    }

    private static void ExecLoadPostfix(SaveState __0)
    {
        try
        {
            if (__0 == null)
                return;
            // Sweep before the load, so a sidecar reattached here is one this load can still read.
            ModStateSidecarRepair.RunOnce(_log);
            var path = __0.GetFilePath();
            LoaderDebug.Write(_log, $"mod state: load <- {path}");
            Store?.LoadAll(path);
        }
        catch (Exception ex)
        {
            _log.Error($"mod state: load postfix failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    // A save the game drops takes its sidecars with it, so autosave rotation and a manual delete
    // both stop leaving state behind for a slot that no longer exists.
    private static void DeletePostfix(string __0)
    {
        try
        {
            if (string.IsNullOrEmpty(__0))
                return;
            Store?.DeleteAll(__0);
        }
        catch (Exception ex)
        {
            _log.Error($"mod state: delete postfix failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void CreateNewGamePostfix()
    {
        try
        {
            Store?.ResetAll();
            _log.Msg("mod state: reset for new game");
        }
        catch (Exception ex)
        {
            _log.Error($"mod state: new-game reset failed: {ex.GetType().Name}: {ex.Message}");
        }
    }
}
