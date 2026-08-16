namespace DeadworksManaged.Api;

/// <summary>
/// A key or mouse button that can be listened for or blocked with
/// <see cref="Input"/>. Values are Windows virtual-key codes, which is exactly
/// what the client module compares against, so the enum value is also the wire
/// value. The wheel directions above <c>0xFF</c> are the exception — Windows
/// has no virtual key for the wheel, so these are Deadworks codes chosen past
/// the end of the VK range where they cannot collide with a real key.
/// </summary>
public enum Key
{
    /// <summary>Middle mouse button.</summary>
    Mouse3 = 0x04,
    /// <summary>Mouse button 4 (thumb, back).</summary>
    Mouse4 = 0x05,
    /// <summary>Mouse button 5 (thumb, forward).</summary>
    Mouse5 = 0x06,

    Backspace = 0x08,
    Tab = 0x09,
    Enter = 0x0D,
    Shift = 0x10,
    Ctrl = 0x11,
    Alt = 0x12,
    Pause = 0x13,
    CapsLock = 0x14,
    Escape = 0x1B,
    Space = 0x20,
    PageUp = 0x21,
    PageDown = 0x22,
    End = 0x23,
    Home = 0x24,
    Left = 0x25,
    Up = 0x26,
    Right = 0x27,
    Down = 0x28,
    PrintScreen = 0x2C,
    Insert = 0x2D,
    Delete = 0x2E,

    D0 = 0x30, D1 = 0x31, D2 = 0x32, D3 = 0x33, D4 = 0x34,
    D5 = 0x35, D6 = 0x36, D7 = 0x37, D8 = 0x38, D9 = 0x39,

    A = 0x41, B = 0x42, C = 0x43, D = 0x44, E = 0x45, F = 0x46,
    G = 0x47, H = 0x48, I = 0x49, J = 0x4A, K = 0x4B, L = 0x4C,
    M = 0x4D, N = 0x4E, O = 0x4F, P = 0x50, Q = 0x51, R = 0x52,
    S = 0x53, T = 0x54, U = 0x55, V = 0x56, W = 0x57, X = 0x58,
    Y = 0x59, Z = 0x5A,

    Numpad0 = 0x60, Numpad1 = 0x61, Numpad2 = 0x62, Numpad3 = 0x63,
    Numpad4 = 0x64, Numpad5 = 0x65, Numpad6 = 0x66, Numpad7 = 0x67,
    Numpad8 = 0x68, Numpad9 = 0x69,
    NumpadMultiply = 0x6A,
    NumpadAdd = 0x6B,
    NumpadSubtract = 0x6D,
    NumpadDecimal = 0x6E,
    NumpadDivide = 0x6F,

    F1 = 0x70, F2 = 0x71, F3 = 0x72, F4 = 0x73, F5 = 0x74, F6 = 0x75,
    F7 = 0x76, F8 = 0x77, F9 = 0x78, F10 = 0x79, F11 = 0x7A, F12 = 0x7B,

    NumLock = 0x90,
    ScrollLock = 0x91,

    /// <summary><c>;:</c> on a US layout.</summary>
    Semicolon = 0xBA,
    Plus = 0xBB,
    Comma = 0xBC,
    Minus = 0xBD,
    Period = 0xBE,
    /// <summary><c>/?</c> on a US layout.</summary>
    Slash = 0xBF,
    /// <summary><c>`~</c> on a US layout - the console key.</summary>
    Tilde = 0xC0,
    LeftBracket = 0xDB,
    Backslash = 0xDC,
    RightBracket = 0xDD,
    Quote = 0xDE,

    // --- mouse wheel (Deadworks codes, not Windows VKs; client module v29+) ---
    //
    // A wheel notch is instantaneous: it reports KeyEdge.Down only, once per
    // notch, and never a release. Blocking one stops the game scrolling — which
    // is what makes the wheel usable for your own UI.

    /// <summary>Wheel scrolled up (away from the user), one notch per event.</summary>
    WheelUp = 0x100,

    /// <summary>Wheel scrolled down (toward the user), one notch per event.</summary>
    WheelDown = 0x101,

    /// <summary>Tilt wheel scrolled left, one notch per event.</summary>
    WheelLeft = 0x102,

    /// <summary>Tilt wheel scrolled right, one notch per event.</summary>
    WheelRight = 0x103,
}

/// <summary>What the client should do with a registered key.</summary>
public enum KeyPolicy
{
    /// <summary>The game still acts on the key, and the server is told about it.</summary>
    Listen = 0,

    /// <summary>Swallow the key locally; the game never sees it and the server is not told.</summary>
    Block = 1,

    /// <summary>Swallow the key locally <em>and</em> tell the server about it.</summary>
    BlockAndListen = 2,
}

/// <summary>Which half of a key press an event describes.</summary>
public enum KeyEdge
{
    /// <summary>
    /// The key was pressed. Auto-repeat while held is suppressed. The wheel
    /// directions only ever report this edge — a notch has no release.
    /// </summary>
    Down = 0,

    /// <summary>The key was released.</summary>
    Up = 1,
}

/// <summary>Modifier keys held at the moment a key event happened.</summary>
[Flags]
public enum KeyModifiers
{
    None = 0,
    Shift = 1,
    Ctrl = 2,
    Alt = 4,
}

/// <summary>A key press or release reported by a player's client.</summary>
/// <remarks>
/// This arrives from a client, so treat it the way you would any other client
/// input. <see cref="PlayerSlot"/> is authoritative — it comes from the
/// connection the message arrived on — but the rest is what the client claims.
/// </remarks>
public sealed class KeyEvent
{
    /// <summary>The player who pressed it.</summary>
    public required int PlayerSlot { get; init; }

    /// <summary>The key or mouse button.</summary>
    public required Key Key { get; init; }

    /// <summary>Press or release.</summary>
    public required KeyEdge Edge { get; init; }

    /// <summary>Modifiers held at the time.</summary>
    public required KeyModifiers Modifiers { get; init; }

    /// <summary>The player's controller, if they still have one.</summary>
    public CCitadelPlayerController? Controller => Players.FromSlot(PlayerSlot);
}
