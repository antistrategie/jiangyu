using Jiangyu.Core.Abstractions;

namespace Jiangyu.Core.Tests.Abstractions;

public class NullSinkTests
{
    [Fact]
    public void NullLogSink_Instance_IsSingleton()
    {
        Assert.Same(NullLogSink.Instance, NullLogSink.Instance);
    }

    [Fact]
    public void NullLogSink_ImplementsILogSink()
    {
        ILogSink sink = NullLogSink.Instance;
        // Should not throw
        sink.Info("test");
        sink.Warning("test");
        sink.Error("test");
    }

    [Fact]
    public void NullProgressSink_Instance_IsSingleton()
    {
        Assert.Same(NullProgressSink.Instance, NullProgressSink.Instance);
    }

    [Fact]
    public void NullProgressSink_ImplementsIProgressSink()
    {
        IProgressSink sink = NullProgressSink.Instance;
        // Should not throw
        sink.SetPhase("test");
        sink.ReportProgress(1, 10);
        sink.SetStatus("test");
        sink.Finish();
    }
}
