using System.Text.RegularExpressions;
using AssetRipper.Import.Logging;

namespace Jiangyu.Compiler.Services;

/// <summary>
/// AssetRipper ILogger that renders a terminal progress bar during export.
/// Parses the "(N/Total) Exporting 'name'" messages from PrimaryContentExporter.
/// </summary>
public sealed partial class ProgressLogger : ILogger
{
    private int _lastRenderedWidth;
    private string _phase = "";

    public void SetPhase(string phase)
    {
        _phase = phase;
        Console.Error.Write($"\r\x1b[K{phase}...");
    }

    public void Finish()
    {
        Console.Error.WriteLine($"\r\x1b[K{_phase}... done");
    }

    public void Log(LogType type, LogCategory category, string message)
    {
        if (category == LogCategory.ExportProgress && type == LogType.Info)
        {
            var match = ProgressPattern().Match(message);
            if (match.Success)
            {
                int current = int.Parse(match.Groups[1].Value);
                int total = int.Parse(match.Groups[2].Value);
                RenderProgress(current, total);
            }
        }
    }

    public void BlankLine(int numLines) { }

    private void RenderProgress(int current, int total)
    {
        int width = Math.Max(Console.WindowWidth, 40);
        if (width != _lastRenderedWidth)
        {
            _lastRenderedWidth = width;
        }

        double fraction = (double)current / total;
        int percent = (int)(fraction * 100);

        // "[phase] [=====>    ] 42% (1234/5678)"
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

    [GeneratedRegex(@"^\((\d+)/(\d+)\)")]
    private static partial Regex ProgressPattern();
}
