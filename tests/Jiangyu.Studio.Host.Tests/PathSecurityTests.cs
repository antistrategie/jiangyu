namespace Jiangyu.Studio.Host.Tests;

public class PathSecurityTests : IDisposable
{
    private readonly string _projectDir;

    public PathSecurityTests()
    {
        _projectDir = Path.Combine(Path.GetTempPath(), "jiangyu-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_projectDir);
        File.WriteAllText(Path.Combine(_projectDir, "jiangyu.json"), "{}");
        ProjectWatcher.ProjectRoot = Path.GetFullPath(_projectDir);
    }

    public void Dispose()
    {
        ProjectWatcher.ProjectRoot = null;
        try { Directory.Delete(_projectDir, recursive: true); } catch { }
    }

    [Fact]
    public void PathInsideProject_Accepted()
    {
        var inside = Path.Combine(_projectDir, "templates", "test.kdl");
        RpcHelpers.EnsurePathInsideProject(inside);
    }

    [Fact]
    public void ProjectRootItself_Accepted()
    {
        RpcHelpers.EnsurePathInsideProject(_projectDir);
    }

    [Fact]
    public void PathOutsideProject_Rejected()
    {
        var outside = Path.Combine(Path.GetTempPath(), "other-project", "evil.txt");
        Assert.Throws<UnauthorizedAccessException>(() =>
            RpcHelpers.EnsurePathInsideProject(outside));
    }

    [Fact]
    public void TraversalAttack_Rejected()
    {
        var traversal = Path.Combine(_projectDir, "..", "other", "evil.txt");
        Assert.Throws<UnauthorizedAccessException>(() =>
            RpcHelpers.EnsurePathInsideProject(traversal));
    }

    [Fact]
    public void NoProjectOpen_Throws()
    {
        ProjectWatcher.ProjectRoot = null;
        Assert.Throws<InvalidOperationException>(() =>
            RpcHelpers.EnsurePathInsideProject("/any/path"));
    }
}
