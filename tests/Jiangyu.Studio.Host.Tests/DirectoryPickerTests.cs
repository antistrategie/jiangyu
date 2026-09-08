using System.Reflection;
using InfiniFrame;

namespace Jiangyu.Studio.Host.Tests;

public class DirectoryPickerTests
{
    [Fact]
    public void Picker_ReceivesANormalisedStartingDirectory()
    {
        var frontendPath = Environment.CurrentDirectory.Replace('\\', '/') + "/.";

        Assert.Equal(Environment.CurrentDirectory, CaptureDefaultPath(frontendPath));
    }

    [Fact]
    public void Picker_ReceivesAnExistingParentForAMissingDirectory()
    {
        var parent = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar);
        var missing = Path.Combine(parent, "jiangyu-dialog-test-" + Guid.NewGuid());

        Assert.Equal(parent, CaptureDefaultPath(missing));
    }

    [Fact]
    public void Picker_ReceivesNoDefaultWhenTheDirectoryAndParentAreMissing()
    {
        var missing = Path.Combine(Path.GetTempPath(), "jiangyu-dialog-test-" + Guid.NewGuid(), "gone");

        Assert.Null(CaptureDefaultPath(missing));
    }

    [Fact]
    public void Picker_ReceivesNoDefaultWhenNoneIsSupplied()
    {
        Assert.Null(CaptureDefaultPath(null));
    }

    private static string? CaptureDefaultPath(string? initial)
    {
        var defaultPaths = new List<string?>();
        var picker = DialogProxy.RespondTo<IFilePickerDialogsInfiniFrameWindowFeature>(
            nameof(IFilePickerDialogsInfiniFrameWindowFeature.ShowOpenFolder), args =>
            {
                Assert.Equal("Open Jiangyu project", args[0]);
                defaultPaths.Add((string?)args[1]);
                return Array.Empty<string?>();
            });

        Assert.Null(RpcDispatcher.PickDirectory(picker, "Open Jiangyu project", initial));
        return Assert.Single(defaultPaths);
    }

    public class DialogProxy : DispatchProxy
    {
        private string _memberName = null!;
        private Func<object?[], object?> _respond = null!;

        internal static T RespondTo<T>(string memberName, Func<object?[], object?> respond) where T : class
        {
            var instance = Create<T, DialogProxy>();
            var proxy = (DialogProxy)(object)instance;
            proxy._memberName = memberName;
            proxy._respond = respond;
            return instance;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            Assert.Equal(_memberName, targetMethod?.Name);
            return _respond(args ?? []);
        }
    }
}
