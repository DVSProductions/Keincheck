using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

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
        // Taller than the old 520: with the audit pane now sharing the window rather than
        // owning a fixed 160px, the default should show a useful amount of both panes before
        // anyone has to drag anything.
        Height = 620;
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
        // Star-sized rows either side of a splitter, rather than the audit log's old fixed
        // 160px. A pixel height cannot be dragged, so the log was stuck at four or five visible
        // lines however tall the window got -- and the audit log is the one thing here you
        // actually want to make bigger. MinHeight on both keeps a drag from collapsing either
        // pane to nothing, which is easy to do by accident and awkward to undo.
        var root = new Grid
        {
            Margin = new Thickness(12),
            RowDefinitions =
            [
                new RowDefinition(GridLength.Auto),                        // banner
                new RowDefinition(new GridLength(2, GridUnitType.Star)) { MinHeight = 80 },  // clients
                new RowDefinition(GridLength.Auto),                        // splitter
                new RowDefinition(new GridLength(1, GridUnitType.Star)) { MinHeight = 90 },  // audit
            ],
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

        // --- splitter -------------------------------------------------------
        // Visibly a handle, not just a hit-target. A transparent splitter resizes perfectly well
        // and tells nobody it is there -- and "I can't make the log bigger" is a discoverability
        // complaint as much as a layout one. The grab area is 8px for the pointer; the drawn
        // line is 2px so it reads as a divider rather than a chunk of chrome.
        var splitter = new GridSplitter
        {
            ResizeDirection = GridResizeDirection.Rows,
            Height = 8,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(0, 6, 0, 0),
            Cursor = new Cursor(StandardCursorType.SizeNorthSouth),
            Background = Brushes.Transparent, // the whole 8px is grabbable, not just the line
        };
        ToolTip.SetTip(splitter, "Drag to resize the audit log");

        // The line is drawn by a Border sharing the row, not by the splitter. Setting the
        // splitter's own Background did not render, and replacing its Template would put its
        // drag behaviour at risk -- so the two concerns are simply separated: the Border is
        // what you see, the splitter (on top, transparent) is what you grab.
        var divider = new Border
        {
            Height = 2,
            CornerRadius = new CornerRadius(1),
            Background = new SolidColorBrush(Color.FromRgb(0x50, 0x50, 0x50)),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 6, 0, 0),
            IsHitTestVisible = false, // never steal the drag from the splitter above it
        };
        Grid.SetRow(divider, 2);
        root.Children.Add(divider);

        Grid.SetRow(splitter, 2);
        root.Children.Add(splitter);

        // --- audit pane (header + log, sized together) ----------------------
        // Header and log share one star row so the splitter has exactly two panes to move
        // between. With the header in its own Auto row the splitter's neighbour would have
        // been that header, which cannot resize, and the drag would do nothing.
        var auditPane = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        Grid.SetRow(auditPane, 3);
        root.Children.Add(auditPane);

        // --- audit header (+ hub version on the right) ----------------------
        var auditHeader = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Margin = new Thickness(2, 6, 0, 4),
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
        Grid.SetRow(auditHeader, 0);
        auditPane.Children.Add(auditHeader);

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
        Grid.SetRow(auditScroll, 1);
        auditPane.Children.Add(auditScroll);

        KeepAuditScrolled(audit, _vm.Audit);

        return root;
    }

    /// <summary>
    /// Follows the newest audit entry, but only while the view is already at the bottom.
    /// </summary>
    /// <remarks>
    /// The log appends and never stops, so without this it sat on the first few entries and
    /// every new tool call went out of sight. Following unconditionally is the other failure:
    /// it would yank the view away the moment you scrolled up to read what an agent just did,
    /// which is precisely when the log matters. So it follows from the bottom and gets out of
    /// the way as soon as you scroll off it -- and resumes when you scroll back down.
    /// </remarks>
    /// <param name="entries">
    /// The collection itself, NOT <c>audit.ItemsSource</c>. The binding has not been evaluated
    /// while the layout is being built, so ItemsSource is still null here — reading it silently
    /// subscribed to nothing and the log never followed at all.
    /// </param>
    private static void KeepAuditScrolled(ListBox audit, System.Collections.ObjectModel.ObservableCollection<string> entries)
    {
        entries.CollectionChanged += (_, e) =>
        {
            if (e.Action is not System.Collections.Specialized.NotifyCollectionChangedAction.Add)
                return;

            if (entries.Count == 0)
                return;

            // Measured BEFORE the new row is laid out: once the extent grows, the old offset
            // would no longer look like "at the bottom" and following would stop after one row.
            var scroll = ScrollerOf(audit);

            // No ScrollViewer yet means the list has not been rendered, so there is nothing to
            // scroll away from and following is unambiguously right.
            if (scroll is not null && !IsAtBottom(scroll))
                return;

            // Posted, not called inline: the item is in the collection before a container for
            // it exists, so acting now would scroll to where the list used to end. And the
            // offset is set directly rather than via ScrollIntoView, which does not move a
            // virtualized ListBox whose target container has not been realized -- the reason
            // the log still sat on its first entries after the first attempt at this.
            Dispatcher.UIThread.Post(() =>
            {
                if (ScrollerOf(audit) is { } sv)
                    sv.Offset = new Vector(sv.Offset.X, sv.Extent.Height);
            }, DispatcherPriority.Background);
        };
    }

    /// <summary>
    /// Whether the view is parked at the bottom. The tolerance is one row's worth: a list
    /// scrolled to the end can sit a fraction of a pixel short of its extent, and an exact
    /// comparison would read that as "the user scrolled up" and stop following forever.
    /// </summary>
    private static bool IsAtBottom(ScrollViewer scroll) =>
        scroll.Offset.Y >= scroll.Extent.Height - scroll.Viewport.Height - 24;

    /// <summary>The list's own ScrollViewer, or null before it has been rendered.</summary>
    private static ScrollViewer? ScrollerOf(ListBox list) =>
        list.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();

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
            "Becomes the '@host' in that client's id, e.g. myapp@MACHINENAME. "
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
            ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto,Auto,Auto"),
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

        // Release the write-claim. Only one agent at a time may drive an app, and the claim
        // is normally released when that agent disconnects — so this is the operator's way
        // out of the one case that cannot resolve itself: an agent that died without its
        // transport closing still holds the app until the idle timeout. Enabled only while
        // somebody actually holds it, so the button never invites a no-op.
        var release = new Button
        {
            Content = "Release",
            Margin = new Thickness(4, 0, 0, 0),
            [!Button.IsEnabledProperty] = new Binding(nameof(ClientRow.IsClaimed)) { Mode = BindingMode.OneWay },
        };
        ToolTip.SetTip(release,
            "Free this app from the agent currently driving it, so another agent can take over. "
            + "Use when an agent has gone away without releasing.");
        release.Click += (_, _) => _vm.ReleaseClaim(row.ClientId);
        Grid.SetColumn(release, 2);
        grid.Children.Add(release);

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
        Grid.SetColumn(launch, 3);
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
        Grid.SetColumn(restart, 4);
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
        Grid.SetColumn(readOnly, 5);
        grid.Children.Add(readOnly);

        return grid;
    }

    private static Avalonia.AvaloniaProperty ToggleButton_IsCheckedProperty()
        => Avalonia.Controls.Primitives.ToggleButton.IsCheckedProperty;
}
