using Xunit;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace Keincheck.E2E;

/// <summary>
/// A <see cref="FactAttribute"/> that skips itself unless <c>KEINCHECK_E2E=1</c>.
/// </summary>
/// <remarks>
/// <para>The gate is not politeness — it is damage control. The hub has no way to
/// relocate its state: pipe <c>Keincheck.{user}</c>, mutex
/// <c>Global\Keincheck.Hub.{user}</c>, port 3100, and <c>%APPDATA%\Keincheck</c> are all
/// fixed, and <c>%APPDATA%</c> in particular cannot be redirected because
/// <c>Environment.GetFolderPath</c> resolves it through <c>SHGetKnownFolderPath</c>,
/// which ignores the environment variable. So on a developer machine an accidental run
/// would drive whatever hub is live, rewrite <c>known-clients.json</c>, possibly leave an
/// app persisted read-only, and possibly provision a certificate authority.</para>
/// <para>The <c>Category=E2E</c> trait is the second, independent guard: <c>ci.yml</c>
/// filters on it so the baseline job never even loads these. Two guards, because
/// forgetting either one has a destructive failure mode rather than a noisy one.</para>
/// </remarks>
[TraitDiscoverer("Keincheck.E2E.E2ETraitDiscoverer", "Keincheck.E2E")]
public sealed class E2EFactAttribute : FactAttribute, ITraitAttribute
{
    public E2EFactAttribute()
    {
        if (!E2EEnvironment.IsEnabled)
            Skip = E2EEnvironment.DisabledReason;
    }
}

/// <summary>Supplies <c>Category=E2E</c> so <c>--filter "Category!=E2E"</c> works.</summary>
public sealed class E2ETraitDiscoverer : ITraitDiscoverer
{
    public IEnumerable<KeyValuePair<string, string>> GetTraits(IAttributeInfo traitAttribute)
    {
        yield return new KeyValuePair<string, string>("Category", "E2E");
    }
}

/// <summary>Reads the environment contract this suite is configured through.</summary>
public static class E2EEnvironment
{
    /// <summary>The master gate. Nothing in this assembly runs without it.</summary>
    public const string EnableVar = "KEINCHECK_E2E";

    /// <summary>
    /// Directory holding BOTH <c>Keincheck.Hub.exe</c> and <c>keincheck-connect.exe</c>.
    /// For a Velopack install that is the <c>current</c> subdirectory, never the install
    /// root — the exe in the root is the stub launcher, which re-execs and detaches, so a
    /// harness that started it would immediately lose the process handle it needs.
    /// </summary>
    public const string HubDirVar = "KEINCHECK_E2E_HUB_DIR";

    /// <summary>Path to the built demo app driven through the hub.</summary>
    public const string DemoExeVar = "KEINCHECK_E2E_DEMO_EXE";

    /// <summary>Path to the built WPF demo, for the adapter smoke test.</summary>
    public const string WpfDemoExeVar = "KEINCHECK_E2E_WPF_DEMO_EXE";

    /// <summary>Where logs, screenshots and exported scenarios are written for upload.</summary>
    public const string ArtifactsVar = "KEINCHECK_E2E_ARTIFACTS";

    /// <summary>
    /// Opts into the remote leg. Off by default even when the suite is enabled, because
    /// enabling remote access provisions a real certificate authority into
    /// <c>%APPDATA%\Keincheck\remote</c> and installs the JSONL audit sink.
    /// </summary>
    public const string RemoteVar = "KEINCHECK_E2E_REMOTE";

    public static bool IsEnabled =>
        string.Equals(Environment.GetEnvironmentVariable(EnableVar), "1", StringComparison.Ordinal);

    public static bool RemoteEnabled =>
        string.Equals(Environment.GetEnvironmentVariable(RemoteVar), "1", StringComparison.Ordinal);

    public static string DisabledReason =>
        $"E2E disabled. Set {EnableVar}=1 (and {HubDirVar}, {DemoExeVar}) to run. " +
        "These tests drive a REAL hub process and rewrite %APPDATA%\\Keincheck — " +
        "quit your own hub first.";

    /// <summary>The artifacts directory, created on demand. Falls back to a temp folder.</summary>
    public static string ArtifactsDirectory
    {
        get
        {
            var dir = Environment.GetEnvironmentVariable(ArtifactsVar);
            if (string.IsNullOrWhiteSpace(dir))
                dir = Path.Combine(Path.GetTempPath(), "keincheck-e2e");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }
}
