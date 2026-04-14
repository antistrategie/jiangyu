namespace Jiangyu.Core.Models;

public enum CachedIndexState
{
    Current,
    Missing,
    Stale,
}

public sealed class CachedIndexStatus
{
    public required CachedIndexState State { get; init; }
    public string? Reason { get; init; }
    public bool IsCurrent => State == CachedIndexState.Current;
}
