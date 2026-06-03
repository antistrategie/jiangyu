namespace Jiangyu.Loader.Logging;

/// <summary>Host-side logging seam so the SDK runtime stays testable off the game.</summary>
internal interface IModHostLog
{
    void Debug(string message);

    void Info(string message);

    void Warn(string message);

    void Error(string message);
}
