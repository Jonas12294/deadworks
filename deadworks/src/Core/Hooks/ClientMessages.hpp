#pragma once

// Incoming client->server custom game events (CClientMsg_CustomGameEvent, 280).
//
// This is a SUBSCRIPTION, not a hook: we register an additional handler on the
// server's IGameEventSystem, exactly the way the game's own
// CCustomGameEventManager does (server.dll sub_180DAC7C0 registers
// sub_180DC4E40 for the same message). Incoming messages are queued to every
// subscriber, so ours coexists with the game's - which simply finds no VScript
// listeners for `dw.*` event names and no-ops.
//
// The sender is authenticated for free: the event system reports the source
// entity for the message being dispatched, so a client cannot claim to be
// another player.
//
// PROBE NOTE (2026-08-14): currently logs what arrives. The real feature routes
// (slot, eventName, dataBytes) to managed, which parses CClientMsg_CustomGameEvent
// with its own generated protobuf (managed/protos/clientmessages.proto).

namespace deadworks {
namespace hooks {

// Message id of CClientMsg_CustomGameEvent (EBaseClientMessages::CM_CustomGameEvent).
inline constexpr int kClientCustomGameEventId = 280;

// Subscribes to message 280 on the server event system. Safe to call once at
// init, after g_pGameEventSystem and g_pNetworkMessages are resolved.
void RegisterClientMessageHandler();

} // namespace hooks
} // namespace deadworks
