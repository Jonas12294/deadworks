namespace DeadworksManaged.Api;

/// <summary>
/// The apps sharing one panel on a player's screen — everything published from
/// the same bundle, most often several plugins on the host bundle.
/// </summary>
/// <remarks>
/// <para>One panel means one container, and the host runtime rebuilds that
/// container wholesale on every tree push. So the trees are composed
/// <em>here</em>, on the server: each app keeps its own subtree per player and
/// the group pushes them as one forest. Every root goes out with parent
/// <c>-1</c>, which the runtime already parents straight to its container, so
/// several apps on one panel need no client change at all.</para>
/// <para>The group also stops the shared work from being done twice per
/// player: publishing the bundle, and installing the panel-side helpers.</para>
/// <para>What it deliberately does not do is isolate node ids. The client's
/// id map is flat, so two apps using the same <see cref="UiNode.Id"/> would
/// fight over <see cref="UiApp.UpdateNode"/> — the group detects that and logs
/// it naming both plugins, rather than silently picking a winner.</para>
/// </remarks>
internal sealed class UiHostGroup
{
	private static readonly object _lock = new();
	private static readonly Dictionary<string, UiHostGroup> _groups = new(StringComparer.Ordinal);

	private readonly string _key;
	private readonly List<UiApp> _apps = new();

	/// <summary>Which app published the bundle for a connection, so a second app does not republish it.</summary>
	private readonly UiApp?[] _publisher = new UiApp?[Players.MaxSlot];

	/// <summary>Which app installed the panel helpers for the current announcement.</summary>
	private readonly UiApp?[] _runtimeOwner = new UiApp?[Players.MaxSlot];

	private int _rev;

	private UiHostGroup(string key) => _key = key;

	/// <summary>Joins the group for a panel; the key is the bundle the app publishes.</summary>
	internal static UiHostGroup Join(string key, UiApp app)
	{
		lock (_lock)
		{
			if (!_groups.TryGetValue(key, out var group))
				_groups[key] = group = new UiHostGroup(key);
			group._apps.Add(app);
			return group;
		}
	}

	/// <summary>Leaves the group. The remaining apps re-compose so the departing app's UI goes away.</summary>
	internal void Leave(UiApp app)
	{
		int[] slots;
		lock (_lock)
		{
			_apps.Remove(app);
			for (int slot = 0; slot < _publisher.Length; slot++)
			{
				if (ReferenceEquals(_publisher[slot], app)) _publisher[slot] = null;
				if (ReferenceEquals(_runtimeOwner[slot], app)) _runtimeOwner[slot] = null;
			}
			if (_apps.Count == 0)
			{
				_groups.Remove(_key);
				return;
			}
			slots = SlotsWithContent();
		}

		// A plugin unloading mid-match must take its panels with it, which for
		// the players still looking at them means a push without its subtree.
		foreach (int slot in slots)
			Push(slot);
	}

	/// <summary>
	/// Whether this app should publish the bundle to this player. The first
	/// app to ask owns it for the connection; the rest would be sending the
	/// same content to the same panel.
	/// </summary>
	internal bool ClaimPublish(int slot, UiApp app)
	{
		if ((uint)slot >= Players.MaxSlot)
			return false;
		lock (_lock)
		{
			if (_publisher[slot] is not null && !ReferenceEquals(_publisher[slot], app))
				return false;
			_publisher[slot] = app;
			return true;
		}
	}

	/// <summary>Whether this app should install the panel helpers for this announcement.</summary>
	internal bool ClaimRuntime(int slot, UiApp app)
	{
		if ((uint)slot >= Players.MaxSlot)
			return false;
		lock (_lock)
		{
			if (_runtimeOwner[slot] is not null && !ReferenceEquals(_runtimeOwner[slot], app))
				return false;
			_runtimeOwner[slot] = app;
			return true;
		}
	}

	/// <summary>The player left (or reconnected): the shared claims start over.</summary>
	internal void ReleaseSlot(int slot)
	{
		if ((uint)slot >= Players.MaxSlot)
			return;
		lock (_lock)
		{
			_publisher[slot] = null;
			_runtimeOwner[slot] = null;
		}
	}

	/// <summary>
	/// Sends the whole composition for one player: every app's current subtree
	/// as one tree. Called whenever any app in the group changes what it shows.
	/// </summary>
	internal void Push(int slot)
	{
		List<(UiNode Root, string? ImagePrefix)> roots;
		int rev;
		lock (_lock)
		{
			roots = new List<(UiNode, string?)>(_apps.Count);
			foreach (var app in _apps)
			{
				var root = app.TreeFor(slot);
				if (root is not null)
					roots.Add((root, app.ImageNamespace));
			}
			rev = ++_rev;
		}

		WarnOnSharedNodeIds(roots.Count);

		// Everything gone (the last app dropped its tree) still ships: an empty
		// tree is how the panel is cleared.
		foreach (var chunk in UiTreeCodec.Encode(roots, rev))
			UI.Emit(UiTreeCodec.SetEvent, chunk.ToData(), RecipientFilter.Single(slot));
	}

	/// <summary>Slots any app in the group currently shows something on.</summary>
	private int[] SlotsWithContent()
	{
		var slots = new List<int>();
		for (int slot = 0; slot < Players.MaxSlot; slot++)
			foreach (var app in _apps)
				if (app.TreeFor(slot) is not null)
				{
					slots.Add(slot);
					break;
				}
		return slots.ToArray();
	}

	/// <summary>
	/// Node ids are one namespace per panel (the client's id map is flat), so
	/// two apps using the same id would fight over updates. Logged once per
	/// colliding id, naming both owners, since the fix is a rename in one of
	/// the two plugins.
	/// </summary>
	private readonly HashSet<string> _reportedCollisions = new(StringComparer.Ordinal);

	private void WarnOnSharedNodeIds(int rootCount)
	{
		if (rootCount < 2)
			return;

		var owners = new Dictionary<string, string>(StringComparer.Ordinal);
		lock (_lock)
		{
			foreach (var app in _apps)
			{
				var root = app.AnyTree();
				if (root is null)
					continue;
				foreach (string id in NodeIds(root))
				{
					if (owners.TryGetValue(id, out string? first) && first != app.AppId)
					{
						if (_reportedCollisions.Add(id))
							Console.WriteLine(
								$"[UI] node id '{id}' is used by both '{first}' and '{app.AppId}' on the same panel. " +
								"Ids are shared there, so UpdateNode will reach whichever built last - " +
								"prefix your ids (e.g. '" + app.AppId + "_" + id + "').");
					}
					else
					{
						owners[id] = app.AppId;
					}
				}
			}
		}
	}

	private static IEnumerable<string> NodeIds(UiNode node)
	{
		if (!string.IsNullOrEmpty(node.NodeId))
			yield return node.NodeId!;
		foreach (var child in node.Children)
			foreach (string id in NodeIds(child))
				yield return id;
	}

	/// <summary>Drops every group; for tests only.</summary>
	internal static void ResetForTests()
	{
		lock (_lock)
			_groups.Clear();
	}
}
