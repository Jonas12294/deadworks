using DeadworksManaged.Api;
using Xunit;

namespace DeadworksManaged.Tests;

/// <summary>
/// The [UiActionHandler] path: a plugin answers panel actions with methods
/// instead of lambdas, exactly like [GameEventHandler] answers game events.
/// Registration is driven straight through the loader's discovery so the
/// reflection binding is what is under test, not a re-implementation of it.
/// </summary>
public class UiActionHandlerTests : IDisposable
{
	private readonly List<string> _registered = new();

	public void Dispose()
	{
		foreach (var path in _registered)
			PluginLoader.UnregisterPluginUiActionHandlersForTests(path);
	}

	/// <summary>Runs the loader's attribute discovery over a plugin instance.</summary>
	private void Register(string path, params IDeadworksPlugin[] plugins)
	{
		_registered.Add(path);
		PluginLoader.RegisterPluginUiActionHandlersForTests(path, [.. plugins]);
	}

	/// <summary>Mirrors what deadworks.js sends: a UiPayload, base64 for the console-command trip.</summary>
	private static string Encode(string eventName, params (string Key, string Value)[] data)
	{
		var text = new System.Text.StringBuilder();
		text.Append("v=1\n").Append("event=").Append(eventName).Append('\n');
		foreach (var (key, value) in data)
			text.Append("d.").Append(key).Append('=').Append(value).Append('\n');
		return System.Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(text.ToString()));
	}

	private class TestPlugin : IDeadworksPlugin
	{
		public string Name => "test";
		public void OnLoad(bool isReload) { }
		public void OnUnload() { }
		public readonly List<string> Seen = new();
	}

	private sealed class VoidPlugin : TestPlugin
	{
		[UiActionHandler("t.click")]
		public void OnClick(UiAction e) => Seen.Add($"{e.Name}|{e.PlayerSlot}|{e.GetString("id")}");
	}

	private sealed class TwoEventsPlugin : TestPlugin
	{
		[UiActionHandler("t.a")]
		[UiActionHandler("t.b")]
		public void OnEither(UiAction e) => Seen.Add(e.Name);
	}

	private sealed class StoppingPlugin : TestPlugin
	{
		[UiActionHandler("t.stop")]
		public HookResult OnStop(UiAction e)
		{
			Seen.Add("stopper");
			return HookResult.Stop;
		}
	}

	private sealed class ContinuingPlugin : TestPlugin
	{
		[UiActionHandler("t.stop")]
		[UiActionHandler("t.continue")]
		public HookResult OnAction(UiAction e)
		{
			Seen.Add("second");
			return HookResult.Continue;
		}
	}

	private sealed class BadSignaturePlugin : TestPlugin
	{
		// Neither shape the binder accepts - must be skipped, not thrown on.
		[UiActionHandler("t.bad")]
		public void WrongArgs(int slot, string what) => Seen.Add("wrong");

		[UiActionHandler("t.good")]
		public void Right(UiAction e) => Seen.Add("right");
	}

	private sealed class ReservedNamePlugin : TestPlugin
	{
		// '_' names belong to the UI system (panel-ready announcements).
		[UiActionHandler("_ready")]
		public void OnReserved(UiAction e) => Seen.Add("reserved");
	}

	[Fact]
	public void DeliversAnActionToTheAttributedMethod()
	{
		var plugin = new VoidPlugin();
		Register("void.dll", plugin);

		UI.HandleAction(4, Encode("t.click", ("id", "confirm")));

		Assert.Equal(new[] { "t.click|4|confirm" }, plugin.Seen);
	}

	[Fact]
	public void OneMethodCanAnswerSeveralActions()
	{
		var plugin = new TwoEventsPlugin();
		Register("two.dll", plugin);

		UI.HandleAction(0, Encode("t.a"));
		UI.HandleAction(0, Encode("t.b"));

		Assert.Equal(new[] { "t.a", "t.b" }, plugin.Seen);
	}

	[Fact]
	public void StopKeepsLaterHandlersFromRunning()
	{
		var stopper = new StoppingPlugin();
		var second = new ContinuingPlugin();
		Register("stop.dll", stopper);
		Register("second.dll", second);

		UI.HandleAction(0, Encode("t.stop"));

		Assert.Equal(new[] { "stopper" }, stopper.Seen);
		Assert.Empty(second.Seen);
	}

	[Fact]
	public void AVoidHandlerDoesNotStopLaterHandlers()
	{
		var plugin = new VoidPlugin();
		Register("void.dll", plugin);

		var reached = false;
		var handle = UI.On("t.click", (_, _) => reached = true);

		try
		{
			UI.HandleAction(0, Encode("t.click", ("id", "confirm")));
		}
		finally { handle.Cancel(); }

		Assert.Equal(new[] { "t.click|0|confirm" }, plugin.Seen);
		Assert.True(reached);
	}

	[Fact]
	public void AContinueResultAlsoLetsLaterHandlersRun()
	{
		var second = new ContinuingPlugin();
		Register("second.dll", second);

		var reached = false;
		var handle = UI.On("t.continue", (_, _) => reached = true);

		try
		{
			UI.HandleAction(0, Encode("t.continue"));
		}
		finally { handle.Cancel(); }

		Assert.Equal(new[] { "second" }, second.Seen);
		Assert.True(reached);
	}

	[Fact]
	public void UnloadingThePluginCancelsItsHandlers()
	{
		var plugin = new VoidPlugin();
		Register("unload.dll", plugin);

		PluginLoader.UnregisterPluginUiActionHandlersForTests("unload.dll");
		UI.HandleAction(0, Encode("t.click", ("id", "confirm")));

		Assert.Empty(plugin.Seen);
	}

	[Fact]
	public void AMethodWithTheWrongSignatureIsSkippedAndTheRestStillRegister()
	{
		var plugin = new BadSignaturePlugin();
		Register("bad.dll", plugin);

		UI.HandleAction(0, Encode("t.bad"));
		UI.HandleAction(0, Encode("t.good"));

		Assert.Equal(new[] { "right" }, plugin.Seen);
	}

	[Fact]
	public void AReservedEventNameIsRefusedWithoutFailingTheLoad()
	{
		var plugin = new ReservedNamePlugin();
		Register("reserved.dll", plugin);

		UI.HandleAction(0, Encode("_ready", ("app", "whatever")));

		Assert.Empty(plugin.Seen);
	}
}

/// <summary>
/// Answering the panel that sent the action: e.Emit and friends push through
/// the handler's own app, found from the plugin that declared the handler.
/// </summary>
public class UiActionAnswerTests : IDisposable
{
	private readonly List<string> _paths = new();
	private readonly List<UiApp> _apps = new();

	public void Dispose()
	{
		foreach (var path in _paths)
			PluginLoader.UnregisterPluginUiActionHandlersForTests(path);
		foreach (var app in _apps)
			app.Dispose();
		NetMessages.OnSend = null;
	}

	private void Register(string path, IDeadworksPlugin plugin)
	{
		_paths.Add(path);
		PluginLoader.RegisterPluginUiActionHandlersForTests(path, [plugin]);
	}

	private UiApp CreateApp(IDeadworksPlugin plugin, string id)
	{
		var app = UI.CreateApp(plugin, new UiAppOptions
		{
			Bundle = new UiBundle { Id = id, Url = "https://cdn.example/a.vpk", Sha256 = new string('a', 64) },
			PublishOnConnect = false,
			MinEmitInterval = null,     // no throttle, so no timer is needed here
		});
		_apps.Add(app);
		return app;
	}

	private static string Encode(string eventName)
		=> System.Convert.ToBase64String(
			System.Text.Encoding.UTF8.GetBytes($"v=1\nevent={eventName}\n"));

	private static void AnnounceReady(int slot, string appId)
		=> UI.HandleAction(slot, System.Convert.ToBase64String(
			System.Text.Encoding.UTF8.GetBytes($"v=1\nevent=_ready\nd.app={appId}\n")));

	/// <summary>Collects every UI emit sent while it is alive.</summary>
	private sealed class SendCapture : IDisposable
	{
		public readonly List<(string Event, Dictionary<string, string> Data, ulong Mask)> Emits = new();

		public SendCapture()
		{
			NetMessages.OnSend = (msgId, bytes, mask) =>
			{
				if (msgId != 148) return;
				var msg = CUserMsg_CustomGameEvent.Parser.ParseFrom(bytes);
				if (msg.EventName != "dw.ui.emit") return;

				var entries = UiPayload.Parse(msg.Data.ToByteArray())!;
				var data = new Dictionary<string, string>();
				foreach (var (key, value) in entries)
					if (key.StartsWith("d.", StringComparison.Ordinal))
						data[key[2..]] = value;
				Emits.Add((UiPayload.Find(entries, "event")!, data, mask));
			};
		}

		public void Dispose() => NetMessages.OnSend = null;
	}

	private class AnsweringPlugin : IDeadworksPlugin
	{
		public string Name => "answering";
		public void OnLoad(bool isReload) { }
		public void OnUnload() { }

		public UiApp? SeenApp;

		[UiActionHandler("t.reply")]
		public void OnReply(UiAction e)
		{
			SeenApp = e.App;
			e.Emit("t.pong", new Dictionary<string, string> { ["ok"] = "1" });
		}
	}

	[Fact]
	public void EmitAnswersThePlayerThroughThePluginsApp()
	{
		var plugin = new AnsweringPlugin();
		CreateApp(plugin, "answerapp");
		Register("answer.dll", plugin);

		using var capture = new SendCapture();
		UI.HandleAction(3, Encode("t.reply"));

		var emit = Assert.Single(capture.Emits);
		Assert.Equal("t.pong", emit.Event);
		Assert.Equal("1", emit.Data["ok"]);
		Assert.Equal(1ul << 3, emit.Mask);      // answered the sender, nobody else
	}

	[Fact]
	public void TheAppIsTheOneWhosePanelIsLiveForThatPlayer()
	{
		var plugin = new AnsweringPlugin();
		CreateApp(plugin, "first");
		var second = CreateApp(plugin, "second");
		Register("two-apps.dll", plugin);

		// Two apps is ambiguous until a panel announces itself: the action came
		// from the panel that is live for this player.
		AnnounceReady(2, "second");

		using var capture = new SendCapture();
		UI.HandleAction(2, Encode("t.reply"));

		Assert.Same(second, plugin.SeenApp);
		Assert.Single(capture.Emits);
	}

	[Fact]
	public void SeveralAppsWithNoLivePanelLeaveTheAnswerUnresolved()
	{
		var plugin = new AnsweringPlugin();
		CreateApp(plugin, "one");
		CreateApp(plugin, "other");
		Register("ambiguous.dll", plugin);

		using var capture = new SendCapture();
		UI.HandleAction(1, Encode("t.reply"));      // logs which call could not resolve

		Assert.Null(plugin.SeenApp);
		Assert.Empty(capture.Emits);
	}

	[Fact]
	public void APluginWithNoAppAnswersNothingRatherThanThrowing()
	{
		var plugin = new AnsweringPlugin();
		Register("no-app.dll", plugin);

		using var capture = new SendCapture();
		UI.HandleAction(0, Encode("t.reply"));

		Assert.Null(plugin.SeenApp);
		Assert.Empty(capture.Emits);
	}

	[Fact]
	public void ARuntimeHandlerRegisteredOnAnAppAnswersThroughThatApp()
	{
		// app.On binds the app directly, so it stays unambiguous even for a
		// plugin that owns several.
		var plugin = new AnsweringPlugin();
		CreateApp(plugin, "left");
		var right = CreateApp(plugin, "right");

		UiApp? seen = null;
		right.On("t.runtime", e =>
		{
			seen = e.App;
			e.Emit("t.pong", new Dictionary<string, string>());
		});

		using var capture = new SendCapture();
		UI.HandleAction(4, Encode("t.runtime"));

		Assert.Same(right, seen);
		Assert.Single(capture.Emits);
	}
}

/// <summary>The readers on the action object. Every value here came from a client.</summary>
public class UiActionValueTests
{
	private static UiAction Action(params (string Key, string Value)[] data)
		=> new("t.act", 2, data.ToDictionary(p => p.Key, p => p.Value));

	[Fact]
	public void ExposesTheNameAndTheAuthenticatedSlot()
	{
		var action = Action();
		Assert.Equal("t.act", action.Name);
		Assert.Equal(2, action.PlayerSlot);
	}

	[Fact]
	public void ReadsStringsAndReportsMissingKeys()
	{
		var action = Action(("id", "confirm"));
		Assert.Equal("confirm", action.GetString("id"));
		Assert.Equal("fallback", action.GetString("nope", "fallback"));
		Assert.True(action.Has("id"));
		Assert.False(action.Has("nope"));
	}

	[Fact]
	public void ReadsNumbersAndFallsBackOnJunk()
	{
		var action = Action(("page", "3"), ("junk", "seven"), ("ratio", "1.5"));
		Assert.Equal(3, action.GetInt("page"));
		Assert.Equal(-1, action.GetInt("junk", -1));
		Assert.Equal(-1, action.GetInt("missing", -1));
		Assert.Equal(1.5f, action.GetFloat("ratio"));
		Assert.Equal(0f, action.GetFloat("junk"));
	}

	[Fact]
	public void ClampsIntegersIntoTheAllowedRange()
	{
		var action = Action(("page", "99"), ("neg", "-5"));
		Assert.Equal(9, action.GetInt("page", 0, 9));
		Assert.Equal(0, action.GetInt("neg", 0, 9));
		Assert.Equal(4, action.GetInt("missing", 0, 9, fallback: 4));
	}

	[Fact]
	public void ReadsFlags()
	{
		var action = Action(("on", "1"), ("off", "false"), ("junk", "yes"));
		Assert.True(action.GetBool("on"));
		Assert.False(action.GetBool("off"));
		Assert.True(action.GetBool("junk", def: true));
		Assert.False(action.GetBool("missing"));
	}

	[Fact]
	public void WhitelistsAndCapsText()
	{
		var action = Action(("column", "nonsense"), ("note", new string('x', 200)));
		Assert.Equal("kills", action.OneOf("column", "kills", "deaths"));
		Assert.Equal("deaths", Action(("column", "deaths")).OneOf("column", "kills", "deaths"));
		Assert.Equal(32, action.Text("note", 32).Length);
	}

	[Fact]
	public void ExposesTheRawValuesForAnythingElse()
	{
		var action = Action(("custom", "value"));
		Assert.Equal("value", action.Data["custom"]);
	}
}
