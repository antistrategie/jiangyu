using System.Diagnostics;
using System.Text.Json;
using Jiangyu.Studio.Rpc;

namespace Jiangyu.Studio.Host.Tests;

public sealed class ListAllFilesTests : IDisposable
{
    private const int Cap = 10_000;

    private readonly string _projectDir;

    public ListAllFilesTests()
    {
        _projectDir = Path.Combine(Path.GetTempPath(), "jiangyu-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_projectDir);
        ProjectWatcher.ProjectRoot = Path.GetFullPath(_projectDir);
    }

    public void Dispose()
    {
        ProjectWatcher.ProjectRoot = null;
        try { Directory.Delete(_projectDir, recursive: true); } catch { }
    }

    private static bool GitInit(string dir)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("git", "init -q")
            {
                WorkingDirectory = dir,
                UseShellExecute = false,
            });
            if (process is null) return false;
            process.WaitForExit();
            return process.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    // The names past the cap must overflow the OS pipe buffer, or git exits
    // on its own and the capped path is never exercised.
    private void WriteFiles(int count)
    {
        var padding = new string('x', 60);
        for (var i = 0; i < count; i++)
            File.WriteAllBytes(Path.Combine(_projectDir, $"file-{i:D6}-{padding}.txt"), []);
    }

    private List<string> List()
    {
        var parameters = JsonSerializer.SerializeToElement(new { path = _projectDir });
        return [.. RpcHandlers.ListAllFiles(parameters).EnumerateArray().Select(e => e.GetString()!)];
    }

    [Fact]
    public async Task GitRepoOverTheCap_ReturnsTheCapWithoutHanging()
    {
        if (!GitInit(_projectDir)) return;
        WriteFiles(Cap + 4_000);

        var listing = Task.Run(List);
        var finished = await Task.WhenAny(listing, Task.Delay(TimeSpan.FromSeconds(30)));

        Assert.Same(listing, finished);
        Assert.Equal(Cap, (await listing).Count);
    }

    [Fact]
    public void GitRepoUnderTheCap_ListsEveryFile()
    {
        if (!GitInit(_projectDir)) return;
        WriteFiles(25);

        Assert.Equal(25, List().Count);
    }
}
