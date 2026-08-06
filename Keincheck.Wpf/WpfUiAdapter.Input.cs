using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Keincheck.Core;

namespace Keincheck.Wpf;

/// <summary>
/// Synthetic-input group of <see cref="WpfUiAdapter"/>: synthesised pointer gestures,
/// mouse wheel, literal text input, and key chords — the WPF analog of
/// <c>Keincheck.Avalonia.SyntheticInput</c>.
/// <para>
/// <b>Chosen approach.</b> WPF exposes no public synthetic-pointer pipeline that runs
/// <i>synchronously</i> in window coordinates: the OS-level <c>SendInput</c> API posts
/// asynchronous messages in <i>screen</i> coordinates, requires the target window to be
/// foreground, and moves the real system cursor. The tool contract is synchronous
/// (each method runs on the UI thread via <c>IUiDispatcher</c> and immediately returns
/// the hit element + its observable side effect), and it addresses elements in
/// <i>top-level</i> coordinates. To match the Avalonia adapter's tool outputs exactly —
/// and to keep the gesture deterministic for the hub-driven e2e — the primary mechanism
/// here is to <b>raise WPF routed input events directly on the hit-tested element</b>
/// (<see cref="MouseButtonEventArgs"/>, <see cref="MouseWheelEventArgs"/>,
/// <see cref="TextCompositionEventArgs"/>, <see cref="KeyEventArgs"/>), exactly as the
/// Avalonia synthetic engine raises Avalonia routed events. This drives the routed-event
/// side of every control (the part custom-drawn, peer-less controls actually handle —
/// <c>ClickCount</c>, button promotion to <c>Click</c>, wheel delta, text composition,
/// key down/up) faithfully and synchronously.
/// </para>
/// <para>
/// The Win32 <c>SendInput</c> P/Invoke (the INPUT/MOUSEINPUT/KEYBDINPUT structs + extern)
/// is also provided in this file per the Stage-B contract, and is used to park the real
/// system cursor over the gesture point (translated to screen coordinates via
/// <see cref="Visual.PointToScreen"/>) so screenshots and any code reading
/// <c>Mouse.GetPosition</c> see a consistent location, with the previous cursor position
/// restored afterwards. It is <b>not</b> used to deliver the gesture itself; see the
/// fidelity note in the adapter's <c>risks</c> for why.
/// </para>
/// </summary>
public sealed partial class WpfUiAdapter
{
    // ----------------------------------------------------- synthetic input

    /// <inheritdoc />
    public object? SendPointer(object topLevel, PointerAction action, UiPoint point)
    {
        if (topLevel is not Visual rootVisual)
            return null;

        var p = new Point(point.X, point.Y);
        var target = HitTestInputElement(rootVisual, p);
        if (target is null)
            return null;

        // Park the real cursor over the gesture point (screen coords) so screenshots /
        // Mouse.GetPosition observers stay consistent, then restore it. Best-effort: a
        // detached/zero-size visual yields no screen mapping and we simply skip it.
        using var _ = ParkCursor(rootVisual, p);

        var button = action == PointerAction.RightClick ? MouseButton.Right : MouseButton.Left;

        switch (action)
        {
            case PointerAction.Move:
                RaiseMove(target, p);
                break;
            case PointerAction.Down:
                RaisePressed(target, p, button, clickCount: 1);
                break;
            case PointerAction.Up:
                RaiseReleased(target, p, button);
                break;
            case PointerAction.Click:
            case PointerAction.RightClick:
                RaisePressed(target, p, button, clickCount: 1);
                RaiseReleased(target, p, button);
                break;
            case PointerAction.DoubleClick:
                RaisePressed(target, p, button, clickCount: 1);
                RaiseReleased(target, p, button);
                RaisePressed(target, p, button, clickCount: 2);
                RaiseReleased(target, p, button);
                break;
        }

        return AsControlHandle(target);
    }

    /// <inheritdoc />
    public object? SendWheel(object topLevel, UiPoint point, UiVector delta)
    {
        if (topLevel is not Visual rootVisual)
            return null;

        var p = new Point(point.X, point.Y);
        var target = HitTestInputElement(rootVisual, p);
        if (target is null)
            return null;

        using var _ = ParkCursor(rootVisual, p);
        RaiseWheel(target, p, delta);
        return AsControlHandle(target);
    }

    /// <inheritdoc />
    public object? SendText(object? target, string text)
    {
        // Focus the explicit target first (mirrors the Avalonia adapter focusing the
        // InputElement before routing text), then resolve the actual sink.
        (target as IInputElement)?.Focus();

        var sink = ResolveInputSink(target as DependencyObject);
        if (sink is null)
            return null;

        RaiseText(sink, text);
        return AsControlHandle(sink);
    }

    /// <inheritdoc />
    public bool SendKeys(
        object? target, string chords,
        out IReadOnlyList<string> sentChords, out object? sink, out string error)
    {
        sentChords = Array.Empty<string>();
        sink = null;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(chords))
        {
            error = "keys was empty.";
            return false;
        }

        // Parse ALL chords up-front (same token grammar as the Avalonia adapter: split on
        // space/comma/tab) so a single bad chord fails the whole call without sending any.
        var parsed = new List<(Key key, ModifierKeys mods, string raw)>();
        foreach (var token in chords.Split(new[] { ' ', ',', '\t' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (!TryParseChord(token, out var key, out var mods, out var parseError))
            {
                error = $"could not parse key chord '{token}': {parseError}";
                return false;
            }
            parsed.Add((key, mods, token));
        }

        if (parsed.Count == 0)
        {
            error = "no key chords parsed from input.";
            return false;
        }

        (target as IInputElement)?.Focus();

        var element = ResolveInputSink(target as DependencyObject);
        if (element is null)
        {
            error = "no focused element to receive keys (focus a control via handle/selector first).";
            return false;
        }

        var sent = new List<string>(parsed.Count);
        foreach (var (key, mods, raw) in parsed)
        {
            RaiseKey(element, key, mods, down: true);
            RaiseKey(element, key, mods, down: false);
            sent.Add(raw);
        }

        sentChords = sent;
        sink = AsControlHandle(element);
        return true;
    }

    // ----------------------------------------------------- routed-event engine

    private static readonly int StartTicks = Environment.TickCount;

    private static int Timestamp() => Environment.TickCount - StartTicks;

    /// <summary>
    /// Hit-tests <paramref name="point"/> (top-level coordinates) and returns the deepest
    /// <see cref="IInputElement"/> there, falling back to the root itself, mirroring the
    /// Avalonia <c>SyntheticInput.HitTest</c> (which returns the hit control or the
    /// top-level). The returned element is what we raise the routed event on so it
    /// bubbles/tunnels through every ancestor exactly as a real gesture would.
    /// </summary>
    private static UIElement? HitTestInputElement(Visual root, Point point)
    {
        var result = VisualTreeHelper.HitTest(root, point);
        if (result?.VisualHit is DependencyObject hit)
        {
            for (var node = hit; node is not null; node = VisualTreeHelper.GetParent(node))
            {
                if (node is UIElement ui)
                    return ui;
            }
        }

        // Nothing under the point (e.g. a transparent gap with no Background): fall back to
        // the root if it is itself a usable input element, matching Avalonia's "?? topLevel".
        return root as UIElement;
    }

    private static void RaiseMove(UIElement target, Point point)
    {
        var args = new MouseEventArgs(InputManager.Current.PrimaryMouseDevice, Timestamp())
        {
            RoutedEvent = Mouse.MouseMoveEvent,
            Source = target,
        };
        target.RaiseEvent(args);
    }

    private static void RaisePressed(UIElement target, Point point, MouseButton button, int clickCount)
    {
        // Raise ONLY the generic Mouse.MouseDownEvent and let WPF promote it to the
        // button-specific MouseLeftButtonDownEvent, exactly as real input does.
        //
        // Raising both — which this used to do, on the assumption that a manually raised
        // event would not be promoted — delivered every synthetic click TWICE: the direct
        // raise fired OnMouseLeftButtonDown once, and UIElement's class handler for
        // MouseDownEvent promoted the generic one into a second. A single click_at advanced
        // the WPF demo's gauge two notches, and nothing noticed because the adapter's only
        // coverage drove controls through UI Automation rather than synthetic pointer input.
        var generic = new MouseButtonEventArgs(InputManager.Current.PrimaryMouseDevice, Timestamp(), button)
        {
            RoutedEvent = Mouse.MouseDownEvent,
            Source = target,
        };
        target.RaiseEvent(generic);
    }

    private static void RaiseReleased(UIElement target, Point point, MouseButton button)
    {
        // Same promotion as the press: the generic event alone yields exactly one
        // MouseLeftButtonUp, which is what ButtonBase turns into a single Click.
        var generic = new MouseButtonEventArgs(InputManager.Current.PrimaryMouseDevice, Timestamp(), button)
        {
            RoutedEvent = Mouse.MouseUpEvent,
            Source = target,
        };
        target.RaiseEvent(generic);
    }

    private static void RaiseWheel(UIElement target, Point point, UiVector delta)
    {
        // WPF wheel delta is an integer in 120-unit notches (WHEEL_DELTA). Tool callers
        // pass notches (Avalonia convention: positive = up/away); scale to WPF units and
        // prefer the vertical component (WPF has a single wheel axis), falling back to the
        // horizontal notch when only deltaX was supplied.
        var notches = delta.Y != 0 ? delta.Y : delta.X;
        var wheelDelta = (int)Math.Round(notches * NativeMethods.WHEEL_DELTA);

        var args = new MouseWheelEventArgs(InputManager.Current.PrimaryMouseDevice, Timestamp(), wheelDelta)
        {
            RoutedEvent = Mouse.MouseWheelEvent,
            Source = target,
        };
        target.RaiseEvent(args);
    }

    private static void RaiseText(UIElement sink, string text)
    {
        // A TextComposition carries the literal text; raising TextInputEvent with it is the
        // WPF analog of Avalonia's TextInputEventArgs { Text = ... } — it is what TextBox and
        // custom text editors consume to insert characters.
        var composition = new TextComposition(InputManager.Current, sink, text);
        var args = new TextCompositionEventArgs(InputManager.Current.PrimaryKeyboardDevice, composition)
        {
            RoutedEvent = TextCompositionManager.TextInputEvent,
            Source = sink,
        };
        sink.RaiseEvent(args);
    }

    private static void RaiseKey(UIElement sink, Key key, ModifierKeys mods, bool down)
    {
        // KeyEventArgs requires a non-null PresentationSource. Use the source hosting the
        // sink, falling back to the source of its window, so keys can be sent even to an
        // element whose own visual is not yet directly source-mapped. When the sink is in a
        // live window (the normal case) this is the window's HwndSource.
        var source = PresentationSource.FromVisual(sink);
        if (source is null && Window.GetWindow(sink) is { } win)
            source = PresentationSource.FromVisual(win);
        if (source is null)
            return; // detached element: nothing can host the key event; skip silently.

        var args = new KeyEventArgs(InputManager.Current.PrimaryKeyboardDevice, source, Timestamp(), key)
        {
            RoutedEvent = down ? Keyboard.KeyDownEvent : Keyboard.KeyUpEvent,
            Source = sink,
        };
        sink.RaiseEvent(args);
    }

    // The button-specific routed events are declared on UIElement (the generic
    // MouseDown/Up live on Mouse). Driving the specific event is what invokes
    // OnMouseLeftButtonDown (GaugeControl) and ButtonBase's Click promotion.
    private static RoutedEvent ButtonDownEvent(MouseButton button) => button switch
    {
        MouseButton.Right => UIElement.MouseRightButtonDownEvent,
        _ => UIElement.MouseLeftButtonDownEvent,
    };

    private static RoutedEvent ButtonUpEvent(MouseButton button) => button switch
    {
        MouseButton.Right => UIElement.MouseRightButtonUpEvent,
        _ => UIElement.MouseLeftButtonUpEvent,
    };

    /// <summary>
    /// Resolves the element that should receive text/keys: the explicit
    /// <paramref name="explicitTarget"/> when it is a usable input element, else the
    /// currently keyboard-focused element — mirroring the Avalonia adapter's
    /// <c>ResolveInputSink</c> (explicit target first, then FocusManager). Narrowed to
    /// <see cref="UIElement"/> because that is what can have a routed event raised on it
    /// (a <c>ContentElement</c>/document leaf is not an addressable control handle here).
    /// </summary>
    private static UIElement? ResolveInputSink(DependencyObject? explicitTarget)
    {
        if (explicitTarget is UIElement ui)
            return ui;

        return Keyboard.FocusedElement as UIElement;
    }

    /// <summary>
    /// Returns the addressable control handle for a raised-on element: the nearest
    /// <see cref="FrameworkElement"/> (the <c>IsControl</c> boundary) so the tool layer's
    /// <c>registry.Assign</c> / <c>GetTypeName</c> see the same handle shape the Avalonia
    /// adapter returns (an Avalonia <c>Control</c>). The hit element is almost always a
    /// FrameworkElement already; this only guards the rare visual-only sink.
    /// </summary>
    private static object? AsControlHandle(object? raisedOn)
    {
        for (var node = raisedOn as DependencyObject; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is FrameworkElement fe)
                return fe;
        }
        return raisedOn;
    }

    // ----------------------------------------------------- chord grammar

    /// <summary>
    /// Parses a chord like <c>Ctrl+Shift+S</c> or a bare key like <c>Enter</c> into a
    /// <see cref="Key"/> + <see cref="ModifierKeys"/>. Modifier aliases match the Avalonia
    /// adapter: Ctrl/Control, Alt, Shift, Win/Cmd/Meta/Super. The last token is the key,
    /// preceding tokens are modifiers.
    /// </summary>
    private static bool TryParseChord(string token, out Key key, out ModifierKeys mods, out string error)
    {
        key = Key.None;
        mods = ModifierKeys.None;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(token))
        {
            error = "empty chord.";
            return false;
        }

        var parts = token.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            error = "no key in chord.";
            return false;
        }

        for (var i = 0; i < parts.Length; i++)
        {
            var part = parts[i];
            var isLast = i == parts.Length - 1;

            if (!isLast)
            {
                if (!TryModifier(part, out var m))
                {
                    error = $"'{part}' is not a recognized modifier.";
                    return false;
                }
                mods |= m;
                continue;
            }

            if (TryKey(part, out var k))
            {
                key = k;
                return true;
            }

            if (TryModifier(part, out _))
            {
                error = "chord contains modifiers but no non-modifier key.";
                return false;
            }

            error = $"'{part}' is not a recognized key.";
            return false;
        }

        error = "no key found.";
        return false;
    }

    private static bool TryModifier(string s, out ModifierKeys mod)
    {
        switch (s.ToLowerInvariant())
        {
            case "ctrl":
            case "control":
                mod = ModifierKeys.Control;
                return true;
            case "alt":
                mod = ModifierKeys.Alt;
                return true;
            case "shift":
                mod = ModifierKeys.Shift;
                return true;
            case "win":
            case "cmd":
            case "meta":
            case "super":
                mod = ModifierKeys.Windows;
                return true;
            default:
                mod = ModifierKeys.None;
                return false;
        }
    }

    private static bool TryKey(string s, out Key key)
    {
        switch (s.ToLowerInvariant())
        {
            case "esc": key = Key.Escape; return true;
            case "del": key = Key.Delete; return true;
            case "ins": key = Key.Insert; return true;
            case "pgup": key = Key.PageUp; return true;
            case "pgdn":
            case "pgdown": key = Key.PageDown; return true;
            case "return": key = Key.Return; return true;
        }

        // Digit keys: WPF names them D0..D9 (a bare "1" is not a Key enum member).
        if (s.Length == 1 && s[0] >= '0' && s[0] <= '9')
            return Enum.TryParse("D" + s, ignoreCase: true, out key);

        return Enum.TryParse(s, ignoreCase: true, out key) && key != Key.None;
    }

    // ----------------------------------------------------- Win32 cursor parking

    /// <summary>
    /// Moves the real system cursor over <paramref name="topLevelPoint"/> (translated from
    /// top-level to screen coordinates via <see cref="Visual.PointToScreen"/>) using the
    /// Win32 <c>SendInput</c> absolute-move path, and restores the previous cursor position
    /// when the returned scope is disposed. Best-effort: if the visual has no live screen
    /// mapping (detached / zero-size / no source), the cursor is left untouched.
    /// </summary>
    private static IDisposable ParkCursor(Visual root, Point topLevelPoint)
    {
        try
        {
            // PointToScreen requires the visual to be connected to a presentation source.
            if (PresentationSource.FromVisual(root) is null)
                return NullScope.Instance;

            var screen = root.PointToScreen(topLevelPoint);
            if (!NativeMethods.GetCursorPos(out var prev))
                return NullScope.Instance;

            MoveCursorAbsolute(screen.X, screen.Y);
            return new CursorRestore(prev);
        }
        catch
        {
            // PointToScreen throws for a visual not connected to a presentation source; the
            // gesture is delivered by routed events regardless, so cursor parking is optional.
            return NullScope.Instance;
        }
    }

    private static void MoveCursorAbsolute(double screenX, double screenY)
    {
        // SendInput absolute coordinates are normalized to 0..65535 across the virtual
        // screen. Map the device pixel through the virtual-screen metrics.
        var vx = NativeMethods.GetSystemMetrics(NativeMethods.SM_XVIRTUALSCREEN);
        var vy = NativeMethods.GetSystemMetrics(NativeMethods.SM_YVIRTUALSCREEN);
        var vw = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXVIRTUALSCREEN);
        var vh = NativeMethods.GetSystemMetrics(NativeMethods.SM_CYVIRTUALSCREEN);
        if (vw <= 0 || vh <= 0)
        {
            NativeMethods.SetCursorPos((int)Math.Round(screenX), (int)Math.Round(screenY));
            return;
        }

        var nx = (int)Math.Round((screenX - vx) * 65535.0 / vw);
        var ny = (int)Math.Round((screenY - vy) * 65535.0 / vh);

        var input = new NativeMethods.INPUT
        {
            type = NativeMethods.INPUT_MOUSE,
            U = new NativeMethods.InputUnion
            {
                mi = new NativeMethods.MOUSEINPUT
                {
                    dx = nx,
                    dy = ny,
                    mouseData = 0,
                    dwFlags = NativeMethods.MOUSEEVENTF_MOVE
                            | NativeMethods.MOUSEEVENTF_ABSOLUTE
                            | NativeMethods.MOUSEEVENTF_VIRTUALDESK,
                    time = 0,
                    dwExtraInfo = IntPtr.Zero,
                },
            },
        };

        NativeMethods.SendInput(1, new[] { input }, Marshal.SizeOf<NativeMethods.INPUT>());
    }

    private sealed class CursorRestore(NativeMethods.POINT previous) : IDisposable
    {
        public void Dispose() => NativeMethods.SetCursorPos(previous.X, previous.Y);
    }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();
        public void Dispose() { }
    }

    /// <summary>
    /// Win32 <c>SendInput</c> P/Invoke surface (the INPUT/MOUSEINPUT/KEYBDINPUT structs and
    /// the extern), per the Stage-B contract. Used here only to park the real cursor over
    /// the gesture point; the synthetic gesture itself is delivered via WPF routed events.
    /// </summary>
    private static class NativeMethods
    {
        public const int INPUT_MOUSE = 0;
        public const int INPUT_KEYBOARD = 1;

        public const uint MOUSEEVENTF_MOVE = 0x0001;
        public const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
        public const uint MOUSEEVENTF_LEFTUP = 0x0004;
        public const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
        public const uint MOUSEEVENTF_RIGHTUP = 0x0010;
        public const uint MOUSEEVENTF_WHEEL = 0x0800;
        public const uint MOUSEEVENTF_ABSOLUTE = 0x8000;
        public const uint MOUSEEVENTF_VIRTUALDESK = 0x4000;

        public const uint KEYEVENTF_KEYUP = 0x0002;
        public const uint KEYEVENTF_UNICODE = 0x0004;

        public const int WHEEL_DELTA = 120;

        public const int SM_XVIRTUALSCREEN = 76;
        public const int SM_YVIRTUALSCREEN = 77;
        public const int SM_CXVIRTUALSCREEN = 78;
        public const int SM_CYVIRTUALSCREEN = 79;

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MOUSEINPUT
        {
            public int dx;
            public int dy;
            public uint mouseData;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct KEYBDINPUT
        {
            public ushort wVk;
            public ushort wScan;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct HARDWAREINPUT
        {
            public uint uMsg;
            public ushort wParamL;
            public ushort wParamH;
        }

        [StructLayout(LayoutKind.Explicit)]
        public struct InputUnion
        {
            [FieldOffset(0)] public MOUSEINPUT mi;
            [FieldOffset(0)] public KEYBDINPUT ki;
            [FieldOffset(0)] public HARDWAREINPUT hi;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct INPUT
        {
            public int type;
            public InputUnion U;
        }

        [DllImport("user32.dll", SetLastError = true)]
        public static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetCursorPos(out POINT lpPoint);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetCursorPos(int x, int y);

        [DllImport("user32.dll")]
        public static extern int GetSystemMetrics(int nIndex);
    }
}
