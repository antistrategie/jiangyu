using Microsoft.JSInterop;

namespace Jiangyu.Studio.Host.Infrastructure;

// TODO: remove when InfiniFrame drops the unconditional Blazor IJSRuntime
// dependency for non-Blazor hosts (InfiniFrame 0.11.0 bug). The builder's
// Initialize() calls AddInfiniFrameJs() which registers InfiniFrameJs with
// a constructor dependency on IJSRuntime; that service only exists in Blazor
// apps. This no-op satisfies the DI container so the WebView host can start.
// Tracked upstream: https://github.com/InfiniLore/InfiniFrame
internal sealed class NullJSRuntime : IJSRuntime
{
    public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
        => throw new InvalidOperationException("IJSRuntime is not available outside Blazor.");

    public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        => throw new InvalidOperationException("IJSRuntime is not available outside Blazor.");
}
