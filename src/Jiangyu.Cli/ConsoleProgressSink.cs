using Jiangyu.Core.Abstractions;

namespace Jiangyu.Cli;

public sealed class ConsoleProgressSink : IProgressSink
{
    private int _lastRenderedWidth;
    private string _phase = "";

    public void SetPhase(string phase)
    {
        _phase = phase;
        Console.Error.Write($"\r\x1b[K{phase}...");
    }

    public void ReportProgress(int current, int total)
    {
        int width = Math.Max(Console.WindowWidth, 40);
        if (width != _lastRenderedWidth)
        {
            _lastRenderedWidth = width;
        }

        double fraction = (double)current / total;
        int percent = (int)(fraction * 100);

        string stats = $" {percent}% ({current}/{total})";
        string prefix = $"\r{_phase} [";
        string suffix = $"]{stats}";
        int barWidth = width - prefix.Length - suffix.Length;

        if (barWidth < 5) barWidth = 5;

        int filled = (int)(fraction * barWidth);
        string bar = new string('=', Math.Max(filled - 1, 0))
            + (filled > 0 ? ">" : "")
            + new string(' ', barWidth - filled);

        Console.Error.Write($"{prefix}{bar}{suffix}");
    }

    public void SetStatus(string status)
    {
        Console.Error.Write($"\r\x1b[K{status}");
    }

    public void Finish()
    {
        Console.Error.WriteLine($"\r\x1b[K{_phase}... done");
    }
}
