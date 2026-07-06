namespace Jiangyu.Core.IO;

/// <summary>
/// Filesystem helpers that tolerate the transient locks and deferred deletion common on Windows:
/// antivirus and the Search indexer scan freshly written files, Explorer holds a preview handle,
/// a just-exited process releases its handles slightly late, and a recursive delete returns
/// before NTFS finalises removal (so an immediate recreate of the same path can fault). A locked
/// operation throws on the first try, so a few short retries clear the overwhelming majority. A
/// genuine missing-path error is never a lock, so it is not retried or mislabelled. An operation
/// that still fails after the retries throws with a message naming the likely culprit, instead of
/// the bare <see cref="IOException"/> that reads as an internal error to a modder.
/// </summary>
internal static class ResilientFs
{
    private static readonly int[] BackoffMs = { 50, 100, 200, 400 };

    public static void DeleteFile(string path)
        => Retry(() => File.Delete(path), path, "delete");

    public static void DeleteDirectory(string path)
    {
        if (!Directory.Exists(path))
            return;
        Retry(() => Directory.Delete(path, recursive: true), path, "delete");
    }

    /// <summary>
    /// Create a directory, retrying through the brief window after a sibling recursive delete
    /// during which Windows still reports the path as pending removal.
    /// </summary>
    public static void CreateDirectory(string path)
        => Retry(() => Directory.CreateDirectory(path), path, "create");

    // A transient lock surfaces as IOException or UnauthorizedAccessException. Missing-path errors
    // (DirectoryNotFoundException, FileNotFoundException) also derive from IOException but are not
    // locks, so they fall straight through to their own exception rather than being retried and
    // then reported as a phantom lock.
    private static bool IsTransientLock(Exception ex)
        => (ex is IOException || ex is UnauthorizedAccessException)
            && ex is not DirectoryNotFoundException
            && ex is not FileNotFoundException;

    private static void Retry(Action action, string path, string verb)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                action();
                return;
            }
            catch (Exception ex) when (IsTransientLock(ex) && attempt < BackoffMs.Length)
            {
                Thread.Sleep(BackoffMs[attempt]);
            }
            catch (Exception ex) when (IsTransientLock(ex))
            {
                throw new IOException(
                    $"Could not {verb} '{path}' after {BackoffMs.Length + 1} attempts: {ex.Message}. " +
                    "A file there is locked by another process. On Windows that is usually the game (close MENACE), " +
                    "a File Explorer window showing the folder, or an antivirus/Search-indexer scan. " +
                    "Close whatever is holding it and try again.", ex);
            }
        }
    }
}
