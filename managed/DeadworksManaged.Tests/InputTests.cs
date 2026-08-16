using DeadworksManaged.Api;
using Xunit;

namespace DeadworksManaged.Tests;

/// <summary>
/// The arbitrary-key input API: a plugin registers keys with a policy, the
/// client module enforces the policy locally and reports presses back over the
/// 280 channel. Shares NetMessages.OnSend, and parallelization is disabled
/// assembly-wide, so the hook is safe here.
/// </summary>
public class InputTests : IDisposable
{
    public void Dispose()
    {
        NetMessages.OnSend = null;
        Input.ResetForTests();
    }

    private sealed class DummyPlugin : IDeadworksPlugin
    {
        public string Name => "dummy";
        public void OnLoad(bool isReload) { }
        public void OnUnload() { }
    }

    /// <summary>Collects every dw.input.* control message sent while alive.</summary>
    private sealed class BindCapture : IDisposable
    {
        public readonly List<(string Event, Dictionary<string, string> Fields, ulong Mask)> Sent = new();

        public BindCapture()
        {
            NetMessages.OnSend = (msgId, bytes, mask) =>
            {
                if (msgId != 148) return;
                var msg = CUserMsg_CustomGameEvent.Parser.ParseFrom(bytes);
                if (!msg.EventName.StartsWith("dw.input.")) return;

                var entries = UiPayload.Parse(msg.Data.ToByteArray())!;
                var fields = new Dictionary<string, string>();
                foreach (var (key, value) in entries)
                    fields[key] = value;
                Sent.Add((msg.EventName, fields, mask));
            };
        }

        public void Dispose() => NetMessages.OnSend = null;
    }

    /// <summary>Feeds a key report in as if it arrived from the client module.</summary>
    private static void ReportKey(int slot, Key key, KeyEdge edge, KeyModifiers mods = KeyModifiers.None)
    {
        var payload = $"v=1\nkey={(int)key}\nedge={(int)edge}\nmods={(int)mods}\n";
        Input.HandleKeyReport(slot, System.Text.Encoding.UTF8.GetBytes(payload));
    }

    [Fact]
    public void RegisteringAKeyTellsClientsToListenForIt()
    {
        using var capture = new BindCapture();
        var keys = Input.For(new DummyPlugin());

        keys.On(Key.K, e => { });

        var sent = Assert.Single(capture.Sent);
        Assert.Equal("dw.input.bind", sent.Event);
        Assert.Equal(((int)Key.K).ToString(), sent.Fields["key"]);
        Assert.Equal(((int)KeyPolicy.Listen).ToString(), sent.Fields["policy"]);
        Assert.Equal(ulong.MaxValue, sent.Mask);   // everyone by default
    }

    [Fact]
    public void BlockSendsABlockPolicyAndNeedsNoHandler()
    {
        using var capture = new BindCapture();
        var keys = Input.For(new DummyPlugin());

        keys.Block(Key.F5, RecipientFilter.Single(3));

        var sent = Assert.Single(capture.Sent);
        Assert.Equal("dw.input.bind", sent.Event);
        Assert.Equal(((int)Key.F5).ToString(), sent.Fields["key"]);
        Assert.Equal(((int)KeyPolicy.Block).ToString(), sent.Fields["policy"]);
        Assert.Equal(1ul << 3, sent.Mask);
    }

    [Fact]
    public void AKeyReportReachesTheHandlerWithSlotEdgeAndModifiers()
    {
        var keys = Input.For(new DummyPlugin());
        var seen = new List<KeyEvent>();
        keys.On(Key.K, KeyPolicy.BlockAndListen, seen.Add);

        ReportKey(5, Key.K, KeyEdge.Down, KeyModifiers.Shift | KeyModifiers.Ctrl);
        ReportKey(5, Key.K, KeyEdge.Up);

        Assert.Equal(2, seen.Count);
        Assert.Equal(5, seen[0].PlayerSlot);
        Assert.Equal(Key.K, seen[0].Key);
        Assert.Equal(KeyEdge.Down, seen[0].Edge);
        Assert.Equal(KeyModifiers.Shift | KeyModifiers.Ctrl, seen[0].Modifiers);
        Assert.Equal(KeyEdge.Up, seen[1].Edge);
        Assert.Equal(KeyModifiers.None, seen[1].Modifiers);
    }

    [Fact]
    public void OnlyTheHandlersForThatKeyRun()
    {
        var keys = Input.For(new DummyPlugin());
        int k = 0, j = 0;
        keys.On(Key.K, _ => k++);
        keys.On(Key.J, _ => j++);

        ReportKey(0, Key.K, KeyEdge.Down);

        Assert.Equal(1, k);
        Assert.Equal(0, j);
    }

    [Fact]
    public void CancellingABindUnbindsItOnClientsAndStopsTheHandler()
    {
        using var capture = new BindCapture();
        var keys = Input.For(new DummyPlugin());
        int fired = 0;
        var handle = keys.On(Key.K, _ => fired++);
        capture.Sent.Clear();

        handle.Cancel();

        var sent = Assert.Single(capture.Sent);
        Assert.Equal("dw.input.unbind", sent.Event);
        Assert.Equal(((int)Key.K).ToString(), sent.Fields["key"]);

        ReportKey(0, Key.K, KeyEdge.Down);
        Assert.Equal(0, fired);
    }

    [Fact]
    public void DisposingUnbindsEverythingThePluginRegistered()
    {
        using var capture = new BindCapture();
        var keys = Input.For(new DummyPlugin());
        int fired = 0;
        keys.On(Key.K, _ => fired++);
        keys.Block(Key.F5);
        capture.Sent.Clear();

        keys.Dispose();

        Assert.Equal(2, capture.Sent.Count);
        Assert.All(capture.Sent, s => Assert.Equal("dw.input.unbind", s.Event));

        ReportKey(0, Key.K, KeyEdge.Down);
        Assert.Equal(0, fired);
    }

    [Fact]
    public void AJoiningPlayerIsSentTheCurrentBinds()
    {
        using var capture = new BindCapture();
        var keys = Input.For(new DummyPlugin());
        keys.On(Key.K, _ => { });
        keys.Block(Key.F5);
        capture.Sent.Clear();

        Input.HandleConnect(7);

        // Both binds re-pushed, to that player only.
        Assert.Equal(2, capture.Sent.Count);
        Assert.All(capture.Sent, s => Assert.Equal("dw.input.bind", s.Event));
        Assert.All(capture.Sent, s => Assert.Equal(1ul << 7, s.Mask));
    }

    [Fact]
    public void APlayerOnlyGetsBindsAddressedToThem()
    {
        using var capture = new BindCapture();
        var keys = Input.For(new DummyPlugin());
        keys.Block(Key.F5, RecipientFilter.Single(3));
        capture.Sent.Clear();

        Input.HandleConnect(7);   // a different slot

        Assert.Empty(capture.Sent);
    }

    [Fact]
    public void AHandlerThatThrowsDoesNotStopTheOthers()
    {
        var keys = Input.For(new DummyPlugin());
        int second = 0;
        keys.On(Key.K, _ => throw new InvalidOperationException("boom"));
        keys.On(Key.K, _ => second++);

        ReportKey(0, Key.K, KeyEdge.Down);

        Assert.Equal(1, second);
    }

    [Fact]
    public void MalformedOrUnknownReportsAreIgnored()
    {
        var keys = Input.For(new DummyPlugin());
        int fired = 0;
        keys.On(Key.K, _ => fired++);

        // Not a payload at all, wrong version, and a key nobody registered.
        Input.HandleKeyReport(0, System.Text.Encoding.UTF8.GetBytes("garbage"));
        Input.HandleKeyReport(0, System.Text.Encoding.UTF8.GetBytes("v=9\nkey=75\nedge=0\nmods=0\n"));
        ReportKey(0, Key.J, KeyEdge.Down);

        Assert.Equal(0, fired);
    }

    [Fact]
    public void RebindingAKeyReplacesItsPolicy()
    {
        using var capture = new BindCapture();
        var keys = Input.For(new DummyPlugin());
        keys.On(Key.K, KeyPolicy.Listen, _ => { });
        capture.Sent.Clear();

        keys.On(Key.K, KeyPolicy.BlockAndListen, _ => { });

        var sent = Assert.Single(capture.Sent);
        Assert.Equal(((int)KeyPolicy.BlockAndListen).ToString(), sent.Fields["policy"]);
    }

    // --- SetPolicy: blocking a key only in certain circumstances ---
    //
    // The client decides at press time, so the server cannot block the press it
    // is handling - it changes the policy for what comes next. These cover that
    // the change reaches the right players and survives a reconnect.

    [Fact]
    public void SetPolicyBlocksAKeyForOnePlayerWithoutTouchingTheOthers()
    {
        using var capture = new BindCapture();
        var keys = Input.For(new DummyPlugin());
        keys.On(Key.Tab, KeyPolicy.Listen, _ => { });
        capture.Sent.Clear();

        keys.SetPolicy(Key.Tab, KeyPolicy.BlockAndListen, RecipientFilter.Single(4));

        var sent = Assert.Single(capture.Sent);
        Assert.Equal("dw.input.bind", sent.Event);
        Assert.Equal(((int)Key.Tab).ToString(), sent.Fields["key"]);
        Assert.Equal(((int)KeyPolicy.BlockAndListen).ToString(), sent.Fields["policy"]);
        Assert.Equal(1ul << 4, sent.Mask);          // only that player
    }

    [Fact]
    public void SetPolicyLeavesTheHandlerRegistered()
    {
        var keys = Input.For(new DummyPlugin());
        int fired = 0;
        keys.On(Key.Tab, KeyPolicy.Listen, _ => fired++);

        keys.SetPolicy(Key.Tab, KeyPolicy.BlockAndListen, RecipientFilter.Single(2));
        ReportKey(2, Key.Tab, KeyEdge.Down);

        Assert.Equal(1, fired);
    }

    [Fact]
    public void AJoinerGetsTheBasePolicyFirstAndTheOverrideLast()
    {
        // The client keeps one policy per key, so the override has to be the
        // last word on a reconnect or the block would silently vanish.
        var keys = Input.For(new DummyPlugin());
        keys.On(Key.Tab, KeyPolicy.Listen, _ => { });
        keys.SetPolicy(Key.Tab, KeyPolicy.BlockAndListen, RecipientFilter.Single(6));

        using var capture = new BindCapture();
        Input.HandleConnect(6);

        Assert.Equal(2, capture.Sent.Count);
        Assert.Equal(((int)KeyPolicy.Listen).ToString(), capture.Sent[0].Fields["policy"]);
        Assert.Equal(((int)KeyPolicy.BlockAndListen).ToString(), capture.Sent[1].Fields["policy"]);
    }

    [Fact]
    public void AnOverrideForAnotherPlayerIsNotReplayedToThisOne()
    {
        var keys = Input.For(new DummyPlugin());
        keys.On(Key.Tab, KeyPolicy.Listen, _ => { });
        keys.SetPolicy(Key.Tab, KeyPolicy.BlockAndListen, RecipientFilter.Single(6));

        using var capture = new BindCapture();
        Input.HandleConnect(7);

        var sent = Assert.Single(capture.Sent);
        Assert.Equal(((int)KeyPolicy.Listen).ToString(), sent.Fields["policy"]);
    }

    [Fact]
    public void AnOverrideDiesWithThePlayerItWasAimedAt()
    {
        // Slots are reused: the next player in slot 6 must not inherit a block
        // set for whoever was there before.
        var keys = Input.For(new DummyPlugin());
        keys.On(Key.Tab, KeyPolicy.Listen, _ => { });
        keys.SetPolicy(Key.Tab, KeyPolicy.BlockAndListen, RecipientFilter.Single(6));

        Input.HandleDisconnect(6);

        using var capture = new BindCapture();
        Input.HandleConnect(6);

        var sent = Assert.Single(capture.Sent);
        Assert.Equal(((int)KeyPolicy.Listen).ToString(), sent.Fields["policy"]);
    }

    [Fact]
    public void DisconnectOnlyClearsThatPlayerFromASharedOverride()
    {
        var keys = Input.For(new DummyPlugin());
        keys.On(Key.Tab, KeyPolicy.Listen, _ => { });
        keys.SetPolicy(Key.Tab, KeyPolicy.BlockAndListen, new RecipientFilter { Mask = (1ul << 1) | (1ul << 2) });

        Input.HandleDisconnect(1);

        using var capture = new BindCapture();
        Input.HandleConnect(2);

        Assert.Equal(2, capture.Sent.Count);
        Assert.Equal(((int)KeyPolicy.BlockAndListen).ToString(), capture.Sent[1].Fields["policy"]);
    }

    [Fact]
    public void RebindingAKeyDropsItsOverrideSoTheTwoCannotDisagree()
    {
        var keys = Input.For(new DummyPlugin());
        keys.On(Key.Tab, KeyPolicy.Listen, _ => { });
        keys.SetPolicy(Key.Tab, KeyPolicy.BlockAndListen, RecipientFilter.Single(3));

        // Re-declaring the key is the plugin saying how it behaves now.
        keys.On(Key.Tab, KeyPolicy.Listen, _ => { });

        using var capture = new BindCapture();
        Input.HandleConnect(3);

        var sent = Assert.Single(capture.Sent);
        Assert.Equal(((int)KeyPolicy.Listen).ToString(), sent.Fields["policy"]);
    }

    // --- mouse wheel ---
    //
    // The wheel has no Windows virtual key, so its codes sit past the end of
    // the VK range; a notch is instantaneous and only ever reports Down.

    [Fact]
    public void WheelDirectionsBindLikeAnyOtherKey()
    {
        using var capture = new BindCapture();
        var keys = Input.For(new DummyPlugin());

        keys.On(Key.WheelUp, KeyPolicy.BlockAndListen, _ => { });

        // The literal codes are the contract with the client module's
        // kWheelUp/Down/Left/Right - changing one side alone breaks the wheel.
        var sent = Assert.Single(capture.Sent);
        Assert.Equal("256", sent.Fields["key"]);                 // 0x100
        Assert.Equal(((int)KeyPolicy.BlockAndListen).ToString(), sent.Fields["policy"]);
        Assert.Equal(257, (int)Key.WheelDown);                   // 0x101
        Assert.Equal(258, (int)Key.WheelLeft);                   // 0x102
        Assert.Equal(259, (int)Key.WheelRight);                  // 0x103
    }

    [Fact]
    public void WheelCodesCannotCollideWithARealKey()
    {
        // Everything Windows can report is <= 0xFF; ours must be above it.
        foreach (var wheel in new[] { Key.WheelUp, Key.WheelDown, Key.WheelLeft, Key.WheelRight })
            Assert.True((int)wheel > 0xFF, $"{wheel} would collide with a virtual key");
    }

    [Fact]
    public void EachWheelNotchArrivesAsItsOwnDownEvent()
    {
        var keys = Input.For(new DummyPlugin());
        var seen = new List<KeyEvent>();
        keys.On(Key.WheelDown, KeyPolicy.BlockAndListen, seen.Add);

        // A flick reports one message per notch, as the module queues them.
        ReportKey(2, Key.WheelDown, KeyEdge.Down);
        ReportKey(2, Key.WheelDown, KeyEdge.Down);

        Assert.Equal(2, seen.Count);
        Assert.All(seen, e => Assert.Equal(KeyEdge.Down, e.Edge));
        Assert.All(seen, e => Assert.Equal(Key.WheelDown, e.Key));
    }

    [Fact]
    public void ScrollingUpDoesNotReachTheScrollDownHandler()
    {
        var keys = Input.For(new DummyPlugin());
        int up = 0, down = 0;
        keys.On(Key.WheelUp, _ => up++);
        keys.On(Key.WheelDown, _ => down++);

        ReportKey(0, Key.WheelUp, KeyEdge.Down);

        Assert.Equal(1, up);
        Assert.Equal(0, down);
    }

    [Fact]
    public void TheWheelCanBeTakenOverForOnePlayerAndGivenBack()
    {
        using var capture = new BindCapture();
        var keys = Input.For(new DummyPlugin());
        keys.On(Key.WheelUp, KeyPolicy.Listen, _ => { });
        capture.Sent.Clear();

        keys.SetPolicy(Key.WheelUp, KeyPolicy.BlockAndListen, RecipientFilter.Single(5));
        keys.SetPolicy(Key.WheelUp, KeyPolicy.Listen, RecipientFilter.Single(5));

        Assert.Equal(2, capture.Sent.Count);
        Assert.Equal(((int)KeyPolicy.BlockAndListen).ToString(), capture.Sent[0].Fields["policy"]);
        Assert.Equal(((int)KeyPolicy.Listen).ToString(), capture.Sent[1].Fields["policy"]);
        Assert.All(capture.Sent, s => Assert.Equal(1ul << 5, s.Mask));
    }

    [Fact]
    public void DisposeReleasesAKeyThatOnlyEverHadAnOverride()
    {
        using var capture = new BindCapture();
        var keys = Input.For(new DummyPlugin());
        keys.SetPolicy(Key.F5, KeyPolicy.Block, RecipientFilter.Single(1));
        capture.Sent.Clear();

        keys.Dispose();

        var sent = Assert.Single(capture.Sent);
        Assert.Equal("dw.input.unbind", sent.Event);
        Assert.Equal(((int)Key.F5).ToString(), sent.Fields["key"]);
        Assert.Equal(1ul << 1, sent.Mask);
    }
}
