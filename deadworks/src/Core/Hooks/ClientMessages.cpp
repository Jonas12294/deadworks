#include "ClientMessages.hpp"

#include "../Deadworks.hpp"
#include "../../Memory/MemoryDataLoader.hpp"

#include <igameeventsystem.h>
#include <networksystem/inetworkserializer.h>
#include <networksystem/netmessage.h>

#include <google/protobuf/message.h>

#include <string>

namespace deadworks {
namespace hooks {

namespace {

// CUtlAbstractDelegate as the engine actually uses it: TWO pointers
// { m_pthis, m_pFunction }, not the three in utldelegateimpl.h. Registered
// handlers get nDelegateParamCount hard-coded to 1, so the handler always
// receives the queued CNetMessage* as its second argument.
struct AbstractDelegate {
    void *pthis;
    void *fn;
};

AbstractDelegate g_delegate{};
int g_anchor = 0;   // a unique, stable m_pthis
bool g_registered = false;

// The event system reports the source of the message being dispatched as an
// ENTITY index; player controllers are entity index = slot + 1 (the same
// conversion ProcessUsercmds already does). Returned through a hidden pointer
// because CEntityIndex is a class.
int CurrentSenderSlot() {
    if (!g_pGameEventSystem)
        return -1;

    static const int idx = []() {
        auto v = MemoryDataLoader::Get().GetVirtual("IGameEventSystem::GetEventSource");
        return v.value_or(-1);
    }();
    if (idx < 0)
        return -1;

    auto **vtable = *reinterpret_cast<void ***>(g_pGameEventSystem);
    using GetEventSourceFn = void *(__fastcall *)(void *, void *);
    int entityIndex = -1;
    reinterpret_cast<GetEventSourceFn>(vtable[idx])(g_pGameEventSystem, &entityIndex);
    return entityIndex - 1;
}

// Reads the two fields of CClientMsg_CustomGameEvent out of its serialized
// form. Hand-decoded rather than linking a generated message: the wire shape is
// two length-delimited fields and this keeps the native side free of another
// .proto (managed already has clientmessages.proto for the real routing).
//
//   field 1 (event_name): tag 0x0A, varint length, bytes
//   field 2 (data):       tag 0x12, varint length, bytes
bool DecodeCustomGameEvent(const std::string &bytes, std::string &eventName, std::string &data) {
    size_t i = 0;
    while (i < bytes.size()) {
        const uint8_t tag = static_cast<uint8_t>(bytes[i++]);

        uint64_t len = 0;
        int shift = 0;
        bool ok = false;
        while (i < bytes.size() && shift <= 63) {
            const uint8_t b = static_cast<uint8_t>(bytes[i++]);
            len |= static_cast<uint64_t>(b & 0x7F) << shift;
            if ((b & 0x80) == 0) { ok = true; break; }
            shift += 7;
        }
        if (!ok || len > bytes.size() - i)
            return false;

        if (tag == 0x0A)
            eventName.assign(bytes, i, static_cast<size_t>(len));
        else if (tag == 0x12)
            data.assign(bytes, i, static_cast<size_t>(len));
        else if ((tag & 0x07) != 2)
            return false;   // only length-delimited fields are expected here

        i += static_cast<size_t>(len);
    }
    return true;
}

// Free __fastcall handler; the delegate is { &g_anchor, &this }.
void __fastcall OnClientCustomGameEvent(void * /*self*/, void *netMessage) {
    if (!netMessage)
        return;

    auto *msg = static_cast<CNetMessage *>(netMessage);
    const auto *pbMsg = msg->AsMessage();
    if (!pbMsg)
        return;

    std::string raw;
    if (!pbMsg->SerializeToString(&raw))
        return;

    std::string eventName, data;
    if (!DecodeCustomGameEvent(raw, eventName, data))
        return;

    const int slot = CurrentSenderSlot();
    if (slot < 0)
        return;

    g_Deadworks.OnClientCustomGameEvent(slot, eventName.c_str(),
                                        reinterpret_cast<const uint8_t *>(data.data()),
                                        static_cast<int>(data.size()));
}

} // namespace

void RegisterClientMessageHandler() {
    if (g_registered)
        return;
    if (!g_pGameEventSystem || !g_pNetworkMessages) {
        g_Log->Error("[clientmsg] event system / network messages unavailable - 280 receive off");
        return;
    }

    auto *binding = g_pNetworkMessages->FindNetworkMessageById(
        static_cast<NetworkMessageId>(kClientCustomGameEventId));
    if (!binding) {
        g_Log->Error("[clientmsg] FindNetworkMessageById({}) returned null - 280 receive off",
                     kClientCustomGameEventId);
        return;
    }

    auto idx = MemoryDataLoader::Get().GetVirtual("IGameEventSystem::RegisterGameEventHandlerAbstract");
    if (!idx.has_value()) {
        g_Log->Error("[clientmsg] RegisterGameEventHandlerAbstract index not configured - 280 receive off");
        return;
    }

    g_delegate.pthis = &g_anchor;
    g_delegate.fn = reinterpret_cast<void *>(&OnClientCustomGameEvent);

    auto **vtable = *reinterpret_cast<void ***>(g_pGameEventSystem);
    using RegisterFn = void(__fastcall *)(void *, void *, const void *, void *, int);
    reinterpret_cast<RegisterFn>(vtable[idx.value()])(
        g_pGameEventSystem, nullptr, &g_delegate, binding, 0);

    g_registered = true;
    g_Log->Info("[clientmsg] subscribed to CClientMsg_CustomGameEvent ({}) via vtable[{}]",
                kClientCustomGameEventId, idx.value());
}

} // namespace hooks
} // namespace deadworks
