using Keincheck.Protocol;
using Keincheck.Remote;

namespace Keincheck.Enroll;

/// <summary>
/// <c>keincheck-enroll</c> — asks the locally-running hub for a remote client credential.
/// </summary>
/// <remarks>
/// <para>
/// It talks to the hub over the <b>local control pipe</b>, which is created
/// <c>PipeOptions.CurrentUserOnly</c>. The operating system has therefore already established
/// that the requester is this user — the same boundary that already lets any local process
/// drive every registered app — so no additional prompt is needed on this path. That is what
/// makes a build-time enrollment step practical rather than an interruption.
/// </para>
/// <para>
/// Two ways to use it. A build step runs it with <c>--if-missing</c> so it enrolls once and
/// then does nothing until the credential nears expiry. A human runs it once by hand to mint
/// a credential to bake into a CI build or store as a secret.
/// </para>
/// </remarks>
internal static class Program
{
    private const int Ok = 0;
    private const int Usage = 2;
    private const int Refused = 3;
    private const int NoHub = 4;

    private static async Task<int> Main(string[] args)
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

        // Reuse an existing credential rather than minting a fresh one on every build. Without
        // this, a build step would fill the hub's issued-credential list with one entry per
        // compile and make the revocation list useless for spotting anything unusual.
        if (options.IfMissing && options.OutputPath is { } path && File.Exists(path))
        {
            try
            {
                using var existing = RemoteCredential.LoadFile(path);
                if (!existing.IsExpiringWithin(options.RenewWithin))
                {
                    Console.Error.WriteLine(
                        $"keincheck-enroll: '{existing.Host}' credential is valid until {existing.NotAfter:u}; nothing to do.");
                    return Ok;
                }
                Console.Error.WriteLine(
                    $"keincheck-enroll: credential expires {existing.NotAfter:u}; renewing.");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"keincheck-enroll: existing credential unusable ({ex.Message}); re-issuing.");
            }
        }

        EnrollResponseMessage response;
        try
        {
            response = await RequestAsync(options).ConfigureAwait(false);
        }
        // Both shapes mean the same thing to a user: nothing is listening on the control pipe.
        // The connect helper surfaces the deadline as a TimeoutException on one path and as a
        // cancelled task on another, and reporting the latter verbatim produced
        // "could not reach the hub — A task was canceled", which explains nothing.
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            // A developer's hub not being open must not break their build. The caller decides
            // whether that is fatal; the .targets file treats it as a warning.
            Console.Error.WriteLine(
                "keincheck-enroll: no Keincheck hub is running for this user. Start the hub and try again.");
            return NoHub;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"keincheck-enroll: could not reach the hub — {ex.Message}");
            return NoHub;
        }

        if (!response.Accepted || string.IsNullOrEmpty(response.Bundle))
        {
            Console.Error.WriteLine($"keincheck-enroll: the hub refused — {response.Reason ?? "no reason given"}");
            return Refused;
        }

        if (options.OutputPath is { } outputPath)
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);
            File.WriteAllText(outputPath, response.Bundle);

            Console.Error.WriteLine(
                $"keincheck-enroll: wrote a credential for '{options.Target}' to {outputPath} " +
                $"(serial {response.Serial}, valid until {response.NotAfter:u}).");
            Console.Error.WriteLine(
                "keincheck-enroll: this file is a SECRET — anything holding it can attach to that hub. " +
                "Keep it out of source control; revoke it from the hub window if it leaks.");
        }
        else
        {
            // The bundle alone on stdout, so it can be piped or captured.
            Console.Out.WriteLine(response.Bundle);
            Console.Error.WriteLine(
                $"keincheck-enroll: serial {response.Serial}, valid until {response.NotAfter:u}.");
        }

        return Ok;
    }

    private static async Task<EnrollResponseMessage> RequestAsync(Options options)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        await using var channel = await PipeTransport
            .ConnectAsync(options.PipeName, TimeSpan.FromSeconds(5), cts.Token)
            .ConfigureAwait(false);

        await channel.SendAsync(MessageKind.EnrollRequest, new EnrollRequestMessage
        {
            TargetName = options.Target,
            RequestedDays = options.Days,
            Note = options.Note,
        }, cancellationToken: cts.Token).ConfigureAwait(false);

        var envelope = await channel.ReceiveAsync(cts.Token).ConfigureAwait(false)
            ?? throw new IOException("The hub closed the connection without answering.");

        if (envelope.Kind == MessageKind.Rejected)
        {
            var rejected = envelope.Unwrap<RejectedMessage>();
            return new EnrollResponseMessage
            {
                Accepted = false,
                Reason = $"{rejected?.Code}: {rejected?.Reason}",
            };
        }

        if (envelope.Kind != MessageKind.EnrollResponse)
            throw new ProtocolException($"Expected an enrollment response from the hub, got {envelope.Kind}.");

        return envelope.Unwrap<EnrollResponseMessage>()
            ?? throw new ProtocolException("The hub's enrollment response was empty.");
    }

    private static void PrintUsage() => Console.Error.WriteLine(
        """
        keincheck-enroll — request a remote client credential from the local Keincheck hub.

          keincheck-enroll --target <host-label> [options]

        Options:
          --target <label>     Required. The machine label this credential authenticates as.
                               Letters, digits, '.', '-' and '_' only; becomes the '@host' in
                               the client's hub id (e.g. protoface@OP3R4T0RV2#1).
          --out <path>         Write the credential to a file. Without this it goes to stdout.
          --days <n>           Requested validity. The hub clamps this to its own maximum.
          --note <text>        Recorded against the credential in the hub's issued list.
          --if-missing         Do nothing if --out already holds a credential that is not
                               close to expiring. Use this in a build step.
          --renew-within <n>   With --if-missing, renew when fewer than n days remain
                               (default 14).
          --pipe <name>        Override the hub's control pipe name.
          -h, --help           Show this.

        Exit codes: 0 ok, 2 usage, 3 refused by the hub, 4 no hub reachable.

        The credential is a SECRET: it carries a private key, and anything holding it can
        attach to that hub until it expires or is revoked. Do not commit it.
        """);

    private sealed class Options
    {
        public string Target { get; private init; } = string.Empty;
        public string? OutputPath { get; private init; }
        public string? Note { get; private init; }
        public string? PipeName { get; private init; }
        public int Days { get; private init; }
        public bool IfMissing { get; private init; }
        public TimeSpan RenewWithin { get; private init; } = TimeSpan.FromDays(14);

        /// <summary>Returns null for --help, or sets <paramref name="error"/> on bad input.</summary>
        public static Options? Parse(string[] args, out string? error)
        {
            error = null;
            string? target = null, output = null, note = null, pipe = null;
            var days = 0;
            var ifMissing = false;
            var renewWithinDays = 14;

            for (var i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
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
                    case "--pipe":
                        if (!Next(args, ref i, out pipe)) { error = "--pipe needs a value."; return null; }
                        break;
                    case "--days":
                        if (!Next(args, ref i, out var d) || !int.TryParse(d, out days) || days <= 0)
                        {
                            error = "--days needs a positive number.";
                            return null;
                        }
                        break;
                    case "--renew-within":
                        if (!Next(args, ref i, out var r) || !int.TryParse(r, out renewWithinDays) || renewWithinDays < 0)
                        {
                            error = "--renew-within needs a non-negative number.";
                            return null;
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

            // Validate locally so a bad label fails immediately with a clear message rather
            // than after a round-trip to the hub.
            try
            {
                RemoteCertificates.ValidateHostLabel(target);
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
                PipeName = pipe,
                Days = days,
                IfMissing = ifMissing,
                RenewWithin = TimeSpan.FromDays(renewWithinDays),
            };
        }

        private static bool Next(string[] args, ref int i, out string? value)
        {
            if (i + 1 >= args.Length)
            {
                value = null;
                return false;
            }
            value = args[++i];
            return !string.IsNullOrWhiteSpace(value);
        }
    }
}
