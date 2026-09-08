namespace Jiangyu.Studio.Host.Infrastructure;

internal static class StudioBrowserProfile
{
    // WebView2 stores localStorage in this directory. A process-specific temp
    // path loses project history on restart, so every Studio window uses the
    // same persistent profile, independent of the executable's location.
    internal static string DataPath => Path.Combine(
        Environment.GetEnvironmentVariable("XDG_DATA_HOME")
            ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "jiangyu", "studio", "webview");
}
