using System.Text.Json;

namespace Keincheck.Hub;

/// <summary>
/// The hub's persisted user preferences, JSON-backed under
/// <c>%APPDATA%/Keincheck/hub-settings.json</c>. Follows the <see cref="KnownClientStore"/>
/// conventions: eagerly loaded, each mutation persists immediately and best-effort, and a
/// missing or corrupt file falls back to defaults instead of throwing.
/// </summary>
public sealed class HubSettings
{
    private readonly object _gate = new();
    private readonly string _path;

    private static readonly JsonSerializerOptions s_json = new() { WriteIndented = true };

    private HubSettings(string path)
    {
        _path = path;
    }

    /// <summary>The on-disk path the settings are read from / written to.</summary>
    public string FilePath => _path;

    /// <summary>
    /// Whether the hub registers itself to start at login. Default <c>true</c> — the
    /// historical behavior — so existing installs keep starting automatically until the
    /// user opts out. Opting out does not break the AI flow: the
    /// <c>keincheck-connect</c> shim launches the hub on demand when a client connects.
    /// </summary>
    public bool StartAtLogin { get; private set; } = true;

    /// <summary>
    /// Whether the hub starts as a pure tray daemon (no window) rather than opening its
    /// window on launch.
    /// </summary>
    /// <remarks>
    /// Defaults to <c>true</c> on Windows and macOS, which both guarantee a system tray, and
    /// to <c>false</c> on Linux, which does not: a bare GNOME session has no StatusNotifierItem
    /// host, so a tray-only hub there is an invisible process the user can neither open nor
    /// quit. Linux users running a desktop with a working tray (KDE, XFCE, Cinnamon, MATE, or
    /// GNOME with the AppIndicator extension) can turn this on from the tray menu.
    /// </remarks>
    public bool StartInTrayOnly { get; private set; } = DefaultTrayOnly;

    /// <summary>The platform's default for <see cref="StartInTrayOnly"/>. See that property.</summary>
    public static bool DefaultTrayOnly => !OperatingSystem.IsLinux();

    /// <summary>
    /// Opens (and eagerly loads) the settings under
    /// <c>%APPDATA%/Keincheck/hub-settings.json</c>, or a custom path for tests.
    /// </summary>
    public static HubSettings Open(string? path = null)
    {
        var settings = new HubSettings(path ?? DefaultPath());
        settings.Load();
        return settings;
    }

    /// <summary>The default per-user settings location under %APPDATA%/Keincheck.</summary>
    public static string DefaultPath()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Keincheck");
        return Path.Combine(dir, "hub-settings.json");
    }

    /// <summary>Sets <see cref="StartAtLogin"/> and persists immediately (best-effort).</summary>
    public void SetStartAtLogin(bool value)
    {
        lock (_gate)
        {
            if (StartAtLogin == value)
                return;
            StartAtLogin = value;
            Save();
        }
    }

    /// <summary>Sets <see cref="StartInTrayOnly"/> and persists immediately (best-effort).</summary>
    public void SetStartInTrayOnly(bool value)
    {
        lock (_gate)
        {
            if (StartInTrayOnly == value)
                return;
            StartInTrayOnly = value;
            Save();
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path))
                return;

            // A property missing from an older settings file keeps the DTO's initializer
            // value, so adding a setting never rewrites an existing user's other choices.
            var dto = JsonSerializer.Deserialize<Dto>(File.ReadAllText(_path));
            if (dto is not null)
            {
                StartAtLogin = dto.StartAtLogin;
                StartInTrayOnly = dto.StartInTrayOnly;
            }
        }
        catch
        {
            // Corrupt or unreadable file — defaults are safer than a dead daemon.
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var dto = new Dto { StartAtLogin = StartAtLogin, StartInTrayOnly = StartInTrayOnly };
            File.WriteAllText(_path, JsonSerializer.Serialize(dto, s_json));
        }
        catch
        {
            // Best-effort: a failed save just means the toggle doesn't survive a restart.
        }
    }

    private sealed class Dto
    {
        public bool StartAtLogin { get; set; } = true;
        public bool StartInTrayOnly { get; set; } = DefaultTrayOnly;
    }
}
