using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;

namespace Keincheck.Hub;

/// <summary>
/// The hub's status window (built in code to match the project's XAML-free style). It
/// shows the "AI is driving X" banner, the live client list with per-row launch /
/// restart / read-only / make-active controls, and a scrolling audit log of AI tool
/// calls. Closing the window hides it (the daemon stays in the tray).
/// </summary>
public sealed class HubWindow : Window
{
    private readonly HubViewModel _vm;

    public HubWindow(HubViewModel vm)
    {
        _vm = vm;
        DataContext = vm;

        Title = $"Keincheck Hub {VersionLabel()}";
        Icon = LoadWindowIcon();
        Width = 640;
        Height = 520;
        MinWidth = 480;
        MinHeight = 360;

        Content = BuildLayout();
    }

    /// <summary>The hub's version for display (e.g. "v0.8.0"), shared with the tray tooltip.</summary>
    internal static string VersionLabel() => "v" + (HubMetaTools.ResolveHubAssemblyVersion() ?? "?");

    /// <summary>Loads the embedded logo for the taskbar / title-bar icon (best-effort).</summary>
    private static WindowIcon? LoadWindowIcon()
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
            // No asset available — Avalonia draws its default window glyph.
        }
        return null;
    }

    private Control BuildLayout()
    {
        var root = new Grid
        {
            Margin = new Thickness(12),
            RowDefinitions = new RowDefinitions("Auto,*,Auto,160"),
        };

        // --- driving banner -------------------------------------------------
        var banner = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x2D, 0x2D, 0x3A)),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12, 8),
            Margin = new Thickness(0, 0, 0, 10),
            Child = new TextBlock
            {
                FontWeight = FontWeight.SemiBold,
                Foreground = Brushes.White,
                [!TextBlock.TextProperty] = new Binding(nameof(HubViewModel.DrivingText)),
            },
        };
        Grid.SetRow(banner, 0);
        root.Children.Add(banner);

        // --- client list ----------------------------------------------------
        var list = new ListBox
        {
            [!ItemsControl.ItemsSourceProperty] = new Binding(nameof(HubViewModel.Clients)),
            ItemTemplate = new FuncDataTemplate<ClientRow>((row, _) => BuildClientRow(row), supportsRecycling: true),
        };
        Grid.SetRow(list, 1);
        root.Children.Add(list);

        // --- audit header (+ hub version on the right) ----------------------
        var auditHeader = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Margin = new Thickness(2, 12, 0, 4),
        };
        auditHeader.Children.Add(new TextBlock
        {
            Text = "Audit log — AI tool calls",
            FontWeight = FontWeight.SemiBold,
        });
        var version = new TextBlock
        {
            Text = $"Keincheck Hub {VersionLabel()}",
            FontSize = 11,
            Foreground = Brushes.Gray,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 2, 0),
        };
        Grid.SetColumn(version, 1);
        auditHeader.Children.Add(version);
        Grid.SetRow(auditHeader, 2);
        root.Children.Add(auditHeader);

        // --- audit log ------------------------------------------------------
        var audit = new ListBox
        {
            [!ItemsControl.ItemsSourceProperty] = new Binding(nameof(HubViewModel.Audit)),
            FontFamily = new FontFamily("Consolas, Menlo, monospace"),
            FontSize = 12,
        };
        var auditScroll = new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x44, 0x44, 0x44)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Child = audit,
        };
        Grid.SetRow(auditScroll, 3);
        root.Children.Add(auditScroll);

        return root;
    }

    // ---------------------------------------------------------------- remote panel

    /// <summary>
    /// The remote-access panel: enable/disable, the bind address, and the credential list.
    /// </summary>
    /// <remarks>
    /// One of three equivalent ways to issue a credential, alongside <c>hub_remote_issue</c>
    /// and <c>keincheck-enroll</c>. They all carry the same authorization — runs as this user —
    /// so none of them is privileged over the others; this one simply exists because clicking
    /// is sometimes what you want.
    /// </remarks>
    internal static Window BuildRemoteWindow(Remote.RemoteAccess remote)
    {
        var window = new Window
        {
            Title = "Keincheck Hub — remote access",
            Width = 660,
            Height = 480,
            Icon = LoadWindowIcon(),
        };

        var root = new Grid
        {
            Margin = new Thickness(12),
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,*"),
        };

        var status = new TextBlock
        {
            FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8),
        };
        var credentials = new ListBox { FontSize = 12 };

        // Newly-issued bundles land here rather than on the clipboard: the operator can see
        // exactly what they are about to hand over, and select it themselves.
        var bundleBox = new TextBox
        {
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            FontFamily = new FontFamily("Consolas, Menlo, monospace"),
            FontSize = 11,
            Height = 72,
            IsVisible = false,
            Margin = new Thickness(0, 0, 0, 10),
            PlaceholderText = "an issued credential appears here",
        };

        void Refresh()
        {
            var settings = remote.Store.Settings;
            status.Text = remote.IsListening
                ? $"Accepting remote clients on {remote.BoundEndpoint}."
                : settings.Enabled
                    ? "Remote access is enabled but the listener is not running."
                    : "Remote access is OFF. No socket is open and no client can attach.";

            credentials.ItemsSource = remote.Store.Issued()
                .Select(c =>
                {
                    var state = c.Revoked ? "REVOKED" : c.IsUsable ? "valid" : "expired";
                    var note = string.IsNullOrEmpty(c.Note) ? string.Empty : $"  — {c.Note}";
                    return $"{c.Host,-20} {state,-8} until {c.NotAfter:yyyy-MM-dd}  [{c.Serial}] via {c.IssuedVia}{note}";
                })
                .ToList();
        }

        // --- explanation ----------------------------------------------------
        var blurb = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brushes.Gray,
            FontSize = 12,
            Margin = new Thickness(0, 0, 0, 10),
            Text =
                "A remote client authenticates with a certificate issued here, and verifies this hub "
                + "against the same authority. Opening the listener grants nothing on its own — a client "
                + "also needs a credential. Remote clients start read-only and are never selected "
                + "automatically. A leaked credential can be revoked below; it stops working on the "
                + "client's next connection.",
        };
        Grid.SetRow(blurb, 0);
        root.Children.Add(blurb);

        Grid.SetRow(status, 1);
        root.Children.Add(status);

        // --- controls -------------------------------------------------------
        var controls = new WrapPanel { Margin = new Thickness(0, 0, 0, 10) };

        var bindBox = new TextBox
        {
            Width = 130,
            PlaceholderText = "127.0.0.1",
            Text = remote.Store.Settings.BindAddress,
            Margin = new Thickness(0, 0, 6, 6),
        };
        ToolTip.SetTip(bindBox,
            "127.0.0.1 is reachable only through an SSH tunnel or another forwarder. "
            + "A specific interface address or 0.0.0.0 exposes the port on the network, "
            + "where the client certificate is the only barrier.");

        var portBox = new TextBox
        {
            Width = 80,
            PlaceholderText = "7423",
            Text = remote.Store.Settings.Port.ToString(),
            Margin = new Thickness(0, 0, 6, 6),
        };

        var enable = new Button { Content = "Enable", Margin = new Thickness(0, 0, 6, 6) };
        enable.Click += async (_, _) =>
        {
            if (!System.Net.IPAddress.TryParse(bindBox.Text, out _))
            {
                status.Text = $"'{bindBox.Text}' is not a valid IP address.";
                return;
            }
            if (!int.TryParse(portBox.Text, out var port) || port is <= 0 or > 65535)
            {
                status.Text = $"'{portBox.Text}' is not a valid port.";
                return;
            }

            try
            {
                status.Text = await remote.EnableAsync(
                    remote.Store.Settings with { BindAddress = bindBox.Text!, Port = port });
            }
            catch (Exception ex)
            {
                status.Text = $"Could not enable remote access: {ex.Message}";
                return;
            }
            Refresh();
        };
        var disable = new Button { Content = "Disable", Margin = new Thickness(0, 0, 6, 6) };
        disable.Click += async (_, _) => { await remote.DisableAsync(); Refresh(); };

        var targetBox = new TextBox
        {
            Width = 160,
            PlaceholderText = "machine label",
            Margin = new Thickness(16, 0, 6, 6),
        };
        ToolTip.SetTip(targetBox,
            "Becomes the '@host' in that client's id, e.g. protoface@OP3R4T0RV2. "
            + "Letters, digits, '.', '-' and '_' only.");

        var issue = new Button { Content = "Issue credential", Margin = new Thickness(0, 0, 6, 6) };
        issue.Click += (_, _) =>
        {
            try
            {
                var (bundle, record) = remote.Issue(targetBox.Text ?? string.Empty);
                Refresh();
                bundleBox.Text = bundle;
                bundleBox.IsVisible = true;
                status.Text =
                    $"Issued a credential for '{record.Host}', valid until {record.NotAfter:yyyy-MM-dd}. "
                    + "This is a SECRET — anything holding it can attach to this hub as that machine. "
                    + "Set it as KEINCHECK_REMOTE on the target, or revoke it below if it leaks.";
            }
            catch (Exception ex)
            {
                bundleBox.IsVisible = false;
                status.Text = ex.Message;
            }
        };

        var revoke = new Button { Content = "Revoke selected", Margin = new Thickness(0, 0, 6, 6) };
        revoke.Click += (_, _) =>
        {
            // The serial is the stable revocation key, rendered in [brackets] in the row.
            if (credentials.SelectedItem is not string line)
                return;
            var open = line.IndexOf('[');
            var close = line.IndexOf(']');
            if (open < 0 || close <= open)
                return;

            var serial = line[(open + 1)..close];
            status.Text = remote.Revoke(serial)
                ? $"Revoked {serial}. It stops working on that client's next connection."
                : $"{serial} is already revoked or unknown.";
            Refresh();
        };

        foreach (var control in new Control[] { bindBox, portBox, enable, disable, targetBox, issue, revoke })
            controls.Children.Add(control);
        Grid.SetRow(controls, 2);
        root.Children.Add(controls);

        Grid.SetRow(bundleBox, 3);
        root.Children.Add(bundleBox);

        var listBorder = new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x44, 0x44, 0x44)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Child = credentials,
        };
        Grid.SetRow(listBorder, 4);
        root.Children.Add(listBorder);

        Refresh();
        window.Content = root;
        return window;
    }

    private Control BuildClientRow(ClientRow row)
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto,Auto"),
            Margin = new Thickness(2, 4),
        };

        // name + status
        var info = new StackPanel { Orientation = Orientation.Vertical, VerticalAlignment = VerticalAlignment.Center };
        info.Children.Add(new TextBlock
        {
            FontWeight = FontWeight.SemiBold,
            [!TextBlock.TextProperty] = new Binding(nameof(ClientRow.Display)),
        });
        info.Children.Add(new TextBlock
        {
            FontSize = 11,
            Foreground = Brushes.Gray,
            [!TextBlock.TextProperty] = new Binding(nameof(ClientRow.StatusLine)),
        });
        Grid.SetColumn(info, 0);
        grid.Children.Add(info);

        // make-active
        var activate = new Button { Content = "Make active", Margin = new Thickness(4, 0, 0, 0) };
        activate.Click += (_, _) => _vm.SelectActive(row.ClientId);
        Grid.SetColumn(activate, 1);
        grid.Children.Add(activate);

        // launch / restart -- disabled for a remote client, whose process is on another
        // machine. The broker refuses these anyway; greying them out means the operator is
        // never left wondering why a button did nothing.
        var launch = new Button
        {
            Content = "Launch",
            Margin = new Thickness(4, 0, 0, 0),
            IsEnabled = row.CanLaunch,
        };
        if (!row.CanLaunch)
            ToolTip.SetTip(launch, "This client is on another machine; the hub cannot start processes there.");
        launch.Click += async (_, _) => await _vm.LaunchAsync(row.ClientId);
        Grid.SetColumn(launch, 2);
        grid.Children.Add(launch);

        var restart = new Button
        {
            Content = "Restart",
            Margin = new Thickness(4, 0, 0, 0),
            IsEnabled = row.CanLaunch,
        };
        if (!row.CanLaunch)
            ToolTip.SetTip(restart, "This client is on another machine; the hub cannot restart it.");
        restart.Click += async (_, _) => await _vm.RestartAsync(row.ClientId);
        Grid.SetColumn(restart, 3);
        grid.Children.Add(restart);

        // read-only toggle
        var readOnly = new CheckBox
        {
            Content = "Read-only",
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            [!ToggleButton_IsCheckedProperty()] = new Binding(nameof(ClientRow.ReadOnly)) { Mode = BindingMode.OneWay },
        };
        readOnly.IsCheckedChanged += (_, _) =>
        {
            var v = readOnly.IsChecked ?? false;
            if (v != row.ReadOnly)
                _vm.SetReadOnly(row.ClientId, v);
        };
        Grid.SetColumn(readOnly, 4);
        grid.Children.Add(readOnly);

        return grid;
    }

    private static Avalonia.AvaloniaProperty ToggleButton_IsCheckedProperty()
        => Avalonia.Controls.Primitives.ToggleButton.IsCheckedProperty;
}
