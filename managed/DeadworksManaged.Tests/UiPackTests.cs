using System.Security.Cryptography;
using DeadworksManaged.Api;
using Xunit;

namespace DeadworksManaged.Tests;

/// <summary>
/// The phase-2 image-push wire: a VPK chunked into dw.ui.pack payloads
/// { id, sha256, n, i, b } the client module reassembles, verifies and mounts.
/// Uses NetMessages.OnSend like the UiApp tests; parallelization is disabled
/// assembly-wide, so the shared hook is safe here.
/// </summary>
public class UiPackSenderTests
{
    /// <summary>Collects every dw.ui.pack chunk sent while it is alive.</summary>
    private sealed class PackCapture : IDisposable
    {
        public readonly List<(Dictionary<string, string> Fields, ulong Mask)> Chunks = new();

        public PackCapture()
        {
            NetMessages.OnSend = (msgId, bytes, mask) =>
            {
                if (msgId != 148) return;
                var msg = CUserMsg_CustomGameEvent.Parser.ParseFrom(bytes);
                if (msg.EventName != "dw.ui.pack") return;

                var entries = UiPayload.Parse(msg.Data.ToByteArray())!;
                var fields = new Dictionary<string, string>();
                foreach (var (key, value) in entries)
                    fields[key] = value;
                Chunks.Add((fields, mask));
            };
        }

        public void Dispose() => NetMessages.OnSend = null;
    }

    [Fact]
    public void ChunksAVpkIntoOrderedBase64SlicesTheClientCanReassemble()
    {
        using var capture = new PackCapture();

        // Big enough that the base64 spans several chunks.
        byte[] vpk = new byte[3000];
        for (int i = 0; i < vpk.Length; i++) vpk[i] = (byte)(i * 31);
        string sha = Convert.ToHexStringLower(SHA256.HashData(vpk));

        UI.PushPack("imgs", vpk, sha, RecipientFilter.Single(3));

        string b64 = Convert.ToBase64String(vpk);
        int expected = (b64.Length + UI.PackChunkChars - 1) / UI.PackChunkChars;
        Assert.True(expected >= 2, "the fixture must actually chunk");
        Assert.Equal(expected, capture.Chunks.Count);

        var joined = new System.Text.StringBuilder();
        for (int i = 0; i < capture.Chunks.Count; i++)
        {
            var (fields, mask) = capture.Chunks[i];
            Assert.Equal(1ul << 3, mask);
            Assert.Equal("imgs", fields["id"]);
            Assert.Equal(sha, fields["sha256"]);
            Assert.Equal(expected.ToString(), fields["n"]);
            Assert.Equal(i.ToString(), fields["i"]);
            joined.Append(fields["b"]);
        }

        // The client concatenates the slices in order and decodes once.
        Assert.Equal(b64, joined.ToString());
        Assert.Equal(vpk, Convert.FromBase64String(joined.ToString()));
    }

    [Fact]
    public void NormalizesTheHashToLowercaseOnTheWire()
    {
        using var capture = new PackCapture();
        byte[] vpk = { 1, 2, 3 };
        string sha = Convert.ToHexStringLower(SHA256.HashData(vpk));

        UI.PushPack("imgs", vpk, sha.ToUpperInvariant(), RecipientFilter.Single(0));

        var (fields, _) = Assert.Single(capture.Chunks);
        Assert.Equal(sha, fields["sha256"]);
    }

    [Fact]
    public void RejectsAMissingIdEmptyVpkOrMalformedHash()
    {
        string sha = new string('a', 64);
        Assert.Throws<ArgumentException>(() => UI.PushPack("", new byte[1], sha, RecipientFilter.All));
        Assert.Throws<ArgumentException>(() => UI.PushPack("imgs", Array.Empty<byte>(), sha, RecipientFilter.All));
        Assert.Throws<ArgumentException>(() => UI.PushPack("imgs", new byte[1], "not-a-hash", RecipientFilter.All));
        Assert.Throws<ArgumentException>(() => UI.PushPack("imgs", new byte[1], sha[..63], RecipientFilter.All));
    }
}

/// <summary>
/// UiApp.ServeImages: a hot-watched PNG folder in the plugin data dir that
/// becomes a pushed images pack — dropped PNG appears in game, no hosting.
/// </summary>
public class ServeImagesTests : IDisposable
{
    // The PNG fixtures from the decoder tests: a 4x4 RGBA and a 3x2 RGB.
    private const string RgbaPng =
        "iVBORw0KGgoAAAANSUhEUgAAAAQAAAAECAYAAACp8Z5+AAAAMklEQVR4nGNgYGD4b8Mg97WCweb1Foaoh4wMNnIggW8wzAQUYEDGzAwVNg1ycnK/YRgAeoIRxyXE2KYAAAAASUVORK5CYII=";
    private const string RgbPng =
        "iVBORw0KGgoAAAANSUhEUgAAAAMAAAACCAIAAAASFvFNAAAAE0lEQVR4nGPhEpGDAMaUvAoICwAawgL0x1Uj5AAAAABJRU5ErkJggg==";

    private readonly string _root;

    public ServeImagesTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "dw-serveimages-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        // Every serving app on the box shares one merged pack, so the registry
        // is process-wide state: each test starts from an empty one.
        UiImageRegistry.ResetForTests();
    }

    public void Dispose()
    {
        NetMessages.OnSend = null;
        UiImageRegistry.ResetForTests();
        UiHostGroup.ResetForTests();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    /// <summary>Collects every UI wire message (any dw.* event) in send order.</summary>
    private sealed class WireCapture : IDisposable
    {
        public readonly List<(string Event, Dictionary<string, string> Fields, ulong Mask)> Messages = new();

        public WireCapture()
        {
            NetMessages.OnSend = (msgId, bytes, mask) =>
            {
                if (msgId != 148) return;
                var msg = CUserMsg_CustomGameEvent.Parser.ParseFrom(bytes);
                var entries = UiPayload.Parse(msg.Data.ToByteArray());
                var fields = new Dictionary<string, string>();
                if (entries is not null)
                    foreach (var (key, value) in entries)
                        fields[key] = value;
                Messages.Add((msg.EventName, fields, mask));
            };
        }

        public void Dispose() => NetMessages.OnSend = null;
    }

    private sealed class FakeTimer : DeadworksManaged.Api.ITimer
    {
        public readonly List<(Duration Interval, Action Callback)> Every = new();

        IHandle DeadworksManaged.Api.ITimer.Every(Duration interval, Action callback)
        {
            Every.Add((interval, callback));
            return new CallbackHandle(() => { });
        }

        IHandle DeadworksManaged.Api.ITimer.Once(Duration delay, Action callback) => new CallbackHandle(() => { });
        IHandle DeadworksManaged.Api.ITimer.Sequence(Func<IStep, Pace> callback) => new CallbackHandle(() => { });
        void DeadworksManaged.Api.ITimer.NextTick(Action callback) => callback();
    }

    private static UiApp App(FakeTimer timer, bool publishOnConnect = true) => new(
        new UiAppOptions
        {
            Bundle = new UiBundle
            {
                Id = "imgapp",
                Url = "https://cdn.example/host.vpk",
                Sha256 = new string('a', 64),
            },
            PublishOnConnect = publishOnConnect,
        },
        () => timer);

    /// <summary>Reassembles the pack from captured chunks and returns (vpk, sha).</summary>
    private static (byte[] Vpk, string Sha) Reassemble(IEnumerable<(string Event, Dictionary<string, string> Fields, ulong Mask)> packChunks)
    {
        var joined = new System.Text.StringBuilder();
        string sha = "";
        foreach (var (_, fields, _) in packChunks)
        {
            joined.Append(fields["b"]);
            sha = fields["sha256"];
        }
        return (Convert.FromBase64String(joined.ToString()), sha);
    }

    [Fact]
    public void PushesThePackOnConnectAheadOfTheBundle()
    {
        File.WriteAllBytes(Path.Combine(_root, "icon.png"), Convert.FromBase64String(RgbaPng));
        using var capture = new WireCapture();
        var timer = new FakeTimer();
        using var app = App(timer);
        app.ServeImages(_root);

        app.HandleConnect(5);

        int bundleAt = capture.Messages.FindIndex(m => m.Event == "dw.ui.bundle");
        var packChunks = capture.Messages.Where(m => m.Event == "dw.ui.pack").ToList();
        Assert.True(packChunks.Count > 0, "the pack must be pushed on connect");
        Assert.True(bundleAt > capture.Messages.FindLastIndex(m => m.Event == "dw.ui.pack"),
            "the pack must be pushed before the bundle publish");
        Assert.All(packChunks, m => Assert.Equal(1ul << 5, m.Mask));
        Assert.All(packChunks, m => Assert.Equal("dwimages", m.Fields["id"]));

        // What arrived is exactly the pack built from the same folder.
        var expected = UiImagePack.BuildMerged([("imgapp", new Dictionary<string, byte[]>
        {
            ["icon"] = Convert.FromBase64String(RgbaPng),
        })]);
        var (vpk, sha) = Reassemble(packChunks);
        Assert.Equal(expected.Vpk, vpk);
        Assert.Equal(expected.Sha256, sha);
    }

    [Fact]
    public void ExposesTheNameToS2rPathMap()
    {
        File.WriteAllBytes(Path.Combine(_root, "icon.png"), Convert.FromBase64String(RgbaPng));
        var timer = new FakeTimer();
        using var app = App(timer);
        var images = app.ServeImages(_root);

        Assert.Equal("s2r://panorama/images/dw/imgapp/icon.vtex", images.Paths["icon"]);
    }

    [Fact]
    public void AnEditedPngIsRebuiltAndRepushedToConnectedPlayers()
    {
        string png = Path.Combine(_root, "icon.png");
        File.WriteAllBytes(png, Convert.FromBase64String(RgbaPng));
        using var capture = new WireCapture();
        var timer = new FakeTimer();
        using var app = App(timer);
        app.ServeImages(_root);
        app.HandleConnect(2);
        var (firstVpk, firstSha) = Reassemble(capture.Messages.Where(m => m.Event == "dw.ui.pack"));
        capture.Messages.Clear();

        File.WriteAllBytes(png, Convert.FromBase64String(RgbPng));
        timer.Every.Last().Callback();   // the ServeImages poll tick

        var repush = capture.Messages.Where(m => m.Event == "dw.ui.pack").ToList();
        Assert.True(repush.Count > 0, "the edited folder must be repushed");
        Assert.All(repush, m => Assert.Equal(1ul << 2, m.Mask));
        var (vpk, sha) = Reassemble(repush);
        Assert.NotEqual(firstSha, sha);
        Assert.Equal(UiImagePack.BuildMerged([("imgapp", new Dictionary<string, byte[]>
        {
            ["icon"] = Convert.FromBase64String(RgbPng),
        })]).Vpk, vpk);
    }

    [Fact]
    public void AnEmptyFolderPushesNothingUntilAPngAppears()
    {
        using var capture = new WireCapture();
        var timer = new FakeTimer();
        using var app = App(timer);
        var images = app.ServeImages(_root);
        app.HandleConnect(1);

        Assert.Empty(images.Paths);
        Assert.DoesNotContain(capture.Messages, m => m.Event == "dw.ui.pack");

        File.WriteAllBytes(Path.Combine(_root, "logo.png"), Convert.FromBase64String(RgbaPng));
        timer.Every.Last().Callback();

        Assert.Contains(capture.Messages, m => m.Event == "dw.ui.pack");
        Assert.Equal("s2r://panorama/images/dw/imgapp/logo.vtex", images.Paths["logo"]);
    }

    [Fact]
    public void ABrokenEditKeepsTheLastGoodPack()
    {
        string png = Path.Combine(_root, "icon.png");
        File.WriteAllBytes(png, Convert.FromBase64String(RgbaPng));
        using var capture = new WireCapture();
        var timer = new FakeTimer();
        using var app = App(timer);
        var images = app.ServeImages(_root);
        app.HandleConnect(2);
        int pushed = capture.Messages.Count(m => m.Event == "dw.ui.pack");

        File.WriteAllBytes(png, new byte[] { 1, 2, 3 });   // not a PNG
        timer.Every.Last().Callback();

        Assert.Equal(pushed, capture.Messages.Count(m => m.Event == "dw.ui.pack"));
        Assert.Equal("s2r://panorama/images/dw/imgapp/icon.vtex", images.Paths["icon"]);   // last good map
    }

    // --- two apps serving images (one mount, one pack) ---

    private static UiApp NamedApp(FakeTimer timer, string id) => new(
        new UiAppOptions
        {
            Bundle = new UiBundle { Id = "dwhost", Url = "https://cdn.example/host.vpk", Sha256 = new string('a', 64) },
            AppId = id,
            PublishOnConnect = false,
        },
        () => timer);

    [Fact]
    public void TwoAppsImagesMergeIntoOnePackUnderTheirOwnFolders()
    {
        // The client mounts a single images pack, so the two folders have to
        // become one - and each app's logo.png has to stay its own.
        string other = Path.Combine(Path.GetTempPath(), "dw-serveimages-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(other);
        try
        {
            File.WriteAllBytes(Path.Combine(_root, "logo.png"), Convert.FromBase64String(RgbaPng));
            File.WriteAllBytes(Path.Combine(other, "logo.png"), Convert.FromBase64String(RgbPng));

            using var capture = new WireCapture();
            var timer = new FakeTimer();
            using var alpha = NamedApp(timer, "alpha");
            using var beta = NamedApp(timer, "beta");
            var alphaImages = alpha.ServeImages(_root);
            var betaImages = beta.ServeImages(other);

            alpha.HandleConnect(3);
            beta.HandleConnect(3);

            // One pack, one push - the second app's connect adds nothing new.
            var chunks = capture.Messages.Where(m => m.Event == "dw.ui.pack").ToList();
            Assert.NotEmpty(chunks);
            Assert.Single(chunks.Select(c => c.Fields["sha256"]).Distinct());
            Assert.All(chunks, c => Assert.Equal("dwimages", c.Fields["id"]));

            // Both images are in it, each under its own app.
            Assert.Equal("s2r://panorama/images/dw/alpha/logo.vtex", alphaImages.Paths["logo"]);
            Assert.Equal("s2r://panorama/images/dw/beta/logo.vtex", betaImages.Paths["logo"]);
        }
        finally
        {
            try { Directory.Delete(other, recursive: true); } catch { }
        }
    }

    [Fact]
    public void ABareDwSrcResolvesToTheAppsOwnImage()
    {
        File.WriteAllBytes(Path.Combine(_root, "logo.png"), Convert.FromBase64String(RgbaPng));
        using var capture = new WireCapture();
        var timer = new FakeTimer();
        using var alpha = NamedApp(timer, "alpha");
        alpha.ServeImages(_root);

        alpha.SetTree(1, Ui.Image("dw://logo.png"));

        // Authors keep writing dw://logo.png; the server points it at their
        // own folder on the way out, so another app's logo.png cannot win.
        var tree = capture.Messages.Last(m => m.Event == "dw.ui.emit" && m.Fields.ContainsKey("d.r0.src"));
        Assert.Equal("dw://alpha/logo.png", tree.Fields["d.r0.src"]);
    }

    [Fact]
    public void AnExplicitFolderInADwSrcIsLeftAlone()
    {
        // How one app deliberately shows another's image.
        Assert.Equal("dw://beta/logo.png", UiTreeCodec.NamespaceSrc("dw://beta/logo.png", "alpha"));
        Assert.Equal("dw://alpha/logo.png", UiTreeCodec.NamespaceSrc("dw://logo.png", "alpha"));
        Assert.Equal("dw://logo.png", UiTreeCodec.NamespaceSrc("dw://logo.png", null));
        Assert.Equal("s2r://whatever.vtex", UiTreeCodec.NamespaceSrc("s2r://whatever.vtex", "alpha"));
    }

    [Fact]
    public void ARedundantConnectDoesNotRepushButAReconnectDoes()
    {
        File.WriteAllBytes(Path.Combine(_root, "icon.png"), Convert.FromBase64String(RgbaPng));
        using var capture = new WireCapture();
        var timer = new FakeTimer();
        using var app = App(timer);
        app.ServeImages(_root);

        app.HandleConnect(4);
        int once = capture.Messages.Count(m => m.Event == "dw.ui.pack");
        app.HandleConnect(4);   // redundant forward, same connection
        Assert.Equal(once, capture.Messages.Count(m => m.Event == "dw.ui.pack"));

        app.HandleDisconnect(4);
        app.HandleConnect(4);   // a new player on the reused slot
        Assert.Equal(once * 2, capture.Messages.Count(m => m.Event == "dw.ui.pack"));
    }
}
