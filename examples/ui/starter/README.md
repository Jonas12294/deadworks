# Deadworks UI starter

The template for a new UI mod: the smallest panel + plugin pair that
exercises the whole system — publish on connect, panel presence, live data
in, actions back. Copy it, rename it, build on it.

## What's here

| File | Role |
|---|---|
| `bundle.json` | Build settings: addon name + the helper mapped in under its versioned name (`dwcore3.js`) |
| `panorama/layout/hud_health.xml` | Stock health-bar layout + the `#DwStarter` block (message, clock, ping button) |
| `panorama/scripts/dwstart1.js` | Panel logic: `Ready` announce, one `Subscribe`, one `SendToServer` |
| `panorama/scripts/dwstart1.test.js` | `bun test` coverage of the pure display logic |

The matching server side is `examples/plugins/StarterUiPlugin/` — a UiApp
plugin that publishes this bundle, greets live panels with a message + the
match clock, and answers pings.

## Use it as-is (prove your setup)

```
python ../build_bundle.py .          # from examples/ui/starter
```

Upload the built VPK to your HTTPS host, copy `dist/dwstarter.manifest.json`
into the plugin's data folder (`managed\plugins\StarterUiPlugin\`), build + install
the plugin, join a match. You should see: orange "waiting for server" → cyan
with the message and a ticking clock. Click PING SERVER → chat reply.
(Pasting Url/Sha256 into the config still works if you prefer.)

## Make it yours

1. Copy this directory and `examples/plugins/StarterUiPlugin/`, rename both
   (pick a new `name` in `bundle.json` — it must match the plugin config's
   `BundleId`, and the `APP_ID` in the panel script).
2. Edit the `#DwStarter` block in the layout and the panel script.
3. **Every script edit needs a new filename** (`dwstart1.js` → `dwstart2.js`)
   and an updated `<scripts>` include — the engine never reloads a changed
   script under an old name. Layout edits reload fine.
4. Rebuild, upload, copy the fresh manifest over the old one, reload the
   plugin.

Docs: `docs/ui/` (start with `making-your-own-ui.md`).
