using Keincheck.Hub;
using Xunit;

namespace Keincheck.Tests;

/// <summary>
/// Unit tests for <see cref="HubSettings"/> — the hub's tiny JSON-backed preference store.
/// The contract that matters: the default preserves the historical always-start behavior, a
/// mutation survives a reload, and a corrupt or missing file degrades to defaults instead of
/// throwing into startup.
/// </summary>
public sealed class HubSettingsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "keincheck-settings-" + Guid.NewGuid().ToString("N"));

    private string Path_ => Path.Combine(_dir, "hub-settings.json");

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public void Missing_File_Falls_Back_To_Defaults()
    {
        var settings = HubSettings.Open(Path_);

        Assert.True(settings.StartAtLogin);   // historical behavior: existing installs unaffected
        Assert.False(File.Exists(Path_));     // reading does not create the file
    }

    [Fact]
    public void Setting_Round_Trips_Through_Disk()
    {
        HubSettings.Open(Path_).SetStartAtLogin(false);

        Assert.True(File.Exists(Path_));
        Assert.False(HubSettings.Open(Path_).StartAtLogin);
    }

    [Fact]
    public void Setting_Back_To_True_Round_Trips_Too()
    {
        var first = HubSettings.Open(Path_);
        first.SetStartAtLogin(false);
        first.SetStartAtLogin(true);

        Assert.True(HubSettings.Open(Path_).StartAtLogin);
    }

    [Fact]
    public void Corrupt_File_Falls_Back_To_Defaults()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path_, "{ not valid json");

        var settings = HubSettings.Open(Path_);

        Assert.True(settings.StartAtLogin);
    }

    [Fact]
    public void Unwritable_Path_Does_Not_Throw()
    {
        // A directory where the settings file should be: writing can only fail.
        Directory.CreateDirectory(Path_);

        var settings = HubSettings.Open(Path_);
        settings.SetStartAtLogin(false);

        Assert.False(settings.StartAtLogin); // in-memory value still flips
    }
}
