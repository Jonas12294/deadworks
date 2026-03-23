namespace DeadworksManaged.Api;

/// <summary>A generic handle that invokes a cancellation callback when cancelled.</summary>
public sealed class CallbackHandle : IHandle {
	private Action? _cancel;
	private volatile bool _finished;

	public CallbackHandle(Action cancel) => _cancel = cancel;

	public bool IsFinished => _finished;

	public void Cancel() {
		if (_finished) return;
		_finished = true;
		var c = _cancel;
		_cancel = null;
		c?.Invoke();
	}

	public IHandle CancelOnMapChange() => this;

	internal static readonly CallbackHandle Noop = new(() => { });
	static CallbackHandle() { Noop._finished = true; }
}
