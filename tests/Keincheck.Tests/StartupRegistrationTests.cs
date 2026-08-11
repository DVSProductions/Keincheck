using System.Xml.Linq;
using Keincheck.Hub;
using Xunit;

namespace Keincheck.Tests;

/// <summary>
/// Unit tests for the POSIX autostart files <see cref="StartupRegistration"/> writes.
///
/// Only the file *contents* are exercised, deliberately: <c>TryRegister</c> and
/// <c>TryUnregister</c> touch the developer's real login items (the Windows <c>Run</c> key,
/// <c>~/.config/autostart</c>, <c>~/Library/LaunchAgents</c>), so a test that called them
/// would either break the tester's own hub install or silently depend on it. What can go
/// wrong here and would go unnoticed until a user's hub stopped starting is the escaping —
/// a Velopack install directory contains a space often enough to matter — so that is what is
/// pinned.
/// </summary>
public sealed class StartupRegistrationTests
{
    [Fact]
    public void Desktop_Entry_Is_A_Well_Formed_Autostart_File()
    {
        var entry = StartupRegistration.BuildLinuxDesktopEntry("/opt/keincheck/Keincheck.Hub");

        Assert.StartsWith("[Desktop Entry]", entry);
        Assert.Contains("Type=Application", entry);
        Assert.Contains("Exec=\"/opt/keincheck/Keincheck.Hub\"", entry);
        // GNOME's Startup Applications reads this key; without it the entry is ignored there.
        Assert.Contains("X-GNOME-Autostart-enabled=true", entry);
        // The hub is a daemon reached through its tray, not an app to launch from a menu.
        Assert.Contains("NoDisplay=true", entry);
    }

    [Theory]
    // A space is the realistic case: Velopack installs under a per-user directory that can
    // have one. Unquoted, the .desktop spec parses this as two arguments and the entry
    // launches nothing.
    [InlineData("/home/a b/Keincheck Hub/Keincheck.Hub", "\"/home/a b/Keincheck Hub/Keincheck.Hub\"")]
    // Backslash and double quote are both reserved inside a quoted Exec argument.
    [InlineData("/tmp/we\"ird", "\"/tmp/we\\\"ird\"")]
    [InlineData("/tmp/back\\slash", "\"/tmp/back\\\\slash\"")]
    public void Desktop_Entry_Exec_Is_Quoted_And_Escaped(string exe, string expectedExec)
    {
        var entry = StartupRegistration.BuildLinuxDesktopEntry(exe);

        Assert.Contains($"Exec={expectedExec}", entry);
    }

    [Fact]
    public void Launch_Agent_Is_Parseable_Plist_That_Runs_At_Load()
    {
        const string exe = "/Applications/Keincheck Hub.app/Contents/MacOS/Keincheck.Hub";

        var plist = StartupRegistration.BuildMacLaunchAgentPlist(exe);
        var doc = XDocument.Parse(plist);

        var keys = doc.Descendants("key").Select(k => k.Value).ToList();
        Assert.Contains("Label", keys);
        Assert.Contains("RunAtLoad", keys);
        // KeepAlive would have launchd resurrect the hub the moment it is quit from the tray.
        Assert.DoesNotContain("KeepAlive", keys);

        Assert.Equal(exe, doc.Descendants("array").Single().Elements("string").Single().Value);
    }

    [Fact]
    public void Launch_Agent_Escapes_Xml_In_The_Executable_Path()
    {
        // An unescaped '&' makes the plist unparseable, and launchd drops it silently —
        // the hub just never starts at login and nothing says why.
        var plist = StartupRegistration.BuildMacLaunchAgentPlist("/opt/a&b/<hub>/Keincheck.Hub");

        var doc = XDocument.Parse(plist);
        Assert.Equal(
            "/opt/a&b/<hub>/Keincheck.Hub",
            doc.Descendants("array").Single().Elements("string").Single().Value);
    }
}
