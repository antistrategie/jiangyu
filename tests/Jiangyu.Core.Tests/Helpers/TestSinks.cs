using Jiangyu.Core.Abstractions;

namespace Jiangyu.Core.Tests.Helpers;

internal sealed class NullLogSink : ILogSink
{
    public void Info(string message) { }
    public void Warning(string message) { }
    public void Error(string message) { }
}

internal sealed class NullProgressSink : IProgressSink
{
    public void SetPhase(string phase) { }
    public void ReportProgress(int current, int total) { }
    public void SetStatus(string status) { }
    public void Finish() { }
}
