namespace Jiangyu.Core.IO;

public static class DirectoryCopier
{
    /// <summary>
    /// Recursively copy <paramref name="source"/> into <paramref name="dest"/>, creating
    /// the destination tree. <paramref name="overwrite"/> controls whether an existing
    /// destination file is replaced. Sequential: project copies are small enough that
    /// thread-pool overhead outweighs any parallelism win, and concurrent writes regress
    /// on spinning disks.
    /// </summary>
    public static void Copy(string source, string dest, bool overwrite)
    {
        Directory.CreateDirectory(dest);
        foreach (var file in Directory.EnumerateFiles(source))
            File.Copy(file, Path.Combine(dest, Path.GetFileName(file)), overwrite);
        foreach (var dir in Directory.EnumerateDirectories(source))
            Copy(dir, Path.Combine(dest, Path.GetFileName(dir)), overwrite);
    }
}
