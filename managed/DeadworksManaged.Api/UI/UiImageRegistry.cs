namespace DeadworksManaged.Api;

/// <summary>
/// Every <see cref="UiImageServer"/> on the server, merged into the one images
/// pack a client mounts.
/// </summary>
/// <remarks>
/// <para>The client module has a single images mount, so two plugins each
/// pushing their own pack would replace each other. This packs them together
/// instead: each server's folder becomes a subfolder named after its app, so
/// two plugins can both ship a <c>logo.png</c>. The <c>dw://logo.png</c> in an
/// app's own trees is namespaced to match on the way out
/// (<see cref="UiTreeCodec.NamespaceSrc"/>), so nothing an author writes
/// changes.</para>
/// <para>Any server's edit rebuilds the merged pack; a player is only sent it
/// when its hash differs from what they already have, so one plugin editing a
/// PNG does not re-send another plugin's images to anyone who already has
/// them.</para>
/// </remarks>
internal static class UiImageRegistry
{
	/// <summary>The pack id every merged push uses; the client keys its one mount by it.</summary>
	internal const string PackId = "dwimages";

	private static readonly object _lock = new();
	private static readonly List<UiImageServer> _servers = new();

	private static UiImagePackResult? _merged;
	private static bool _stale = true;

	/// <summary>What each player last received, so nothing is pushed twice.</summary>
	private static readonly string?[] _delivered = new string?[Players.MaxSlot];

	/// <summary>
	/// Who is currently being served, from the serving apps' own connect
	/// forwards rather than global connection state — a repush has to reach
	/// exactly the players a push reached.
	/// </summary>
	private static readonly bool[] _connected = new bool[Players.MaxSlot];

	internal static void Register(UiImageServer server)
	{
		lock (_lock)
		{
			_servers.Add(server);
			_stale = true;
		}
	}

	internal static void Unregister(UiImageServer server)
	{
		lock (_lock)
		{
			_servers.Remove(server);
			_stale = true;
		}
	}

	/// <summary>A folder changed: the merged pack has to be rebuilt before the next push.</summary>
	internal static void Invalidate()
	{
		lock (_lock)
			_stale = true;
	}

	/// <summary>The player left; their next connection starts from nothing.</summary>
	internal static void DropSlot(int slot)
	{
		if ((uint)slot >= Players.MaxSlot)
			return;
		lock (_lock)
		{
			_delivered[slot] = null;
			_connected[slot] = false;
		}
	}

	/// <summary>
	/// Sends the merged pack to one player unless they already have exactly
	/// it. Called when a player connects and whenever any folder changes.
	/// </summary>
	internal static void PushTo(int slot)
	{
		if ((uint)slot >= Players.MaxSlot)
			return;

		lock (_lock)
			_connected[slot] = true;

		UiImagePackResult? pack = Merged();
		if (pack is null)
			return;

		lock (_lock)
		{
			if (_delivered[slot] == pack.Sha256)
				return;
			_delivered[slot] = pack.Sha256;
		}

		UI.PushPack(PackId, pack.Vpk, pack.Sha256, RecipientFilter.Single(slot));
	}

	/// <summary>Re-sends to everyone holding an older pack after a rebuild.</summary>
	internal static void PushToConnected()
	{
		int[] slots;
		lock (_lock)
			slots = Enumerable.Range(0, Players.MaxSlot).Where(s => _connected[s]).ToArray();

		foreach (int slot in slots)
			PushTo(slot);
	}

	/// <summary>The current merged pack, rebuilt if any folder changed since the last one.</summary>
	internal static UiImagePackResult? Merged()
	{
		List<(string Folder, IReadOnlyDictionary<string, byte[]> Pngs)> sets;
		lock (_lock)
		{
			if (!_stale)
				return _merged;

			sets = new List<(string, IReadOnlyDictionary<string, byte[]>)>(_servers.Count);
			foreach (var server in _servers)
			{
				var pngs = server.CurrentPngs;
				if (pngs.Count > 0)
					sets.Add((server.Folder, pngs));
			}
			_stale = false;
		}

		if (sets.Count == 0)
		{
			lock (_lock)
				return _merged = null;
		}

		try
		{
			var built = UiImagePack.BuildMerged(sets);
			lock (_lock)
				return _merged = built;
		}
		catch (Exception ex)
		{
			// A broken PNG in one folder must not take everyone's images down:
			// keep serving the last good pack.
			Console.WriteLine($"[UI] images not packed: {ex.Message}");
			lock (_lock)
				return _merged;
		}
	}

	/// <summary>Drops every registration; for tests only.</summary>
	internal static void ResetForTests()
	{
		lock (_lock)
		{
			_servers.Clear();
			_merged = null;
			_stale = true;
			Array.Clear(_delivered);
			Array.Clear(_connected);
		}
	}
}
