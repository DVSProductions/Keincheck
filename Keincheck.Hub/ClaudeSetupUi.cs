using System.Text;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Keincheck.Hub;

/// <summary>
/// The small tray-driven UI for <see cref="ClaudeMcpSetup"/>: a one-time first-run offer and the
/// result dialog shown after configuring. Built in code to match the hub's XAML-free style. Must
/// be called on the UI thread.
/// </summary>
internal static class ClaudeSetupUi
{
    /// <summary>Configures each <paramref name="targets"/> entry and shows a summary dialog.</summary>
    public static void RunAndShowResult(IReadOnlyList<ClaudeTarget> targets, string connectExe)
    {
        var results = targets.Select(t => ClaudeMcpSetup.Configure(t, connectExe)).ToList();

        var body = new StringBuilder();
        foreach (var r in results)
        {
            var mark = r.Outcome == ConfigOutcome.Failed ? "✗" : "✓";
            body.AppendLine($"{mark} {r.Message}");
            body.AppendLine($"    {r.Path}");
        }
        if (results.Any(r => r.Outcome is ConfigOutcome.Added or ConfigOutcome.Updated))
            body.Append("\nRestart Claude to pick up the change.");

        ShowMessage("Keincheck — Claude setup", body.ToString().TrimEnd());
    }

    /// <summary>
    /// Shows the one-time first-run offer. Invokes <paramref name="onClosed"/> after the user
    /// dismisses it (whatever they chose) so the caller can record that the offer was made.
    /// </summary>
    public static void ShowFirstRunOffer(string connectExe, Action onClosed)
    {
        var intro = new TextBlock
        {
            Text = "Let Claude see and drive this app's UI?\n\n" +
                   "Keincheck can add a \"keincheck-hub\" MCP server to Claude Code and Claude " +
                   "Desktop so an AI assistant can inspect and operate apps connected to this hub. " +
                   "You can always do this later from the tray menu.",
            TextWrapping = TextWrapping.Wrap,
        };

        var setup = new Button { Content = "Set up Claude Code + Desktop", HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center };
        var notNow = new Button { Content = "Not now", HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center };

        var window = NewDialog("Keincheck", out var root);
        root.Children.Add(intro);
        root.Children.Add(setup);
        root.Children.Add(notNow);

        setup.Click += (_, _) =>
        {
            window.Close();
            RunAndShowResult(new[] { ClaudeTarget.Code, ClaudeTarget.Desktop }, connectExe);
            onClosed();
        };
        notNow.Click += (_, _) =>
        {
            window.Close();
            onClosed();
        };

        window.Show();
        window.Activate();
    }

    /// <summary>Shows a simple informational note (e.g. the shim could not be located).</summary>
    public static void ShowNote(string title, string body) => ShowMessage(title, body);

    private static void ShowMessage(string title, string body)
    {
        var text = new TextBlock { Text = body, TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("Consolas, Menlo, monospace") };
        var close = new Button { Content = "Close", HorizontalAlignment = HorizontalAlignment.Right };

        var window = NewDialog(title, out var root);
        root.Children.Add(text);
        root.Children.Add(close);

        close.Click += (_, _) => window.Close();
        window.Show();
        window.Activate();
    }

    private static Window NewDialog(string title, out StackPanel root)
    {
        root = new StackPanel { Margin = new Avalonia.Thickness(16), Spacing = 12 };
        return new Window
        {
            Title = title,
            Width = 520,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            ShowInTaskbar = true,
            Icon = LoadIcon(),
            Content = root,
        };
    }

    private static WindowIcon? LoadIcon()
    {
        try
        {
            var uri = new Uri("avares://Keincheck.Hub/Assets/tray.ico");
            if (Avalonia.Platform.AssetLoader.Exists(uri))
                using (var stream = Avalonia.Platform.AssetLoader.Open(uri))
                    return new WindowIcon(stream);
        }
        catch
        {
            // default glyph
        }
        return null;
    }
}
