# Getting started: run the demo end to end

Goal: see server-pushed UI working in a real match — a live dashboard fed by
the server, with buttons that run real server-side actions — before writing
anything of your own. Budget ~15 minutes the first time.

The demo is `TreeUiPlugin`, which uses the **server-built route**: the SDK
pins an already-published host bundle, so there is nothing to compile, hash or
host. Your UI is three plain files (`ui.xml`, `ui.css`, `ui.js`) that you can
edit mid-match.

## What you need

| Thing | Why |
|---|---|
| Deadlock (retail) | The client the UI is delivered into |
| A Deadworks server (`deadworks.exe` from `<Deadlock>\game\bin\win64\`) | Runs the plugin that publishes the UI |
| `TreeUiPlugin.dll` built from `examples/plugins/TreeUiPlugin/` | The demo plugin |
| The Deadworks launcher **or** Process Hacker | To load the client module into the game |
| `-console -condebug` on Deadlock's launch options | Without them you see nothing (`con_enable` defaults to off) |

You do **not** need the CSDK, Python, or any hosting — the host bundle is
already built, published and pinned inside the SDK.

## Step 1 — build and install the demo plugin

```
cd examples/plugins/TreeUiPlugin
dotnet build -c Release
```

Copy `bin/Release/net10.0/TreeUiPlugin.dll` into
`<Deadlock>\game\bin\win64\managed\plugins\` by hand. (The `MSB3023` warning
during build is expected — auto-deploy is deliberately disabled.)

Then copy the sample UIs into the plugin's data folder:

```
examples/plugins/TreeUiPlugin/uis/  →  <Deadlock>\game\bin\win64\managed\plugins\TreeUiPlugin\uis\
```

Each subfolder (`dashboard/`, `hello/`) is one UI: `ui.xml` + optional
`ui.css`/`ui.js`. That folder is the whole content pipeline.

## Step 2 — start everything

1. Run `deadworks.exe` from `<Deadlock>\game\bin\win64\`.
2. Launch Deadlock with `-console -condebug`.
3. Load the client module, either way works:
   - **Launcher:** turn the custom-UI setting **on** (it is off by default) and
     let it inject; or
   - **By hand:** inject `uiwatch.dll` with Process Hacker, run as admin.
4. In the Deadlock console: `connect localhost:27067`.
5. **Get into an actual match.** This matters: the host panel (`hud_health`)
   does not exist in the main menu, and no server messages arrive there either.
   Nothing can be tested from the menu.

## Step 3 — watch it work

No commands needed: the plugin publishes the host bundle to joining players
automatically, the panel announces itself, and the server serves the
`dashboard` UI. You should see, in order:

1. **The dashboard appears** over the HUD — content delivery works.
2. **The clock, player count and tick counter update every second** — the
   server → panel data channel works.
3. **Click an action button** (or type `!act gold` in chat) — +1000 gold on
   your hero, an event line in the panel. The panel → server channel works,
   and the action ran server-side on *you* (the sender's slot is
   authenticated by the transport).

Then try the parts that make this route special:

- **Hot editing:** open
  `managed\plugins\TreeUiPlugin\uis\dashboard\ui.xml` on the server box, change some
  text, save. Every panel showing the dashboard updates within a second,
  mid-match, no restart anywhere.
- **Per-player UIs:** `!ui` in chat lists the available UIs; `!ui hello`
  switches *your* panel only; `!ui hello all` switches everyone. Panel
  buttons with `onclick="ui.load"` do the same thing.

## Where to look when something is wrong

- **Module log:** `%TEMP%\uiwatch.log` when the launcher injected the module,
  otherwise next to `uiwatch.dll`.
- **Panel script output:** `$.Msg` lines land in
  `<Deadlock>\game\citadel\console.log` as `[PanoramaScript]` — only with
  `-condebug`.
- **The definitive reload signal:** `Panel <name> is reloading layout <path>`
  in the console — it is printed unconditionally whenever a panel reloads.
- **Server side:** `[TreeUiPlugin] UI library: …` on load tells you exactly
  which folder it serves; a missing UI is logged with the path it expected.

Then see [Troubleshooting](troubleshooting.md) — most first-run failures are
one of the same five things (main menu, no `-condebug`, module not injected,
not in a match, or panel cached before content mounted).

## Testing the transport itself

`examples/plugins/CustomUiPlugin/` is an internal harness for the content
channel: `!ui magenta` / `!ui cyan` swap raw test bundles live, `!ui default`
revokes, and `!ui corrupt` serves a truncated VPK that the client **must
reject** (the only test of the hash-verification failure branch). You don't
need it to build UI — it exists to prove delivery still works after game
patches.

## Next

You've seen it work — now build your own. Start by copying a sample UI folder
and editing it live (no tooling at all). When you need full Panorama power —
custom assets, fonts, complete layouts — graduate to the bundle route:
[Making your own UI](making-your-own-ui.md).
