# Deadworks custom UI — start here

A Deadworks server can ship its own Panorama UI to connected players, update it
live mid-match without a restart, and exchange data with it in both directions.
All of it is working and tested live against retail Deadlock.

What you can build with it today:

- **Replace or extend HUD panels** with your own layout, styles and scripts,
  delivered the moment a player connects.
- **Push live data into your panels** from a server plugin (`UI.Emit`), as often
  as you like — scoreboards, timers, objectives, vote prompts.
- **React to player actions** — buttons and clicks in your panel reach your
  plugin (`[UiActionHandler]`) with the sender's player slot already authenticated.
- **Swap or revoke the UI live** — publish a new bundle version mid-match, or
  revert players to the stock UI.

There are two ways to author UI:

- **Server-built route** (start here) — describe the UI from your plugin and
  the prebuilt host bundle renders it: **no CSDK, no compiling, no hosting;
  edits appear live in a running match.** The SDK pins the published host
  bundle, so `UI.CreateHostApp(this)` plus
  `app.ServeUiLibrary("uis", "dashboard")` is a complete UI plugin: each UI
  is a folder of plain `ui.xml` + `ui.css` + `ui.js` (run natively in the
  panel's V8 context), served per player with hot reload built in. See the
  [Server API reference](server-api.md) and `examples/plugins/TreeUiPlugin/`.
- **Bundle route** — ship your own layout/scripts as a VPK (needs the CSDK's
  resource compiler once per change; full Panorama power, custom assets).

So the full stack — HTML, CSS, and JS — can be pushed and live-edited from the
server with no compiler and no hosting.

What does *not* exist yet: a per-server trust prompt (the launcher toggle is
currently global).

## The five-minute picture

```
┌────────────────┐   UI.PublishBundle    ┌────────────────┐
│  Your plugin    │ ────────────────────► │  Player's game │
│  (server, C#)   │                       │  (client module│
│                 │   UI.Emit("ev", data) │   + your panel)│
│                 │ ────────────────────► │                │
│                 │  [UiActionHandler]    │                │
│                 │ ◄──────────────────── │  Deadworks.    │
└────────────────┘                       │  SendToServer  │
                                          └────────────────┘
```

1. Your plugin creates a **`UiApp`** — `UI.CreateHostApp(this)` for the
   server-built route (the SDK pins the published host bundle), or
   `UI.CreateApp` with your own bundle's URL + SHA-256 (or the manifest
   `build_bundle.py` writes, via `UiBundle.FromManifest`).
2. **The host runs the lifecycle**: the bundle is published to joining
   players (and anyone already connected when the plugin loads); clients
   download, verify, mount, and your panel appears — live, no restart. On
   plugin unload the app is disposed. No forwarding code anywhere.
3. Your panel announces itself (the host bundle does this for you; own
   bundles ship the **`deadworks.js` helper** and call
   `Deadworks.Ready("<id>")`), and uses `Deadworks.Subscribe(...)` /
   `Deadworks.SendToServer(...)`.
4. Your app fires **`StateRequested`** whenever a panel needs state (it
   appeared, or the resync tick) — you answer with `Emit`/`EmitTable` (or let
   `ServeUiLibrary` answer with UI files from disk); player actions arrive at
   your **`[UiActionHandler]`** methods with the sender's slot.

Only players running the Deadworks client module (an opt-in launcher setting)
see any of this; for everyone else the messages are silently ignored. **Treat
custom UI as an enhancement, never something your game mode depends on.**

## Documentation map

| Read this | When you want to |
|---|---|
| [Getting started](getting-started.md) | Run the existing demo end to end and see it work |
| [Making your own UI](making-your-own-ui.md) | Build your first bundle from the starter template |
| [Server API reference](server-api.md) | Look up `UiApp` / `UiTable` / raw `UI.*` details and limits |
| [Panel API reference](panel-api.md) | Look up the `Deadworks` JS helper and panel-side rules |
| [Troubleshooting](troubleshooting.md) | Fix "nothing appears" / "my change did nothing" fast |

Deep background, in increasing order of internals:

- `managed/DeadworksManaged.Api/UI/` — the UiApp/UiTable/helper layer this
  documentation describes
- `examples/ui/host/panorama/scripts/` — the panel-side host runtime that
  rebuilds panels from what the server sends
- `client/dllmain.cpp` — the client module: search-path swap, Panorama reload
  driver, and the 280 data channel, with the verified offsets in comments

## Working examples in this repo

| Example | What it shows |
|---|---|
| `examples/plugins/TreeUiPlugin/` (+ its `uis/` samples) | **The template for server-built UI**: `CreateHostApp` + `ServeUiLibrary` + game logic — no CSDK, no hosting |
| `examples/ui/starter/` + `examples/plugins/StarterUiPlugin/` | Minimal bundle + plugin pair — copy these to start a bundle-route UI |
| `examples/ui/scoreboard/` | Legacy interactive bundle demo (its plugin logic predates the library route) |
| `examples/ui/deadworks.js` | The panel-side helper every bundle ships |
| `examples/ui/build_bundle.py` | One command: compile + pack + hash + manifest |
| `examples/ui/host/` | The host bundle source (compiled once, published, pinned in the SDK) |
| `examples/plugins/CustomUiPlugin/` | Internal transport-test harness (`!ui magenta/cyan/corrupt`) — not a template |
