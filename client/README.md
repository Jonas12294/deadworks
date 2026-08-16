# Deadworks client module

The client half of the custom UI system: the piece that lets a Deadworks
server ship Panorama UI to a connected player, update it live mid-match, and
exchange data with it both ways. The server half lives in
`managed/DeadworksManaged.Api/UI/`; the whole picture is in
[`docs/ui/README.md`](../docs/ui/README.md).

Nothing here is required to run or play on a Deadworks server. It is opt-in
per player (the launcher's *Allow servers to replace game UI* setting) and a
server must treat custom UI as an enhancement — players without the module
silently ignore every UI message.

## What it does

- Mounts server-sent content by repacking a staging tree into a VPK and
  sequencing the search-path swap from inside the process (Source 2 always
  orders VPKs ahead of loose directories, and a mounted VPK can't be
  overwritten from outside).
- Arms Panorama's own reload driver. The retail client already polls
  `CDirWatcher` at 5 Hz, but nothing ever calls `AddDirWatch`, so the watcher
  list is empty — that single missing call is the whole reason packed UI only
  refreshes on restart.
- Carries the data channel: custom game events (message 280) in both
  directions, so `UI.Emit` reaches panels and `Deadworks.SendToServer` /
  key reports reach the server, where the sending slot is authenticated from
  the connection it arrived on.

## How players get it

They don't build it. `launcher/src-tauri/src/inject.rs` downloads it on
connect from a pinned manifest URL, checks it against the SHA-256 in that
manifest, writes it to temp, injects it, and deletes it once it unloads. The
manifest URL is a constant in the launcher on purpose: this is code going into
the player's game, so the server they are joining gets no say in where it comes
from.

`uiwatch.dll` in this folder is the build that manifest points at:

```
sha256  a5a131f9d1532363658e2f9936719da69750e63d2820908d7a195f0e69131700
```

## Building

```
build.bat
```

Needs VS2022 with the C++ toolset and the SDK's protobuf-lite built at
`sourcesdk/thirdparty/protobuf/build/Release/`. Links `/MT` to match it, which
also keeps the injected DLL free of any CRT dependency in the target process.
Only `MessageLite::ParseFromArray` is used — no generated message is compiled
in, since registering our own descriptor next to `client.dll`'s would be a
duplicate-registration conflict.

`LNK1104: cannot open uiwatch.dll` means the game still has it loaded; close
Deadlock or let the module self-eject.

## Per-patch maintenance

Every signature, vtable index and offset it uses is client-side and documented
in a comment next to the code that uses it in `dllmain.cpp`, along with the
`panorama.dll` build it was verified against. They are checked against `.text`
before use, so a shifted vtable fails loudly instead of calling garbage — but
**all of them must be re-verified after a game patch**. These are separate from
the server-side entries in `config/deadworks_mem.jsonc`.
