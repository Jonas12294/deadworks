namespace DeadworksManaged.Api;

/// <summary>
/// A hot-watched folder of PNGs served as a pushed images pack, created with
/// <see cref="UiApp.ServeImages"/>. Drop <c>name.png</c> into the folder and
/// panels can show it as <c>&lt;img src="dw://name.png"&gt;</c> (or the full
/// <see cref="Paths"/> value) — no VPK hosting anywhere: the server compiles
/// the textures in memory (<see cref="UiImagePack"/>) and delivers the pack
/// over the data channel for the client module (v27+) to mount.
/// </summary>
/// <remarks>
/// <para>The pack is pushed to each player on connect, ahead of any state —
/// images must be mounted before a tree references them, because the client
/// resolves an image when a tree next uses it (no cache eviction involved).
/// Edits are polled once a second: a change rebuilds the pack and repushes it
/// to everyone connected; a broken edit (undecodable PNG) is logged and the
/// last good pack stays live.</para>
/// <para>Image names are the file names without extension and must be plain
/// (no dots); files that don't qualify are skipped with a log line. The client
/// mounts a single images pack, so every serving app's folder is merged into
/// one (<see cref="UiImageRegistry"/>) under a subfolder named after the app —
/// two plugins can each ship a <c>logo.png</c>, and <c>dw://logo.png</c> in an
/// app's own trees resolves to its own.</para>
/// </remarks>
public sealed class UiImageServer : IHandle
{
	private readonly UiApp _app;
	private readonly string _root;
	private readonly IHandle _poll;

	private readonly bool[] _connected = new bool[Players.MaxSlot];

	private Dictionary<string, byte[]> _pngs = new(StringComparer.Ordinal);
	private List<(string Path, long WriteTicks, long Length)> _stamp;
	private bool _cancelled;

	private static readonly IReadOnlyDictionary<string, string> NoPaths = new Dictionary<string, string>();

	/// <summary>The subfolder this app's images live under in the merged pack.</summary>
	internal string Folder => _app.AppId;

	/// <summary>This folder's images, for the registry to merge.</summary>
	internal IReadOnlyDictionary<string, byte[]> CurrentPngs => _pngs;

	/// <summary>Image name → the <c>s2r://…vtex</c> path a tree or markup src
	/// uses. <c>dw://&lt;name&gt;.png</c> is the shorthand for the same thing
	/// and is namespaced to this app automatically.</summary>
	public IReadOnlyDictionary<string, string> Paths
	{
		get
		{
			var merged = UiImageRegistry.Merged();
			if (merged is null)
				return NoPaths;

			// The merged pack keys every path by "<app>/<name>"; this app sees
			// its own images under their plain names.
			var mine = new Dictionary<string, string>(StringComparer.Ordinal);
			string prefix = Folder + "/";
			foreach (var (key, path) in merged.Paths)
				if (key.StartsWith(prefix, StringComparison.Ordinal))
					mine[key[prefix.Length..]] = path;
			return mine;
		}
	}

	/// <summary>SHA-256 of the merged pack currently served, or null while no app has images.</summary>
	public string? Sha256 => UiImageRegistry.Merged()?.Sha256;

	internal UiImageServer(UiApp app, ITimer timer, string root)
	{
		_app = app;
		_root = root;

		// Pre-create the folder so "drop a PNG in" works from a fresh install.
		try { Directory.CreateDirectory(root); }
		catch (Exception ex) { Console.WriteLine($"[UI] images folder '{root}': {ex.Message}"); }

		_stamp = Stamp();
		_pngs = ReadFolder() ?? new Dictionary<string, byte[]>(StringComparer.Ordinal);
		UiImageRegistry.Register(this);

		app.SlotConnected += OnSlotConnected;
		app.SlotDisconnected += OnSlotDisconnected;
		_poll = timer.Every(1.Seconds(), Poll);

		// A server created mid-match (plugin load or reload) never sees another
		// full connect for the players already in the server — treat them as
		// connecting now, exactly like UI.CreateApp does for the bundle.
		for (int slot = 0; slot < Players.MaxSlot; slot++)
			if (Players.IsConnected(slot))
				OnSlotConnected(slot);
	}

	/// <summary>Whether <see cref="Cancel"/> has run.</summary>
	public bool IsFinished => _cancelled;

	/// <inheritdoc/>
	public IHandle CancelOnMapChange() => this;

	/// <summary>Stops serving: mounted packs stay with their clients, nothing further is pushed.</summary>
	public void Cancel()
	{
		if (_cancelled)
			return;
		_cancelled = true;
		_app.SlotConnected -= OnSlotConnected;
		_app.SlotDisconnected -= OnSlotDisconnected;
		_poll.Cancel();
		UiImageRegistry.Unregister(this);
	}

	// --- internals ---

	private void OnSlotConnected(int slot)
	{
		if (_cancelled || (uint)slot >= Players.MaxSlot)
			return;
		_connected[slot] = true;
		// The registry knows what this player already has, so a second app's
		// connect forward is a no-op rather than a duplicate pack.
		UiImageRegistry.PushTo(slot);
	}

	private void OnSlotDisconnected(int slot)
	{
		if ((uint)slot >= Players.MaxSlot)
			return;
		_connected[slot] = false;
		UiImageRegistry.DropSlot(slot);
	}

	/// <summary>Hot reload: re-read on any file change and repush a changed pack.</summary>
	private void Poll()
	{
		if (_cancelled)
			return;

		var stamp = Stamp();
		if (stamp.SequenceEqual(_stamp))
			return;
		_stamp = stamp;

		var pngs = ReadFolder();
		if (pngs is null)
			return;   // broken read, logged; keep serving what we had

		_pngs = pngs;
		UiImageRegistry.Invalidate();

		var merged = UiImageRegistry.Merged();
		UiImageRegistry.PushToConnected();
		if (merged is not null)
			Console.WriteLine($"[UI] '{_app.AppId}' images changed ({_pngs.Count} file(s); " +
				$"pack now {merged.Paths.Count} file(s), {merged.Vpk.Length} bytes)");
	}

	/// <summary>Reads the folder's PNGs; null when the read itself failed (logged).</summary>
	private Dictionary<string, byte[]>? ReadFolder()
	{
		var pngs = new Dictionary<string, byte[]>(StringComparer.Ordinal);
		try
		{
			foreach (string file in Directory.GetFiles(_root, "*.png"))
			{
				string name = Path.GetFileNameWithoutExtension(file);
				if (name.Length == 0 || name.Contains('.'))
				{
					Console.WriteLine($"[UI] images: skipping '{Path.GetFileName(file)}' - the name must be plain (no dots)");
					continue;
				}
				pngs[name] = File.ReadAllBytes(file);
			}
			return pngs;
		}
		catch (Exception ex)
		{
			Console.WriteLine($"[UI] images folder '{_root}' not read: {ex.Message}");
			return null;
		}
	}

	/// <summary>The folder's change fingerprint: every PNG with its write time and size.</summary>
	private List<(string, long, long)> Stamp()
	{
		var stamp = new List<(string, long, long)>();
		try
		{
			foreach (string file in Directory.GetFiles(_root, "*.png").OrderBy(f => f, StringComparer.Ordinal))
			{
				var info = new FileInfo(file);
				stamp.Add((file, info.LastWriteTimeUtc.Ticks, info.Length));
			}
		}
		catch { /* transient IO while an editor saves */ }
		return stamp;
	}
}
