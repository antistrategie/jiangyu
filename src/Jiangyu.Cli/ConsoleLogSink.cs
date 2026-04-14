using Jiangyu.Core.Abstractions;

namespace Jiangyu.Cli;

public sealed class ConsoleLogSink : ILogSink
{
    public void Info(string message)
    {
        Console.WriteLine(message);
    }

    public void Warning(string message)
    {
        Console.Error.WriteLine($"WARNING: {message}");
    }

    public void Error(string message)
    {
        Console.Error.WriteLine($"Error: {message}");
    }
}
