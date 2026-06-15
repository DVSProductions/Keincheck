using System.Text.Json;
using System.Text.Json.Serialization;

namespace AvaloniaMcp.Hub;

/// <summary>
/// The launch profile persisted for a previously-seen app, keyed by its self-reported
/// <c>AppId</c>. It records everything needed to <see cref="IClientBroker.LaunchClientAsync"/>
/// an app the hub is not currently connected to: the executable path, arguments, and
/// working directory captured the last time the app connected and announced itself.
/// </summary>
public sealed record KnownClientProfile
{
    /// <summary>The app's stable self-reported id (the persistence key).</summary>
    public required string AppId { get; init; }

    /// <summary>The friendly name last reported by the app.</summary>
    public string? DisplayName { get; init; }

    /// <summary>The executable used to launch the app (resolved from the live process).</summary>
    public string? ExecutablePath { get; init; }

    /// <summary>The command-line arguments to relaunch with.</summary>
    public string? Arguments { get; init; }

    /// <summary>The working directory to launch in.</summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>Whether the app was last marked read-only (persisted across restarts).</summary>
    public bool ReadOnly { get; init; }

    /// <summary>UTC time the profile was last refreshed (an app connection).</summary>
    public DateTimeOffset LastSeenUtc { get; init; }
}

/// <summary>
/// A small JSON-backed store of <see cref="KnownClientProfile"/> records under
/// <c>%APPDATA%/AvaloniaMcp/known-clients.json</c>. It lets the hub launch or restart
/// apps it has seen before, even across hub restarts. All members are thread-safe;
/// writes are debounced-free (each mutation persists immediately, best-effort).
/// </summary>
public sealed class KnownClientStore
{
    private readonly object _gate = new();
    private readonly string _path;
    private readonly Dictionary<string, KnownClientProfile> _profiles = new(StringComparer.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions s_json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>The on-disk path the store reads from / writes to.</summary>
    public string FilePath => _path;

    private KnownClientStore(string path)
    {
        _path = path;
    }

    /// <summary>
    /// Opens (and eagerly loads) the store under
    /// <c>%APPDATA%/AvaloniaMcp/known-clients.json</c>, or a custom path for tests.
    /// A missing or corrupt file starts empty rather than throwing.
    /// </summary>
    public static KnownClientStore Open(string? path = null)
    {
        path ??= DefaultPath();
        var store = new KnownClientStore(path);
        store.Load();
        return store;
    }

    /// <summary>The default per-user store location under %APPDATA%/AvaloniaMcp.</summary>
    public static string DefaultPath()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "AvaloniaMcp");
        return Path.Combine(dir, "known-clients.json");
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path))
                return;

            var json = File.ReadAllText(_path);
            var list = JsonSerializer.Deserialize<List<KnownClientProfile>>(json, s_json);
            if (list is null)
                return;

            lock (_gate)
            {
                _profiles.Clear();
                foreach (var p in list)
                {
                    if (!string.IsNullOrEmpty(p.AppId))
                        _profiles[p.AppId] = p;
                }
            }
        }
        catch
        {
            // Corrupt/unreadable store: start empty. The next Upsert rewrites it cleanly.
        }
    }

    /// <summary>All known profiles, newest-seen first.</summary>
    public IReadOnlyList<KnownClientProfile> All()
    {
        lock (_gate)
            return _profiles.Values.OrderByDescending(p => p.LastSeenUtc).ToList();
    }

    /// <summary>Looks up a single profile by app id (case-insensitive), or null.</summary>
    public KnownClientProfile? Get(string appId)
    {
        if (string.IsNullOrEmpty(appId))
            return null;
        lock (_gate)
            return _profiles.TryGetValue(appId, out var p) ? p : null;
    }

    /// <summary>
    /// Inserts or refreshes the profile for <paramref name="profile"/>'s app id and
    /// persists the whole store. Best-effort: a failed write does not throw.
    /// </summary>
    public void Upsert(KnownClientProfile profile)
    {
        if (string.IsNullOrEmpty(profile.AppId))
            return;

        lock (_gate)
        {
            _profiles[profile.AppId] = profile;
            Save_NoLock();
        }
    }

    /// <summary>
    /// Updates only the persisted read-only flag for an app id (so a tray toggle on a
    /// live app survives a restart), if the app is known. No-op when unknown.
    /// </summary>
    public void SetReadOnly(string appId, bool readOnly)
    {
        lock (_gate)
        {
            if (!_profiles.TryGetValue(appId, out var existing))
                return;
            _profiles[appId] = existing with { ReadOnly = readOnly };
            Save_NoLock();
        }
    }

    private void Save_NoLock()
    {
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(_profiles.Values.ToList(), s_json);

            // Write to a temp sibling then move into place so a crash mid-write never
            // truncates the live store.
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, _path, overwrite: true);
        }
        catch
        {
            // Persistence is best-effort; an unwritable %APPDATA% must not crash the hub.
        }
    }
}
