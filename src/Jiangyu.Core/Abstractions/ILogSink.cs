namespace Jiangyu.Core.Abstractions;

public interface ILogSink
{
    void Info(string message);
    void Warning(string message);
    void Error(string message);
}
