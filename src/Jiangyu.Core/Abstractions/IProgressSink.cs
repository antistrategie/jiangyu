namespace Jiangyu.Core.Abstractions;

public interface IProgressSink
{
    void SetPhase(string phase);
    void ReportProgress(int current, int total);
    void SetStatus(string status);
    void Finish();
}
