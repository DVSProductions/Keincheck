using System.Runtime.InteropServices;
using Keincheck.Protocol;

namespace Keincheck.Hub.Remote;

/// <summary>
/// The hub's own <c>--issue-credential</c> command line: mints a remote client credential and
/// writes it to a file or standard output.
/// </summary>
/// <remarks>
/// <para>
/// This lives in the hub rather than in a separate tool on purpose. Enrollment is the hub's
/// job — it owns the certificate authority — and shipping a second executable to ask it for
/// something meant packaging, versioning and updating one more binary, plus a second copy of
/// the pipe-and-framing code, for a feature that is one call.
/// </para>
/// <para>
/// It runs before the single-instance election, so it works whether or not a hub is already
/// up. When one is running it asks over the control pipe, so both processes never write the
/// issued-credential list at once — a lost record there would mean a live credential that
/// cannot be revoked. When none is running it opens the store directly, which is what makes
/// this usable on a build agent where the hub is not resident.
/// </para>
/// </remarks>
internal static class CredentialCli
{
    public const string Verb = "--issue-credential";

    private const int Ok = 0;
    private const int Usage = 2;
    private const int Refused = 3;
    private const int Unavailable = 4;

    /// <summary>Runs the command. Returns the process exit code.</summary>
    public static int Run(string[] args)
    {
        AttachToParentConsole();

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

        // Reuse an existing credential rather than minting one per build. Without this a
        // build step would add an entry to the issued list on every compile, which makes that
        // list useless for spotting anything unusual.
        if (options.IfMissing && options.OutputPath is { } existingPath && File.Exists(existingPath))
        {
            try
            {
                using var existing = Keincheck.Remote.RemoteCredential.LoadFile(existingPath);
                if (!existing.IsExpiringWithin(options.RenewWithin))
                {
                    Console.Error.WriteLine(
                        $"keincheck: '{existing.Host}' credential is valid until {existing.NotAfter:u}; nothing to do.");
                    return Ok;
                }
                Console.Error.WriteLine($"keincheck: credential expires {existing.NotAfter:u}; renewing.");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"keincheck: existing credential unusable ({ex.Message}); re-issuing.");
            }
        }

        try
        {
            var (bundle, serial, notAfter) = IsHubRunning()
                ? IssueViaRunningHub(options)
                : IssueFromStoreDirectly(options);

            if (options.OutputPath is { } path)
            {
                var directory = Path.GetDirectoryName(Path.GetFullPath(path));
                if (!string.IsNullOrEmpty(directory))
                    Directory.CreateDirectory(directory);
                File.WriteAllText(path, bundle);
                Console.Error.WriteLine(
                    $"keincheck: wrote a credential for '{options.Target}' to {path} " +
                    $"(serial {serial}, valid until {notAfter:u}).");
                Console.Error.WriteLine(
                    "keincheck: this file is a SECRET. Keep it out of source control; revoke it from " +
                    "the hub window or hub_remote_revoke if it leaks.");
            }
            else
            {
                Console.Out.WriteLine(bundle);
                Console.Error.WriteLine($"keincheck: serial {serial}, valid until {notAfter:u}.");
            }

            return Ok;
        }
        catch (CredentialRefusedException ex)
        {
            Console.Error.WriteLine($"keincheck: {ex.Message}");
            return ex.Unavailable ? Unavailable : Refused;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"keincheck: could not issue a credential — {ex.Message}");
            return Refused;
        }
    }

    // ---------------------------------------------------------------- issuing

    /// <summary>Asks the running hub over the control pipe.</summary>
    private static (string Bundle, string? Serial, DateTimeOffset? NotAfter) IssueViaRunningHub(Options options)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var channel = PipeTransport.ConnectAsync(null, TimeSpan.FromSeconds(10), cts.Token)
            .GetAwaiter().GetResult();
        try
        {
            channel.SendAsync(MessageKind.EnrollRequest, new EnrollRequestMessage
            {
                TargetName = options.Target,
                RequestedDays = options.Days,
                Note = options.Note,
            }, cancellationToken: cts.Token).GetAwaiter().GetResult();

            var envelope = channel.ReceiveAsync(cts.Token).GetAwaiter().GetResult()
                ?? throw new CredentialRefusedException("the hub closed the connection without answering.", false);

            if (envelope.Kind == MessageKind.Rejected)
            {
                var rejected = envelope.Unwrap<RejectedMessage>();
                throw new CredentialRefusedException(
                    $"the hub refused: {rejected?.Code} {rejected?.Reason}", false);
            }

            var response = envelope.Unwrap<EnrollResponseMessage>()
                ?? throw new CredentialRefusedException("the hub's response was empty.", false);

            if (!response.Accepted || string.IsNullOrEmpty(response.Bundle))
                throw new CredentialRefusedException(response.Reason ?? "the hub refused.", false);

            return (response.Bundle, response.Serial, response.NotAfter);
        }
        finally
        {
            channel.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    /// <summary>
    /// Issues straight from the on-disk store when no hub is running — the build-agent case.
    /// </summary>
    /// <remarks>
    /// Deliberately does NOT provision a certificate authority. Creating one is an operator
    /// decision made in the hub window; a build step silently standing up remote access on a
    /// machine whose owner never asked for it is exactly the accident this design avoids.
    /// </remarks>
    private static (string Bundle, string? Serial, DateTimeOffset? NotAfter) IssueFromStoreDirectly(Options options)
    {
        using var store = RemoteStore.Open();
        if (!store.IsProvisioned)
        {
            throw new CredentialRefusedException(
                "remote access is not set up on this machine. Open the hub window and enable it " +
                "(or call hub_remote_enable) once; after that this works with the hub closed.",
                unavailable: true);
        }

        var lifetime = options.Days > 0 ? TimeSpan.FromDays(options.Days) : RemoteStore.BuildLifetime;
        var (bundle, record) = store.Issue(options.Target, lifetime, issuedVia: "cli", note: options.Note);
        return (bundle, record.Serial, record.NotAfter);
    }

    private static bool IsHubRunning()
    {
        try
        {
            using var mutex = System.Threading.Mutex.OpenExisting(PipeNames.SingleInstanceMutex);
            return true;
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            // It exists but belongs to another user's session; we cannot use its pipe either.
            return false;
        }
    }

    private sealed class CredentialRefusedException(string message, bool unavailable) : Exception(message)
    {
        /// <summary>True when nothing is set up yet, as opposed to a request being rejected.</summary>
        public bool Unavailable { get; } = unavailable;
    }

    // ---------------------------------------------------------------- console

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int processId);

    /// <summary>
    /// Borrows the launching terminal's console.
    /// </summary>
    /// <remarks>
    /// The hub is a WinExe, so it has no console of its own and anything it writes is
    /// discarded when a human runs it from a prompt. Redirected output (MSBuild's Exec) works
    /// regardless; this is purely so the same command is usable by hand.
    /// </remarks>
    private static void AttachToParentConsole()
    {
        try { AttachConsole(-1); } catch { /* no parent console; redirected output still works */ }
    }

    private static void PrintUsage() => Console.Error.WriteLine(
        """
        Keincheck.Hub --issue-credential — mint a credential for a client on another machine.

          Keincheck.Hub.exe --issue-credential --target <host-label> [options]

        Options:
          --target <label>     Required. The machine this credential authenticates as. Letters,
                               digits, '.', '-' and '_' only; becomes the '@host' in that
                               client's id (e.g. myapp@MACHINENAME#1).
          --out <path>         Write the credential to a file instead of standard output.
          --days <n>           Requested validity; the hub clamps it to its own maximum.
          --note <text>        Recorded against the credential in hub_remote_status.
          --if-missing         Do nothing if --out already holds a credential that is not close
                               to expiring. Intended for a build step.
          --renew-within <n>   With --if-missing, renew when fewer than n days remain (14).

        Exit codes: 0 ok, 2 usage, 3 refused, 4 remote access not set up on this machine.

        Works whether or not a hub is running: it asks the running hub when there is one, and
        reads the store directly when there is not. It never enables remote access by itself.
        """);

    private sealed class Options
    {
        public string Target { get; private init; } = string.Empty;
        public string? OutputPath { get; private init; }
        public string? Note { get; private init; }
        public int Days { get; private init; }
        public bool IfMissing { get; private init; }
        public TimeSpan RenewWithin { get; private init; } = TimeSpan.FromDays(14);

        public static Options? Parse(string[] args, out string? error)
        {
            error = null;
            string? target = null, output = null, note = null;
            var days = 0;
            var ifMissing = false;
            var renewWithinDays = 14;

            for (var i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case Verb:
                        break;   // the verb itself
                    case "-h" or "--help":
                        return null;
                    case "--if-missing":
                        ifMissing = true;
                        break;
                    case "--target":
                        if (!Next(args, ref i, out target)) { error = "--target needs a value."; return null; }
                        break;
                    case "--out":
                        if (!Next(args, ref i, out output)) { error = "--out needs a value."; return null; }
                        break;
                    case "--note":
                        if (!Next(args, ref i, out note)) { error = "--note needs a value."; return null; }
                        break;
                    case "--days":
                        if (!Next(args, ref i, out var d) || !int.TryParse(d, out days) || days <= 0)
                        {
                            error = "--days needs a positive number."; return null;
                        }
                        break;
                    case "--renew-within":
                        if (!Next(args, ref i, out var r) || !int.TryParse(r, out renewWithinDays) || renewWithinDays < 0)
                        {
                            error = "--renew-within needs a non-negative number."; return null;
                        }
                        break;
                    default:
                        error = $"Unknown argument '{args[i]}'.";
                        return null;
                }
            }

            if (string.IsNullOrWhiteSpace(target))
            {
                error = "--target is required.";
                return null;
            }

            // Validate locally so a bad label fails immediately, not after a round trip.
            try
            {
                Keincheck.Remote.RemoteCertificates.ValidateHostLabel(target);
            }
            catch (ArgumentException ex)
            {
                error = ex.Message;
                return null;
            }

            if (ifMissing && output is null)
            {
                error = "--if-missing needs --out, since it decides by looking at the existing file.";
                return null;
            }

            return new Options
            {
                Target = target,
                OutputPath = output,
                Note = note,
                Days = days,
                IfMissing = ifMissing,
                RenewWithin = TimeSpan.FromDays(renewWithinDays),
            };
        }

        private static bool Next(string[] args, ref int i, out string? value)
        {
            if (i + 1 >= args.Length) { value = null; return false; }
            value = args[++i];
            return !string.IsNullOrWhiteSpace(value);
        }
    }
}
