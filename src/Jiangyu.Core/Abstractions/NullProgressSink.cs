namespace Jiangyu.Core.Abstractions;

/// <summary>
/// <see cref="IProgressSink"/> implementation that ignores every update. Use
/// when a service requires a progress sink but the caller doesn't care about
/// reporting (e.g. short-lived RPC handlers).
/// </summary>
public sealed class NullProgressSink : IProgressSink
{
    public static readonly NullProgressSink Instance = new();

    private NullProgressSink() { }

    public void SetPhase(string phase) { }
    public void ReportProgress(int current, int total) { }
    public void SetStatus(string status) { }
    public void Finish() { }
}
