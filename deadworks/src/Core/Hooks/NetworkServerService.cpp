#include "NetworkServerService.hpp"

#include "../Deadworks.hpp"
#include "ClientMessages.hpp"

namespace deadworks {
namespace hooks {

void NetworkServerServiceHook::Hook_StartupServer(const GameSessionConfiguration_t &config, ISource2WorldSession *pWorldSession, const char *pszMapName) {
    g_NetworkServerService_StartupServer.thiscall<void>(this, config, pWorldSession, pszMapName);

    // The engine's network-message registry is not populated at Deadworks init
    // (FindNetworkMessageById(280) returns null there), so the client-message
    // subscription is made here instead, once the server is up. Guarded, so
    // repeated map changes re-use the first successful registration.
    RegisterClientMessageHandler();

    g_Deadworks.On_StartupServer(pszMapName);
}

} // namespace hooks
} // namespace deadworks
