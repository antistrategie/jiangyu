using Jiangyu.Core.Abstractions;

namespace Jiangyu.Studio.Host.Infrastructure;

/// <summary>
/// Process-wide logging seam for Studio.Host. Every log site routes
/// through <see cref="Instance"/>, so a file appender, structured
/// formatter, or test-capture sink can replace the destination once at
/// startup without rippling through every static class.
///
/// <para>The default <see cref="ConsoleErrorLogSink"/> writes to stderr
/// to preserve the existing host behaviour. Composition root may
/// reassign <see cref="Instance"/> before any logging happens. Tests
/// assigning <c>NullLogSink.Instance</c> in fixture setup silences
/// stderr noise.</para>
/// </summary>
internal static class HostLog
{
    public static ILogSink Instance { get; set; } = new ConsoleErrorLogSink();
}

/// <summary>
/// Default <see cref="ILogSink"/> implementation that emits to stderr.
/// Matches the historic Studio.Host shape: free-form messages with the
/// caller's <c>[Tag]</c> prefix carried as part of the message string.
/// </summary>
internal sealed class ConsoleErrorLogSink : ILogSink
{
    public void Info(string message) => Console.Error.WriteLine(message);
    public void Warning(string message) => Console.Error.WriteLine(message);
    public void Error(string message) => Console.Error.WriteLine(message);
}
