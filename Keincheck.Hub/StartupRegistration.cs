using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32;

namespace Keincheck.Hub;

/// <summary>
/// Best-effort login-startup registration so the hub is already up when the AI's stdio
/// shim connects. Every platform uses the per-user mechanism that needs no elevation:
/// <list type="bullet">
///   <item>Windows — the per-user <c>Run</c> registry key (no admin rights, no UAC).</item>
///   <item>Linux — an XDG autostart <c>.desktop</c> entry under <c>$XDG_CONFIG_HOME/autostart</c>.</item>
///   <item>macOS — a <c>LaunchAgent</c> plist under <c>~/Library/LaunchAgents</c>.</item>
/// </list>
/// A missing/locked target fails silently: opting out of autostart breaks nothing, because
/// the <c>keincheck-connect</c> shim launches the hub on demand when a client connects.
/// </summary>
public static class StartupRegistration
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Keincheck.Hub";

    /// <summary>The reverse-DNS label the macOS LaunchAgent is filed under.</summary>
    private const string LaunchAgentLabel = "com.dvsproductions.keincheck-hub";

    /// <summary>The XDG autostart entry file name (Linux).</summary>
    private const string DesktopEntryFile = "keincheck-hub.desktop";

    /// <summary>
    /// Registers the current executable to launch at login (current user only). Returns
    /// true if the registration is now in place, false if it could not be written (or the
    /// platform has no supported mechanism). Never throws.
    /// </summary>
    public static bool TryRegister()
    {
        try
        {
            if (OperatingSystem.IsWindows())
                return RegisterWindows();
            if (OperatingSystem.IsLinux())
                return RegisterLinux();
            if (OperatingSystem.IsMacOS())
                return RegisterMacOS();
            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Removes the login-startup registration, if present. Never throws.</summary>
    public static bool TryUnregister()
    {
        try
        {
            if (OperatingSystem.IsWindows())
                return UnregisterWindows();
            if (OperatingSystem.IsLinux())
                return DeleteIfPresent(LinuxAutostartPath());
            if (OperatingSystem.IsMacOS())
                return DeleteIfPresent(MacLaunchAgentPath());
            return false;
        }
        catch
        {
            return false;
        }
    }

    // ------------------------------------------------------------------- Windows

    [SupportedOSPlatform("windows")]
    private static bool RegisterWindows()
    {
        var exe = ExecutablePath();
        if (exe is null)
            return false;

        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
                        ?? Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
        if (key is null)
            return false;

        var command = $"\"{exe}\"";
        // Only write when changed so we don't churn the registry on every launch.
        if (key.GetValue(ValueName) as string != command)
            key.SetValue(ValueName, command, RegistryValueKind.String);
        return true;
    }

    [SupportedOSPlatform("windows")]
    private static bool UnregisterWindows()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
        if (key is null)
            return false;
        if (key.GetValue(ValueName) is not null)
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        return true;
    }

    // --------------------------------------------------------------------- Linux

    /// <summary>
    /// Writes an XDG autostart entry. <c>NoDisplay=true</c> keeps the hub out of the
    /// application menu — it is a daemon, discovered through its tray icon, not launched
    /// from a menu — and <c>X-GNOME-Autostart-enabled</c> is the GNOME-specific opt-in that
    /// GNOME's own Startup Applications UI toggles.
    /// </summary>
    private static bool RegisterLinux()
    {
        var exe = ExecutablePath();
        if (exe is null)
            return false;

        return WriteIfChanged(LinuxAutostartPath(), BuildLinuxDesktopEntry(exe));
    }

    /// <summary>The autostart entry's contents. Internal so its escaping can be tested.</summary>
    internal static string BuildLinuxDesktopEntry(string exe) => $"""
        [Desktop Entry]
        Type=Application
        Name=Keincheck Hub
        Comment=Broker between AI assistants and running Keincheck-enabled apps
        Exec={QuoteForDesktopExec(exe)}
        Terminal=false
        NoDisplay=true
        X-GNOME-Autostart-enabled=true

        """;

    private static string LinuxAutostartPath()
    {
        // $XDG_CONFIG_HOME wins when set, per the XDG base-directory spec; otherwise ~/.config.
        var config = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (string.IsNullOrWhiteSpace(config))
            config = Path.Combine(HomeDirectory(), ".config");
        return Path.Combine(config, "autostart", DesktopEntryFile);
    }

    /// <summary>
    /// Escapes a path for a <c>.desktop</c> <c>Exec=</c> line. The spec reserves backslash and
    /// double quote inside quoted arguments, so both are backslash-escaped; a path with a
    /// space in it (a Velopack install under a user directory can have one) would otherwise
    /// be parsed as two arguments.
    /// </summary>
    private static string QuoteForDesktopExec(string path)
        => "\"" + path.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    // --------------------------------------------------------------------- macOS

    /// <summary>
    /// Writes a per-user LaunchAgent. <c>RunAtLoad</c> starts it at login; <c>KeepAlive</c> is
    /// deliberately absent so quitting the hub from the tray stays quit until the next login
    /// rather than being resurrected by launchd on the spot.
    /// </summary>
    private static bool RegisterMacOS()
    {
        var exe = ExecutablePath();
        if (exe is null)
            return false;

        return WriteIfChanged(MacLaunchAgentPath(), BuildMacLaunchAgentPlist(exe));
    }

    /// <summary>The LaunchAgent's contents. Internal so its escaping can be tested.</summary>
    internal static string BuildMacLaunchAgentPlist(string exe) => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
        <plist version="1.0">
        <dict>
          <key>Label</key>
          <string>{LaunchAgentLabel}</string>
          <key>ProgramArguments</key>
          <array>
            <string>{EscapeXml(exe)}</string>
          </array>
          <key>RunAtLoad</key>
          <true/>
          <key>ProcessType</key>
          <string>Interactive</string>
        </dict>
        </plist>

        """;

    private static string MacLaunchAgentPath() => Path.Combine(
        HomeDirectory(), "Library", "LaunchAgents", $"{LaunchAgentLabel}.plist");

    private static string EscapeXml(string value) => value
        .Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    // --------------------------------------------------------------------- shared

    /// <summary>
    /// Writes <paramref name="content"/> only when it differs from what is already there, so
    /// a hub that starts twenty times a week does not rewrite the file twenty times.
    /// </summary>
    private static bool WriteIfChanged(string path, string content)
    {
        if (File.Exists(path) && File.ReadAllText(path) == content)
            return true;

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return true;
    }

    private static bool DeleteIfPresent(string path)
    {
        if (File.Exists(path))
            File.Delete(path);
        return true;
    }

    private static string HomeDirectory()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrEmpty(home)
            ? Environment.GetEnvironmentVariable("HOME") ?? "."
            : home;
    }

    private static string? ExecutablePath()
    {
        // Prefer the real host executable (Process.MainModule) over the managed dll path so
        // the registration points at a directly-launchable target. On Windows that means
        // insisting on a .exe; the POSIX hosts are extension-less, so any non-empty path from
        // the process itself is the right answer there.
        try
        {
            var path = Process.GetCurrentProcess().MainModule?.FileName;
            if (!string.IsNullOrEmpty(path)
                && (!OperatingSystem.IsWindows() || path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)))
            {
                return path;
            }
        }
        catch { /* fall through */ }

        var entry = Environment.ProcessPath;
        return string.IsNullOrEmpty(entry) ? null : entry;
    }
}
