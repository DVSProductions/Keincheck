using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;

namespace AvaloniaMcp.Core;

/// <summary>
/// Low-level synthetic-input engine shared by <see cref="AvaloniaUiAdapter"/>.
/// Fabricates Avalonia routed input event args using the public 12.0.4
/// constructors and raises them on the appropriate element. All methods assume
/// they are already on the UI thread.
/// </summary>
/// <remarks>
/// <para>
/// Fidelity note: a real platform pointer is created by the windowing backend and
/// is wired into Avalonia's <c>PointerDevice</c>/capture machinery. The public API
/// does not expose that pipeline, so we construct a standalone
/// <see cref="Pointer"/> and raise the routed events directly on the hit-tested
/// element. This drives the routed-event side of every control (the part
/// custom-drawn controls actually handle) faithfully — including
/// <c>ClickCount</c>, button state, modifiers and wheel delta — but does not run
/// the backend's implicit pointer capture/over bookkeeping.
/// </para>
/// </remarks>
internal static class SyntheticInput
{
    // A single, reused synthetic mouse pointer. id 0, primary.
    private static readonly Pointer MousePointer = new(0, PointerType.Mouse, isPrimary: true);

    private static ulong Timestamp() => (ulong)Environment.TickCount64;

    /// <summary>Hit-tests <paramref name="point"/> (top-level coordinates) and returns the element, or the top level itself.</summary>
    public static Control? HitTest(TopLevel topLevel, Point point)
    {
        var hit = topLevel.InputHitTest(point) as Control;
        return hit ?? topLevel;
    }

    public static void RaiseMove(TopLevel root, Control target, Point point)
    {
        var props = new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.Other);
        var args = new PointerEventArgs(
            InputElement.PointerMovedEvent,
            target,
            MousePointer,
            root,
            point,
            Timestamp(),
            props,
            KeyModifiers.None);
        target.RaiseEvent(args);
    }

    public static void RaisePressed(TopLevel root, Control target, Point point, MouseButton button, int clickCount)
    {
        var props = new PointerPointProperties(ModifiersFor(button, pressed: true), UpdateKind(button, pressed: true));
        var args = new PointerPressedEventArgs(
            target,
            MousePointer,
            root,
            point,
            Timestamp(),
            props,
            KeyModifiers.None,
            clickCount);

        MousePointer.Capture(target);
        target.RaiseEvent(args);
    }

    public static void RaiseReleased(TopLevel root, Control target, Point point, MouseButton button)
    {
        var props = new PointerPointProperties(RawInputModifiers.None, UpdateKind(button, pressed: false));
        var args = new PointerReleasedEventArgs(
            target,
            MousePointer,
            root,
            point,
            Timestamp(),
            props,
            KeyModifiers.None,
            button);
        target.RaiseEvent(args);
        MousePointer.Capture(null);
    }

    public static void RaiseWheel(TopLevel root, Control target, Point point, Vector delta)
    {
        var props = new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.Other);
        var args = new PointerWheelEventArgs(
            target,
            MousePointer,
            root,
            point,
            Timestamp(),
            props,
            KeyModifiers.None,
            delta);
        target.RaiseEvent(args);
    }

    public static void RaiseText(IInputElement sink, string text)
    {
        var args = new TextInputEventArgs
        {
            RoutedEvent = InputElement.TextInputEvent,
            Source = sink,
            Text = text,
        };
        sink.RaiseEvent(args);
    }

    public static void RaiseKey(IInputElement sink, Key key, KeyModifiers mods, bool down)
    {
        var args = new KeyEventArgs
        {
            RoutedEvent = down ? InputElement.KeyDownEvent : InputElement.KeyUpEvent,
            Source = sink,
            Key = key,
            KeyModifiers = mods,
        };
        sink.RaiseEvent(args);
    }

    private static RawInputModifiers ModifiersFor(MouseButton button, bool pressed)
    {
        if (!pressed)
            return RawInputModifiers.None;
        return button switch
        {
            MouseButton.Left => RawInputModifiers.LeftMouseButton,
            MouseButton.Right => RawInputModifiers.RightMouseButton,
            MouseButton.Middle => RawInputModifiers.MiddleMouseButton,
            MouseButton.XButton1 => RawInputModifiers.XButton1MouseButton,
            MouseButton.XButton2 => RawInputModifiers.XButton2MouseButton,
            _ => RawInputModifiers.None,
        };
    }

    private static PointerUpdateKind UpdateKind(MouseButton button, bool pressed) => button switch
    {
        MouseButton.Left => pressed ? PointerUpdateKind.LeftButtonPressed : PointerUpdateKind.LeftButtonReleased,
        MouseButton.Right => pressed ? PointerUpdateKind.RightButtonPressed : PointerUpdateKind.RightButtonReleased,
        MouseButton.Middle => pressed ? PointerUpdateKind.MiddleButtonPressed : PointerUpdateKind.MiddleButtonReleased,
        MouseButton.XButton1 => pressed ? PointerUpdateKind.XButton1Pressed : PointerUpdateKind.XButton1Released,
        MouseButton.XButton2 => pressed ? PointerUpdateKind.XButton2Pressed : PointerUpdateKind.XButton2Released,
        _ => PointerUpdateKind.Other,
    };

    /// <summary>
    /// Parses a chord like <c>Ctrl+Shift+S</c> or a bare key like <c>Enter</c> into a
    /// <see cref="Key"/> + <see cref="KeyModifiers"/>. Modifier aliases: Ctrl/Control,
    /// Alt, Shift, Win/Cmd/Meta/Super.
    /// </summary>
    public static bool TryParseChord(string token, out Key key, out KeyModifiers mods, out string error)
    {
        key = Key.None;
        mods = KeyModifiers.None;
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
            var p = parts[i];
            var isLast = i == parts.Length - 1;

            if (!isLast)
            {
                if (!TryModifier(p, out var m))
                {
                    error = $"'{p}' is not a recognized modifier.";
                    return false;
                }
                mods |= m;
                continue;
            }

            if (TryKey(p, out var k))
            {
                key = k;
                return true;
            }

            if (TryModifier(p, out var trailingMod))
            {
                mods |= trailingMod;
                error = "chord contains modifiers but no non-modifier key.";
                return false;
            }

            error = $"'{p}' is not a recognized key.";
            return false;
        }

        error = "no key found.";
        return false;
    }

    private static bool TryModifier(string s, out KeyModifiers mod)
    {
        switch (s.ToLowerInvariant())
        {
            case "ctrl":
            case "control":
                mod = KeyModifiers.Control;
                return true;
            case "alt":
                mod = KeyModifiers.Alt;
                return true;
            case "shift":
                mod = KeyModifiers.Shift;
                return true;
            case "win":
            case "cmd":
            case "meta":
            case "super":
                mod = KeyModifiers.Meta;
                return true;
            default:
                mod = KeyModifiers.None;
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

        if (s.Length == 1 && s[0] >= '0' && s[0] <= '9')
            return Enum.TryParse("D" + s, ignoreCase: true, out key);

        return Enum.TryParse(s, ignoreCase: true, out key) && key != Key.None;
    }
}
