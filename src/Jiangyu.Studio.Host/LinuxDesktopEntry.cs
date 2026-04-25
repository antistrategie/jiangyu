namespace Jiangyu.Studio.Host;

/// <summary>
/// Installs a user-level <c>.desktop</c> entry and icon on first launch so
/// Wayland compositors can resolve the app's taskbar/dock icon.
/// </summary>
/// <remarks>
/// Wayland deliberately dropped X11's runtime icon hints (<c>_NET_WM_ICON</c>)
/// — the compositor instead reads icons from installed <c>.desktop</c> files
/// matched against the window's <c>xdg-toplevel.app_id</c>. So even though
/// <c>InfiniFrameWindowBuilder.SetIconFile</c> works on X11 and Windows, on
/// Wayland we have to plant a desktop entry and icon or every user falls back
/// to the compositor's default icon. VS Code, Obsidian, JetBrains all do this.
/// </remarks>
internal static class LinuxDesktopEntry
{
    // The window's app_id is derived from the assembly name (jiangyu-studio);
    // the desktop file's basename must match it for the compositor to link
    // icon → window. StartupWMClass is the explicit fallback.
    private const string AppId = "jiangyu-studio";
    private const string DesktopFileName = AppId + ".desktop";
    private const string DisplayName = "Jiangyu Studio";

    /// <summary>
    /// Write the desktop entry and icon if missing. Safe to call
    /// unconditionally on every launch — the file-exists check is the
    /// short-circuit.
    /// </summary>
    public static void Ensure()
    {
        try
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrEmpty(home)) return;

            var applicationsDir = Path.Combine(home, ".local", "share", "applications");
            var desktopPath = Path.Combine(applicationsDir, DesktopFileName);

            var sourceIcon = Path.Combine(AppContext.BaseDirectory, "icon.png");
            if (!File.Exists(sourceIcon)) return;

            // Install the icon into the user's icon theme so the .desktop
            // file can reference it by name (not absolute path). Absolute
            // paths break if the user moves the app directory.
            var iconsDir = Path.Combine(home, ".local", "share", "icons", "hicolor", "256x256", "apps");
            Directory.CreateDirectory(iconsDir);
            File.Copy(sourceIcon, Path.Combine(iconsDir, AppId + ".png"), overwrite: true);

            // ProcessPath is the real binary for self-contained publishes; for
            // `dotnet run` it points at the dotnet host, which means the
            // launcher entry can't relaunch the app — but the icon still
            // resolves while the app is running (StartupWMClass match).
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return;

            Directory.CreateDirectory(applicationsDir);
            File.WriteAllText(desktopPath, Render(exe));
        }
        catch (Exception ex)
        {
            // Non-fatal: worst case the user sees the default icon. Log and
            // continue — we don't want a missing/read-only home directory to
            // break launch.
            Console.Error.WriteLine($"[LinuxDesktopEntry] failed to install entry: {ex.Message}");
        }
    }

    private static string Render(string exe) =>
        $"""
        [Desktop Entry]
        Type=Application
        Name={DisplayName}
        Icon={AppId}
        Exec={exe}
        StartupWMClass={AppId}
        Categories=Development;
        Terminal=false
        """;
}
