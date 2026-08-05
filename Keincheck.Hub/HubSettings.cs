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

    private void Load()
    {
        try
        {
            if (!File.Exists(_path))
                return;

            var dto = JsonSerializer.Deserialize<Dto>(File.ReadAllText(_path));
            if (dto is not null)
                StartAtLogin = dto.StartAtLogin;
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
            File.WriteAllText(_path, JsonSerializer.Serialize(new Dto { StartAtLogin = StartAtLogin }, s_json));
        }
        catch
        {
            // Best-effort: a failed save just means the toggle doesn't survive a restart.
        }
    }

    private sealed class Dto
    {
        public bool StartAtLogin { get; set; } = true;
    }
}
