using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Keincheck.Hub;

/// <summary>
/// Best-effort login-startup registration so the hub is already up when the AI's stdio
/// shim connects. On Windows it writes the per-user <c>Run</c> registry key (no admin
/// rights, no UAC); a missing/locked key fails silently. Non-Windows is a no-op for now
/// (the hub's live target is Windows).
/// </summary>
public static class StartupRegistration
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Keincheck.Hub";

    /// <summary>
    /// Registers the current executable to launch at login (current user only). Returns
    /// true if the registration is now in place, false if it could not be written. Never
    /// throws.
    /// </summary>
    public static bool TryRegister()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return false;

        try
        {
            return RegisterWindows();
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Removes the login-startup registration, if present. Never throws.</summary>
    public static bool TryUnregister()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return false;

        try
        {
            return UnregisterWindows();
        }
        catch
        {
            return false;
        }
    }

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

    private static string? ExecutablePath()
    {
        // Prefer the real host exe (Process.MainModule) over the managed dll path so the
        // Run key points at a directly-launchable target.
        try
        {
            var path = Process.GetCurrentProcess().MainModule?.FileName;
            if (!string.IsNullOrEmpty(path) && path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                return path;
        }
        catch { /* fall through */ }

        var entry = Environment.ProcessPath;
        return string.IsNullOrEmpty(entry) ? null : entry;
    }
}
