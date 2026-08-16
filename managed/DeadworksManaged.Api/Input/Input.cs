using Google.Protobuf;

namespace DeadworksManaged.Api;

/// <summary>
/// Listens for keys and mouse buttons the game does not otherwise send to the
/// server — including ones bound to nothing — and optionally stops the game
/// from acting on them.
/// </summary>
/// <remarks>
/// <para>Usage is two lines:</para>
/// <code>
/// var keys = Input.For(this);
/// keys.On(Key.K, e => Chat.PrintToChat(e.PlayerSlot, $"K {e.Edge}"));
/// keys.On(Key.Tab, KeyPolicy.BlockAndListen, e => ShowMyScoreboard(e.PlayerSlot));
/// keys.On(Key.WheelUp, KeyPolicy.BlockAndListen, e => ScrollMyPanel(e.PlayerSlot, -1));
/// keys.Block(Key.F5);
/// </code>
/// <para>Blocking is decided on the client, up front: the server registers the
/// keys it cares about and the client module enforces that instantly when the
/// key is pressed. It cannot be decided per press after the fact, because by
/// then the game has already acted on the key. Change your mind whenever you
/// like — registering again replaces the policy.</para>
/// <para>Keys never fire while the player is typing (chat, console, a text
/// box), so a blocked key can't eat someone's message.</para>
/// <para>Only players running the Deadworks client module report keys; for
/// everyone else this is silently inert, so treat key input as an enhancement
/// rather than something your mode depends on.</para>
/// </remarks>
public static class Input
{
    /// <summary>Sent to clients to add or replace a key's policy.</summary>
    private const string EventBind = "dw.input.bind";

    /// <summary>Sent to clients to forget a key.</summary>
    private const string EventUnbind = "dw.input.unbind";

    /// <summary>The event name clients report presses under (client → server, message 280).</summary>
    internal const string EventKey = "dw.input.key";

    private static readonly object _lock = new();
    private static readonly List<InputBinds> _all = new();

    /// <summary>
    /// Key bindings owned by <paramref name="plugin"/>. Everything registered
    /// on the result is unbound automatically when the plugin unloads.
    /// </summary>
    public static InputBinds For(IDeadworksPlugin plugin)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        var binds = new InputBinds(plugin);
        lock (_lock)
            _all.Add(binds);
        return binds;
    }

    internal static void Forget(InputBinds binds)
    {
        lock (_lock)
            _all.Remove(binds);
    }

    /// <summary>Re-sends every live binding to a joining player. Called by the host.</summary>
    public static void HandleConnect(int slot)
    {
        InputBinds[] all;
        lock (_lock)
            all = _all.ToArray();

        foreach (var binds in all)
            binds.ResendTo(slot);
    }

    /// <summary>
    /// A player disconnected: drops the per-player policy overrides aimed at
    /// that slot (see <see cref="InputBinds.SetPolicy"/>), so its next occupant
    /// is not handed the previous player's blocks. Called by the host.
    /// </summary>
    public static void HandleDisconnect(int slot)
    {
        InputBinds[] all;
        lock (_lock)
            all = _all.ToArray();

        foreach (var binds in all)
            binds.DropSlot(slot);
    }

    /// <summary>
    /// Entry point for a key report from a client (<c>dw.input.key</c> over
    /// message 280), called by the host once per report.
    /// </summary>
    /// <param name="slot">Slot of the player whose connection it arrived on.</param>
    /// <param name="payload">The raw <see cref="UiPayload"/> body.</param>
    /// <remarks>Every step fails closed: this is client-supplied data and must
    /// never throw back into the native caller.</remarks>
    public static void HandleKeyReport(int slot, ReadOnlySpan<byte> payload)
    {
        var entries = UiPayload.Parse(payload);
        if (entries is null)
            return;

        if (!int.TryParse(UiPayload.Find(entries, "key"), out int key) ||
            !int.TryParse(UiPayload.Find(entries, "edge"), out int edge) ||
            !int.TryParse(UiPayload.Find(entries, "mods"), out int mods))
            return;
        if (edge is not (0 or 1))
            return;

        var ev = new KeyEvent
        {
            PlayerSlot = slot,
            Key = (Key)key,
            Edge = (KeyEdge)edge,
            Modifiers = (KeyModifiers)(mods & 0x7),
        };

        InputBinds[] all;
        lock (_lock)
            all = _all.ToArray();

        foreach (var binds in all)
            binds.Deliver(ev);
    }

    /// <summary>
    /// Host entry: a plugin is unloading — unbind everything it registered,
    /// before its own <c>OnUnload</c> runs, so a plugin that forgets to dispose
    /// cannot leave keys bound (or blocked) on clients.
    /// </summary>
    internal static void DisposePlugin(IDeadworksPlugin plugin)
    {
        InputBinds[] owned;
        lock (_lock)
            owned = _all.Where(b => b.Owns(plugin)).ToArray();

        foreach (var binds in owned)
        {
            try
            {
                binds.Dispose();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Input] disposing binds for '{plugin.Name}' threw: {ex}");
            }
        }
    }

    /// <summary>Drops every registration; for tests only.</summary>
    internal static void ResetForTests()
    {
        lock (_lock)
            _all.Clear();
    }

    // --- wire ---

    internal static void SendBind(Key key, KeyPolicy policy, RecipientFilter recipients)
        => Send(EventBind, new UiPayload()
            .Add("key", ((int)key).ToString())
            .Add("policy", ((int)policy).ToString()), recipients);

    internal static void SendUnbind(Key key, RecipientFilter recipients)
        => Send(EventUnbind, new UiPayload().Add("key", ((int)key).ToString()), recipients);

    private static void Send(string eventName, UiPayload payload, RecipientFilter recipients)
        => NetMessages.Send(new CUserMsg_CustomGameEvent
        {
            EventName = eventName,
            Data = ByteString.CopyFrom(payload.ToBytes()),
        }, recipients);
}
