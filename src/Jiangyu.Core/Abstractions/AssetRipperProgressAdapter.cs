using System.Text.RegularExpressions;
using AssetRipper.Import.Logging;

namespace Jiangyu.Core.Abstractions;

/// <summary>
/// Bridges IProgressSink to AssetRipper's ILogger interface.
/// Parses "(N/Total) Exporting 'name'" progress messages from AssetRipper.
/// </summary>
public sealed partial class AssetRipperProgressAdapter : ILogger
{
    private readonly IProgressSink _sink;

    public AssetRipperProgressAdapter(IProgressSink sink)
    {
        _sink = sink;
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
                _sink.ReportProgress(current, total);
            }
        }
    }

    public void BlankLine(int numLines) { }

    [GeneratedRegex(@"^\((\d+)/(\d+)\)")]
    private static partial Regex ProgressPattern();
}
