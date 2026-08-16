namespace DeadworksManaged.Api;

/// <summary>
/// Per-<c>(slot, event)</c> emit rate limiter with coalescing: an emit inside
/// the window is never dropped — the latest payload is kept and sent when the
/// window expires, so panels always converge on current state.
/// </summary>
/// <remarks>
/// Pure bookkeeping so it can be tested with a fake clock; <see cref="UiApp"/>
/// owns the actual timer scheduling and sending. Not thread-safe — the caller
/// serialises access.
/// </remarks>
internal sealed class EmitThrottle
{
	private sealed class Entry
	{
		public long LastSentMs = long.MinValue / 2;
		public IReadOnlyDictionary<string, string>? Pending;
		public bool FlushScheduled;
	}

	private readonly long _windowMs;
	private readonly Func<long> _nowMs;
	private readonly Dictionary<(int Slot, string Event), Entry> _entries = new();

	public EmitThrottle(long windowMs, Func<long> nowMs)
	{
		_windowMs = windowMs;
		_nowMs = nowMs;
	}

	/// <summary>
	/// Records an emit attempt. Returns <see langword="true"/> when the caller
	/// should send <paramref name="data"/> immediately. Otherwise the payload
	/// is stored (replacing any earlier pending one), and
	/// <paramref name="scheduleFlushInMs"/> is non-negative exactly once per
	/// window — the caller schedules a flush that far in the future.
	/// </summary>
	public bool TrySend(int slot, string eventName, IReadOnlyDictionary<string, string> data,
		out long scheduleFlushInMs)
	{
		scheduleFlushInMs = -1;

		var key = (slot, eventName);
		if (!_entries.TryGetValue(key, out var entry))
			_entries[key] = entry = new Entry();

		long now = _nowMs();
		if (now - entry.LastSentMs >= _windowMs)
		{
			entry.LastSentMs = now;
			return true;
		}

		entry.Pending = data;
		if (!entry.FlushScheduled)
		{
			entry.FlushScheduled = true;
			scheduleFlushInMs = entry.LastSentMs + _windowMs - now;
		}
		return false;
	}

	/// <summary>
	/// Takes the pending payload for a due flush, marking it as sent, or
	/// <see langword="null"/> when nothing is pending (already flushed or the
	/// throttle was raced by a direct send).
	/// </summary>
	public IReadOnlyDictionary<string, string>? TakePending(int slot, string eventName)
	{
		if (!_entries.TryGetValue((slot, eventName), out var entry))
			return null;

		entry.FlushScheduled = false;
		var pending = entry.Pending;
		if (pending is null)
			return null;

		entry.Pending = null;
		entry.LastSentMs = _nowMs();
		return pending;
	}

	/// <summary>Drops any pending payload for a slot (player left).</summary>
	public void DropSlot(int slot)
	{
		foreach (var (key, entry) in _entries)
			if (key.Slot == slot)
				entry.Pending = null;
	}
}
