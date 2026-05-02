using System.Text.Json;

namespace Jiangyu.Studio.Host.Tests;

public class ReadFileTests : IDisposable
{
    private readonly string _projectDir;

    public ReadFileTests()
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
    public void ReadFile_ReturnsFullContent()
    {
        var file = Path.Combine(_projectDir, "full.txt");
        File.WriteAllText(file, "line1\nline2\nline3\n");

        var result = Call("readFile", MakeParams(new { path = file }));
        Assert.Equal("line1\nline2\nline3\n", result.GetString());
    }

    [Fact]
    public void ReadFile_LineRange_ReturnsSubset()
    {
        var file = Path.Combine(_projectDir, "lines.txt");
        File.WriteAllText(file, "a\nb\nc\nd\ne\n");

        var result = Call("readFile", MakeParams(new { path = file, startLine = 2, endLine = 4 }));
        Assert.Equal("b\nc\nd", result.GetString());
    }

    [Fact]
    public void ReadFile_StartLineOnly_ReturnsToEnd()
    {
        var file = Path.Combine(_projectDir, "tail.txt");
        File.WriteAllText(file, "a\nb\nc\nd\n");

        var result = Call("readFile", MakeParams(new { path = file, startLine = 3 }));
        Assert.Equal("c\nd", result.GetString());
    }

    [Fact]
    public void ReadFile_EndLineOnly_ReturnsFromStart()
    {
        var file = Path.Combine(_projectDir, "head.txt");
        File.WriteAllText(file, "a\nb\nc\nd\n");

        var result = Call("readFile", MakeParams(new { path = file, endLine = 2 }));
        Assert.Equal("a\nb", result.GetString());
    }

    [Fact]
    public void ReadFile_NotFound_ReturnsError()
    {
        var ex = Assert.Throws<Exception>(() =>
            Call("readFile", MakeParams(new { path = Path.Combine(_projectDir, "nope.txt") })));
        Assert.Contains("not found", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}
