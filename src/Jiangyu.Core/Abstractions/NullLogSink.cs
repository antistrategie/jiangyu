namespace Jiangyu.Core.Abstractions;

/// <summary>
/// <see cref="ILogSink"/> implementation that drops every message. Use when
/// a service requires a log sink but the caller has nowhere useful to route
/// the output (e.g. short-lived RPC handlers).
/// </summary>
public sealed class NullLogSink : ILogSink
{
    public static readonly NullLogSink Instance = new();

    private NullLogSink() { }

    public void Info(string message) { }
    public void Warning(string message) { }
    public void Error(string message) { }
}
