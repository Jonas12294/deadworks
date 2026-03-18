#include "Deadworks.hpp"
#include "NativeCallbacks.hpp"
#include "NativeHero.hpp"
#include "ManagedCallbacks.hpp"

#include "Hooks/CoreHooks.hpp"
#include "Hooks/Source2GameClients.hpp"
#include "Hooks/Source2Server.hpp"
#include "Hooks/CServerSideClientBase.hpp"
#include "Hooks/NetworkServerService.hpp"
#include "Hooks/CBaseEntity.hpp"
#include "Hooks/GameEvents.hpp"
#include "Hooks/PostEventAbstract.hpp"
#include "Hooks/CCitadelPlayerPawn.hpp"
#include "Hooks/BuildGameSessionManifest.hpp"
#include "Hooks/CCitadelPlayerController.hpp"
#include "Hooks/EntityIO.hpp"
#include "Hooks/TraceShape.hpp"
#include "Hooks/ProcessUsercmds.hpp"
#include "Hooks/AbilityThink.hpp"
#include "Hooks/CModifierProperty.hpp"

#include "../Memory/MemoryDataLoader.hpp"
#include "../SDK/CBaseEntity.hpp"
#include "../SDK/CCitadelPlayerController.hpp"
#include "../SDK/CEntitySystem.hpp"
#include "../Utils/String.hpp"
#include "../SDK/Util.hpp"

#include <tier1/convar.h>

#include <netmessages.h>
#include <serversideclient.h>
#include <irecipientfilter.h>
#include <igameevents.h>
#include <igameeventsystem.h>

#include <citadel_clientmessages.pb.h>
#include <citadel_usermessages.pb.h>
#include <icvar.h>
#include <entity2/entitysystem.h>

IGameEventSystem *g_pGameEventSystem = nullptr;

namespace deadworks {

class CEntityListener : public IEntityListener {
public:
    void OnEntityCreated(CEntityInstance *pEntity) override { g_Deadworks.OnEntityCreated(pEntity); }
    void OnEntitySpawned(CEntityInstance *pEntity) override { g_Deadworks.OnEntitySpawned(pEntity); }
    void OnEntityDeleted(CEntityInstance *pEntity) override { g_Deadworks.OnEntityDeleted(pEntity); }
};

static CEntityListener g_EntityListener;

template <typename Fn>
static bool HookInline(safetyhook::InlineHook &hook, const char *name, Fn detour, bool required = false) {
    auto opt = MemoryDataLoader::Get().GetOffset(name);
    if (!opt) {
        if (required)
            g_Log->Error("{} signature not found", name);
        else
            g_Log->Warning("{} signature not found — hook unavailable", name);
        return false;
    }
    hook = safetyhook::create_inline(opt.value(), detour);
    g_Log->Info("Hooked {}", name);
    return true;
}

void Deadworks::InitFromAppSystem(CAppSystemDict *pAppSystem) {
    // server must be loaded before logging
    Module server("../../citadel/bin/win64/server.dll");
    GetInterfaceFactories();

    g_Log = std::make_unique<S2Logger>("deadworks");
    g_Log->Info("Log Startup");
    g_Log->Info("InitFromAppSystem");

    hooks::g_ServerCreateInterface = safetyhook::create_inline(InterfaceFactories.server, hooks::Hook_ServerCreateInterface);

    g_pNetworkServerService = reinterpret_cast<INetworkServerService *>(InterfaceFactories.engine2(NETWORKSERVERSERVICE_INTERFACE_VERSION, nullptr));
    g_pEngineServer = reinterpret_cast<IVEngineServer2 *>(InterfaceFactories.engine2(SOURCE2ENGINETOSERVER_INTERFACE_VERSION, nullptr));

    if (!g_pNetworkServerService) {
        g_Log->Error("Failed to load INetworkServerService. Abandoning ship!");
        return;
    }

    if (!g_pEngineServer) {
        g_Log->Error("Failed to load IVEngineServer2. Abandoning ship!");
        return;
    }
}

void Deadworks::PostInit() {
    g_Log->Info("PostInit");

    g_pSource2Server = reinterpret_cast<ISource2Server *>(InterfaceFactories.server(SOURCE2SERVER_INTERFACE_VERSION, nullptr));
    g_pSource2GameClients = reinterpret_cast<ISource2GameClients *>(InterfaceFactories.server(SOURCE2GAMECLIENTS_INTERFACE_VERSION, nullptr));
    g_pGameResourceServiceServer = reinterpret_cast<IGameResourceService *>(InterfaceFactories.engine2(GAMERESOURCESERVICESERVER_INTERFACE_VERSION, nullptr));
    g_pSchemaSystem = reinterpret_cast<ISchemaSystem *>(InterfaceFactories.schemasystem(SCHEMASYSTEM_INTERFACE_VERSION, nullptr));
    g_pNetworkMessages = reinterpret_cast<INetworkMessages *>(InterfaceFactories.networksystem(NETWORKMESSAGES_INTERFACE_VERSION, nullptr));
    g_pGameEventSystem = reinterpret_cast<IGameEventSystem *>(InterfaceFactories.engine2(GAMEEVENTSYSTEM_INTERFACE_VERSION, nullptr));
    g_pCVar = reinterpret_cast<ICvar *>(InterfaceFactories.tier0(CVAR_INTERFACE_VERSION, nullptr));

    if (!g_pSource2Server) {
        g_Log->Error("Failed to load ISource2Server. Abandoning ship!");
        return;
    }

    if (!g_pSource2GameClients) {
        g_Log->Error("Failed to load ISource2GameClients. Abandoning ship!");
        return;
    }

    if (!g_pGameResourceServiceServer) {
        g_Log->Error("Failed to load IGameResourceServiceServer. Abandoning ship!");
        return;
    }

    if (!g_pSchemaSystem) {
        g_Log->Error("Failed to load ISchemaSystem. Abandoning ship!");
        return;
    }

    if (!g_pNetworkMessages) {
        g_Log->Error("Failed to load INetworkMessages. Abandoning ship!");
        return;
    }

    if (!g_pGameEventSystem) {
        g_Log->Error("Failed to load IGameEventSystem. Abandoning ship!");
        return;
    }

    if (!g_pCVar) {
        g_Log->Error("Failed to load ICvar. Abandoning ship!");
        return;
    }

    ConVar_Register(FCVAR_RELEASE | FCVAR_CLIENT_CAN_EXECUTE | FCVAR_GAMEDLL);

    // Required inline hooks
    HookInline(hooks::g_CGCClientSystem_OnServerVersionCheck,
               "CGCClientSystem::OnServerVersionCheck",
               &hooks::Hook_CGCClientSystem_OnServerVersionCheck, true);

    // VMT hooks — these use virtual indices, not signatures
    auto &mem = MemoryDataLoader::Get();

    hooks::g_Source2ServerVmt = safetyhook::create_vmt(g_pSource2Server);
    hooks::g_Source2Server_ApplyGameSettings = safetyhook::create_vm(hooks::g_Source2ServerVmt, mem.GetVirtual("ISource2Server::ApplyGameSettings").value(), &hooks::Source2ServerHook::Hook_ApplyGameSettings);
    hooks::g_Source2Server_GameFrame = safetyhook::create_vm(hooks::g_Source2ServerVmt, mem.GetVirtual("ISource2Server::GameFrame").value(), &hooks::Source2ServerHook::Hook_GameFrame);

    hooks::g_Source2GameClientsVmt = safetyhook::create_vmt(g_pSource2GameClients);
    hooks::g_Source2GameClients_ClientPutInServer = safetyhook::create_vm(hooks::g_Source2GameClientsVmt, mem.GetVirtual("ISource2GameClients::ClientPutInServer").value(), &hooks::Source2GameClientsHook::Hook_ClientPutInServer);
    hooks::g_Source2GameClients_ClientConnect = safetyhook::create_vm(hooks::g_Source2GameClientsVmt, mem.GetVirtual("ISource2GameClients::ClientConnect").value(), &hooks::Source2GameClientsHook::Hook_ClientConnect);
    hooks::g_Source2GameClients_ClientDisconnect = safetyhook::create_vm(hooks::g_Source2GameClientsVmt, mem.GetVirtual("ISource2GameClients::ClientDisconnect").value(), &hooks::Source2GameClientsHook::Hook_ClientDisconnect);

    hooks::g_NetworkServerServiceVmt = safetyhook::create_vmt(g_pNetworkServerService);
    hooks::g_NetworkServerService_StartupServer = safetyhook::create_vm(hooks::g_NetworkServerServiceVmt, mem.GetVirtual("INetworkServerService::StartupServer").value(), &hooks::NetworkServerServiceHook::Hook_StartupServer);

    // Required inline hooks (crash if signature not found)
    HookInline(hooks::g_CServerSideClientBase_FilterMessage,
               "CServerSideClientBase::FilterMessage",
               &hooks::Hook_CServerSideClientBase_FilterMessage, true);
    HookInline(hooks::g_CBaseEntity_TakeDamageOld,
               "CBaseEntity::TakeDamageOld",
               &hooks::Hook_CBaseEntity_TakeDamageOld, true);
    HookInline(hooks::g_CCitadelPlayerPawn_ModifyCurrency,
               "CCitadelPlayerPawn::ModifyCurrency",
               &hooks::Hook_CCitadelPlayerPawn_ModifyCurrency, true);
    HookInline(hooks::g_CCitadelPlayerController_ClientConCommand,
               "CCitadelPlayerController::ClientConCommand",
               &hooks::Hook_CCitadelPlayerController_ClientConCommand, true);

    // Resolve IGameEventManager2 from a known xref
    {
        auto xref = MemoryDataLoader::Get().GetOffset("g_GameEventManager").value();
        auto disp = *reinterpret_cast<int32_t *>(xref + 9);
        g_pGameEventManager2 = *reinterpret_cast<IGameEventManager2 **>(xref + 13 + disp);

        if (g_pGameEventManager2) {
            g_Log->Info("IGameEventManager2 resolved: {:p}", static_cast<void *>(g_pGameEventManager2));
            hooks::g_GameEventManager2Vmt = safetyhook::create_vmt(g_pGameEventManager2);
            hooks::g_GameEventManager2_FireEvent = safetyhook::create_vm(hooks::g_GameEventManager2Vmt, mem.GetVirtual("IGameEventManager2::FireEvent").value(), &hooks::Hook_GameEventManager2_FireEvent);
        } else {
            g_Log->Error("Failed to dereference g_GameEventManager pointer");
        }
    }

    hooks::g_GameEventSystemVmt = safetyhook::create_vmt(g_pGameEventSystem);
    hooks::g_PostEventAbstract = safetyhook::create_vm(hooks::g_GameEventSystemVmt, mem.GetVirtual("IGameEventSystem::PostEventAbstract").value(), &hooks::Hook_PostEventAbstract);

    // Required feature hooks
    HookInline(hooks::g_BuildGameSessionManifest,
               "CCitadelGameRules::BuildGameSessionManifest",
               &hooks::Hook_BuildGameSessionManifest, true);
    HookInline(hooks::g_TraceShape,
               "TraceShape",
               &hooks::Hook_TraceShape, true);

    // Touch hooks (StartTouch / EndTouch) are initialized lazily in OnEntityCreated
    // because we need an entity vtable to resolve the virtual function addresses.

    // Optional hooks — features degrade gracefully if signatures are missing
    HookInline(hooks::g_CEntityInstance_AcceptInput,
               "CEntityInstance::AcceptInput",
               &hooks::Hook_CEntityInstance_AcceptInput);
    HookInline(hooks::g_CEntityInstance_FireOutput,
               "CEntityInstance::FireOutput",
               &hooks::Hook_CEntityInstance_FireOutput);
    HookInline(hooks::g_ProcessUsercmds,
               "CBasePlayerController::ProcessUsercmds",
               &hooks::Hook_ProcessUsercmds);
    HookInline(hooks::g_AbilityThink,
               "CCitadelPlayerPawn::AbilityThink",
               &hooks::Hook_AbilityThink);
    HookInline(hooks::g_CModifierProperty_AddModifier,
               "CModifierProperty::AddModifier",
               &hooks::Hook_CModifierProperty_AddModifier);

    // Resolve statics needed by Native* callbacks
    ResolveNativeStatics();
}

void Deadworks::On_ISource2Server_ApplyGameSettings() {
    if (!m_dotnetInitialized) {
        m_dotnetInitialized = true;
        InitializeManagedCallbacks(m_dotnetHost, m_managed);
    }
}

void Deadworks::On_StartupServer(const char *pszMapName) {
    g_Log->Info("StartupServer (map: {})", pszMapName ? pszMapName : "");

    // Register entity listener
    GameEntitySystem()->AddListenerEntity(&g_EntityListener);

    // Unhide all cvars and concommands by raw index iteration
    if (g_pCVar) {
        constexpr uint64 flagsToRemove = FCVAR_HIDDEN | FCVAR_DEVELOPMENTONLY | FCVAR_DEFENSIVE;
        int unhiddenCvars = 0;
        int unhiddenCmds = 0;

        {
            ConVarRef invalidRef{};
            auto *invalidData = g_pCVar->GetConVarData(invalidRef);
            for (uint16 i = 0;; i++) {
                ConVarRef ref(i);
                auto *data = g_pCVar->GetConVarData(ref);
                if (data == invalidData)
                    break;
                if (data->GetFlags() & flagsToRemove) {
                    data->RemoveFlags(flagsToRemove);
                    unhiddenCvars++;
                }
            }
        }

        {
            ConCommandRef invalidRef{};
            auto *invalidData = g_pCVar->GetConCommandData(invalidRef);
            for (uint16 i = 0;; i++) {
                ConCommandRef ref(i);
                auto *data = g_pCVar->GetConCommandData(ref);
                if (data == invalidData)
                    break;
                if (data->GetFlags() & flagsToRemove) {
                    data->RemoveFlags(flagsToRemove);
                    unhiddenCmds++;
                }
            }
        }

        g_Log->Info("Unhid {} cvars and {} concommands", unhiddenCvars, unhiddenCmds);
    }

    if (m_managed.onStartupServer)
        m_managed.onStartupServer(pszMapName ? pszMapName : "");
}

bool Deadworks::OnPre_CBaseEntity_TakeDamageOld(CBaseEntity *entity, CTakeDamageInfo *info, CTakeDamageResult *result) {
    if (m_managed.onTakeDamageOld)
        return m_managed.onTakeDamageOld(entity, info, result);
    return false;
}

bool Deadworks::OnPre_CModifierProperty_AddModifier(void *modifierProp, CBaseEntity *pCaster, uint32_t hAbility, void *vdata, int iTeam) {
    if (!m_managed.onAddModifier || !modifierProp || !vdata)
        return false;

    // Extract modifier name from VData base layout (offset 0x10 = m_pszName in CEntitySubclassVDataBase)
    auto *name = *reinterpret_cast<const char **>(reinterpret_cast<uintptr_t>(vdata) + 0x10);
    if (!name)
        return false;

    // CModifierVData::m_eDebuffType at offset 1008 (ModifierDebuffType_t: 0=enemy, 1=always, 2=never)
    int32_t debuffType = *reinterpret_cast<int32_t *>(reinterpret_cast<uintptr_t>(vdata) + 1008);

    return m_managed.onAddModifier(modifierProp, pCaster, name, debuffType, iTeam, hAbility);
}

bool Deadworks::OnPre_CCitadelPlayerPawn_ModifyCurrency(void *pawn, ECurrencyType nCurrencyType, int32_t nAmount,
                                                    ECurrencySource nSource, bool bSilent, bool bForceGain, bool bSpendOnly,
                                                    void *pSourceAbility, void *pSourceEntity) {
    if (m_managed.onModifyCurrency)
        return m_managed.onModifyCurrency(pawn, static_cast<uint32_t>(nCurrencyType), nAmount,
                                         static_cast<uint32_t>(nSource), bSilent ? 1 : 0, bForceGain ? 1 : 0,
                                         bSpendOnly ? 1 : 0, pSourceAbility, pSourceEntity);
    return false;
}

bool Deadworks::OnPre_ClientConCommand(void *controller, void *args) {
    if (!m_managed.onClientConCommand)
        return false;

    auto *cmd = reinterpret_cast<CCommand *>(args);
    int argc = cmd->ArgC();
    const char *command = argc > 0 ? (*cmd)[0] : "";
    const char **argv = cmd->ArgV();

    int result = m_managed.onClientConCommand(controller, command, argc, argv);
    return result >= 1;
}

int Deadworks::OnPre_GameEvent(const char *eventName, void *eventPtr) {
    if (m_managed.onGameEvent)
        return m_managed.onGameEvent(eventName, eventPtr);
    return 0;
}

bool Deadworks::OnPre_PostEventAbstract(int msgId, const CNetMessage *pData, uint64 *clientsMask) {
    if (!m_managed.onNetMessageOutgoing || !pData)
        return false;

    auto *pb = pData->AsMessageLite();
    if (!pb)
        return false;

    int size = static_cast<int>(pb->ByteSizeLong());
    if (size <= 0)
        return false;

    std::vector<uint8_t> buf(size);
    if (!pb->SerializeToArray(buf.data(), size))
        return false;

    static thread_local uint8_t outBuf[65536];
    int outLen = 0;
    uint64 outRecipientMask = *clientsMask;

    int result = m_managed.onNetMessageOutgoing(msgId, buf.data(), size, *clientsMask, outBuf, &outLen, &outRecipientMask);

    *clientsMask = outRecipientMask;

    if (outLen > 0) {
        auto *mutablePb = const_cast<google::protobuf::MessageLite *>(pb);
        mutablePb->ParseFromArray(outBuf, outLen);
    }

    return result >= 1;
}

void Deadworks::OnEntityCreated(CEntityInstance *pEntity) {
    // Lazily initialize touch hooks from the first entity's vtable
    if (!m_touchHooksInitialized && pEntity) {
        m_touchHooksInitialized = true;
        auto &mem = MemoryDataLoader::Get();

        auto *vtable = *reinterpret_cast<void ***>(pEntity);
        auto startTouchIdx = mem.GetVirtual("CBaseEntity::StartTouch");
        auto endTouchIdx = mem.GetVirtual("CBaseEntity::EndTouch");

        if (startTouchIdx && vtable[*startTouchIdx]) {
            hooks::g_CBaseEntity_StartTouch = safetyhook::create_inline(vtable[*startTouchIdx], &hooks::Hook_CBaseEntity_StartTouch);
            g_Log->Info("Hooked CBaseEntity::StartTouch (vtable index {})", *startTouchIdx);
        } else {
            g_Log->Warning("CBaseEntity::StartTouch virtual index not configured — touch events unavailable");
        }

        if (endTouchIdx && vtable[*endTouchIdx]) {
            hooks::g_CBaseEntity_EndTouch = safetyhook::create_inline(vtable[*endTouchIdx], &hooks::Hook_CBaseEntity_EndTouch);
            g_Log->Info("Hooked CBaseEntity::EndTouch (vtable index {})", *endTouchIdx);
        } else {
            g_Log->Warning("CBaseEntity::EndTouch virtual index not configured — touch events unavailable");
        }
    }

    if (m_managed.onEntityCreated && pEntity)
        m_managed.onEntityCreated(pEntity);
}

void Deadworks::OnEntitySpawned(CEntityInstance *pEntity) {
    if (m_managed.onEntitySpawned && pEntity)
        m_managed.onEntitySpawned(pEntity);
}

void Deadworks::OnEntityDeleted(CEntityInstance *pEntity) {
    if (m_managed.onEntityDeleted && pEntity)
        m_managed.onEntityDeleted(pEntity);
}

void Deadworks::OnBuildGameSessionManifest(void *manifest) {
    if (!m_managed.onPrecacheResources || !manifest)
        return;

    g_pCurrentManifest = manifest;
    m_managed.onPrecacheResources();
    g_pCurrentManifest = nullptr;
}

void Deadworks::On_ISource2Server_GameFrame(bool simulating, bool bFirstTick, bool bLastTick) {
    // Poll for fully-connected transitions
    if (m_managed.onClientFullConnect) {
        auto *server = g_pNetworkServerService->GetIGameServer();
        for (int i = 0; i < 64; ++i) {
            if (m_clientFullyConnected[i])
                continue;
            auto *client = server->GetClientBySlot(CPlayerSlot(i));
            if (client && client->IsInGame()) {
                m_clientFullyConnected[i] = true;
                m_managed.onClientFullConnect(i);
            }
        }
    }

    if (m_managed.onGameFrame)
        m_managed.onGameFrame(simulating ? 1 : 0, bFirstTick ? 1 : 0, bLastTick ? 1 : 0);
}

void Deadworks::On_ISource2GameClients_ClientPutInServer(CPlayerSlot slot, const char *pszName, int type, uint64 xuid) {
    if (m_managed.onClientPutInServer) {
        int wlen = MultiByteToWideChar(CP_UTF8, 0, pszName, -1, nullptr, 0);
        std::wstring wname(wlen - 1, L'\0');
        MultiByteToWideChar(CP_UTF8, 0, pszName, -1, wname.data(), wlen);

        m_managed.onClientPutInServer(
            slot.Get(),
            reinterpret_cast<const char16_t *>(wname.c_str()),
            xuid,
            type != 0 ? 1 : 0);
    }
}

void Deadworks::On_ISource2GameClients_ClientConnect(CPlayerSlot slot, const char *pszName, uint64 xuid, const char *pszNetworkID, bool unk1, CBufferString *pRejectReason) {
    m_clientFullyConnected[slot.Get()] = false;
}

void Deadworks::On_ISource2GameClients_ClientDisconnect(CPlayerSlot slot, ENetworkDisconnectionReason reason, const char *pszName, uint64 xuid, const char *pszNetworkID) {
    m_clientFullyConnected[slot.Get()] = false;
    if (m_managed.onClientDisconnect)
        m_managed.onClientDisconnect(slot.Get(), static_cast<int>(reason));
}

using CCitadelClientMsg_ChatMsg_t = CNetMessagePB<ECitadelClientMessages::CITADEL_CM_ChatMsg, CCitadelClientMsg_ChatMsg, SG_USERMSG, BUF_RELIABLE, true>;
std::optional<bool> Deadworks::OnPre_CServerSideClientBase_FilterMessage(INetworkMessageProcessingPreFilter *thisptr, const CNetMessage *pData) {
    auto *client = static_cast<CServerSideClientBase *>(thisptr);
    auto *info = pData->GetSerializerPB()->GetNetMessageInfo();

    // Forward all incoming messages to managed for generic hook dispatch
    if (m_managed.onNetMessageIncoming) {
        int msgId = info->m_MessageId;
        auto *pb = pData->AsMessageLite();
        if (pb) {
            int size = static_cast<int>(pb->ByteSizeLong());
            if (size > 0) {
                std::vector<uint8_t> buf(size);
                if (pb->SerializeToArray(buf.data(), size)) {
                    int result = m_managed.onNetMessageIncoming(
                        client->GetPlayerSlot().Get(), msgId, buf.data(), size);
                    if (result >= 1) // Stop/block
                        return true;
                }
            }
        }
    }

    // Legacy chat message handler (existing behavior)
    if (info->m_MessageId == ECitadelClientMessages::CITADEL_CM_ChatMsg) {
        auto *msg = pData->As<CCitadelClientMsg_ChatMsg_t>();
        if (msg->has_chat_text()) {
            if (m_managed.onChatMessage) {
                const auto &utf8Text = msg->chat_text();
                int wlen = MultiByteToWideChar(CP_UTF8, 0, utf8Text.data(), static_cast<int>(utf8Text.size()), nullptr, 0);
                std::wstring wtext(wlen, L'\0');
                MultiByteToWideChar(CP_UTF8, 0, utf8Text.data(), static_cast<int>(utf8Text.size()), wtext.data(), wlen);

                bool blocked = m_managed.onChatMessage(
                    client->GetPlayerSlot().Get(),
                    reinterpret_cast<const char16_t *>(wtext.c_str()),
                    msg->has_all_chat() && msg->all_chat() ? 1 : 0,
                    msg->has_lane_color() ? static_cast<int>(msg->lane_color()) : 0);

                if (blocked)
                    return true;
            }
        }
    }

    return std::nullopt;
}

void Deadworks::OnStartTouch(CBaseEntity *entity, CBaseEntity *other) {
    if (m_managed.onEntityStartTouch && entity && other)
        m_managed.onEntityStartTouch(entity, other);
}

void Deadworks::OnEndTouch(CBaseEntity *entity, CBaseEntity *other) {
    if (m_managed.onEntityEndTouch && entity && other)
        m_managed.onEntityEndTouch(entity, other);
}

void Deadworks::OnEntityFireOutput(void *entity, void *activator, void *caller, const char *outputName) {
    if (m_managed.onEntityFireOutput)
        m_managed.onEntityFireOutput(entity, activator, caller, outputName);
}

void Deadworks::OnEntityAcceptInput(void *entity, void *activator, void *caller, const char *inputName, const char *value) {
    if (m_managed.onEntityAcceptInput)
        m_managed.onEntityAcceptInput(entity, activator, caller, inputName, value);
}

void Deadworks::OnPre_ProcessUsercmds(int playerSlot, const uint8_t *batchBytes, int batchLen, int numCmds, bool paused, float margin, uint8_t *outBytes, int *outLen) {
    if (m_managed.onProcessUsercmds)
        m_managed.onProcessUsercmds(playerSlot, batchBytes, batchLen, numCmds, paused ? 1 : 0, margin, outBytes, outLen);
}

uint64_t Deadworks::OnPre_AbilityThink(int playerSlot, void *pawnEntity, uint64_t heldButtons, uint64_t changedButtons, uint64_t scrollButtons, uint64_t *outForcedButtons) {
    if (m_managed.onAbilityAttempt)
        return m_managed.onAbilityAttempt(playerSlot, pawnEntity, heldButtons, changedButtons, scrollButtons, outForcedButtons);
    return 0;
}

void Deadworks::GetInterfaceFactories() {
    Module server("server");
    Module engine2("engine2");
    Module schemasystem("schemasystem");
    Module networksystem("networksystem");
    Module tier0("tier0");

    InterfaceFactories.server = server.GetSymbol<CreateInterfaceFn>("CreateInterface");
    InterfaceFactories.engine2 = engine2.GetSymbol<CreateInterfaceFn>("CreateInterface");
    InterfaceFactories.schemasystem = schemasystem.GetSymbol<CreateInterfaceFn>("CreateInterface");
    InterfaceFactories.networksystem = networksystem.GetSymbol<CreateInterfaceFn>("CreateInterface");
    InterfaceFactories.tier0 = tier0.GetSymbol<CreateInterfaceFn>("CreateInterface");
}
} // namespace deadworks
