namespace DeadworksManaged.Api;

/// <summary>
/// One plugin's key bindings, created with <see cref="Input.For"/>. Register
/// keys with <see cref="On(Key, Action{KeyEvent})"/> or <see cref="Block(Key)"/>;
/// everything is unbound automatically when the plugin unloads.
/// </summary>
public sealed class InputBinds : IDisposable
{
    private sealed record Bind(Key Key, KeyPolicy Policy, ulong Mask, Action<KeyEvent>? Handler);

    /// <summary>
    /// A policy set by <see cref="SetPolicy"/> on top of the binds — the client
    /// keeps one policy per key, so what matters is that these are always sent
    /// after the base binds, both when set and when replayed to a joiner.
    /// </summary>
    private sealed record Override(Key Key, KeyPolicy Policy, ulong Mask);

    private readonly IDeadworksPlugin _plugin;
    private readonly object _lock = new();
    private readonly List<Bind> _binds = new();
    private readonly List<Override> _overrides = new();
    private bool _disposed;

    internal InputBinds(IDeadworksPlugin plugin) => _plugin = plugin;

    /// <summary>Whether these binds belong to the given plugin.</summary>
    internal bool Owns(IDeadworksPlugin plugin) => ReferenceEquals(_plugin, plugin);

    /// <summary>
    /// Calls <paramref name="handler"/> whenever the key is pressed or
    /// released. The game still acts on the key — use
    /// <see cref="On(Key, KeyPolicy, Action{KeyEvent}, RecipientFilter?)"/>
    /// with <see cref="KeyPolicy.BlockAndListen"/> to take it over instead.
    /// </summary>
    /// <returns>A handle that unbinds this key when cancelled.</returns>
    public IHandle On(Key key, Action<KeyEvent> handler)
        => On(key, KeyPolicy.Listen, handler, null);

    /// <summary>
    /// Calls <paramref name="handler"/> for the key, with control over whether
    /// the game still gets it.
    /// </summary>
    /// <param name="key">The key or mouse button.</param>
    /// <param name="policy">Whether the game still acts on the key.</param>
    /// <param name="handler">Called on press and release.</param>
    /// <param name="recipients">Who to bind it for; everyone by default.</param>
    /// <returns>A handle that unbinds this key when cancelled.</returns>
    public IHandle On(Key key, KeyPolicy policy, Action<KeyEvent> handler, RecipientFilter? recipients = null)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return Add(key, policy, handler, recipients);
    }

    /// <summary>
    /// Stops the game from acting on a key, without being told about it.
    /// </summary>
    /// <param name="key">The key or mouse button to swallow.</param>
    /// <param name="recipients">Who to block it for; everyone by default.</param>
    /// <returns>A handle that unbinds this key when cancelled.</returns>
    public IHandle Block(Key key, RecipientFilter? recipients = null)
        => Add(key, KeyPolicy.Block, null, recipients);

    /// <summary>
    /// Changes whether the game acts on a key that is already bound, for some
    /// or all players — the way to block a key only in certain circumstances.
    /// The handler registered with <see cref="On(Key, Action{KeyEvent})"/> is
    /// untouched; only the policy changes.
    /// </summary>
    /// <param name="key">The key or mouse button.</param>
    /// <param name="policy">The policy to apply from now on.</param>
    /// <param name="recipients">
    /// Who it applies to; everyone by default. A per-player override outlives
    /// nothing — it is dropped when that player disconnects, so a slot's next
    /// occupant starts from the plugin's base policy.
    /// </param>
    /// <remarks>
    /// <para>Blocking is enforced on the client, at the moment of the press, so
    /// this must be set <em>before</em> the press it should affect — a server
    /// handler cannot decide per press, because by then the game has already
    /// acted on the key. Drive it from the state that makes the key special:
    /// a menu opening, a round starting, an ability going on cooldown.</para>
    /// <code>
    /// keys.On(Key.Tab, KeyPolicy.Listen, ShowInfo);              // normally the game's
    /// keys.SetPolicy(Key.Tab, KeyPolicy.BlockAndListen,
    ///                RecipientFilter.Single(slot));               // ...mine while a menu is open
    /// keys.SetPolicy(Key.Tab, KeyPolicy.Listen, RecipientFilter.Single(slot));   // ...theirs again
    /// </code>
    /// </remarks>
    public void SetPolicy(Key key, KeyPolicy policy, RecipientFilter? recipients = null)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(InputBinds));

        ulong mask = (recipients ?? RecipientFilter.All).Mask;

        lock (_lock)
        {
            // One override per (key, audience): the latest wins, exactly like
            // re-registering a bind does.
            _overrides.RemoveAll(o => o.Key == key && o.Mask == mask);
            _overrides.Add(new Override(key, policy, mask));
        }

        Input.SendBind(key, policy, new RecipientFilter { Mask = mask });
    }

    /// <summary>Unbinds everything this plugin registered. Called by the host on unload.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        List<(Key Key, ulong Mask)> targets = new();
        lock (_lock)
        {
            foreach (var bind in _binds)
                targets.Add((bind.Key, bind.Mask));
            // A key that only ever had an override still has to be released.
            foreach (var over in _overrides)
                targets.Add((over.Key, over.Mask));
            _binds.Clear();
            _overrides.Clear();
        }

        foreach (var (key, mask) in targets.Distinct())
            Input.SendUnbind(key, new RecipientFilter { Mask = mask });

        Input.Forget(this);
    }

    // --- internals ---

    private IHandle Add(Key key, KeyPolicy policy, Action<KeyEvent>? handler, RecipientFilter? recipients)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(InputBinds));

        ulong mask = (recipients ?? RecipientFilter.All).Mask;
        var bind = new Bind(key, policy, mask, handler);

        lock (_lock)
        {
            // Re-registering a key replaces its policy rather than stacking a
            // second one the client would have to reconcile - and drops any
            // SetPolicy override for it, since the plugin is re-declaring how
            // that key behaves. Otherwise the client would take this policy now
            // and the stale override again on the next reconnect.
            _binds.RemoveAll(b => b.Key == key && b.Mask == mask);
            _overrides.RemoveAll(o => o.Key == key);
            _binds.Add(bind);
        }

        Input.SendBind(key, policy, new RecipientFilter { Mask = mask });

        return new CallbackHandle(() =>
        {
            bool removed;
            lock (_lock)
                removed = _binds.Remove(bind);
            if (removed)
                Input.SendUnbind(key, new RecipientFilter { Mask = mask });
        });
    }

    /// <summary>Re-sends the binds that apply to a joining player.</summary>
    internal void ResendTo(int slot)
    {
        if (_disposed || (uint)slot >= Players.MaxSlot)
            return;

        Bind[] binds;
        Override[] overrides;
        lock (_lock)
        {
            binds = _binds.ToArray();
            overrides = _overrides.ToArray();
        }

        ulong bit = 1ul << slot;

        // Base policies first, overrides second: the client keeps one policy
        // per key, so the last message for a key is the one that counts.
        foreach (var bind in binds)
            if ((bind.Mask & bit) != 0)
                Input.SendBind(bind.Key, bind.Policy, RecipientFilter.Single(slot));

        foreach (var over in overrides)
            if ((over.Mask & bit) != 0)
                Input.SendBind(over.Key, over.Policy, RecipientFilter.Single(slot));
    }

    /// <summary>
    /// A player left: forget the overrides aimed at them, so the next player in
    /// that slot starts from the plugin's base policies rather than inheriting
    /// a menu-open block from whoever was there before.
    /// </summary>
    internal void DropSlot(int slot)
    {
        if (_disposed || (uint)slot >= Players.MaxSlot)
            return;

        ulong bit = 1ul << slot;
        lock (_lock)
        {
            for (int i = _overrides.Count - 1; i >= 0; i--)
            {
                var over = _overrides[i];
                if ((over.Mask & bit) == 0)
                    continue;

                ulong remaining = over.Mask & ~bit;
                if (remaining == 0)
                    _overrides.RemoveAt(i);
                else
                    _overrides[i] = over with { Mask = remaining };
            }
        }
    }

    /// <summary>Runs the handlers registered for this key, for this player.</summary>
    internal void Deliver(KeyEvent ev)
    {
        if (_disposed)
            return;

        Bind[] binds;
        lock (_lock)
            binds = _binds.ToArray();

        ulong bit = (uint)ev.PlayerSlot < Players.MaxSlot ? 1ul << ev.PlayerSlot : 0;
        foreach (var bind in binds)
        {
            if (bind.Key != ev.Key || bind.Handler is null || (bind.Mask & bit) == 0)
                continue;
            try
            {
                bind.Handler(ev);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Input] handler for {ev.Key} in '{_plugin.Name}' threw: {ex}");
            }
        }
    }
}
