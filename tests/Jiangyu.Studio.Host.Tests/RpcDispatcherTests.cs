using System.Text.Json;

namespace Jiangyu.Studio.Host.Tests;

public class RpcDispatcherTests
{
    /// <summary>
    /// Captures the response string from HandleMessage for assertions.
    /// </summary>
    private static string Dispatch(string message)
    {
        string? captured = null;
        RpcDispatcher.HandleMessage(null!, message, response => captured = response);
        return captured ?? throw new InvalidOperationException("No response sent");
    }

    [Fact]
    public void UnknownMethod_ReturnsError()
    {
        var response = Dispatch("""{"id":1,"method":"totallyBogus"}""");

        var doc = JsonDocument.Parse(response);
        Assert.Equal(1, doc.RootElement.GetProperty("id").GetInt32());
        Assert.Contains("Unknown method", doc.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public void MissingMethod_ReturnsError()
    {
        var response = Dispatch("""{"id":2}""");

        var doc = JsonDocument.Parse(response);
        Assert.Contains("method", doc.RootElement.GetProperty("error").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MalformedJson_ReturnsError()
    {
        var response = Dispatch("not valid json {{{");

        var doc = JsonDocument.Parse(response);
        Assert.Contains("Malformed", doc.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public void ResponseId_MatchesRequestId()
    {
        var response = Dispatch("""{"id":99,"method":"bogus"}""");

        var doc = JsonDocument.Parse(response);
        Assert.Equal(99, doc.RootElement.GetProperty("id").GetInt32());
    }

    [Fact]
    public void MalformedJson_StillReturnsId_WhenParseable()
    {
        // A request with a valid id but missing method should still
        // return the id so the frontend promise doesn't hang.
        var response = Dispatch("""{"id":7,"params":{}}""");

        var doc = JsonDocument.Parse(response);
        Assert.Equal(7, doc.RootElement.GetProperty("id").GetInt32());
    }

    // The Unity editor binary is named "Unity", with no extension, everywhere
    // except Windows. The dialog layer rewrites each filter pattern to
    // "*.<ext>", so any filter at all leaves the binary unselectable and the
    // picker unable to do the one thing it exists for.
    [Fact]
    public void UnityEditorFileFilters_AreAbsent_OffWindows()
    {
        Assert.Null(RpcDispatcher.UnityEditorFileFilters(isWindows: false));
    }

    [Fact]
    public void UnityEditorFileFilters_MatchTheWindowsBinary()
    {
        var filters = RpcDispatcher.UnityEditorFileFilters(isWindows: true);

        var extensions = Assert.Single(filters!).Extensions;
        // Bare "exe", not "*.exe" or ".exe": the dialog layer adds the "*." and
        // would otherwise build "*.*.exe".
        Assert.Equal(["exe"], extensions);
    }

    // A directory that does not exist can stop the native dialog opening, and a
    // dialog that never opened is reported the same as a cancelled one. Passing
    // null instead lets it fall back to somewhere real.
    [Fact]
    public void ExistingDialogDirectory_DropsAPathThatIsGone()
    {
        var gone = Path.Combine(Path.GetTempPath(), "jiangyu-tests", Guid.NewGuid().ToString());

        Assert.Null(RpcDispatcher.ExistingDialogDirectory(gone));
    }

    [Fact]
    public void ExistingDialogDirectory_KeepsADirectoryThatExists()
    {
        var real = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar);

        Assert.Equal(real, RpcDispatcher.ExistingDialogDirectory(real));
    }

    // The Unity editor of a version that was uninstalled leaves its Hub root
    // behind. Opening there beats opening at the dialog's own default, since it
    // is where the other installs are.
    [Fact]
    public void ExistingDialogDirectory_FallsBackToAnExistingParent()
    {
        var parent = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar);
        var missingChild = Path.Combine(parent, "jiangyu-tests-" + Guid.NewGuid());

        Assert.Equal(parent, RpcDispatcher.ExistingDialogDirectory(missingChild));
    }

    [Fact]
    public void ExistingDialogDirectory_TreatsNoConfiguredPathAsNoDefault()
    {
        Assert.Null(RpcDispatcher.ExistingDialogDirectory(null));
        Assert.Null(RpcDispatcher.ExistingDialogDirectory(""));
    }
}
