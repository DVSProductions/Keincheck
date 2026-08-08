using Keincheck.Protocol;

namespace Keincheck.Hub;

/// <summary>
/// The hub's <c>--issue-websocket-token</c> command line: allowlists an origin, mints a token
/// for it, and writes a <see cref="WebSocketCredential"/> to a file or standard output.
/// </summary>
/// <remarks>
/// <para>
/// The sibling of <c>--issue-credential</c>, and it lives in the hub for the same reason: the
/// hub owns the access policy, so it is what issues against it. A build step calls this through
/// <c>Keincheck.Client.targets</c> so a browser app can be bound at compile time to the hub it
/// was built against.
/// </para>
/// <para>
/// It writes the policy file directly rather than asking a running hub over the pipe. That is
/// safe because <see cref="HubWebSocketAccess"/> re-reads the file when it changes, so a hub
/// that is already up picks up the new token without a restart — and it is what makes this work
/// on a machine where the hub is not resident at all.
/// </para>
/// <para>
/// <b>It will not enable the endpoint.</b> If the gate is off it refuses with
/// <see cref="Unavailable"/> and says so. Turning on a network-reachable endpoint is an
/// operator's decision made once, deliberately — not something a build does on their behalf
/// because they compiled a project.
/// </para>
/// </remarks>
internal static class WebSocketTokenCli
{
    public const string Verb = "--issue-websocket-token";

    private const int Ok = 0;
    private const int Usage = 2;
    private const int Unavailable = 4;

    /// <summary>Runs the command. Returns the process exit code.</summary>
    /// <param name="access">
    /// The policy to issue against; the machine's own when null. Tests pass a temp-path instance
    /// so they never touch the developer's real hub configuration.
    /// </param>
    public static int Run(string[] args, HubOptions? hubOptions = null, HubWebSocketAccess? access = null)
    {
        var options = Options.Parse(args, out var error);
        if (error is not null)
        {
            Console.Error.WriteLine(error);
            Console.Error.WriteLine();
            PrintUsage();
            return Usage;
        }
        if (options is null)
        {
            PrintUsage();
            return Ok;
        }

        access ??= HubWebSocketAccess.Open();

        if (!access.Enabled)
        {
            Console.Error.WriteLine(
                "Keincheck: the WebSocket endpoint is not enabled on this machine, so no token was " +
                "issued. Enable it once from the hub window. This build will not be able to attach " +
                "from a browser.");
            return Unavailable;
        }

        if (!access.AllowOrigin(options.Origin!))
        {
            Console.Error.WriteLine(
                $"Keincheck: '{options.Origin}' is not a well-formed origin. Expected something " +
                "like http://localhost:5000.");
            return Usage;
        }

        // Reuse an existing credential rather than minting one per build. Without this every
        // compile would add a token to the hub's list, which makes that list useless for
        // noticing anything unusual -- the same reasoning as --issue-credential's --if-missing.
        if (options.IfMissing && options.OutputPath is { } path && File.Exists(path))
        {
            try
            {
                var existing = WebSocketCredential.LoadFile(path);
                if (access.Check(options.Origin, existing.Token) == WebSocketGateResult.Allowed)
                {
                    Console.Error.WriteLine($"Keincheck: reusing the existing token in {path}.");
                    return Ok;
                }
            }
            catch (Exception ex) when (ex is FormatException or IOException)
            {
                // Unreadable or stale -- fall through and mint a fresh one over it.
            }
        }

        var credential = new WebSocketCredential
        {
            Endpoint = options.Endpoint ?? DefaultEndpoint(hubOptions),
            Token = access.IssueToken(options.Label!),
            Label = options.Label,
        };

        var text = credential.Serialize();
        if (options.OutputPath is null)
        {
            Console.WriteLine(text);
        }
        else
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(options.OutputPath))!);
            File.WriteAllText(options.OutputPath, text);
            Console.Error.WriteLine(
                $"Keincheck: issued a WebSocket token for {options.Origin} to {options.OutputPath}.");
        }
        return Ok;
    }

    private static string DefaultEndpoint(HubOptions? options)
    {
        var o = options ?? new HubOptions();
        return $"ws://127.0.0.1:{o.HttpPort}{o.WebSocketPath}";
    }

    private static void PrintUsage() => Console.Error.WriteLine(
        """
        Keincheck.Hub --issue-websocket-token — bind a browser app to this hub at build time.

          Keincheck.Hub --issue-websocket-token --origin <url> [options]

        Options:
          --origin <url>       Required. The origin the app is served from, e.g.
                               http://localhost:5000. Added to the hub's allowlist.
          --label <name>       A name to revoke the token by. Defaults to the origin's host.
          --out <path>         Write the credential here. Defaults to standard output.
          --endpoint <ws-url>  Override the hub endpoint written into the credential.
          --if-missing         Reuse the credential at --out if it is still valid.

        Exit codes: 0 issued or reused, 2 bad usage, 4 the endpoint is not enabled.
        """);

    private sealed class Options
    {
        public string? Origin { get; init; }
        public string? Label { get; init; }
        public string? OutputPath { get; init; }
        public string? Endpoint { get; init; }
        public bool IfMissing { get; init; }

        /// <summary>
        /// Returns null with no error for an explicit help request; null with an error for bad
        /// usage; otherwise the parsed options.
        /// </summary>
        public static Options? Parse(string[] args, out string? error)
        {
            error = null;
            string? origin = null, label = null, output = null, endpoint = null;
            var ifMissing = false;

            for (var i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case Verb:
                        break;
                    case "--help" or "-h" or "-?":
                        return null;
                    case "--if-missing":
                        ifMissing = true;
                        break;
                    case "--origin":
                        if (!TryValue(args, ref i, out origin, "--origin", out error)) return null;
                        break;
                    case "--label":
                        if (!TryValue(args, ref i, out label, "--label", out error)) return null;
                        break;
                    case "--out":
                        if (!TryValue(args, ref i, out output, "--out", out error)) return null;
                        break;
                    case "--endpoint":
                        if (!TryValue(args, ref i, out endpoint, "--endpoint", out error)) return null;
                        break;
                    default:
                        error = $"Unrecognized argument '{args[i]}'.";
                        return null;
                }
            }

            if (string.IsNullOrWhiteSpace(origin))
            {
                error = "--origin is required.";
                return null;
            }

            // A label defaults to the origin's host so the hub's list reads as a list of apps
            // rather than a list of identical entries called "build".
            label ??= Uri.TryCreate(origin, UriKind.Absolute, out var parsed)
                ? parsed.Authority
                : origin;

            return new Options
            {
                Origin = origin,
                Label = label,
                OutputPath = output,
                Endpoint = endpoint,
                IfMissing = ifMissing,
            };
        }

        private static bool TryValue(
            string[] args, ref int i, out string? value, string name, out string? error)
        {
            if (i + 1 >= args.Length)
            {
                value = null;
                error = $"{name} requires a value.";
                return false;
            }
            value = args[++i];
            error = null;
            return true;
        }
    }
}
