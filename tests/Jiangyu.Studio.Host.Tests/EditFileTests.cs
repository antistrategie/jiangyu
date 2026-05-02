using System.Text.Json;

namespace Jiangyu.Studio.Host.Tests;

public class EditFileTests : IDisposable
{
    private readonly string _projectDir;

    public EditFileTests()
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

    private static JsonElement Call(string method, JsonElement? parameters)
    {
        string? response = null;
        var request = new { id = 1, method, @params = parameters };
        var json = JsonSerializer.Serialize(request);
        RpcDispatcher.HandleMessage(null!, json, r => response = r);
        var doc = JsonDocument.Parse(response!);
        if (doc.RootElement.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.String)
            throw new Exception(err.GetString());
        return doc.RootElement.GetProperty("result");
    }

    private JsonElement MakeParams(object obj) =>
        JsonSerializer.SerializeToElement(obj);

    [Fact]
    public void EditFile_ReplacesExactlyOnce()
    {
        var file = Path.Combine(_projectDir, "test.txt");
        File.WriteAllText(file, "hello world\ngoodbye world\n");

        Call("editFile", MakeParams(new { path = file, oldText = "hello", newText = "hi" }));

        var content = File.ReadAllText(file);
        Assert.Equal("hi world\ngoodbye world\n", content);
    }

    [Fact]
    public void EditFile_FailsWhenNotFound()
    {
        var file = Path.Combine(_projectDir, "test2.txt");
        File.WriteAllText(file, "some content\n");

        var ex = Assert.Throws<Exception>(() =>
            Call("editFile", MakeParams(new { path = file, oldText = "nonexistent", newText = "x" })));
        Assert.Contains("not found", ex.Message);
    }

    [Fact]
    public void EditFile_FailsWhenAmbiguous()
    {
        var file = Path.Combine(_projectDir, "test3.txt");
        File.WriteAllText(file, "aaa\naaa\n");

        var ex = Assert.Throws<Exception>(() =>
            Call("editFile", MakeParams(new { path = file, oldText = "aaa", newText = "bbb" })));
        Assert.Contains("more than once", ex.Message);
    }

    [Fact]
    public void EditFile_MultilineReplace()
    {
        var file = Path.Combine(_projectDir, "multi.txt");
        File.WriteAllText(file, "line1\nline2\nline3\n");

        Call("editFile", MakeParams(new { path = file, oldText = "line1\nline2", newText = "replaced" }));

        Assert.Equal("replaced\nline3\n", File.ReadAllText(file));
    }
}
