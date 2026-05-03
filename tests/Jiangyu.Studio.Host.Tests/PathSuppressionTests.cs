using System.Text.Json;

namespace Jiangyu.Studio.Host.Tests;

/// <summary>
/// Tests for the editFile / writeFile path-suppression wrappers in
/// <see cref="RpcDispatcher"/>. The wrappers record an upcoming-event
/// suppression in <see cref="ProjectWatcher"/> for the calling window's id
/// so that a same-window save doesn't fire its own conflict banner. The
/// MCP path (null window id) skips suppression; the WebView path threads
/// the calling window through.
/// </summary>
public class PathSuppressionTests : IDisposable
{
    private readonly string _projectDir;
    private readonly string _file;
    private readonly string _tmp;

    public PathSuppressionTests()
    {
        _projectDir = Path.Combine(Path.GetTempPath(), "jiangyu-suppress-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_projectDir);
        File.WriteAllText(Path.Combine(_projectDir, "jiangyu.json"), "{}");
        ProjectWatcher.ProjectRoot = Path.GetFullPath(_projectDir);
        _file = Path.Combine(_projectDir, "scratch.txt");
        _tmp = _file + ".jiangyu.tmp";
        ProjectWatcher.ResetSuppressionForTesting();
    }

    public void Dispose()
    {
        ProjectWatcher.ResetSuppressionForTesting();
        ProjectWatcher.ProjectRoot = null;
        try { Directory.Delete(_projectDir, recursive: true); } catch { }
    }

    [Fact]
    public void WriteFile_WithWindowId_SuppressesPathAndTmp()
    {
        var windowId = Guid.NewGuid();
        var parameters = JsonSerializer.SerializeToElement(new { path = _file, content = "hello" });

        RpcDispatcher.WriteFileWithSuppression(windowId, parameters);

        Assert.True(ProjectWatcher.HasSuppressionForTesting(_file, windowId),
            "writeFile must suppress the target path for the calling window");
        Assert.True(ProjectWatcher.HasSuppressionForTesting(_tmp, windowId),
            "writeFile must suppress the staged tmp path so the rename's own event isn't surfaced");
        Assert.Equal("hello", File.ReadAllText(_file));
    }

    [Fact]
    public void WriteFile_NullWindow_NoSuppressionRecorded()
    {
        var someWindowId = Guid.NewGuid();
        var parameters = JsonSerializer.SerializeToElement(new { path = _file, content = "hello" });

        RpcDispatcher.WriteFileWithSuppression(windowId: null, parameters);

        Assert.False(ProjectWatcher.HasSuppressionForTesting(_file, someWindowId));
        Assert.False(ProjectWatcher.HasSuppressionForTesting(_tmp, someWindowId));
        Assert.Equal("hello", File.ReadAllText(_file));
    }

    [Fact]
    public void WriteFile_OtherWindowsNotSuppressed_StillSeeChange()
    {
        // Cross-window conflict surface: a save in window A looks like an
        // external edit to window B. The suppression must be scoped to the
        // calling window only.
        var windowA = Guid.NewGuid();
        var windowB = Guid.NewGuid();
        var parameters = JsonSerializer.SerializeToElement(new { path = _file, content = "from-A" });

        RpcDispatcher.WriteFileWithSuppression(windowA, parameters);

        Assert.True(ProjectWatcher.HasSuppressionForTesting(_file, windowA));
        Assert.False(ProjectWatcher.HasSuppressionForTesting(_file, windowB),
            "Suppression must be window-scoped; window B should still see the change");
    }

    [Fact]
    public void EditFile_WithWindowId_SuppressesPathAndTmp()
    {
        File.WriteAllText(_file, "before middle after");
        var windowId = Guid.NewGuid();
        var parameters = JsonSerializer.SerializeToElement(new
        {
            path = _file,
            oldText = "middle",
            newText = "centre",
        });

        RpcDispatcher.EditFileWithSuppression(windowId, parameters);

        Assert.True(ProjectWatcher.HasSuppressionForTesting(_file, windowId));
        Assert.True(ProjectWatcher.HasSuppressionForTesting(_tmp, windowId));
        Assert.Equal("before centre after", File.ReadAllText(_file));
    }

    [Fact]
    public void EditFile_NullWindow_NoSuppressionRecorded()
    {
        File.WriteAllText(_file, "before middle after");
        var someWindowId = Guid.NewGuid();
        var parameters = JsonSerializer.SerializeToElement(new
        {
            path = _file,
            oldText = "middle",
            newText = "centre",
        });

        RpcDispatcher.EditFileWithSuppression(windowId: null, parameters);

        Assert.False(ProjectWatcher.HasSuppressionForTesting(_file, someWindowId));
        Assert.Equal("before centre after", File.ReadAllText(_file));
    }
}
