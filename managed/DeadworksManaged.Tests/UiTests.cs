using DeadworksManaged.Api;
using Xunit;
using ITimer = DeadworksManaged.Api.ITimer;

namespace DeadworksManaged.Tests;

public class UiPayloadTests
{
    [Fact]
    public void RoundTripsEntriesInOrder()
    {
        var bytes = new UiPayload()
            .Add("id", "scoreboard")
            .Add("key", @"panorama\layout\a.xml")
            .Add("key", @"panorama\layout\b.xml")
            .ToBytes();

        var entries = UiPayload.Parse(bytes);

        Assert.NotNull(entries);
        Assert.Equal("scoreboard", UiPayload.Find(entries!, "id"));
        Assert.Equal(
            new[] { @"panorama\layout\a.xml", @"panorama\layout\b.xml" },
            UiPayload.FindAll(entries!, "key"));
    }

    [Fact]
    public void KeepsEqualsSignsInsideValues()
    {
        // URLs carry query strings; splitting on the last '=' would corrupt them.
        const string url = "https://cdn.example/bundle.vpk?v=3&sig=abc==";
        var entries = UiPayload.Parse(new UiPayload().Add("url", url).ToBytes());

        Assert.Equal(url, UiPayload.Find(entries!, "url"));
    }

    [Fact]
    public void RejectsPayloadWithoutAMatchingVersion()
    {
        // A client reading an unknown version must fail closed rather than
        // misinterpret fields it does not understand.
        Assert.Null(UiPayload.Parse("v=99\nid=x\n"u8));
        Assert.Null(UiPayload.Parse("id=x\n"u8));
        Assert.Null(UiPayload.Parse(""u8));
    }

    [Theory]
    [InlineData("a\nb")]
    [InlineData("a\rb")]
    public void RejectsLineBreaksInValues(string value)
    {
        // Line breaks are the record separator, so they would let a caller
        // inject extra entries.
        Assert.Throws<ArgumentException>(() => new UiPayload().Add("k", value));
    }

    [Fact]
    public void RejectsEqualsSignInKeys()
    {
        Assert.Throws<ArgumentException>(() => new UiPayload().Add("a=b", "v"));
    }
}

public class UiBundleTests
{
    private static UiBundle Valid() => new()
    {
        Id = "scoreboard",
        Url = "https://cdn.example/scoreboard.vpk",
        Sha256 = new string('a', 64),
    };

    [Fact]
    public void AcceptsAWellFormedBundle()
    {
        Valid().Validate();
    }

    [Fact]
    public void RejectsNonHttpsUrls()
    {
        var bundle = new UiBundle { Id = "x", Url = "http://cdn.example/a.vpk", Sha256 = new string('a', 64) };
        Assert.Throws<ArgumentException>(bundle.Validate);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("zzzz")]
    public void RejectsMalformedHashes(string hash)
    {
        var bundle = new UiBundle { Id = "x", Url = "https://cdn.example/a.vpk", Sha256 = hash };
        Assert.Throws<ArgumentException>(bundle.Validate);
    }

    [Fact]
    public void RejectsForwardSlashCacheKeys()
    {
        // Panorama keys use backslashes; a forward-slash key silently matches
        // nothing, which is far more confusing than an exception.
        var bundle = new UiBundle
        {
            Id = "x",
            Url = "https://cdn.example/a.vpk",
            Sha256 = new string('a', 64),
            CacheKeys = ["panorama/layout/a.xml"],
        };
        Assert.Throws<ArgumentException>(bundle.Validate);
    }
}

public class NetMessageRegistryTests
{
    [Fact]
    public void ResolvesTheCustomGameEventMessages()
    {
        // These carry the UI channel. CUserMsg_CustomGameEvent breaks the usual
        // CUserMessageFoo convention, and EBaseClientMessages was not mapped at
        // all until the UI work, so both are worth pinning.
        Assert.Equal(148, NetMessageRegistry.GetMessageId(typeof(CUserMsg_CustomGameEvent)));
        Assert.Equal(280, NetMessageRegistry.GetMessageId(typeof(CClientMsg_CustomGameEvent)));
    }

    [Fact]
    public void StillResolvesConventionallyNamedMessages()
    {
        Assert.True(NetMessageRegistry.GetMessageId(typeof(CCitadelUserMsg_ChatMsg)) > 0);
    }
}

public class UiActionTests
{
    // Mirrors what deadworks.js sends: a UiPayload, base64 for the trip through
    // the console command.
    private static string Encode(string eventName, params (string Key, string Value)[] data)
    {
        var text = new System.Text.StringBuilder();
        text.Append("v=1\n").Append("event=").Append(eventName).Append('\n');
        foreach (var (key, value) in data)
            text.Append("d.").Append(key).Append('=').Append(value).Append('\n');
        return System.Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(text.ToString()));
    }

    [Fact]
    public void DeliversAnActionToItsHandlerWithTheCallersSlot()
    {
        int slot = -99;
        IReadOnlyDictionary<string, string>? received = null;
        var handle = UI.On("t.sort", (s, d) => { slot = s; received = d; });

        try
        {
            UI.HandleAction(3, Encode("t.sort", ("column", "kills")));
        }
        finally { handle.Cancel(); }

        Assert.Equal(3, slot);
        Assert.Equal("kills", received!["column"]);
    }

    [Fact]
    public void DoesNotDeliverToAHandlerForADifferentEvent()
    {
        var called = false;
        var handle = UI.On("t.mine", (_, _) => called = true);

        try
        {
            UI.HandleAction(0, Encode("t.theirs"));
        }
        finally { handle.Cancel(); }

        Assert.False(called);
    }

    [Fact]
    public void IgnoresAnArgumentThatIsNotBase64()
    {
        var called = false;
        var handle = UI.On("t.garbage", (_, _) => called = true);

        try
        {
            // Arrives straight from a client, so it is arbitrary text.
            UI.HandleAction(0, "not!base64");
        }
        finally { handle.Cancel(); }

        Assert.False(called);
    }

    [Fact]
    public void IgnoresAPayloadWithAnUnknownVersion()
    {
        var called = false;
        var handle = UI.On("t.version", (_, _) => called = true);

        try
        {
            var text = System.Text.Encoding.UTF8.GetBytes("v=99\nevent=t.version\n");
            UI.HandleAction(0, System.Convert.ToBase64String(text));
        }
        finally { handle.Cancel(); }

        Assert.False(called);
    }

    [Fact]
    public void ReservedEventNamesCannotBeSubscribedPublicly()
    {
        // '_'-prefixed events are the system's (panel-ready announcements);
        // letting plugin code subscribe would let it shadow them.
        Assert.Throws<ArgumentException>(() => UI.On("_ready", (_, _) => { }));
    }
}

public class UiTableTests
{
    [Fact]
    public void EmptyTableIsJustACount()
    {
        var data = new UiTable().ToData();
        Assert.Equal(new Dictionary<string, string> { ["n"] = "0" }, data);
    }

    [Fact]
    public void FlattensRowsAndMetaIntoTheWireShape()
    {
        var data = new UiTable { Meta = { ["sort"] = "kills" } }
            .AddRow(new() { ["name"] = "Alice", ["k"] = 5 })
            .AddRow(new() { ["name"] = "Bob", ["k"] = 2 })
            .ToData();

        Assert.Equal("2", data["n"]);
        Assert.Equal("Alice", data["r0.name"]);
        Assert.Equal("5", data["r0.k"]);
        Assert.Equal("Bob", data["r1.name"]);
        Assert.Equal("2", data["r1.k"]);
        Assert.Equal("kills", data["sort"]);
    }

    [Fact]
    public void StringifiesWithTheInvariantCulture()
    {
        // A comma decimal separator would corrupt any numeric field a panel parses.
        var data = new UiTable().AddRow(new() { ["x"] = 1.5, ["y"] = (object?)null }).ToData();

        Assert.Equal("1.5", data["r0.x"]);
        Assert.Equal("", data["r0.y"]);
    }

    [Fact]
    public void StripsLineBreaksFromValues()
    {
        // Player names are the recurring source; a cleaned name beats a crashed
        // emit. Newline is the payload separator, so it simply cannot pass through.
        var data = new UiTable { Meta = { ["m"] = "a\rb" } }
            .AddRow(new() { ["name"] = "evil\r\nname" })
            .ToData();

        Assert.Equal("evil name", data["r0.name"]);
        Assert.Equal("ab", data["m"]);
    }

    [Fact]
    public void ThrowsOnMalformedFieldKeys()
    {
        Assert.Throws<ArgumentException>(() => new UiTable().AddRow(new() { ["a=b"] = 1 }));
        Assert.Throws<ArgumentException>(() => new UiTable().AddRow(new() { [""] = 1 }));
    }

    [Theory]
    [InlineData("n")]
    [InlineData("r0.name")]
    [InlineData("r12.k")]
    public void ThrowsOnMetaKeysThatCollideWithTheRowEncoding(string key)
    {
        var table = new UiTable { Meta = { [key] = "x" } };
        Assert.Throws<ArgumentException>(() => table.ToData());
    }

    [Theory]
    [InlineData("reason")]
    [InlineData("rate")]     // starts with 'r' but is not a row key
    [InlineData("r0x")]      // no dot after the digits
    public void AllowsMetaKeysThatMerelyResembleRowKeys(string key)
    {
        var data = new UiTable { Meta = { [key] = "x" } }.ToData();
        Assert.Equal("x", data[key]);
    }
}

public class EmitThrottleTests
{
    private static Dictionary<string, string> Payload(string marker) => new() { ["m"] = marker };

    [Fact]
    public void FirstEmitPassesThrough()
    {
        var throttle = new EmitThrottle(100, () => 0);
        Assert.True(throttle.TrySend(1, "e", Payload("a"), out _));
    }

    [Fact]
    public void BurstInsideTheWindowCoalescesToTheLatestPayload()
    {
        long now = 0;
        var throttle = new EmitThrottle(100, () => now);

        Assert.True(throttle.TrySend(1, "e", Payload("a"), out _));

        now = 30;
        Assert.False(throttle.TrySend(1, "e", Payload("b"), out long flushIn));
        Assert.Equal(70, flushIn);   // one flush scheduled, at the window edge

        now = 60;
        Assert.False(throttle.TrySend(1, "e", Payload("c"), out long again));
        Assert.Equal(-1, again);     // flush already scheduled, don't schedule twice

        now = 100;
        var pending = throttle.TakePending(1, "e");
        Assert.Equal("c", pending!["m"]);          // the latest wins
        Assert.Null(throttle.TakePending(1, "e")); // and it is taken exactly once
    }

    [Fact]
    public void SendsDirectlyAgainAfterTheWindow()
    {
        long now = 0;
        var throttle = new EmitThrottle(100, () => now);
        throttle.TrySend(1, "e", Payload("a"), out _);

        now = 150;
        Assert.True(throttle.TrySend(1, "e", Payload("b"), out _));
    }

    [Fact]
    public void AFlushCountsAsASendForTheNextWindow()
    {
        long now = 0;
        var throttle = new EmitThrottle(100, () => now);
        throttle.TrySend(1, "e", Payload("a"), out _);

        now = 50;
        throttle.TrySend(1, "e", Payload("b"), out _);
        now = 100;
        throttle.TakePending(1, "e");

        now = 120;   // only 20ms after the flush went out
        Assert.False(throttle.TrySend(1, "e", Payload("c"), out _));
    }

    [Fact]
    public void SlotsAndEventsThrottleIndependently()
    {
        var throttle = new EmitThrottle(100, () => 0);
        Assert.True(throttle.TrySend(1, "e", Payload("a"), out _));
        Assert.True(throttle.TrySend(2, "e", Payload("b"), out _));
        Assert.True(throttle.TrySend(1, "f", Payload("c"), out _));
    }

    [Fact]
    public void DropSlotDiscardsPendingPayloads()
    {
        long now = 0;
        var throttle = new EmitThrottle(100, () => now);
        throttle.TrySend(1, "e", Payload("a"), out _);
        now = 50;
        throttle.TrySend(1, "e", Payload("b"), out _);

        throttle.DropSlot(1);

        now = 100;
        Assert.Null(throttle.TakePending(1, "e"));
    }
}

/// <summary>
/// UiApp tests share the static UI dispatch table and NetMessages.OnSend, so
/// they live in one class (xunit runs a class sequentially) and every test
/// restores what it touched.
/// </summary>
public class UiAppTests
{
    private static UiBundle Bundle(string id = "testapp") => new()
    {
        Id = id,
        Url = "https://cdn.example/a.vpk",
        Sha256 = new string('a', 64),
    };

    private static void AnnounceReady(int slot, string appId)
    {
        var text = System.Text.Encoding.UTF8.GetBytes($"v=1\nevent=_ready\nd.app={appId}\n");
        UI.HandleAction(slot, System.Convert.ToBase64String(text));
    }

    private sealed class FakeTimer : ITimer
    {
        public readonly List<(Duration Interval, Action Callback)> Every = new();
        public readonly List<(Duration Delay, Action Callback)> Once = new();

        IHandle ITimer.Every(Duration interval, Action callback)
        {
            Every.Add((interval, callback));
            return new CallbackHandle(() => { });
        }

        IHandle ITimer.Once(Duration delay, Action callback)
        {
            Once.Add((delay, callback));
            return new CallbackHandle(() => { });
        }

        IHandle ITimer.Sequence(Func<IStep, Pace> callback) => new CallbackHandle(() => { });
        void ITimer.NextTick(Action callback) => callback();
    }

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

    private static UiApp App(UiAppOptions options, ITimer? timer = null, Func<long>? clock = null)
        => new(options, () => timer ?? throw new InvalidOperationException("no timer expected"), clock);

    [Fact]
    public void BurstEmitsAreCoalescedWithoutAnyConfiguration()
    {
        // The data channel has no engine-side rate limit, so the safe default
        // belongs in the SDK: unconfigured apps throttle; opting out is explicit.
        using var capture = new SendCapture();
        var timer = new FakeTimer();
        long now = 0;
        using var app = App(new UiAppOptions { Bundle = Bundle(), PublishOnConnect = false },
            timer, () => now);

        app.Emit(1, "e.v", new Dictionary<string, string> { ["n"] = "1" });
        app.Emit(1, "e.v", new Dictionary<string, string> { ["n"] = "2" });

        var sent = Assert.Single(capture.Emits);
        Assert.Equal("1", sent.Data["n"]);
        Assert.Single(timer.Once);   // the second is pending a flush, not dropped
    }

    [Fact]
    public void CreateAppValidatesTheBundle()
    {
        var plugin = new DummyPlugin();
        var bad = new UiAppOptions
        {
            Bundle = new UiBundle { Id = "x", Url = "http://insecure/a.vpk", Sha256 = new string('a', 64) },
        };
        Assert.Throws<ArgumentException>(() => UI.CreateApp(plugin, bad));
    }

    private sealed class DummyPlugin : IDeadworksPlugin
    {
        public string Name => "dummy";
        public void OnLoad(bool isReload) { }
        public void OnUnload() { }
    }

    /// <summary>Two plugins sharing the host bundle are told apart by name.</summary>
    private sealed class NamedPlugin(string name) : IDeadworksPlugin
    {
        public string Name { get; } = name;
        public void OnLoad(bool isReload) { }
        public void OnUnload() { }
    }

    /// <summary>Collects every bundle publish sent while it is alive.</summary>
    private sealed class PublishCapture : IDisposable
    {
        public readonly List<(string Id, ulong Mask)> Publishes = new();

        public PublishCapture()
        {
            NetMessages.OnSend = (msgId, bytes, mask) =>
            {
                if (msgId != 148) return;
                var msg = CUserMsg_CustomGameEvent.Parser.ParseFrom(bytes);
                if (msg.EventName != "dw.ui.bundle") return;

                var entries = UiPayload.Parse(msg.Data.ToByteArray())!;
                Publishes.Add((UiPayload.Find(entries, "id")!, mask));
            };
        }

        public void Dispose() => NetMessages.OnSend = null;
    }

    [Fact]
    public void TheHostDispatchPublishesAndClearsPresenceWithoutAnyForwarding()
    {
        // The whole point of the registry: a plugin creates an app and never
        // touches connect/disconnect again - the host feeds the registry.
        using var capture = new PublishCapture();
        var app = UI.CreateApp(new DummyPlugin(), new UiAppOptions { Bundle = Bundle() });
        try
        {
            UiAppRegistry.HandleConnect(5);
            var published = Assert.Single(capture.Publishes);
            Assert.Equal("testapp", published.Id);
            Assert.Equal(1ul << 5, published.Mask);

            AnnounceReady(5, "testapp");
            Assert.True(app.IsPanelLive(5));

            UiAppRegistry.HandleDisconnect(5);
            Assert.False(app.IsPanelLive(5));
        }
        finally
        {
            app.Dispose();
        }
    }

    [Fact]
    public void ManualForwardingOnTopOfTheRegistryDoesNotDoublePublish()
    {
        // Plugins written against the old SDK still forward HandleConnect
        // themselves; the second call for the same connection must be a no-op
        // or every joiner would mount the bundle twice.
        using var capture = new PublishCapture();
        var app = UI.CreateApp(new DummyPlugin(), new UiAppOptions { Bundle = Bundle() });
        try
        {
            UiAppRegistry.HandleConnect(4);
            app.HandleConnect(4);
            Assert.Single(capture.Publishes);

            // A real reconnect publishes again.
            UiAppRegistry.HandleDisconnect(4);
            UiAppRegistry.HandleConnect(4);
            Assert.Equal(2, capture.Publishes.Count);
        }
        finally
        {
            app.Dispose();
        }
    }

    [Fact]
    public void ADisposedAppLeavesTheRegistry()
    {
        using var capture = new PublishCapture();
        var app = UI.CreateApp(new DummyPlugin(), new UiAppOptions { Bundle = Bundle() });
        app.Dispose();

        UiAppRegistry.HandleConnect(2);

        Assert.Empty(capture.Publishes);
    }

    [Fact]
    public void UnloadingAPluginDisposesEveryAppItCreated()
    {
        using var capture = new PublishCapture();
        var plugin = new DummyPlugin();
        UI.CreateApp(plugin, new UiAppOptions { Bundle = Bundle() });
        UI.CreateApp(plugin, new UiAppOptions { Bundle = Bundle("otherapp") });

        UiAppRegistry.DisposePlugin(plugin);
        UiAppRegistry.HandleConnect(2);

        Assert.Empty(capture.Publishes);
    }

    [Fact]
    public void CreateAppPublishesToPlayersWhoAreAlreadyConnected()
    {
        // On a plugin (re)load mid-match the players never get another
        // full-connect, so creation itself must treat them as connecting -
        // this is what used to require `if (isReload) Publish(All)`.
        using var capture = new PublishCapture();
        Players.SetConnected(7, true);
        UiApp? app = null;
        try
        {
            app = UI.CreateApp(new DummyPlugin(), new UiAppOptions { Bundle = Bundle() });
            var published = Assert.Single(capture.Publishes);
            Assert.Equal(1ul << 7, published.Mask);
        }
        finally
        {
            Players.SetConnected(7, false);
            app?.Dispose();
        }
    }

    // --- the per-player UI library (UiApp.ServeUiLibrary) ---

    /// <summary>Captures both markup pushes (tree.set emits) and script runs.</summary>
    private sealed class UiTraffic : IDisposable
    {
        public readonly List<(string Event, Dictionary<string, string> Data, ulong Mask)> Emits = new();
        public readonly List<(string Js, ulong Mask)> Scripts = new();

        public UiTraffic()
        {
            NetMessages.OnSend = (msgId, bytes, mask) =>
            {
                if (msgId != 148) return;
                var msg = CUserMsg_CustomGameEvent.Parser.ParseFrom(bytes);
                if (msg.EventName == "dw.ui.script")
                {
                    string js = System.Text.Encoding.UTF8.GetString(
                        System.Convert.FromBase64String(msg.Data.ToStringUtf8()));
                    // The panel-side helpers go out on every announcement and
                    // are plumbing, not a served script - covered by their own
                    // tests, and ignored here so these stay about the library.
                    if (js != UiPanelRuntime.Script)
                        Scripts.Add((js, mask));
                    return;
                }
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

    /// <summary>Builds a temp UI library folder: one subfolder per UI.</summary>
    private static string MakeUiDir(params (string Name, string Xml, string? Js)[] uis)
    {
        string root = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        foreach (var ui in uis)
        {
            string dir = Path.Combine(root, ui.Name);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "ui.xml"), ui.Xml);
            if (ui.Js is not null)
                File.WriteAllText(Path.Combine(dir, "ui.js"), ui.Js);
        }
        return root;
    }

    private static string EncodeAction(string eventName, params (string Key, string Value)[] data)
    {
        var text = new System.Text.StringBuilder("v=1\nevent=").Append(eventName).Append('\n');
        foreach (var (key, value) in data)
            text.Append("d.").Append(key).Append('=').Append(value).Append('\n');
        return System.Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(text.ToString()));
    }

    [Fact]
    public void TheUiLibraryServesPerPlayerUisFromAFolder()
    {
        string root = MakeUiDir(
            ("dashboard", "<label>dash</label>", "$.Msg('hi');"),
            ("admin", "<label>admin</label>", null));
        using var capture = new UiTraffic();
        var timer = new FakeTimer();
        using var app = App(new UiAppOptions { Bundle = Bundle(), PublishOnConnect = false }, timer);
        try
        {
            var library = app.ServeUiLibrary(root, "dashboard");
            Assert.Equal(new[] { "admin", "dashboard" }, library.Available.OrderBy(n => n));

            // A panel announcing itself gets the default UI: markup plus script.
            AnnounceReady(3, "testapp");
            Assert.Contains(capture.Emits,
                e => e.Event == "dw.tree.set" && e.Data["r0.txt"] == "dash" && e.Mask == 1ul << 3);
            var script = Assert.Single(capture.Scripts);
            Assert.Contains("$.Msg('hi');", script.Js);   // preceded by the auto-reset
            Assert.Equal(1ul << 3, script.Mask);

            // Assigning switches one player, immediately, without touching others.
            capture.Emits.Clear();
            Assert.True(library.Assign(3, "admin"));
            Assert.Equal("admin", library.UiFor(3));
            Assert.Contains(capture.Emits, e => e.Data.GetValueOrDefault("r0.txt") == "admin");

            Assert.False(library.Assign(3, "missing"));
            Assert.Equal("admin", library.UiFor(3));

            // A different player still gets the default.
            capture.Emits.Clear();
            AnnounceReady(4, "testapp");
            Assert.Contains(capture.Emits,
                e => e.Data.GetValueOrDefault("r0.txt") == "dash" && e.Mask == 1ul << 4);

            // Cancelling stops serving.
            library.Cancel();
            capture.Emits.Clear();
            AnnounceReady(5, "testapp");
            Assert.DoesNotContain(capture.Emits, e => e.Event == "dw.tree.set");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void EveryServedScriptIsPrecededByASubscriptionReset()
    {
        // Subscribe only ever appends, so re-running a UI's script (hot
        // reload) or switching UIs would stack callbacks forever unless
        // something resets first. The library owns that now - ui.js files
        // no longer need the manual Deadworks.Reset() convention.
        string root = MakeUiDir(
            ("dashboard", "<label>dash</label>", "$.Msg('hi');"),
            ("plain", "<label>p</label>", null));
        using var capture = new UiTraffic();
        var timer = new FakeTimer();
        using var app = App(new UiAppOptions { Bundle = Bundle(), PublishOnConnect = false }, timer);
        try
        {
            var library = app.ServeUiLibrary(root, "dashboard");

            AnnounceReady(3, "testapp");
            var script = Assert.Single(capture.Scripts);
            Assert.StartsWith("Deadworks.Reset();", script.Js);
            Assert.Contains("$.Msg('hi');", script.Js);

            // A UI with no script of its own still clears the previous one's
            // subscriptions on switch.
            capture.Scripts.Clear();
            library.Assign(3, "plain");
            var reset = Assert.Single(capture.Scripts);
            Assert.Equal("Deadworks.Reset();", reset.Js.Trim());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ServeScriptFileAlsoResetsBeforeEachRun()
    {
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".js");
        File.WriteAllText(path, "$.Msg('served');");
        using var capture = new UiTraffic();
        var timer = new FakeTimer();
        using var app = App(new UiAppOptions { Bundle = Bundle(), PublishOnConnect = false }, timer);
        try
        {
            app.ServeScriptFile(path);
            AnnounceReady(2, "testapp");

            var script = Assert.Single(capture.Scripts);
            Assert.StartsWith("Deadworks.Reset();", script.Js);
            Assert.Contains("$.Msg('served');", script.Js);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void TheUiLibraryHotReloadsAnEditedUiToExactlyItsUsers()
    {
        string root = MakeUiDir(
            ("dashboard", "<label>dash</label>", null),
            ("admin", "<label>admin</label>", null));
        using var capture = new UiTraffic();
        var timer = new FakeTimer();
        using var app = App(new UiAppOptions { Bundle = Bundle(), PublishOnConnect = false }, timer);
        try
        {
            var library = app.ServeUiLibrary(root, "dashboard");
            var poll = Assert.Single(timer.Every).Callback;

            AnnounceReady(2, "testapp");            // on the default
            AnnounceReady(3, "testapp");
            library.Assign(3, "admin");
            poll();                                  // first sighting of the default, not an edit
            capture.Emits.Clear();

            // Editing the dashboard re-pushes to its users only.
            string dashXml = Path.Combine(root, "dashboard", "ui.xml");
            File.WriteAllText(dashXml, "<label>dash2</label>");
            File.SetLastWriteTimeUtc(dashXml, DateTime.UtcNow.AddSeconds(3));
            poll();
            var push = Assert.Single(capture.Emits, e => e.Event == "dw.tree.set");
            Assert.Equal("dash2", push.Data["r0.txt"]);
            Assert.Equal(1ul << 2, push.Mask);

            // A broken edit is logged and nothing is pushed; panels keep the
            // last good UI.
            capture.Emits.Clear();
            File.WriteAllText(dashXml, "<label>unclosed");
            File.SetLastWriteTimeUtc(dashXml, DateTime.UtcNow.AddSeconds(6));
            poll();
            Assert.DoesNotContain(capture.Emits, e => e.Event == "dw.tree.set");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void TheUiLibraryForgetsAPlayersAssignmentWhenTheirSlotIsReused()
    {
        string root = MakeUiDir(
            ("dashboard", "<label>dash</label>", null),
            ("admin", "<label>admin</label>", null));
        using var capture = new UiTraffic();
        var timer = new FakeTimer();
        using var app = App(new UiAppOptions { Bundle = Bundle(), PublishOnConnect = false }, timer);
        try
        {
            var library = app.ServeUiLibrary(root, "dashboard");
            AnnounceReady(3, "testapp");
            library.Assign(3, "admin");

            // The next player on this slot must not inherit the admin panel.
            app.HandleConnect(3);
            Assert.Equal("dashboard", library.UiFor(3));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void TheUiLoadActionSwitchesOnlyTheSendingPlayerAndRejectsPathTricks()
    {
        string root = MakeUiDir(
            ("dashboard", "<label>dash</label>", null),
            ("admin", "<label>admin</label>", null));
        using var capture = new UiTraffic();
        var timer = new FakeTimer();
        using var app = App(new UiAppOptions { Bundle = Bundle(), PublishOnConnect = false }, timer);
        try
        {
            var library = app.ServeUiLibrary(root, "dashboard");
            AnnounceReady(3, "testapp");
            AnnounceReady(4, "testapp");

            // The built-in navigation: a panel button dispatches ui.load with
            // the target's name; the slot comes from the connection.
            UI.HandleAction(3, EncodeAction("ui.load", ("id", "admin")));
            Assert.Equal("admin", library.UiFor(3));
            Assert.Equal("dashboard", library.UiFor(4));

            // The name is client input; an id that is not a plain folder name
            // must never touch the filesystem outside the library.
            UI.HandleAction(3, EncodeAction("ui.load", ("id", "..\\..\\secrets")));
            Assert.Equal("admin", library.UiFor(3));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void SetFreeCursorTogglesTheStockCursorConVarOnOneClient()
    {
        // The shop brings the cursor up with hud_free_cursor over
        // CitadelConCommand; SetFreeCursor is that exact toggle so panels
        // with buttons don't each reinvent it.
        using var capture = new UiTraffic();
        var timer = new FakeTimer();
        using var app = App(new UiAppOptions { Bundle = Bundle(), PublishOnConnect = false }, timer);

        app.SetFreeCursor(3, true);
        app.SetFreeCursor(3, false);

        Assert.Equal(2, capture.Scripts.Count);
        Assert.Contains("CitadelConCommand", capture.Scripts[0].Js);
        Assert.Contains("hud_free_cursor 1", capture.Scripts[0].Js);
        Assert.Equal(1ul << 3, capture.Scripts[0].Mask);
        Assert.Contains("hud_free_cursor 0", capture.Scripts[1].Js);
    }

    [Fact]
    public void ASecondLiveAppWithTheSameIdIsRejectedLoudly()
    {
        // Two apps on one id would both serve every _ready announcement and
        // fight over the same panel (the dwhost id especially). Better a
        // clear failure at load than two plugins silently overwriting each
        // other's trees mid-match.
        var first = UI.CreateApp(new DummyPlugin(), new UiAppOptions { Bundle = Bundle(), PublishOnConnect = false });
        try
        {
            var ex = Assert.Throws<InvalidOperationException>(() =>
                UI.CreateApp(new DummyPlugin(), new UiAppOptions { Bundle = Bundle(), PublishOnConnect = false }));
            Assert.Contains("testapp", ex.Message);
            Assert.Contains("dummy", ex.Message);   // names the owning plugin
        }
        finally
        {
            first.Dispose();
        }

        // Disposing frees the id (that is what a plugin reload does).
        var second = UI.CreateApp(new DummyPlugin(), new UiAppOptions { Bundle = Bundle(), PublishOnConnect = false });
        second.Dispose();
    }

    [Fact]
    public void CreateHostAppPinsTheOfficialHostBundleByDefault()
    {
        // The zero-config path for server-built UI: no url, no hash, no keys.
        using var app = UI.CreateHostApp(new DummyPlugin());

        Assert.Equal(UiHostBundle.Id, app.Bundle!.Id);
        Assert.Equal(UiHostBundle.Url, app.Bundle.Url);
        Assert.Equal(UiHostBundle.Sha256, app.Bundle.Sha256);
        // The app id is the plugin's own, so a second plugin on the same
        // bundle does not collide with this one.
        Assert.Equal("dummy", app.AppId);
    }

    [Fact]
    public void TwoPluginsCanEachCreateAHostApp()
    {
        using var first = UI.CreateHostApp(new NamedPlugin("alpha"));
        using var second = UI.CreateHostApp(new NamedPlugin("beta"));

        Assert.Equal("alpha", first.AppId);
        Assert.Equal("beta", second.AppId);
        Assert.Equal(first.Bundle!.Id, second.Bundle!.Id);   // one panel, shared
    }

    [Fact]
    public void TwoAppsOnOnePanelAreBothToldWhenItAnnounces()
    {
        // The panel announces the bundle it came from, so every app on that
        // bundle hears it - that is what lets them share the panel.
        using var first = UI.CreateHostApp(new NamedPlugin("alpha"),
            new UiAppOptions { PublishOnConnect = false });
        using var second = UI.CreateHostApp(new NamedPlugin("beta"),
            new UiAppOptions { PublishOnConnect = false });

        var told = new List<string>();
        first.StateRequested += _ => told.Add("alpha");
        second.StateRequested += _ => told.Add("beta");

        AnnounceReady(3, UiHostBundle.Id);

        Assert.Equal(new[] { "alpha", "beta" }, told);
    }

    [Fact]
    public void TheirTreesAreComposedIntoOnePushRatherThanReplacingEachOther()
    {
        using var capture = new SendCapture();
        using var first = UI.CreateHostApp(new NamedPlugin("alpha"),
            new UiAppOptions { PublishOnConnect = false, MinEmitInterval = null });
        using var second = UI.CreateHostApp(new NamedPlugin("beta"),
            new UiAppOptions { PublishOnConnect = false, MinEmitInterval = null });

        first.SetTree(2, Ui.Label("from alpha").Id("a_msg"));
        second.SetTree(2, Ui.Label("from beta").Id("b_msg"));

        // The last push carries both trees: two roots, each parented to the
        // container (-1), which is what the host runtime already builds.
        var last = capture.Emits.Last(e => e.Event == UiTreeCodec.SetEvent);
        Assert.Equal("2", last.Data["n"]);
        Assert.Equal("from alpha", last.Data["r0.txt"]);
        Assert.Equal("-1", last.Data["r0.p"]);
        Assert.Equal("from beta", last.Data["r1.txt"]);
        Assert.Equal("-1", last.Data["r1.p"]);
    }

    [Fact]
    public void ClearingOneAppsTreeLeavesTheOtherOnScreen()
    {
        using var capture = new SendCapture();
        using var first = UI.CreateHostApp(new NamedPlugin("alpha"),
            new UiAppOptions { PublishOnConnect = false, MinEmitInterval = null });
        using var second = UI.CreateHostApp(new NamedPlugin("beta"),
            new UiAppOptions { PublishOnConnect = false, MinEmitInterval = null });
        first.SetTree(2, Ui.Label("from alpha"));
        second.SetTree(2, Ui.Label("from beta"));

        first.ClearTree(2);

        var last = capture.Emits.Last(e => e.Event == UiTreeCodec.SetEvent);
        Assert.Equal("1", last.Data["n"]);
        Assert.Equal("from beta", last.Data["r0.txt"]);
    }

    [Fact]
    public void AnUnloadedPluginsUiLeavesThePanelWithoutTakingTheOtherWithIt()
    {
        using var capture = new SendCapture();
        using var keeper = UI.CreateHostApp(new NamedPlugin("keeper"),
            new UiAppOptions { PublishOnConnect = false, MinEmitInterval = null });
        var leaver = UI.CreateHostApp(new NamedPlugin("leaver"),
            new UiAppOptions { PublishOnConnect = false, MinEmitInterval = null });
        keeper.SetTree(4, Ui.Label("stays"));
        leaver.SetTree(4, Ui.Label("goes"));

        leaver.Dispose();   // what the host does when a plugin unloads

        var last = capture.Emits.Last(e => e.Event == UiTreeCodec.SetEvent);
        Assert.Equal("1", last.Data["n"]);
        Assert.Equal("stays", last.Data["r0.txt"]);
    }

    [Fact]
    public void TheBundleAndThePanelHelpersGoOutOnceForThePanelNotOncePerApp()
    {
        var sent = new List<string>();
        NetMessages.OnSend = (msgId, bytes, mask) =>
        {
            if (msgId != 148) return;
            sent.Add(CUserMsg_CustomGameEvent.Parser.ParseFrom(bytes).EventName);
        };
        try
        {
            using var first = UI.CreateHostApp(new NamedPlugin("alpha"));
            using var second = UI.CreateHostApp(new NamedPlugin("beta"));

            first.HandleConnect(6);
            second.HandleConnect(6);
            // One panel, one copy of its content - the second app would only
            // be re-sending what the first already sent.
            Assert.Single(sent.Where(e => e == "dw.ui.bundle"));

            sent.Clear();
            AnnounceReady(6, UiHostBundle.Id);
            Assert.Single(sent.Where(e => e == "dw.ui.script"));
        }
        finally { NetMessages.OnSend = null; }
    }

    [Fact]
    public void CreateHostAppHonoursACustomHostBuild()
    {
        // Servers running their own host build override the bundle; everything
        // else about the app behaves the same.
        var custom = Bundle("dwhost");
        using var app = UI.CreateHostApp(new DummyPlugin(),
            new UiAppOptions { Bundle = custom, PublishOnConnect = false });

        Assert.Same(custom, app.Bundle);
    }

    [Fact]
    public void AnAppWithoutABundleStillTracksPresenceButCannotPublish()
    {
        // Variant-swapping plugins publish through the raw API but still want
        // presence and resync; AppId alone covers that.
        using var app = App(new UiAppOptions { AppId = "testapp", PublishOnConnect = false });

        AnnounceReady(2, "testapp");

        Assert.True(app.IsPanelLive(2));
        Assert.Throws<InvalidOperationException>(() => app.Publish(RecipientFilter.All));
    }

    [Fact]
    public void AnAppNeedsABundleOrAnAppId()
    {
        Assert.Throws<ArgumentException>(() => App(new UiAppOptions { PublishOnConnect = false }));
        // PublishOnConnect (the default) is meaningless without a bundle.
        Assert.Throws<ArgumentException>(() => App(new UiAppOptions { AppId = "x" }));
    }

    [Fact]
    public void ReadyAnnouncementMarksThePanelLiveAndRequestsState()
    {
        using var app = App(new UiAppOptions { Bundle = Bundle(), PublishOnConnect = false });
        var requested = new List<int>();
        app.StateRequested += requested.Add;

        AnnounceReady(3, "testapp");

        Assert.True(app.IsPanelLive(3));
        Assert.Equal(new[] { 3 }, app.LivePanels);
        Assert.Equal(new[] { 3 }, requested);
    }

    [Fact]
    public void RapidReAnnouncementsAreIgnoredSoAPushLoopCannotWedgeTheServer()
    {
        // A pushed script that calls Deadworks.Ready() would make answering an
        // announce trigger another announce, forever. Only the first is served
        // until the storm stops.
        long now = 0;
        using var app = App(new UiAppOptions { Bundle = Bundle(), PublishOnConnect = false },
            clock: () => now);
        var requested = new List<int>();
        app.StateRequested += requested.Add;

        // A few fast re-announcements are legitimate (a panel can genuinely
        // reload twice) and are still served...
        AnnounceReady(2, "testapp");
        now = 10; AnnounceReady(2, "testapp");
        now = 20; AnnounceReady(2, "testapp");
        Assert.Equal(new[] { 2, 2, 2 }, requested);

        // ...but a sustained run is a loop and stops being served, so it can
        // never wedge the server.
        for (int i = 0; i < 50; i++) { now += 10; AnnounceReady(2, "testapp"); }
        Assert.Equal(3, requested.Count);
        Assert.True(app.IsPanelLive(2));      // still live, just not re-served

        // Once the storm stops, a real reload is served normally again.
        now += 5000;
        AnnounceReady(2, "testapp");
        Assert.Equal(4, requested.Count);
    }

    [Fact]
    public void IgnoresAnnouncementsForOtherAppsAndBadSlots()
    {
        using var app = App(new UiAppOptions { Bundle = Bundle(), PublishOnConnect = false });
        var requested = new List<int>();
        app.StateRequested += requested.Add;

        AnnounceReady(3, "someoneelse");
        AnnounceReady(64, "testapp");   // out of range slot

        Assert.False(app.IsPanelLive(3));
        Assert.Empty(requested);
    }

    [Fact]
    public void ConnectAndDisconnectClearPresence()
    {
        using var app = App(new UiAppOptions { Bundle = Bundle(), PublishOnConnect = false });

        AnnounceReady(3, "testapp");
        app.HandleConnect(3);           // slot reuse: a new player took the slot
        Assert.False(app.IsPanelLive(3));

        AnnounceReady(3, "testapp");
        app.HandleDisconnect(3);
        Assert.False(app.IsPanelLive(3));
    }

    [Fact]
    public void DisposeStopsListening()
    {
        var app = App(new UiAppOptions { Bundle = Bundle(), PublishOnConnect = false });
        var requested = new List<int>();
        app.StateRequested += requested.Add;

        app.Dispose();
        AnnounceReady(3, "testapp");

        Assert.False(app.IsPanelLive(3));
        Assert.Empty(requested);
    }

    [Fact]
    public void AThrowingStateHandlerDoesNotStopTheOthers()
    {
        using var app = App(new UiAppOptions { Bundle = Bundle(), PublishOnConnect = false });
        var reached = false;
        app.StateRequested += _ => throw new InvalidOperationException("boom");
        app.StateRequested += _ => reached = true;

        AnnounceReady(1, "testapp");

        Assert.True(reached);
    }

    [Fact]
    public void ResyncTicksRequestStateForEveryLivePanel()
    {
        var timer = new FakeTimer();
        using var app = App(
            new UiAppOptions { Bundle = Bundle(), PublishOnConnect = false, ResyncInterval = 2.Seconds() },
            timer);
        var requested = new List<int>();
        app.StateRequested += requested.Add;

        AnnounceReady(1, "testapp");
        AnnounceReady(5, "testapp");
        requested.Clear();

        var tick = Assert.Single(timer.Every).Callback;
        tick();

        Assert.Equal(new[] { 1, 5 }, requested);
    }

    [Fact]
    public void EmitWithoutAThrottleSendsDirectly()
    {
        using var capture = new SendCapture();
        using var app = App(new UiAppOptions { Bundle = Bundle(), PublishOnConnect = false });

        app.Emit(4, "t.update", new Dictionary<string, string> { ["x"] = "1" });

        var emit = Assert.Single(capture.Emits);
        Assert.Equal("t.update", emit.Event);
        Assert.Equal("1", emit.Data["x"]);
        Assert.Equal(1UL << 4, emit.Mask);
    }

    [Fact]
    public void RunScriptSendsBase64FramedSourceOnItsOwnEvent()
    {
        (string Event, Dictionary<string, string> Data, ulong Mask)? captured = null;
        string? scriptB64 = null;
        NetMessages.OnSend = (msgId, bytes, mask) =>
        {
            if (msgId != 148) return;
            var msg = CUserMsg_CustomGameEvent.Parser.ParseFrom(bytes);
            if (msg.EventName == "dw.ui.script")
                scriptB64 = msg.Data.ToStringUtf8();
        };
        try
        {
            using var app = App(new UiAppOptions { Bundle = Bundle(), PublishOnConnect = false });
            app.RunScript(3, "$('#x').text = 'hi';");

            Assert.NotNull(scriptB64);
            // Base64 of the exact source, so any quotes/newlines survive the wire.
            var decoded = System.Text.Encoding.UTF8.GetString(System.Convert.FromBase64String(scriptB64!));
            Assert.Equal("$('#x').text = 'hi';", decoded);
        }
        finally { NetMessages.OnSend = null; }
        _ = captured;
    }

    // --- panel-side helpers (UiPanelRuntime) ---
    //
    // Hand-written ui.js needs Deadworks.Container/Create to add panels that
    // the host's own tree rebuild will not delete. They ride the script channel
    // so they reach players without republishing the host bundle.

    /// <summary>Every message sent, in order, as (event, decoded script or null, mask).</summary>
    private sealed class ScriptCapture : IDisposable
    {
        public readonly List<(string Event, string? Script, ulong Mask)> Sent = new();

        public ScriptCapture()
        {
            NetMessages.OnSend = (msgId, bytes, mask) =>
            {
                if (msgId != 148) return;
                var msg = CUserMsg_CustomGameEvent.Parser.ParseFrom(bytes);
                string? script = msg.EventName == "dw.ui.script"
                    ? System.Text.Encoding.UTF8.GetString(System.Convert.FromBase64String(msg.Data.ToStringUtf8()))
                    : null;
                Sent.Add((msg.EventName, script, mask));
            };
        }

        public void Dispose() => NetMessages.OnSend = null;
    }

    [Fact]
    public void AnnouncingPanelsGetTheHelpersBeforeTheirState()
    {
        using var capture = new ScriptCapture();
        using var app = App(new UiAppOptions { Bundle = Bundle(), PublishOnConnect = false });
        app.StateRequested += slot => app.Emit(slot, "t.state", new Dictionary<string, string>());

        AnnounceReady(2, "testapp");

        // The channel is ordered, so "helpers first" on the server is
        // "helpers first" in the panel - a served script may use them on its
        // very first run.
        int helper = capture.Sent.FindIndex(s => s.Script is not null && s.Script.Contains("Deadworks.Container"));
        int state = capture.Sent.FindIndex(s => s.Event == "dw.ui.emit");
        Assert.True(helper >= 0, "the helpers were never sent");
        Assert.True(state >= 0, "the state push was never sent");
        Assert.True(helper < state, "the helpers must go out before any state");
        Assert.Equal(1UL << 2, capture.Sent[helper].Mask);      // only the announcing panel
    }

    [Fact]
    public void TheHelpersDefineCreateRootAndContainer()
    {
        using var capture = new ScriptCapture();
        using var app = App(new UiAppOptions { Bundle = Bundle(), PublishOnConnect = false });

        AnnounceReady(0, "testapp");

        var script = capture.Sent.Single(s => s.Script is not null).Script!;
        Assert.Contains("Deadworks.Create =", script);
        Assert.Contains("Deadworks.Root =", script);
        Assert.Contains("Deadworks.Container =", script);
        // No helper, no client module, no panel: it must define nothing rather
        // than throw in someone's HUD.
        Assert.Contains("typeof Deadworks === \"undefined\"", script);
    }

    [Fact]
    public void EmitTableSendsTheFlattenedTable()
    {
        using var capture = new SendCapture();
        using var app = App(new UiAppOptions { Bundle = Bundle(), PublishOnConnect = false });

        app.EmitTable(2, "t.rows", new UiTable().AddRow(new() { ["name"] = "Alice" }));

        var emit = Assert.Single(capture.Emits);
        Assert.Equal("1", emit.Data["n"]);
        Assert.Equal("Alice", emit.Data["r0.name"]);
    }

    [Fact]
    public void EmitAllLiveTargetsOnlyLivePanels()
    {
        using var capture = new SendCapture();
        using var app = App(new UiAppOptions { Bundle = Bundle(), PublishOnConnect = false });

        AnnounceReady(1, "testapp");
        AnnounceReady(6, "testapp");

        app.EmitAllLive("t.update", slot => new Dictionary<string, string> { ["slot"] = slot.ToString() });

        Assert.Equal(2, capture.Emits.Count);
        Assert.Equal(1UL << 1, capture.Emits[0].Mask);
        Assert.Equal("1", capture.Emits[0].Data["slot"]);
        Assert.Equal(1UL << 6, capture.Emits[1].Mask);
    }

    [Fact]
    public void ThrottledBurstsCoalesceAndFlushTheLatestPayload()
    {
        long now = 0;
        var timer = new FakeTimer();
        using var capture = new SendCapture();
        using var app = App(
            new UiAppOptions { Bundle = Bundle(), PublishOnConnect = false, MinEmitInterval = 100.Milliseconds() },
            timer, () => now);

        Players.SetConnected(7, true);
        try
        {
            app.Emit(7, "t.update", new Dictionary<string, string> { ["m"] = "a" });
            now = 30;
            app.Emit(7, "t.update", new Dictionary<string, string> { ["m"] = "b" });
            now = 60;
            app.Emit(7, "t.update", new Dictionary<string, string> { ["m"] = "c" });

            Assert.Single(capture.Emits);                 // only the first went out
            var flush = Assert.Single(timer.Once);        // one flush scheduled

            now = 100;
            flush.Callback();
        }
        finally { Players.SetConnected(7, false); }

        Assert.Equal(2, capture.Emits.Count);
        Assert.Equal("c", capture.Emits[1].Data["m"]);    // the latest payload won
    }

    [Fact]
    public void AFlushForADisconnectedPlayerIsDropped()
    {
        long now = 0;
        var timer = new FakeTimer();
        using var capture = new SendCapture();
        using var app = App(
            new UiAppOptions { Bundle = Bundle(), PublishOnConnect = false, MinEmitInterval = 100.Milliseconds() },
            timer, () => now);

        app.Emit(7, "t.update", new Dictionary<string, string> { ["m"] = "a" });
        now = 30;
        app.Emit(7, "t.update", new Dictionary<string, string> { ["m"] = "b" });

        now = 100;
        Assert.Single(timer.Once).Callback();   // slot 7 was never marked connected

        Assert.Single(capture.Emits);
    }

    [Fact]
    public void ThrottleRequiresARealTimeDuration()
    {
        Assert.Throws<ArgumentException>(() => App(new UiAppOptions
        {
            Bundle = Bundle(),
            PublishOnConnect = false,
            MinEmitInterval = 64.Ticks(),
        }));
    }

    // --- server-built trees ---

    [Fact]
    public void SetTreeBypassesTheThrottle()
    {
        // A tree ships in ordered chunks; the coalescing throttle would drop
        // some, so tree pushes must go around it.
        long now = 0;
        var timer = new FakeTimer();
        using var capture = new SendCapture();
        using var app = App(
            new UiAppOptions { Bundle = Bundle(), PublishOnConnect = false, MinEmitInterval = 100.Milliseconds() },
            timer, () => now);

        var tree = Ui.Panel().Add(Ui.Label("a"));
        app.SetTree(3, tree);
        app.SetTree(3, tree);   // immediately after - would coalesce if throttled

        Assert.Equal(2, capture.Emits.Count);
        Assert.All(capture.Emits, e => Assert.Equal("dw.tree.set", e.Event));
        // Revisions are distinct so the runtime can discard the stale one.
        Assert.NotEqual(capture.Emits[0].Data["rev"], capture.Emits[1].Data["rev"]);
    }

    [Fact]
    public void SetMarkupParsesAndSendsATree()
    {
        using var capture = new SendCapture();
        using var app = App(new UiAppOptions { Bundle = Bundle(), PublishOnConnect = false });

        app.SetMarkup(3, """<panel><label id="msg">hi</label></panel>""", "#msg { color: red; }");

        var emit = Assert.Single(capture.Emits);
        Assert.Equal("dw.tree.set", emit.Event);
        Assert.Equal("2", emit.Data["n"]);
        Assert.Equal("Label", emit.Data["r1.t"]);
        Assert.Equal("hi", emit.Data["r1.txt"]);
        Assert.Contains("color: red", emit.Data["r1.sty"]);   // css baked in server-side
    }

    [Fact]
    public void ServeMarkupFileRepushesWhenEitherFileChanges()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"dwmk_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        string xml = Path.Combine(dir, "ui.xml");
        string css = Path.Combine(dir, "ui.css");
        File.WriteAllText(xml, """<panel><label id="m">one</label></panel>""");
        File.WriteAllText(css, "#m { color: #111; }");
        try
        {
            var timer = new FakeTimer();
            using var capture = new SendCapture();
            using var app = App(new UiAppOptions { Bundle = Bundle(), PublishOnConnect = false }, timer);
            app.ServeMarkupFile(xml, css);
            var poll = Assert.Single(timer.Every).Callback;

            AnnounceReady(2, "testapp");
            Assert.Equal("one", capture.Emits[^1].Data["r1.txt"]);
            Assert.Contains("color: #111", capture.Emits[^1].Data["r1.sty"]);

            // Editing the CSS alone re-pushes.
            capture.Emits.Clear();
            File.WriteAllText(css, "#m { color: #eee; }");
            File.SetLastWriteTimeUtc(css, DateTime.UtcNow.AddSeconds(2));
            poll();
            Assert.Contains("color: #eee", Assert.Single(capture.Emits).Data["r1.sty"]);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void UpdateNodeSendsATargetedRow()
    {
        using var capture = new SendCapture();
        using var app = App(new UiAppOptions { Bundle = Bundle(), PublishOnConnect = false });

        app.UpdateNode(2, "msg", text: "hi", visible: false);
        app.UpdateNode(2, "msg");   // nothing to change - no send

        var emit = Assert.Single(capture.Emits);
        Assert.Equal("dw.tree.update", emit.Event);
        Assert.Equal("msg", emit.Data["r0.id"]);
        Assert.Equal("hi", emit.Data["r0.txt"]);
        Assert.Equal("0", emit.Data["r0.vis"]);
        Assert.False(emit.Data.ContainsKey("r0.sty"));
    }

    [Fact]
    public void ServeTreeFilePushesOnReadyReloadsOnChangeAndSurvivesBadEdits()
    {
        string path = Path.Combine(Path.GetTempPath(), $"dwtree_{Guid.NewGuid():N}.json");
        File.WriteAllText(path, """{ "type": "Label", "id": "msg", "text": "one" }""");
        try
        {
            var timer = new FakeTimer();
            using var capture = new SendCapture();
            using var app = App(new UiAppOptions { Bundle = Bundle(), PublishOnConnect = false }, timer);
            app.ServeTreeFile(path);
            var poll = Assert.Single(timer.Every).Callback;

            // A panel announcing itself gets the current tree.
            AnnounceReady(4, "testapp");
            var first = Assert.Single(capture.Emits);
            Assert.Equal("dw.tree.set", first.Event);
            Assert.Equal("one", first.Data["r0.txt"]);

            // Editing the file re-pushes to every live panel. The mtime is
            // bumped explicitly - two writes can land in the same tick.
            capture.Emits.Clear();
            File.WriteAllText(path, """{ "type": "Label", "id": "msg", "text": "two" }""");
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(2));
            poll();
            var second = Assert.Single(capture.Emits);
            Assert.Equal("two", second.Data["r0.txt"]);

            // A broken edit is logged and the last good tree keeps serving.
            capture.Emits.Clear();
            File.WriteAllText(path, "{ not json");
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(4));
            poll();
            Assert.Empty(capture.Emits);

            AnnounceReady(4, "testapp");
            Assert.Equal("two", Assert.Single(capture.Emits).Data["r0.txt"]);
        }
        finally
        {
            File.Delete(path);
        }
    }
}

public class UiTreeCodecTests
{
    /// <summary>Reads the rows of one encoded chunk back out of its wire dictionary.</summary>
    private static List<Dictionary<string, string>> Rows(UiTable table)
    {
        var data = table.ToData();
        int n = int.Parse(data["n"]);
        var rows = new List<Dictionary<string, string>>();
        for (int i = 0; i < n; i++)
        {
            var row = new Dictionary<string, string>();
            string prefix = "r" + i + ".";
            foreach (var (key, value) in data)
                if (key.StartsWith(prefix, StringComparison.Ordinal))
                    row[key[prefix.Length..]] = value;
            rows.Add(row);
        }
        return rows;
    }

    [Fact]
    public void FlattensDepthFirstWithParentIndicesAndOmitsEmptyFields()
    {
        var tree = Ui.Panel().Style("flow-children: down;")
            .Add(Ui.Label("hello").Id("msg"))
            .Add(Ui.Button("Ping").OnClick("tree.ping")
                .Add(Ui.Image("s2r://x.vsvg").Visible(false)));

        var chunk = Assert.Single(UiTreeCodec.Encode(tree, rev: 7));
        var data = chunk.ToData();
        Assert.Equal("7", data["rev"]);
        Assert.Equal("0", data["chunk"]);
        Assert.Equal("1", data["of"]);

        var rows = Rows(chunk);
        Assert.Equal(4, rows.Count);

        Assert.Equal("Panel", rows[0]["t"]);
        Assert.Equal("-1", rows[0]["p"]);
        Assert.Equal("flow-children: down;", rows[0]["sty"]);
        Assert.False(rows[0].ContainsKey("txt"));    // empty fields are omitted
        Assert.False(rows[0].ContainsKey("vis"));    // visible is the default

        Assert.Equal("Label", rows[1]["t"]);
        Assert.Equal("0", rows[1]["p"]);
        Assert.Equal("msg", rows[1]["id"]);
        Assert.Equal("hello", rows[1]["txt"]);

        Assert.Equal("Button", rows[2]["t"]);
        Assert.Equal("tree.ping", rows[2]["act"]);

        Assert.Equal("Image", rows[3]["t"]);
        Assert.Equal("2", rows[3]["p"]);             // child of the button, depth-first
        Assert.Equal("s2r://x.vsvg", rows[3]["src"]);
        Assert.Equal("0", rows[3]["vis"]);
    }

    [Fact]
    public void EncodesHoverAndSubmitFieldsOmittingThemWhenAbsent()
    {
        var tree = Ui.Panel()
            .Add(Ui.Button("Buy").Id("buy").OnClick("shop.buy")
                .OnHover("shop.peek").OnHoverOut("shop.unpeek")
                .HoverStyle("background-color: #333;")
                .SubmitFrom("form1"))
            .Add(Ui.Label("plain"));

        var rows = Rows(Assert.Single(UiTreeCodec.Encode(tree, rev: 1)));

        Assert.Equal("shop.peek", rows[1]["hov"]);
        Assert.Equal("shop.unpeek", rows[1]["hout"]);
        Assert.Equal("background-color: #333;", rows[1]["hsty"]);
        Assert.Equal("form1", rows[1]["sub"]);

        foreach (var key in new[] { "hov", "hout", "hsty", "sub" })
            Assert.False(rows[2].ContainsKey(key), key + " must be omitted when unset");
    }

    [Fact]
    public void ChunksLargeTreesPreservingGlobalNodeOrder()
    {
        var root = Ui.Panel();
        for (int i = 0; i < 40; i++)
            root.Add(Ui.Label("row " + i).Id("n" + i).Style(new string('x', 100)));

        var chunks = UiTreeCodec.Encode(root, rev: 1);
        Assert.True(chunks.Count > 1, "a ~4KB tree must split");

        var all = new List<Dictionary<string, string>>();
        for (int i = 0; i < chunks.Count; i++)
        {
            var data = chunks[i].ToData();
            Assert.Equal(i.ToString(), data["chunk"]);
            Assert.Equal(chunks.Count.ToString(), data["of"]);
            Assert.Equal("1", data["rev"]);
            all.AddRange(Rows(chunks[i]));
        }

        // Concatenation in chunk order restores document order, which the
        // parent indices rely on.
        Assert.Equal(41, all.Count);
        Assert.Equal("Panel", all[0]["t"]);
        for (int i = 0; i < 40; i++)
        {
            Assert.Equal("n" + i, all[i + 1]["id"]);
            Assert.Equal("0", all[i + 1]["p"]);
        }
    }
}

public class UiMarkupTests
{
    [Fact]
    public void MapsTagsToNodeTypesAndReadsAttributes()
    {
        var root = UiMarkup.Parse("""
            <panel style="flow-children: down;">
              <label id="msg">Hello</label>
              <button text="Ping" onclick="my.ping"/>
              <img src="s2r://x.vsvg" visible="false"/>
            </panel>
            """);

        Assert.Equal("Panel", root.TypeName);
        Assert.Equal("flow-children: down;", root.StyleText);
        Assert.Equal(3, root.Children.Count);

        Assert.Equal("Label", root.Children[0].TypeName);
        Assert.Equal("msg", root.Children[0].NodeId);
        Assert.Equal("Hello", root.Children[0].Text);          // inner text

        Assert.Equal("Button", root.Children[1].TypeName);
        Assert.Equal("Ping", root.Children[1].Text);
        Assert.Equal("my.ping", root.Children[1].OnClickEvent); // onclick -> server event

        Assert.Equal("Image", root.Children[2].TypeName);
        Assert.Equal("s2r://x.vsvg", root.Children[2].Src);
        Assert.False(root.Children[2].IsVisible);
    }

    [Fact]
    public void MapsInputToTextEntryAndPassesUnknownTagsThrough()
    {
        var root = UiMarkup.Parse("""<panel><input id="q"/><ToggleButton id="t"/></panel>""");
        Assert.Equal("TextEntry", root.Children[0].TypeName);   // input alias
        Assert.Equal("ToggleButton", root.Children[1].TypeName); // passthrough
    }

    [Fact]
    public void AppliesCssBySelectorWithInlineWinningLast()
    {
        var root = UiMarkup.Parse(
            """<panel><label id="a" class="big warn" style="color: #111;">x</label></panel>""",
            """
            * { margin: 1px; }
            label { color: #999; }
            .big { font-size: 20px; }
            #a { color: #eee; }
            """);

        string style = root.Children[0].StyleText!;
        // Universal + tag + class + id all merged; inline color wins last.
        Assert.Contains("margin: 1px", style);
        Assert.Contains("font-size: 20px", style);
        // #a set color #eee (specificity 100 > tag), then inline #111 appended last.
        Assert.EndsWith("color: #111;", style);
    }

    [Fact]
    public void OnActivateIsAcceptedAsAnAliasForOnClick()
    {
        var root = UiMarkup.Parse("""<button onactivate="do.it">Go</button>""");
        Assert.Equal("do.it", root.OnClickEvent);
    }

    [Fact]
    public void ClassMatchingHandlesMultipleClasses()
    {
        var root = UiMarkup.Parse(
            """<panel class="a b c" />""",
            ".b { color: red; }");
        Assert.Contains("color: red", root.StyleText);
    }

    [Fact]
    public void ReadsHoverAndSubmitAttributes()
    {
        var root = UiMarkup.Parse("""
            <panel>
              <button id="peek" onclick="shop.buy" onmouseover="shop.peek" onmouseout="shop.unpeek"
                      hoverstyle="background-color: #333;" submit="form1">Buy</button>
            </panel>
            """);

        var btn = root.Children[0];
        Assert.Equal("shop.peek", btn.OnHoverEvent);
        Assert.Equal("shop.unpeek", btn.OnHoverOutEvent);
        Assert.Equal("background-color: #333;", btn.HoverStyleText);
        Assert.Equal("form1", btn.SubmitScopeId);
    }

    [Fact]
    public void HoverCssSelectorsBecomeTheNodesHoverStyle()
    {
        // .btn:hover rules land in HoverStyleText (merged by specificity like
        // base rules, hoverstyle attribute winning last) and never leak into
        // the base style.
        var root = UiMarkup.Parse(
            """<panel><button id="b" class="btn" hoverstyle="color: #fff;">Go</button></panel>""",
            """
            .btn { background-color: #111; }
            .btn:hover { background-color: #333; }
            #b:hover { border: 1px solid #0ff; }
            """);

        var btn = root.Children[0];
        Assert.Contains("background-color: #111", btn.StyleText);
        Assert.DoesNotContain("#333", btn.StyleText);

        Assert.Contains("background-color: #333", btn.HoverStyleText);
        Assert.Contains("border: 1px solid #0ff", btn.HoverStyleText);
        Assert.EndsWith("color: #fff;", btn.HoverStyleText);   // attribute wins last
    }

    [Fact]
    public void RejectsMalformedOrEmptyMarkup()
    {
        Assert.Throws<FormatException>(() => UiMarkup.Parse("<panel><label></panel>"));  // not well-formed
        Assert.Throws<FormatException>(() => UiMarkup.Parse(""));                        // empty
        // An unfamiliar tag is no longer an error - it passes through as a type.
        Assert.Equal("Slider", UiMarkup.Parse("<Slider/>").TypeName);
    }

    [Fact]
    public void StripsCssCommentsAndToleratesMissingCss()
    {
        var root = UiMarkup.Parse(
            """<label class="x">hi</label>""",
            "/* a comment */ .x { color: blue; } /* trailing */");
        Assert.Contains("color: blue", root.StyleText);

        // No CSS at all is fine.
        Assert.Null(UiMarkup.Parse("<panel/>").StyleText);
    }

    [Fact]
    public void ProducesATreeThatEncodesLikeAnyOther()
    {
        // The whole point: markup rides the existing tree transport unchanged.
        var root = UiMarkup.Parse("""<panel><label>hi</label></panel>""");
        var chunk = Assert.Single(UiTreeCodec.Encode(root, rev: 1));
        Assert.Equal("2", chunk.ToData()["n"]);
    }
}

public class UiBundleDefaultsTests
{
    [Fact]
    public void CacheKeysDefaultToTheProvenReloadTarget()
    {
        // hud_health is the one panel the whole pipeline is proven against;
        // an empty default silently mounts content that never appears.
        var bundle = new UiBundle
        {
            Id = "x",
            Url = "https://cdn.example/a.vpk",
            Sha256 = new string('a', 64),
        };

        Assert.Equal(new[] { @"panorama\layout\hud_health.xml" }, bundle.CacheKeys);
    }

    [Fact]
    public void FromManifestReadsTheBuildToolsOutput()
    {
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");
        File.WriteAllText(path, """
            {
              "id": "myui",
              "url": "https://cdn.example/myui_3fa1b2c8.vpk",
              "sha256": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
              "cacheKeys": ["panorama\\layout\\hud_health.xml"]
            }
            """);
        try
        {
            var bundle = UiBundle.FromManifest(path);

            Assert.Equal("myui", bundle.Id);
            Assert.Equal("https://cdn.example/myui_3fa1b2c8.vpk", bundle.Url);
            Assert.Equal(new string('a', 64), bundle.Sha256);
            Assert.Equal(new[] { @"panorama\layout\hud_health.xml" }, bundle.CacheKeys);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void FromManifestFailsEarlyNamingTheFile()
    {
        // A bad manifest must fail at load, with the path in the message -
        // not at publish time with a bare field name.
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");
        File.WriteAllText(path, """{ "id": "myui" }""");
        try
        {
            var ex = Assert.Throws<ArgumentException>(() => UiBundle.FromManifest(path));
            Assert.Contains(path, ex.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }
}

public class UiActionDataTests
{
    private static IReadOnlyDictionary<string, string> Data(params (string K, string V)[] pairs)
    {
        var d = new Dictionary<string, string>();
        foreach (var (k, v) in pairs) d[k] = v;
        return d;
    }

    [Fact]
    public void IntParsesClampsAndFallsBack()
    {
        var data = Data(("count", "7"), ("big", "999"), ("junk", "x7"));

        Assert.Equal(7, data.Int("count", 0, 10, fallback: 1));
        Assert.Equal(10, data.Int("big", 0, 10, fallback: 1));      // clamped
        Assert.Equal(1, data.Int("junk", 0, 10, fallback: 1));      // unparsable
        Assert.Equal(1, data.Int("missing", 0, 10, fallback: 1));
    }

    [Fact]
    public void OneOfWhitelistsWithTheFirstEntryAsDefault()
    {
        var data = Data(("column", "deaths"), ("evil", "'; drop"));

        Assert.Equal("deaths", data.OneOf("column", "kills", "deaths"));
        Assert.Equal("kills", data.OneOf("evil", "kills", "deaths"));    // not allowed -> default
        Assert.Equal("kills", data.OneOf("missing", "kills", "deaths"));
        Assert.Throws<ArgumentException>(() => data.OneOf("column"));    // no whitelist is author error
    }

    [Fact]
    public void FlagAndTextHandleClientNoise()
    {
        var data = Data(("on", "1"), ("off", "false"), ("noise", "banana"), ("name", "  Alice  "));

        Assert.True(data.Flag("on"));
        Assert.False(data.Flag("off", fallback: true));
        Assert.True(data.Flag("noise", fallback: true));                 // unrecognised -> fallback
        Assert.False(data.Flag("missing"));

        Assert.Equal("Alice", data.Text("name"));
        Assert.Equal("Al", data.Text("name", maxLength: 2));             // hard cap on client strings
        Assert.Equal("", data.Text("missing"));
    }
}

public class PluginDataDirectoryTests
{
    private sealed class ProbePlugin : IDeadworksPlugin
    {
        public string Name => "probe";
        public void OnLoad(bool isReload) { }
        public void OnUnload() { }
    }

    [Fact]
    public void ResolvesThroughTheHostAndCreatesTheFolder()
    {
        // Plugins load from memory (Assembly.Location is empty) and managed/
        // is wiped post-build, so the host owns the answer to "where can I
        // keep my files" - plugins must never have to probe for it.
        string root = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        ConfigResolver.GetDataDirectory = p => Path.Combine(root, p.GetType().Name);
        try
        {
            string dir = new ProbePlugin().GetDataDirectory();

            Assert.Equal(Path.Combine(root, nameof(ProbePlugin)), dir);
            Assert.True(Directory.Exists(dir), "the folder is created on demand");
        }
        finally
        {
            ConfigResolver.GetDataDirectory = null;
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ThrowsWhenTheHostHasNotInitialisedIt()
    {
        Assert.Throws<InvalidOperationException>(() => new ProbePlugin().GetDataDirectory());
    }

    [Fact]
    public void TheHostMapsItBesideThePluginDll()
    {
        // Plugin files live where modders expect them: in a folder named
        // after the plugin, next to the DLL - managed\plugins\<Name>\.
        DeadworksManaged.ConfigManager.Initialize();
        string? dir = null;
        try
        {
            dir = new ProbePlugin().GetDataDirectory();
            Assert.EndsWith(Path.Combine("plugins", "probe"), dir);
        }
        finally
        {
            ConfigResolver.GetDataDirectory = null;
            if (dir is not null && Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }
}

public class UiTreeFileTests
{
    [Fact]
    public void ParsesTheDocumentedShapeIncludingCommentsAndTrailingCommas()
    {
        const string json = """
            // hand-edited files get comments and trailing commas
            { "type": "Panel", "style": "flow-children: down;",
              "children": [
                { "TYPE": "Label", "id": "msg", "text": "Hello" },
                { "type": "button", "text": "Ping", "onclick": "tree.ping", "visible": false },
              ],
            }
            """;

        var root = UiTreeFile.Parse(json);

        Assert.Equal("Panel", root.TypeName);
        Assert.Equal("flow-children: down;", root.StyleText);
        Assert.Equal(2, root.Children.Count);

        Assert.Equal("Label", root.Children[0].TypeName);   // keys are case-insensitive
        Assert.Equal("msg", root.Children[0].NodeId);
        Assert.Equal("Hello", root.Children[0].Text);

        Assert.Equal("Button", root.Children[1].TypeName);
        Assert.Equal("tree.ping", root.Children[1].OnClickEvent);
        Assert.False(root.Children[1].IsVisible);
    }

    [Fact]
    public void RejectsMissingTypeButPassesUnknownTypesThrough()
    {
        Assert.Throws<FormatException>(() => UiTreeFile.Parse("""{ "text": "no type" }"""));
        Assert.Throws<FormatException>(() => UiTreeFile.Parse("[1, 2]"));
        // Any Panorama type name is allowed now, not just the four aliases.
        Assert.Equal("Slider", UiTreeFile.Parse("""{ "type": "Slider" }""").TypeName);
    }
}
