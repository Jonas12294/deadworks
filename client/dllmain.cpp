// Panorama live UI reload for retail Deadlock.
//
// Workflow: drop compiled panorama files into the staging directory and they
// appear in game. No restart, no manual steps.
//
//   citadel\addons_dev\content\      <- you edit here (loose, never mounted)
//   citadel\addons_dev\live.vpk      <- managed by this module, mounted
//
// On a change under content\ the module debounces briefly, repacks the whole
// staging tree into a VPK, then performs the swap sequence and invalidates the
// affected panels.
//
// Why it has to work this way (all verified against retail):
//
//   - Source 2 orders every VPK ahead of every loose directory in a search
//     path group, so loose files can never override packed content no matter
//     what add type is used. Overrides must be packed.
//   - A VPK added at runtime with PATH_ADD_TO_HEAD does land above pak01.
//   - A mounted VPK is held open by the process and cannot be overwritten
//     externally, so the byte swap must be sequenced between RemoveSearchPath
//     and AddSearchPath from inside the process.
//   - Panorama's reload driver already runs at 5 Hz but nothing ever registers
//     a directory to watch, and the watch must be armed on the polling thread
//     because CDirWatcher uses APC completion.
//   - The layout/style/JS cache is keyed by the *source* path with
//     backslashes, e.g. panorama\layout\hud_health.xml.

#include <windows.h>

#include <bcrypt.h>
#include <winhttp.h>

// PROBE 2: only the lite runtime, and only for MessageLite::ParseFromArray -
// no generated message is linked (see SendClientEvent).
#include <google/protobuf/message_lite.h>

#include <cstdarg>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <string>
#include <unordered_map>
#include <unordered_set>
#include <vector>

#pragma comment(lib, "winhttp.lib")
#pragma comment(lib, "bcrypt.lib")
#pragma comment(lib, "user32.lib")

namespace {

// ---------------------------------------------------------------------------
// Verified offsets and indices
// ---------------------------------------------------------------------------

// panorama_print_cache_status thunk; unique in panorama.dll .text.
//   mov rcx, cs:g_pUIEngine / mov rax,[rcx] / jmp qword ptr [rax+408h]
constexpr unsigned char kEnginePattern[] = {
    0x48, 0x8B, 0x0D, 0x00, 0x00, 0x00, 0x00, 0x48,
    0x8B, 0x01, 0x48, 0xFF, 0xA0, 0x08, 0x04, 0x00, 0x00,
};
constexpr char kEngineMask[] = "xxx????xxxxxxxxxx";

constexpr size_t kAddDirWatchIndex = 75;        // CUIEngine, +0x258

// --- server data channel (CUIEngine) ---------------------------------------
//
// Panels receive server data through a real Panorama event, dispatched from
// here and picked up by the stock $.RegisterForUnhandledEvent binding. No V8
// work: the engine marshals the event's argument into the JS callback itself.
//
// Indices read out of panorama.dll (md5 9c4f114d3b3059cf07e435392a64553d).
// Each one is checked against .text before use.
constexpr size_t kDispatchEventIndex = 46;      // +0x170, bool(CUIEvent**), main thread only
constexpr size_t kIsValidEventTypeIndex = 98;   // +0x310, bool(CPanoramaSymbol)
constexpr size_t kRegisterEventTypeIndex = 97;  // +0x308, (sym, UIEventFactory*)
// +0x328, (out, panel, "Name('arg')", scratch). This is the one to use, not
// CreateEvent at 100: for a single-string event the builder slot CreateEvent
// calls is null, while this path calls the argument builder the JS binding uses.
constexpr size_t kCreateEventFromStringIndex = 101;

// Panorama's global symbol table. It is an RVA, so RegisterUiDataEvent proves
// it by round-tripping the symbol it interns back to a string before trusting
// anything else about it.
constexpr uintptr_t kPanoramaSymbolTableRva = 0x560580;

// The event-type registry on CUIEngine: a CUtlOrderedMap<CPanoramaSymbol,
// panorama::UIEventFactory> whose nodes carry the key at +0x10 and the 48-byte
// factory value at +0x18.
//
// The field map comes from client.dll's registrar (client.dll+0x1EE3590) and the
// sync that feeds the engine (client.dll+0x1EE33B0), which copies exactly these
// 48 bytes into CUIEngine::RegisterEventType:
//
//   +0x18 argc          +0x1C kind flag     +0x1D mode
//   +0x20 selected builder                  +0x28 build-from-arg-string builder
//   +0x30 another builder                   +0x38 arg names   +0x40 description
constexpr size_t kEventRegistryCountRva = 0x7D4;   // & 0x7FFFFFFF
constexpr size_t kEventRegistryDataRva = 0x7D8;
constexpr size_t kEventNodeStride = 72;
constexpr size_t kEventNodeKeyOffset = 0x10;
constexpr size_t kEventNodeValueOffset = 0x18;
constexpr size_t kEventFactorySize = 48;
constexpr size_t kEventArgCountOffset = 0;      // within the factory value
constexpr size_t kEventFromStringBuilderOffset = 0x10;   // value +0x10 = node +0x28
constexpr size_t kEventArgNamesOffset = 0x20;            // value +0x20 = node +0x38

// Our own event type. Registered by cloning the factory of an existing
// single-string event, so the engine's own builder does the marshalling.
//
// Reusing a stock event name instead was considered and rejected: every
// single-string event the game registers is a real action (CitadelJoinTeam,
// MoveUp, CitadelCastCheaterVote...), so dispatching one would fire real game
// behaviour and most already have handlers that would eat the event first.
constexpr char kUiDataEvent[] = "DWData";

// The template has to be chosen by name, not by shape. The mode byte only says
// which builder slots were filled in - it says nothing about the argument type -
// so picking "the first single-argument event" lands on things like
// MovePanelLeft, whose one argument is an integer repeatCount. Its arg-string
// builder then rejects our payload and the engine reports
// "Event arguments could not be parsed", which is exactly what happened.
//
// These all take a single string and are core Panorama rather than Citadel
// gameplay, so they are the least likely to move between patches. Only the
// factory is borrowed; DWData gets its own symbol, so cloning one of these
// cannot trigger the behaviour it normally drives.
constexpr const char* kTemplateEventNames[] = {
    "AddStyle", "RemoveStyle", "ToggleStyle", "TriggerStyle",
    "SetImageSource", "TextEntrySetText", "PlaySoundEffect",
};

// Cap on one emitted payload, after encoding. Nothing legitimate approaches it.
constexpr size_t kMaxEmitBytes = 8192;

// Exported by tier0, so no offsets here. CUtlSymbol is returned through a
// hidden pointer, which is why `out` is the second argument.
constexpr char kAddStringExport[] = "?AddString@CUtlSymbolTable@@QEAA?AVCUtlSymbol@@PEBDPEA_N@Z";
constexpr char kSymbolStringExport[] = "?String@CUtlSymbolTable@@QEBAPEBDVCUtlSymbol@@@Z";
constexpr char kFindSymbolExport[] = "?Find@CUtlSymbolTable@@QEBA?AVCUtlSymbol@@PEBD@Z";

// A built CUIEvent carries its own event symbol at +8; DispatchEvent_Internal
// reads it from there to decide who receives the event.
constexpr size_t kUiEventSymbolOffset = 8;

constexpr uintptr_t kInvalidateCallRva = 0xB3FAC;
constexpr unsigned char kInvalidateCallBytes[] = {0xE8, 0x6F, 0x72, 0x09, 0x00};
constexpr uintptr_t kGetChangedFileIatRva = 0x3D1B58;
constexpr uintptr_t kSetDirToWatchIatRva = 0x3D1BA0;
constexpr unsigned int kWatchFlags = 0x11;      // FILE_NAME | LAST_WRITE
constexpr size_t kWatcherDirOffset = 80;        // CDirWatcher's own CUtlString

// CFileSystem_Stdio vtable. Read from the binary - do NOT derive these by
// counting virtuals in sourcesdk/public/filesystem.h, which is reconstructed
// and is missing one virtual after AddSearchPath.
constexpr size_t kAddSearchPathIndex = 31;
constexpr size_t kRemoveSearchPathIndex = 32;
constexpr int kPathAddToHead = 0;

// CGameEventSystem (GameEventSystemClientV001, engine2.dll). Indices read from
// the binary - vtable at engine2.dll+0x588A90, 21 entries.
constexpr size_t kRegisterGameEventHandlerIndex = 12;

// INetworkMessages (NetworkMessagesVersion001, networksystem.dll).
//
// Slot 12 is solid - engine2 calls it on its cached instance from both
// RegisterGameEventHandlerAbstract and ProcessQueuedEvents and reads
// NetMessageInfo_t out of what comes back. 31 is only header order; engine2
// never calls it. Deadworks uses it server-side against the same DLL so it's
// almost certainly right, but we check the result below instead of assuming.
constexpr size_t kGetNetMessageInfoIndex = 12;
constexpr size_t kFindNetworkMessageByIdIndex = 31;

// Messages to subscribe to and log, from [options] netmsgid as a comma-separated
// list. 148 is CUserMsg_CustomGameEvent, what a Deadworks server sends.
constexpr char kDefaultNetMsgIds[] = "148";
constexpr size_t kMaxNetMsgIds = 8;

// net_Tick keeps coming for as long as the connection is up, so it works as a
// "still connected" signal. Never logged, it fires ~60 times a second.
//
// Don't be tempted to use 207 (CMsgSource1LegacyGameEvent) for this like an
// earlier version did. It's event-driven, so a lobby or hero select goes quiet
// for tens of seconds and the module unloads itself out from under you.
constexpr int kDefaultHeartbeatId = 4;
int g_heartbeat_id = kDefaultHeartbeatId;

// NetMessageInfo_t. Offsets taken from how engine2 itself reads the struct.
constexpr size_t kInfoBindingOffset = 8;    // IProtobufBinding*
constexpr size_t kInfoGroupOffset = 16;     // const char*
constexpr size_t kInfoMessageIdOffset = 24; // int

constexpr size_t kNetMessageGetSerializerIndex = 3;  // CNetMessage vtable

// IProtobufBinding - nine methods, no dtor, no unk placeholders. GetName at 0
// is confirmed live so we know where the vtable starts, but the rest is still
// just header order.
//
// ValidateBindingLayout() checks it before we call ToString: GetGroup should
// return the same string NetMessageInfo_t already gave us. Everything in this
// vtable except ToString is a no-arg getter, so that probe can't blow up even
// if it lands on the wrong one, and we page-check the result before reading it.
constexpr size_t kBindingGetNameIndex = 0;
constexpr size_t kBindingToStringIndex = 2;
constexpr size_t kBindingGetGroupIndex = 3;

// engine2's own cached INetworkMessages*, from its AppSystem table entry for
// "NetworkMessagesVersion001". Only used to check that CreateInterface hands
// us the same instance engine2 uses - a mismatch would make every
// GetNetMessageInfo lookup quietly miss.
constexpr uintptr_t kEngineNetMessagesRva = 0x691288;

using AddDirWatchFn = bool(__fastcall*)(void*, const char*);
// CUtlSymbol is a class, so it comes back through a hidden return pointer.
using AddSymbolFn = void(__fastcall*)(void*, uint16_t*, const char*, bool*);
using SymbolStringFn = const char*(__fastcall*)(void*, const uint16_t*);
using FindSymbolFn = void(__fastcall*)(void*, uint16_t*, const char*);
// The builder the JS DispatchEvent binding and CreateEventFromString both call:
// (out, panel, "'arg'", scratch). Returns `out`; leaves *out null if the
// argument text does not match what the event expects.
using BuildFromArgsFn = void*(__fastcall*)(void**, void*, const char*, void*);
using CreateEventFromStringFn = void*(__fastcall*)(void*, void**, void*, const char*, void*);
using RegisterEventTypeFn = void(__fastcall*)(void*, uint16_t, const void*);
using IsValidEventTypeFn = bool(__fastcall*)(void*, uint16_t);
// CreateEvent is variadic; for a single-string event type the one vararg is the
// string, which lands in the same stack slot a fifth fixed argument would.
using CreateEventFn = void*(__fastcall*)(void*, void**, uint16_t, void*, const char*);
using DispatchEventFn = char(__fastcall*)(void*, void**);
using InvalidateFn = void(__fastcall*)(void*, const char*);
using GetChangedFileFn = bool(__fastcall*)(void*, void*);
using SetDirToWatchFn = bool(__fastcall*)(void*, const char*, unsigned int, bool,
                                          unsigned int, unsigned __int64);
using CreateInterfaceFn = void*(__cdecl*)(const char*, int*);
using AddSearchPathFn = void(__fastcall*)(void*, const char*, const char*, int, int, int);
using RemoveSearchPathFn = bool(__fastcall*)(void*, const char*, const char*);
using FindNetworkMessageByIdFn = void*(__fastcall*)(void*, int);
using GetNetMessageInfoFn = void*(__fastcall*)(void*, void*);
using GetSerializerPBFn = void*(__fastcall*)(void*);
using GetBindingNameFn = const char*(__fastcall*)(void*);
// ToString(CNetMessage *pData, CUtlString &sResult) const - CUtlString is a
// bare char*, so a zeroed scratch buffer serves as the out parameter.
using BindingToStringFn = const char*(__fastcall*)(void*, void*, void*);
// RegisterGameEventHandlerAbstract(this, CUtlSlot*, const CUtlAbstractDelegate&,
//                                  INetworkMessageInternal*, int nPriority)
using RegisterGameEventHandlerFn = void(__fastcall*)(void*, void*, const void*, void*, int);

// Quiet period after the last write before compiling, so a burst of writes
// (or a copy in progress) produces one rebuild rather than several.
// Overridable via [options] debounce in uiwatch.ini.
uint64_t kDebounceMsRuntime = 400;

// ---------------------------------------------------------------------------
// State
// ---------------------------------------------------------------------------

HMODULE g_self = nullptr;
char g_log_path[MAX_PATH] = {};

InvalidateFn g_original_invalidate = nullptr;
GetChangedFileFn g_original_get_changed = nullptr;
SetDirToWatchFn g_set_dir_to_watch = nullptr;

// --- self-eject ---
//
// You can't just FreeLibrary this thing. We've patched a call site in
// panorama's .text to jump to a stub that lands in our image, and swapped an
// IAT entry for one of our functions. Unload with either still in place and the
// engine jumps into freed memory on the next reload tick.
//
// Order that works: undo both hooks and drop our search paths on the engine
// thread, wait out anything already inside our code, free the stub, then unload
// from a thread that isn't in the image.
uint8_t* g_panorama_base = nullptr;
uint8_t* g_invalidate_site = nullptr;   // patched call site, kept so we can put it back
uint8_t* g_stub = nullptr;              // trampoline, freed once unhooked
volatile LONG g_eject_requested = 0;
volatile LONG g_unhooked = 0;           // nothing can enter our code past this
volatile LONG g_shutdown = 0;           // tells the workers to quit
volatile LONG64 g_last_netmsg_ms = 0;
uint64_t g_eject_idle_ms = 30000;       // 0 disables

// Liveness comes from the client's own netchannel, not from message traffic.
// The earlier design armed on CNETMsg_Tick (4) and timed out on any message,
// but the client event system only ever delivers user messages to us - a
// subscription to 4 succeeds and then never fires - so the eject could never
// arm and the module stayed resident for the life of the game. The netchannel
// pointer IS the connection: null until connected, null again once it drops.
volatile LONG g_channel_seen = 0;       // a live channel has been observed at least once
volatile LONG64 g_channel_lost_ms = 0;  // when it went away (0 while live)
volatile LONG g_channel_warned = 0;     // "never resolved" said once, not every tick
uint64_t g_started_ms = 0;              // for that warning's grace period
uint64_t g_last_eject_file_check = 0;   // the manual-eject file is polled, not stat'd every tick
std::string g_eject_marker;             // <dll dir>\DELETE-TO-EJECT.eject
bool g_eject_marker_written = false;    // only watch for its deletion if we wrote it

/// How long the netchannel may stay unresolvable before the module says so.
constexpr uint64_t kChannelWarnAfterMs = 180000;

/// Where the service -> conn -> netchannel walk last stopped, so a failure can
/// name the step rather than just "not connected".
enum class ChannelStage : int { NoSlot = 0, NoService = 1, NoConn = 2, NoChannel = 3, Live = 4 };
volatile LONG g_channel_stage = 0;

/// Last signon state seen, so transitions are logged once rather than per tick.
volatile LONG g_signon_state = -1;
volatile LONG g_signon_bogus_logged = 0;

// Deadlock never really leaves you disconnected: quitting a match drops you
// into a LOCAL lobby server, which connects again immediately, so "no
// connection" only ever shows up as a few seconds of loading screen. What
// actually means "done" is therefore not the absence of a connection but the
// absence of a DEADWORKS one - so each connection is watched for our own
// traffic, and one that never carries any is not a server this module has any
// business staying loaded for.
void* g_conn_object = nullptr;          // identity of the current connection
volatile LONG g_deadworks_seen = 0;     // any dw.* message on THIS connection
uint64_t g_session_ingame_ms = 0;       // when the current connection reached "in game"

/// How long a fully-joined connection may stay silent before the module leaves.
constexpr uint64_t kNoDeadworksMs = 90000;
constexpr int kSignonInGame = 6;

void* g_engine = nullptr;
void* g_filesystem = nullptr;

// Client mode is what the launcher ships - server bundles only, no config,
// paths worked out from the running game. Developer mode adds the compile and
// hot-reload loop and needs uiwatch.ini plus a CSDK. A complete ini picks dev.
//
// Client mode still watches a directory and primes the layout manager. Those
// aren't hot-reload extras: the watch is what gets us an engine-thread
// callback, and the layout manager is what reloads a panel. Skip them and a
// bundle downloads and mounts but never actually shows up.
bool g_dev_mode = false;

// Configured in uiwatch.ini next to the DLL.
std::string g_compiler;      // bin_cs2 resourcecompiler.exe
std::string g_gamedir;       // -game argument
std::string g_source_dir;    // watched; .xml/.css/.js you edit
std::string g_output_dir;    // compiler output; packed into the VPK
std::string g_live_vpk;      // managed, mounted at head
std::string g_bundle_vpk;    // server-delivered bundle, mounted separately

// Compiling spawns a process and takes a second or more, so it cannot run on
// the engine thread without stalling the game. The worker compiles and packs;
// the engine thread only does the swap, which is fast.
enum class Stage { Idle, Building, ReadySwap };
volatile LONG g_stage = static_cast<LONG>(Stage::Idle);
HANDLE g_work_event = nullptr;
std::vector<std::string> g_building_sources;  // handed to the worker
std::vector<std::string> g_building_keys;     // invalidated after the swap

constexpr size_t kMaxWatchers = 64;
void* g_rearmed[kMaxWatchers] = {};
size_t g_rearmed_count = 0;

// Net message spike. The delegate is the two-pointer form the engine actually
// uses: { m_pthis, m_pFunction }.
struct AbstractDelegate { void* pthis; void* fn; };
void* g_game_event_system = nullptr;
void* g_net_messages = nullptr;
AbstractDelegate g_ui_delegate = {};
int g_handler_anchor = 0;              // a unique, stable m_pthis

// One entry per subscribed message; the same delegate is registered for each,
// and the handler tells them apart from the CNetMessage it is handed.
struct SubscribedMsg {
    int id;
    void* internal;                    // INetworkMessageInternal*
    char name[96];                     // e.g. "CUserMsg_CustomGameEvent [148]"
    bool dump;                         // decode the payload when this arrives
    bool heartbeat;                    // liveness only: never logged
};

// Idle-eject stays off until we've actually seen a heartbeat. If net_Tick ever
// stops reaching handlers, we'd rather sit resident than unload every 30s.
volatile LONG g_heartbeat_seen = 0;
SubscribedMsg g_netmsgs[kMaxNetMsgIds] = {};
size_t g_netmsg_id_count = 0;

// -1 not tried yet, 0 layout rejected, 1 layout matches and ToString is usable.
int g_binding_layout_ok = -1;
// 0 = inactive, 1 = resolved and waiting for the poll thread to register,
// 2 = registered. Registration must not run on the injected thread.
volatile LONG g_netmsg_stage = 0;
volatile LONG g_netmsg_count = 0;

// Server data channel. A panel calling $.RegisterForUnhandledEvent throws if the
// event type does not exist yet, and a panel only tries once when it loads, so
// the type has to be registered before any bundle arrives - the first poll tick,
// not lazily on the first emit.
uint16_t g_ui_data_symbol = 0xFFFF;
volatile LONG g_ui_event_stage = 0;   // 0 untried, 1 registered, 2 unavailable
volatile LONG g_emit_drop_warned = 0; // emits arrive repeatedly; complain once

// The borrowed builder that turns one quoted string into a CUIEvent. Called by
// BuildUiDataEvent below, which then restamps the event with our own symbol.
BuildFromArgsFn g_template_builder = nullptr;

// Every key we've overridden, hot-reload or bundle. Eject has to evict the lot
// or panels keep showing our content after the search paths are gone.
// Engine thread only.
std::vector<std::string> g_overridden_keys;

void RememberOverride(const std::string& key) {
    for (const auto& k : g_overridden_keys)
        if (k == key) return;
    g_overridden_keys.push_back(key);
}

// Touched only from the engine (poll) thread.
std::vector<std::string> g_pending_keys;
std::vector<std::string> g_pending_sources;
uint64_t g_last_change_ms = 0;
bool g_dirty = false;
// The driver's invalidation call is the only place this pointer is ever handed
// out, so until it fires once we can't reload anything. Startup touches a
// watched file just to force it; g_prime_pending flags that event as ours so we
// grab the pointer without kicking off a pointless recompile.
void* g_layout_manager = nullptr;
volatile LONG g_prime_pending = 0;
volatile LONG g_poll_count = 0;
DWORD g_inject_tid = 0;

// Defined in the Teardown section; called from the poll probe above it.
void UnhookAndUnmount();
void RequestEject(const char* why);

// Defined with the 280 send path below; the poll probe reads it as the
// connection's liveness signal.
bool ClientConnected();
void* GetClientConnection();

// Key input: defined in the input section below, used by the poll probe.
bool SendClientEvent(const std::string& event_name, const std::string& data);
void HandleInputBind(const std::string& payload, bool bind);
void FlushInputReports();
void InstallInputHook();
void RemoveInputHook();

void Log(const char* fmt, ...) {
    if (!g_log_path[0]) return;
    FILE* f = nullptr;
    if (fopen_s(&f, g_log_path, "a") != 0 || !f) return;
    va_list args;
    va_start(args, fmt);
    vfprintf(f, fmt, args);
    va_end(args);
    fputc('\n', f);
    fclose(f);
}

void InitLog() {
    if (GetModuleFileNameA(g_self, g_log_path, MAX_PATH) == 0) return;
    char* slash = strrchr(g_log_path, '\\');
    if (!slash) { g_log_path[0] = '\0'; return; }
    strcpy_s(slash + 1, MAX_PATH - (slash + 1 - g_log_path), "uiwatch.log");
    FILE* f = nullptr;
    if (fopen_s(&f, g_log_path, "w") == 0 && f) fclose(f);
}

// ---------------------------------------------------------------------------
// PE helpers
// ---------------------------------------------------------------------------

bool GetSection(HMODULE module, const char* name, uint8_t** begin, size_t* size) {
    auto* base = reinterpret_cast<uint8_t*>(module);
    auto* dos = reinterpret_cast<IMAGE_DOS_HEADER*>(base);
    if (dos->e_magic != IMAGE_DOS_SIGNATURE) return false;
    auto* nt = reinterpret_cast<IMAGE_NT_HEADERS64*>(base + dos->e_lfanew);
    if (nt->Signature != IMAGE_NT_SIGNATURE) return false;
    auto* section = IMAGE_FIRST_SECTION(nt);
    for (WORD i = 0; i < nt->FileHeader.NumberOfSections; ++i, ++section) {
        char n[9] = {};
        memcpy(n, section->Name, 8);
        if (_stricmp(n, name) == 0) {
            *begin = base + section->VirtualAddress;
            *size = section->Misc.VirtualSize;
            return true;
        }
    }
    return false;
}

uint8_t* FindPattern(uint8_t* begin, size_t size, const unsigned char* pat, const char* mask) {
    const size_t len = strlen(mask);
    if (size < len) return nullptr;
    for (size_t i = 0; i <= size - len; ++i) {
        bool ok = true;
        for (size_t j = 0; j < len; ++j) {
            if (mask[j] == 'x' && begin[i + j] != pat[j]) { ok = false; break; }
        }
        if (ok) return begin + i;
    }
    return nullptr;
}

void* HookIat(uint8_t* base, uintptr_t rva, void* replacement) {
    auto** slot = reinterpret_cast<void**>(base + rva);
    DWORD old = 0;
    if (!VirtualProtect(slot, sizeof(void*), PAGE_READWRITE, &old)) return nullptr;
    void* prev = *slot;
    *slot = replacement;
    VirtualProtect(slot, sizeof(void*), old, &old);
    return prev;
}

uint8_t* AllocStubNear(uint8_t* near_addr, void* target) {
    SYSTEM_INFO si;
    GetSystemInfo(&si);
    const uintptr_t gran = si.dwAllocationGranularity;
    const uintptr_t origin = reinterpret_cast<uintptr_t>(near_addr);
    for (uintptr_t delta = gran; delta < 0x40000000; delta += gran) {
        for (int dir = 0; dir < 2; ++dir) {
            const uintptr_t cand = dir ? origin + delta : origin - delta;
            auto* mem = static_cast<uint8_t*>(VirtualAlloc(
                reinterpret_cast<LPVOID>(cand & ~(gran - 1)), 64,
                MEM_RESERVE | MEM_COMMIT, PAGE_EXECUTE_READWRITE));
            if (!mem) continue;
            mem[0] = 0xFF; mem[1] = 0x25;
            *reinterpret_cast<uint32_t*>(mem + 2) = 0;
            *reinterpret_cast<void**>(mem + 6) = target;
            return mem;
        }
    }
    return nullptr;
}

// ---------------------------------------------------------------------------
// VPK v2 writer (inline data, archive index 0x7FFF)
// ---------------------------------------------------------------------------

uint32_t Crc32(const uint8_t* data, size_t len) {
    static uint32_t table[256];
    static bool built = false;
    if (!built) {
        for (uint32_t i = 0; i < 256; ++i) {
            uint32_t c = i;
            for (int k = 0; k < 8; ++k) c = (c & 1) ? (0xEDB88320u ^ (c >> 1)) : (c >> 1);
            table[i] = c;
        }
        built = true;
    }
    uint32_t crc = 0xFFFFFFFFu;
    for (size_t i = 0; i < len; ++i) crc = table[(crc ^ data[i]) & 0xFF] ^ (crc >> 8);
    return crc ^ 0xFFFFFFFFu;
}

struct StagedFile {
    std::string vpk_path;          // forward slashes, relative to content root
    std::vector<uint8_t> bytes;
};

void CollectFiles(const std::string& root, const std::string& rel,
                  std::vector<StagedFile>& out) {
    const std::string pattern = root + (rel.empty() ? "" : "\\" + rel) + "\\*";
    WIN32_FIND_DATAA fd;
    HANDLE h = FindFirstFileA(pattern.c_str(), &fd);
    if (h == INVALID_HANDLE_VALUE) return;
    do {
        if (fd.cFileName[0] == '.') continue;
        const std::string child = rel.empty() ? fd.cFileName : rel + "\\" + fd.cFileName;
        if (fd.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) {
            CollectFiles(root, child, out);
            continue;
        }
        const std::string full = root + "\\" + child;
        HANDLE fh = CreateFileA(full.c_str(), GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE,
                                nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
        if (fh == INVALID_HANDLE_VALUE) continue;
        LARGE_INTEGER sz;
        StagedFile sf;
        if (GetFileSizeEx(fh, &sz) && sz.QuadPart >= 0 && sz.QuadPart < (64 << 20)) {
            sf.bytes.resize(static_cast<size_t>(sz.QuadPart));
            DWORD read = 0;
            if (!sf.bytes.empty())
                ReadFile(fh, sf.bytes.data(), static_cast<DWORD>(sf.bytes.size()), &read, nullptr);
            if (read == sf.bytes.size()) {
                sf.vpk_path = child;
                for (char& c : sf.vpk_path) if (c == '\\') c = '/';
                out.push_back(std::move(sf));
            }
        }
        CloseHandle(fh);
    } while (FindNextFileA(h, &fd));
    FindClose(h);
}

void PutU32(std::vector<uint8_t>& v, uint32_t x) {
    v.push_back(x & 0xFF); v.push_back((x >> 8) & 0xFF);
    v.push_back((x >> 16) & 0xFF); v.push_back((x >> 24) & 0xFF);
}
void PutU16(std::vector<uint8_t>& v, uint16_t x) {
    v.push_back(x & 0xFF); v.push_back((x >> 8) & 0xFF);
}
void PutStr(std::vector<uint8_t>& v, const std::string& s) {
    v.insert(v.end(), s.begin(), s.end());
    v.push_back(0);
}

// Returns the number of files packed, or -1 on failure.
int BuildVpk(const std::string& content_dir, const std::string& out_path) {
    std::vector<StagedFile> files;
    CollectFiles(content_dir, "", files);
    if (files.empty()) return 0;

    // Group by extension, then directory - the order the VPK tree requires.
    struct Entry { std::string name; const StagedFile* file; };
    std::vector<std::string> exts;
    std::vector<std::vector<std::string>> dirs;
    std::vector<std::vector<std::vector<Entry>>> names;

    for (const auto& f : files) {
        const size_t slash = f.vpk_path.rfind('/');
        const std::string dir = (slash == std::string::npos) ? " " : f.vpk_path.substr(0, slash);
        const std::string leaf = (slash == std::string::npos) ? f.vpk_path : f.vpk_path.substr(slash + 1);
        const size_t dot = leaf.rfind('.');
        const std::string ext = (dot == std::string::npos) ? "" : leaf.substr(dot + 1);
        const std::string name = (dot == std::string::npos) ? leaf : leaf.substr(0, dot);

        size_t ei = 0;
        for (; ei < exts.size(); ++ei) if (exts[ei] == ext) break;
        if (ei == exts.size()) { exts.push_back(ext); dirs.emplace_back(); names.emplace_back(); }
        size_t di = 0;
        for (; di < dirs[ei].size(); ++di) if (dirs[ei][di] == dir) break;
        if (di == dirs[ei].size()) { dirs[ei].push_back(dir); names[ei].emplace_back(); }
        names[ei][di].push_back({name, &f});
    }

    std::vector<uint8_t> tree, data;
    for (size_t ei = 0; ei < exts.size(); ++ei) {
        PutStr(tree, exts[ei]);
        for (size_t di = 0; di < dirs[ei].size(); ++di) {
            PutStr(tree, dirs[ei][di]);
            for (const auto& e : names[ei][di]) {
                PutStr(tree, e.name);
                PutU32(tree, Crc32(e.file->bytes.data(), e.file->bytes.size()));
                PutU16(tree, 0);        // preload bytes
                PutU16(tree, 0x7FFF);   // inline data
                PutU32(tree, static_cast<uint32_t>(data.size()));
                PutU32(tree, static_cast<uint32_t>(e.file->bytes.size()));
                PutU16(tree, 0xFFFF);   // terminator
                data.insert(data.end(), e.file->bytes.begin(), e.file->bytes.end());
            }
            tree.push_back(0);  // end of names
        }
        tree.push_back(0);      // end of dirs
    }
    tree.push_back(0);          // end of extensions

    HANDLE h = CreateFileA(out_path.c_str(), GENERIC_WRITE, 0, nullptr,
                           CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (h == INVALID_HANDLE_VALUE) return -1;
    std::vector<uint8_t> header;
    PutU32(header, 0x55AA1234);
    PutU32(header, 2);
    PutU32(header, static_cast<uint32_t>(tree.size()));
    PutU32(header, static_cast<uint32_t>(data.size()));
    PutU32(header, 0); PutU32(header, 0); PutU32(header, 0);
    DWORD written = 0;
    bool ok = WriteFile(h, header.data(), (DWORD)header.size(), &written, nullptr) &&
              WriteFile(h, tree.data(), (DWORD)tree.size(), &written, nullptr) &&
              (data.empty() || WriteFile(h, data.data(), (DWORD)data.size(), &written, nullptr));
    CloseHandle(h);
    return ok ? static_cast<int>(files.size()) : -1;
}

// ---------------------------------------------------------------------------
// Filesystem interface
// ---------------------------------------------------------------------------

bool ResolveFilesystem() {
    HMODULE m = GetModuleHandleW(L"filesystem_stdio.dll");
    if (!m) { Log("[uiwatch] filesystem_stdio.dll not loaded"); return false; }
    auto create = reinterpret_cast<CreateInterfaceFn>(GetProcAddress(m, "CreateInterface"));
    if (!create) { Log("[uiwatch] no CreateInterface export"); return false; }
    g_filesystem = create("VFileSystem017", nullptr);
    if (!g_filesystem) { Log("[uiwatch] VFileSystem017 returned null"); return false; }

    uint8_t* text = nullptr; size_t size = 0;
    GetSection(m, ".text", &text, &size);
    auto** vt = *reinterpret_cast<void***>(g_filesystem);
    for (size_t i : {kAddSearchPathIndex, kRemoveSearchPathIndex}) {
        void* p = vt[i];
        if (text && (p < text || p >= text + size)) {
            Log("[uiwatch] fs vtable[%zu] = %p is outside .text - ABORT", i, p);
            g_filesystem = nullptr;
            return false;
        }
    }
    return true;
}

void MountLiveVpk() {
    auto** vt = *reinterpret_cast<void***>(g_filesystem);
    reinterpret_cast<AddSearchPathFn>(vt[kAddSearchPathIndex])(
        g_filesystem, g_live_vpk.c_str(), "GAME", kPathAddToHead, 0, 0);
}

void UnmountLiveVpk() {
    auto** vt = *reinterpret_cast<void***>(g_filesystem);
    reinterpret_cast<RemoveSearchPathFn>(vt[kRemoveSearchPathIndex])(
        g_filesystem, g_live_vpk.c_str(), "GAME");
}

void MountVpk(const std::string& path) {
    auto** vt = *reinterpret_cast<void***>(g_filesystem);
    reinterpret_cast<AddSearchPathFn>(vt[kAddSearchPathIndex])(
        g_filesystem, path.c_str(), "GAME", kPathAddToHead, 0, 0);
}

void UnmountVpk(const std::string& path) {
    auto** vt = *reinterpret_cast<void***>(g_filesystem);
    reinterpret_cast<RemoveSearchPathFn>(vt[kRemoveSearchPathIndex])(
        g_filesystem, path.c_str(), "GAME");
}

// ---------------------------------------------------------------------------
// Server bundles: manifest -> download -> verify -> mount
//
// FIXME: the manifest is scraped out of ToString's TextFormat output. Fine for
// proving the download/mount path works (that half doesn't care where the URL
// came from), but the real client should link protobuf and go through
// AsMessage(), CNetMessage vtable 2. NativeSendNetMessage on the server side
// already does exactly that against the engine's own message objects.
// ---------------------------------------------------------------------------

constexpr size_t kMaxBundleBytes = 32u << 20;

struct Manifest {
    std::string id;
    std::string url;
    std::string sha256;
    std::vector<std::string> keys;
};

// Pulls out `name: "..."` and undoes protobuf TextFormat's C escaping.
bool ExtractQuoted(const char* text, const char* name, std::string& out) {
    std::string needle = std::string(name) + ": \"";
    const char* p = strstr(text, needle.c_str());
    if (!p) return false;
    p += needle.size();

    out.clear();
    for (; *p && *p != '"'; ++p) {
        if (*p != '\\') { out.push_back(*p); continue; }
        ++p;
        if (!*p) break;
        switch (*p) {
            case 'n': out.push_back('\n'); break;
            case 'r': out.push_back('\r'); break;
            case 't': out.push_back('\t'); break;
            case '\\': out.push_back('\\'); break;
            case '"': out.push_back('"'); break;
            case '\'': out.push_back('\''); break;
            case 'x': {
                int v = 0, n = 0;
                while (n < 2 && isxdigit(static_cast<unsigned char>(p[1]))) {
                    const char c = *++p;
                    v = v * 16 + (isdigit(static_cast<unsigned char>(c)) ? c - '0'
                                                                        : (tolower(c) - 'a' + 10));
                    ++n;
                }
                out.push_back(static_cast<char>(v));
                break;
            }
            default:
                if (*p >= '0' && *p <= '7') {   // octal, which is what protobuf emits
                    int v = 0, n = 0;
                    while (n < 3 && *p >= '0' && *p <= '7') { v = v * 8 + (*p - '0'); ++p; ++n; }
                    --p;
                    out.push_back(static_cast<char>(v));
                } else {
                    out.push_back(*p);
                }
        }
    }
    return true;
}

bool IsHex64(const std::string& s) {
    if (s.size() != 64) return false;
    for (char c : s)
        if (!isxdigit(static_cast<unsigned char>(c))) return false;
    return true;
}

// Mirrors UiPayload: line-oriented key=value, split on the FIRST '='.
bool ParseManifest(const std::string& payload, Manifest& out) {
    size_t start = 0;
    std::string version;
    while (start <= payload.size()) {
        size_t nl = payload.find('\n', start);
        if (nl == std::string::npos) nl = payload.size();
        const std::string line = payload.substr(start, nl - start);
        start = nl + 1;
        if (line.empty()) { if (nl == payload.size()) break; continue; }

        const size_t eq = line.find('=');
        if (eq == std::string::npos) continue;
        const std::string k = line.substr(0, eq);
        const std::string v = line.substr(eq + 1);

        if (k == "v") version = v;
        else if (k == "id") out.id = v;
        else if (k == "url") out.url = v;
        else if (k == "sha256") out.sha256 = v;
        else if (k == "key") out.keys.push_back(v);

        if (nl == payload.size()) break;
    }

    if (version != "1") {
        Log("[uiwatch] bundle: unsupported payload version '%s' - ignoring", version.c_str());
        return false;
    }
    if (out.url.compare(0, 8, "https://") != 0) {
        Log("[uiwatch] bundle: refusing non-https url '%s'", out.url.c_str());
        return false;
    }
    if (!IsHex64(out.sha256)) {
        Log("[uiwatch] bundle: sha256 is not 64 hex characters - ignoring");
        return false;
    }
    for (const auto& k : out.keys) {
        if (k.find('/') != std::string::npos) {
            Log("[uiwatch] bundle: cache key '%s' uses forward slashes; Panorama keys use "
                "backslashes and this would match nothing", k.c_str());
            return false;
        }
    }
    return true;
}

std::wstring Widen(const std::string& s) {
    if (s.empty()) return std::wstring();
    const int n = MultiByteToWideChar(CP_UTF8, 0, s.c_str(), static_cast<int>(s.size()), nullptr, 0);
    std::wstring w(static_cast<size_t>(n), L'\0');
    MultiByteToWideChar(CP_UTF8, 0, s.c_str(), static_cast<int>(s.size()), &w[0], n);
    return w;
}

struct InetHandle {
    HINTERNET h = nullptr;
    ~InetHandle() { if (h) WinHttpCloseHandle(h); }
};

bool HttpsGet(const std::string& url, std::vector<uint8_t>& out, std::string& err) {
    const std::wstring wurl = Widen(url);
    wchar_t host[256] = {}, path[2048] = {};
    URL_COMPONENTS uc = {};
    uc.dwStructSize = sizeof(uc);
    uc.lpszHostName = host;  uc.dwHostNameLength = _countof(host) - 1;
    uc.lpszUrlPath = path;   uc.dwUrlPathLength = _countof(path) - 1;

    if (!WinHttpCrackUrl(wurl.c_str(), 0, 0, &uc)) { err = "malformed url"; return false; }
    if (uc.nScheme != INTERNET_SCHEME_HTTPS) { err = "not https"; return false; }

    InetHandle session, connect, request;
    session.h = WinHttpOpen(L"uiwatch/1.0", WINHTTP_ACCESS_TYPE_DEFAULT_PROXY,
                            WINHTTP_NO_PROXY_NAME, WINHTTP_NO_PROXY_BYPASS, 0);
    if (!session.h) { err = "WinHttpOpen failed"; return false; }
    WinHttpSetTimeouts(session.h, 10000, 10000, 20000, 20000);

    connect.h = WinHttpConnect(session.h, host, uc.nPort, 0);
    if (!connect.h) { err = "connect failed"; return false; }

    request.h = WinHttpOpenRequest(connect.h, L"GET", path, nullptr, WINHTTP_NO_REFERER,
                                   WINHTTP_DEFAULT_ACCEPT_TYPES, WINHTTP_FLAG_SECURE);
    if (!request.h) { err = "OpenRequest failed"; return false; }

    if (!WinHttpSendRequest(request.h, WINHTTP_NO_ADDITIONAL_HEADERS, 0,
                            WINHTTP_NO_REQUEST_DATA, 0, 0, 0) ||
        !WinHttpReceiveResponse(request.h, nullptr)) {
        char buf[64];
        sprintf_s(buf, "request failed (%lu)", GetLastError());
        err = buf;
        return false;
    }

    DWORD status = 0, len = sizeof(status);
    if (!WinHttpQueryHeaders(request.h, WINHTTP_QUERY_STATUS_CODE | WINHTTP_QUERY_FLAG_NUMBER,
                             WINHTTP_HEADER_NAME_BY_INDEX, &status, &len, WINHTTP_NO_HEADER_INDEX)) {
        err = "could not read status"; return false;
    }
    if (status != 200) {
        char buf[64];
        sprintf_s(buf, "http %lu", status);
        err = buf;
        return false;
    }

    out.clear();
    for (;;) {
        DWORD avail = 0;
        if (!WinHttpQueryDataAvailable(request.h, &avail)) { err = "read failed"; return false; }
        if (avail == 0) break;
        if (out.size() + avail > kMaxBundleBytes) { err = "bundle exceeds size cap"; return false; }
        const size_t at = out.size();
        out.resize(at + avail);
        DWORD got = 0;
        if (!WinHttpReadData(request.h, out.data() + at, avail, &got)) { err = "read failed"; return false; }
        out.resize(at + got);
        if (got == 0) break;
    }
    return true;
}

bool Sha256Hex(const std::vector<uint8_t>& data, std::string& hex) {
    BCRYPT_ALG_HANDLE alg = nullptr;
    BCRYPT_HASH_HANDLE hash = nullptr;
    bool ok = false;
    uint8_t digest[32] = {};

    if (BCryptOpenAlgorithmProvider(&alg, BCRYPT_SHA256_ALGORITHM, nullptr, 0) >= 0) {
        if (BCryptCreateHash(alg, &hash, nullptr, 0, nullptr, 0, 0) >= 0) {
            if (BCryptHashData(hash, const_cast<PUCHAR>(data.data()),
                               static_cast<ULONG>(data.size()), 0) >= 0 &&
                BCryptFinishHash(hash, digest, sizeof(digest), 0) >= 0) {
                ok = true;
            }
            BCryptDestroyHash(hash);
        }
        BCryptCloseAlgorithmProvider(alg, 0);
    }
    if (!ok) return false;

    char buf[65] = {};
    for (int i = 0; i < 32; ++i) sprintf_s(buf + i * 2, 3, "%02x", digest[i]);
    hex = buf;
    return true;
}

bool WriteAllBytes(const std::string& path, const std::vector<uint8_t>& bytes) {
    HANDLE h = CreateFileA(path.c_str(), GENERIC_WRITE, 0, nullptr, CREATE_ALWAYS,
                           FILE_ATTRIBUTE_NORMAL, nullptr);
    if (h == INVALID_HANDLE_VALUE) return false;
    DWORD written = 0;
    const bool ok = WriteFile(h, bytes.data(), static_cast<DWORD>(bytes.size()), &written, nullptr) &&
                    written == bytes.size();
    CloseHandle(h);
    return ok;
}

// Download can't run on the engine thread, the mount has to. Same split as the
// compile path: worker fetches and verifies, poll thread swaps.
enum class BundleStage { Idle, Fetching, ReadyMount };
volatile LONG g_bundle_stage = static_cast<LONG>(BundleStage::Idle);
HANDLE g_bundle_event = nullptr;
Manifest g_bundle_pending;      // handed to the worker
Manifest g_bundle_ready;        // what the poll thread should mount
Manifest g_bundle_current;      // what is mounted right now, for revoke
bool g_bundle_mounted = false;

// Revoke needs no download so it skips the worker - the handler just asks the
// poll thread to unmount.
volatile LONG g_bundle_revoke = 0;

// Revoke payloads only carry the version and the bundle id.
bool ParseRevoke(const std::string& payload, std::string& id) {
    size_t start = 0;
    std::string version;
    while (start <= payload.size()) {
        size_t nl = payload.find('\n', start);
        if (nl == std::string::npos) nl = payload.size();
        const std::string line = payload.substr(start, nl - start);
        const bool last = (nl == payload.size());
        start = nl + 1;
        if (!line.empty()) {
            const size_t eq = line.find('=');
            if (eq != std::string::npos) {
                const std::string k = line.substr(0, eq);
                if (k == "v") version = line.substr(eq + 1);
                else if (k == "id") id = line.substr(eq + 1);
            }
        }
        if (last) break;
    }
    if (version != "1") {
        Log("[uiwatch] bundle: unsupported revoke payload version '%s'", version.c_str());
        return false;
    }
    return !id.empty();
}

// ---------------------------------------------------------------------------
// Subscribing to CUserMsg_CustomGameEvent (148)
//
// Both ABI questions here were answered by reading engine2.dll
// (md5 5e10a4184c0d33bd0f837fbc8284eb52) rather than guessing:
//
// RegisterGameEventHandlerAbstract passes a literal 1 for nDelegateParamCount
// into RegisterEventListener_Base on every path (+0x220749 mov edi,1 feeding
// +0x220779 mov r8d,edi). So EventListenerInfo_t+28 is always 1 here and the
// handler always gets the message as arg 2. The r8/r9 that look uninitialised
// in the decompiler belong to a loop above and are dead.
//
// That arg is the queued CNetMessage* - ProcessQueuedEvents calls vtable[3]
// (GetSerializerPB) on the same pointer, then releases it through vtable[0].
//
// Passing null for CUtlSlot is fine, the slot bookkeeping helper bails out
// immediately when it's null.
// ---------------------------------------------------------------------------

// If the vtable layout isn't what the header claims, a "string" we get back
// might really be a Color or a bool. Check the page before touching it.
bool IsReadableString(const void* p) {
    if (!p) return false;
    MEMORY_BASIC_INFORMATION mbi;
    if (VirtualQuery(p, &mbi, sizeof(mbi)) != sizeof(mbi)) return false;
    if (mbi.State != MEM_COMMIT) return false;
    const DWORD readable = PAGE_READONLY | PAGE_READWRITE | PAGE_WRITECOPY |
                           PAGE_EXECUTE_READ | PAGE_EXECUTE_READWRITE |
                           PAGE_EXECUTE_WRITECOPY;
    return (mbi.Protect & readable) != 0 && (mbi.Protect & PAGE_GUARD) == 0;
}

// Sanity check before we trust ToString: GetGroup should hand back the same
// string NetMessageInfo_t already gave us. If those agree the header order
// holds and index 2 is what we think it is.
bool ValidateBindingLayout(void* binding, const char* expected_group) {
    if (g_binding_layout_ok >= 0) return g_binding_layout_ok == 1;
    g_binding_layout_ok = 0;

    if (!binding || !expected_group || !*expected_group) {
        Log("[uiwatch] netmsg: no group string to validate the binding against; "
            "payload decoding stays off");
        return false;
    }
    const char* group = (*reinterpret_cast<GetBindingNameFn**>(binding))
                            [kBindingGetGroupIndex](binding);
    if (!IsReadableString(group)) {
        Log("[uiwatch] netmsg: binding vtable[%zu] did not return a readable string - "
            "layout is not what iprotobufbinding.h says, payload decoding stays off",
            kBindingGetGroupIndex);
        return false;
    }
    if (strcmp(group, expected_group) != 0) {
        Log("[uiwatch] netmsg: binding GetGroup gave '%s' but NetMessageInfo_t says '%s' - "
            "layout mismatch, payload decoding stays off", group, expected_group);
        return false;
    }
    Log("[uiwatch] netmsg: binding layout confirmed (GetGroup agrees with "
        "NetMessageInfo_t on '%s'); ToString at index %zu is usable",
        group, kBindingToStringIndex);
    g_binding_layout_ok = 1;
    return true;
}

// ---------------------------------------------------------------------------
// Server data channel: UI.Emit -> Panorama event -> panel script
//
// The engine delivers an event to $.RegisterForUnhandledEvent handlers with its
// arguments intact regardless of who dispatched it, so a C++ dispatch reaches
// panel JS with no V8 work at all. What it will not do is accept an event name
// it has never heard of, hence the registration below.
//
// The payload is base64 so it survives both legs unescaped: the console command
// on the way back cannot carry spaces or quotes.
// ---------------------------------------------------------------------------

std::string Base64Encode(const std::string& in) {
    static const char kChars[] =
        "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
    std::string out;
    out.reserve((in.size() + 2) / 3 * 4);
    for (size_t i = 0; i < in.size(); i += 3) {
        const bool has1 = i + 1 < in.size();
        const bool has2 = i + 2 < in.size();
        const unsigned b0 = static_cast<unsigned char>(in[i]);
        const unsigned b1 = has1 ? static_cast<unsigned char>(in[i + 1]) : 0u;
        const unsigned b2 = has2 ? static_cast<unsigned char>(in[i + 2]) : 0u;
        out.push_back(kChars[b0 >> 2]);
        out.push_back(kChars[((b0 & 0x03) << 4) | (b1 >> 4)]);
        out.push_back(has1 ? kChars[((b1 & 0x0F) << 2) | (b2 >> 6)] : '=');
        out.push_back(has2 ? kChars[b2 & 0x3F] : '=');
    }
    return out;
}

// Decodes standard base64; ignores anything outside the alphabet (so it also
// tolerates the whitespace TextFormat scraping might leave in). Returns the
// decoded bytes, empty on nothing usable.
std::string Base64Decode(const std::string& in) {
    auto val = [](char c) -> int {
        if (c >= 'A' && c <= 'Z') return c - 'A';
        if (c >= 'a' && c <= 'z') return c - 'a' + 26;
        if (c >= '0' && c <= '9') return c - '0' + 52;
        if (c == '+') return 62;
        if (c == '/') return 63;
        return -1;
    };
    std::string out;
    out.reserve(in.size() * 3 / 4);
    int acc = 0, bits = 0;
    for (char c : in) {
        if (c == '=') break;
        int v = val(c);
        if (v < 0) continue;
        acc = (acc << 6) | v;
        bits += 6;
        if (bits >= 8) {
            bits -= 8;
            out.push_back(static_cast<char>((acc >> bits) & 0xFF));
        }
    }
    return out;
}

// ---------------------------------------------------------------------------
// Server-pushed image packs: dw.ui.pack -> reassemble -> verify -> mount
//
// The zero-hosting image path (client v27). The server compiles PNGs into a
// textures VPK itself (UiImagePack, SDK-side) and delivers the bytes over the
// data channel as base64 chunks { id, sha256, n, i, b }. Reassembled here,
// hash-verified, written to addons_dev\dwimages.vpk and mounted as a SECOND
// vpk at head, beside the bundle slot - never sharing it, so a pack can
// never unmount a server's UI bundle or vice versa.
//
// No cache keys on purpose: images resolve when a tree next references them,
// which is why the server pushes the pack on connect, before any tree that
// uses it.
// ---------------------------------------------------------------------------

constexpr size_t kMaxPackBytes = 16u << 20;    // decoded size cap
constexpr size_t kMaxPackChunks = 32768;
constexpr size_t kMaxPackChunkChars = 8192;    // the server sends 1024

std::string g_images_vpk;       // addons_dev\dwimages.vpk, the second mount slot
bool g_images_mounted = false;
std::string g_images_sha;       // sha256 of the mounted pack, for repush dedupe

// Chunk reassembly. Engine thread only (the 148 handler runs there), so no
// locking - same rule as g_pending_js.
struct PackAssembly {
    std::string id;
    std::string sha256;
    size_t total = 0;
    size_t got = 0;
    size_t chars = 0;               // accumulated base64, for the size cap
    std::vector<std::string> chunks;
};
PackAssembly g_pack;

// One chunk's payload: v/id/sha256/n/i/b, UiPayload line format.
bool ParsePackChunk(const std::string& payload, std::string& id, std::string& sha,
                    size_t& total, size_t& index, std::string& b64) {
    size_t start = 0;
    std::string version, n_text, i_text;
    while (start <= payload.size()) {
        size_t nl = payload.find('\n', start);
        if (nl == std::string::npos) nl = payload.size();
        const std::string line = payload.substr(start, nl - start);
        const bool last = (nl == payload.size());
        start = nl + 1;
        if (!line.empty()) {
            const size_t eq = line.find('=');
            if (eq != std::string::npos) {
                const std::string k = line.substr(0, eq);
                const std::string v = line.substr(eq + 1);
                if (k == "v") version = v;
                else if (k == "id") id = v;
                else if (k == "sha256") sha = v;
                else if (k == "n") n_text = v;
                else if (k == "i") i_text = v;
                else if (k == "b") b64 = v;
            }
        }
        if (last) break;
    }
    if (version != "1") return false;
    if (id.empty() || !IsHex64(sha) || b64.empty() || n_text.empty() || i_text.empty())
        return false;
    if (b64.size() > kMaxPackChunkChars) return false;
    char* end = nullptr;
    const unsigned long n = strtoul(n_text.c_str(), &end, 10);
    if (!end || *end || n == 0 || n > kMaxPackChunks) return false;
    end = nullptr;
    const unsigned long i = strtoul(i_text.c_str(), &end, 10);
    if (!end || *end || i >= n) return false;
    total = n;
    index = i;
    return true;
}

// Engine thread. Unmount -> replace -> mount, the same sequence as the bundle
// slot, because a mounted VPK is held open and cannot be overwritten in place.
void MountImagesPack(const std::string& sha) {
    if (g_images_mounted) UnmountVpk(g_images_vpk);
    if (!MoveFileExA((g_images_vpk + ".new").c_str(), g_images_vpk.c_str(),
                     MOVEFILE_REPLACE_EXISTING)) {
        Log("[uiwatch] pack: could not replace the vpk (%lu)", GetLastError());
        if (g_images_mounted) MountVpk(g_images_vpk);
        return;
    }
    MountVpk(g_images_vpk);
    g_images_mounted = true;
    g_images_sha = sha;
    Log("[uiwatch] pack: mounted %s at head of GAME (no cache keys; images "
        "resolve when a tree next references them)", g_images_vpk.c_str());
}

// Engine thread (the 148 handler and the reload poll share it, so mounting
// inline is the same thread MountBundleAndReload runs on). One dw.ui.pack
// chunk; when the set completes, verify and mount. Deliberately quiet per
// chunk - a pack is hundreds of them.
void HandlePackChunk(const std::string& payload) {
    std::string id, sha, b64;
    size_t total = 0, index = 0;
    if (!ParsePackChunk(payload, id, sha, total, index, b64)) {
        Log("[uiwatch] pack: dropping a malformed chunk");
        return;
    }

    // Servers repush the same pack on every connect; when the bytes cannot
    // have changed, skip the whole reassembly and remount.
    if (g_images_mounted && _stricmp(sha.c_str(), g_images_sha.c_str()) == 0) {
        if (index == 0)
            Log("[uiwatch] pack: '%s' already mounted (sha256 match) - ignoring", id.c_str());
        return;
    }

    if (g_pack.id != id || g_pack.sha256 != sha || g_pack.total != total) {
        // A different pack replaces any half-received one.
        g_pack = PackAssembly();
        g_pack.id = id;
        g_pack.sha256 = sha;
        g_pack.total = total;
        g_pack.chunks.assign(total, std::string());
        Log("[uiwatch] pack: '%s' incoming, %zu chunk(s), expect sha256=%s",
            id.c_str(), total, sha.c_str());
    }

    if (index >= g_pack.chunks.size() || !g_pack.chunks[index].empty())
        return;   // out of range, or a duplicate
    if (g_pack.chars + b64.size() > (kMaxPackBytes / 3) * 4 + 8) {
        Log("[uiwatch] pack: '%s' exceeds the %zu MB cap - dropping", id.c_str(),
            kMaxPackBytes >> 20);
        g_pack = PackAssembly();
        return;
    }
    g_pack.chunks[index] = b64;
    g_pack.chars += b64.size();
    if (++g_pack.got < g_pack.total) return;

    std::string joined;
    joined.reserve(g_pack.chars);
    for (const auto& c : g_pack.chunks) joined += c;
    const std::string decoded = Base64Decode(joined);
    g_pack = PackAssembly();

    std::vector<uint8_t> bytes(decoded.begin(), decoded.end());
    if (bytes.empty() || bytes.size() > kMaxPackBytes) {
        Log("[uiwatch] pack: decoded to %zu bytes - refusing", bytes.size());
        return;
    }
    std::string got_sha;
    if (!Sha256Hex(bytes, got_sha)) {
        Log("[uiwatch] pack: could not hash the payload");
        return;
    }
    if (_stricmp(got_sha.c_str(), sha.c_str()) != 0) {
        Log("[uiwatch] pack: REJECTED - sha256 mismatch");
        Log("[uiwatch] pack:   expected %s", sha.c_str());
        Log("[uiwatch] pack:   actual   %s", got_sha.c_str());
        return;
    }
    Log("[uiwatch] pack: %zu bytes reassembled, sha256 verified", bytes.size());

    if (!WriteAllBytes(g_images_vpk + ".new", bytes)) {
        Log("[uiwatch] pack: could not stage %s.new (%lu)", g_images_vpk.c_str(),
            GetLastError());
        return;
    }
    MountImagesPack(got_sha);   // Sha256Hex output, already lowercase
}

// The builder we register for DWData. It borrows a stock single-string builder
// to do the actual work, then corrects the one thing that borrowing gets wrong.
//
// Each stock builder stamps the event with the symbol of the event it was
// written for, read from a static inside client.dll that the registrar was
// handed. A borrowed builder therefore produces, say, an AddStyle event, and
// dispatch would route it to AddStyle handlers instead of ours. Overwriting the
// symbol afterwards is what makes the event genuinely ours.
void* __fastcall BuildUiDataEvent(void** out, void* panel, const char* args, void* scratch) {
    if (!g_template_builder) {
        if (out) *out = nullptr;
        return out;
    }
    void* result = g_template_builder(out, panel, args, scratch);
    if (out && *out)
        *reinterpret_cast<uint16_t*>(static_cast<uint8_t*>(*out) + kUiEventSymbolOffset) =
            g_ui_data_symbol;
    return result;
}

// Engine thread. Registers our own event type by borrowing the argument builder
// of an existing single-string event, so the engine marshals the string into the
// JS callback for us and no V8 work is needed.
//
// Everything here is checked before it is trusted: the vtable slots against
// .text, and the symbol table rva by reading the interned symbol back.
bool RegisterUiDataEvent() {
    if (!g_engine || !g_panorama_base) return false;

    auto** vt = *reinterpret_cast<void***>(g_engine);
    uint8_t* text = nullptr;
    size_t text_size = 0;
    GetSection(reinterpret_cast<HMODULE>(g_panorama_base), ".text", &text, &text_size);
    for (size_t i : {kRegisterEventTypeIndex, kIsValidEventTypeIndex,
                     kCreateEventFromStringIndex, kDispatchEventIndex}) {
        if (text && (vt[i] < text || vt[i] >= text + text_size)) {
            Log("[uiwatch] uidata: CUIEngine vtable[%zu] = %p is outside .text - resig "
                "needed, server data stays off", i, vt[i]);
            return false;
        }
    }

    auto* engine_bytes = static_cast<uint8_t*>(g_engine);
    const uint32_t count =
        *reinterpret_cast<const uint32_t*>(engine_bytes + kEventRegistryCountRva) & 0x7FFFFFFF;
    auto* nodes = *reinterpret_cast<uint8_t**>(engine_bytes + kEventRegistryDataRva);
    if (!nodes || count == 0 || count > 0x10000) {
        Log("[uiwatch] uidata: event registry looks wrong (count %u, nodes %p) - "
            "server data stays off", count, nodes);
        return false;
    }

    HMODULE tier0 = GetModuleHandleW(L"tier0.dll");
    if (!tier0) { Log("[uiwatch] uidata: tier0.dll not loaded"); return false; }
    auto add_string = reinterpret_cast<AddSymbolFn>(GetProcAddress(tier0, kAddStringExport));
    auto symbol_string =
        reinterpret_cast<SymbolStringFn>(GetProcAddress(tier0, kSymbolStringExport));
    auto find_symbol = reinterpret_cast<FindSymbolFn>(GetProcAddress(tier0, kFindSymbolExport));
    if (!add_string || !symbol_string || !find_symbol) {
        Log("[uiwatch] uidata: tier0 does not export the symbol table helpers");
        return false;
    }

    void* table = g_panorama_base + kPanoramaSymbolTableRva;

    // Pick the template by name. Shape alone is not enough: argc==1 says nothing
    // about the argument's type, so "the first single-argument event" lands on
    // MovePanelLeft, whose one argument is an integer, and its builder rejects
    // anything that is not a number.
    const uint8_t* factory = nullptr;
    const char* template_name = nullptr;
    for (const char* candidate : kTemplateEventNames) {
        uint16_t candidate_symbol = 0xFFFF;
        find_symbol(table, &candidate_symbol, candidate);
        if (candidate_symbol == 0xFFFF) continue;

        for (uint32_t i = 0; i < count; ++i) {
            const uint8_t* node = nodes + static_cast<size_t>(i) * kEventNodeStride;
            if (*reinterpret_cast<const uint16_t*>(node + kEventNodeKeyOffset) != candidate_symbol)
                continue;
            const uint8_t* value = node + kEventNodeValueOffset;
            // One argument, and a builder that can parse it out of a string -
            // that builder is the one both the JS binding and
            // CreateEventFromString call.
            if (*reinterpret_cast<const uint32_t*>(value + kEventArgCountOffset) == 1 &&
                *reinterpret_cast<void* const*>(value + kEventFromStringBuilderOffset)) {
                factory = value;
                template_name = candidate;
            }
            break;
        }
        if (factory) break;
    }
    if (!factory) {
        Log("[uiwatch] uidata: none of the known single-string event types is "
            "registered (checked %zu names against %u types) - server data stays off",
            sizeof(kTemplateEventNames) / sizeof(kTemplateEventNames[0]), count);
        return false;
    }
    uint16_t symbol = 0xFFFF;
    bool created = false;
    add_string(table, &symbol, kUiDataEvent, &created);
    if (symbol == 0xFFFF) {
        Log("[uiwatch] uidata: could not intern '%s'", kUiDataEvent);
        return false;
    }
    const char* readback = symbol_string(table, &symbol);
    if (!IsReadableString(readback) || strcmp(readback, kUiDataEvent) != 0) {
        Log("[uiwatch] uidata: symbol %u did not read back as '%s' - the symbol table "
            "rva is wrong, server data stays off", symbol, kUiDataEvent);
        return false;
    }

    g_ui_data_symbol = symbol;
    g_template_builder = *reinterpret_cast<BuildFromArgsFn const*>(
        factory + kEventFromStringBuilderOffset);

    // Register a copy of the template's factory with our own builder swapped in,
    // so the event comes out carrying our symbol rather than the template's.
    uint8_t clone[kEventFactorySize];
    memcpy(clone, factory, sizeof(clone));
    *reinterpret_cast<void**>(clone + kEventFromStringBuilderOffset) =
        reinterpret_cast<void*>(&BuildUiDataEvent);

    auto is_valid = reinterpret_cast<IsValidEventTypeFn>(vt[kIsValidEventTypeIndex]);
    reinterpret_cast<RegisterEventTypeFn>(vt[kRegisterEventTypeIndex])(g_engine, symbol, clone);
    if (!is_valid(g_engine, symbol)) {
        Log("[uiwatch] uidata: registering '%s' did not take", kUiDataEvent);
        return false;
    }

    const char* arg_names =
        *reinterpret_cast<const char* const*>(factory + kEventArgNamesOffset);
    Log("[uiwatch] uidata: registered '%s' as symbol %u, borrowing %s's argument "
        "builder %p (arg '%s')", kUiDataEvent, symbol, template_name, g_template_builder,
        IsReadableString(arg_names) ? arg_names : "?");
    return true;
}

// ---------------------------------------------------------------------------
// SPIKE: native V8 execution.
//
// Panorama blocks eval / new Function from JS (proven live), but that only gates
// JS-*initiated* codegen; the embedder-level v8::Script path is how the game runs
// its own scripts and is not gated. Deadlock ships the full V8 11.9 as v8.dll with
// the C++ API exported, so we resolve the entry points and try to compile+run a
// probe on the engine thread. Reports to uiwatch.log. Remove once answered.
// ---------------------------------------------------------------------------
// V8 11.9's Local<T>/MaybeLocal<T> returns are all sret (verified by
// disassembling v8.dll): a hidden return-buffer pointer, not RAX.
//   static  fn(sret in RCX, arg1 RDX, arg2 R8, arg3 R9, ...)
//   member  fn(this RCX, sret RDX, arg1 R8, arg2 R9)
// GetCurrent alone returns a raw Isolate* in RAX.
struct V8Api {
    bool resolved = false;
    bool ok = false;
    void* (*GetCurrent)() = nullptr;                                        // -> Isolate* (RAX)
    void (*GetCurrentContext)(void* iso, void* sret) = nullptr;            // member sret
    void (*GetEnteredOrMicrotaskContext)(void* iso, void* sret) = nullptr; // member sret
    void (*NewFromUtf8)(void* sret, void* iso, const char*, int, int) = nullptr; // static sret
    void (*Compile)(void* sret, void* ctx, void* src, void* origin) = nullptr;   // static sret
    void (*Run)(void* self, void* sret, void* ctx) = nullptr;             // member sret
    void (*HandleScopeCtor)(void*, void*) = nullptr;
    void (*HandleScopeDtor)(void*) = nullptr;
    void (*TryCatchCtor)(void*, void*) = nullptr;
    void (*TryCatchDtor)(void*) = nullptr;
    void (*ContextEnter)(void*) = nullptr;
    void (*ContextExit)(void*) = nullptr;
};

V8Api g_v8;

void ResolveV8() {
    if (g_v8.resolved) return;
    g_v8.resolved = true;
    HMODULE v8 = GetModuleHandleW(L"v8.dll");
    if (!v8) { Log("[uiwatch] v8probe: v8.dll not loaded"); return; }
    auto R = [&](const char* name) { return GetProcAddress(v8, name); };
    g_v8.GetCurrent = reinterpret_cast<void*(*)()>(R("?GetCurrent@Isolate@v8@@SAPEAV12@XZ"));
    g_v8.GetCurrentContext = reinterpret_cast<void(*)(void*, void*)>(
        R("?GetCurrentContext@Isolate@v8@@QEAA?AV?$Local@VContext@v8@@@2@XZ"));
    g_v8.GetEnteredOrMicrotaskContext = reinterpret_cast<void(*)(void*, void*)>(
        R("?GetEnteredOrMicrotaskContext@Isolate@v8@@QEAA?AV?$Local@VContext@v8@@@2@XZ"));
    g_v8.NewFromUtf8 = reinterpret_cast<void(*)(void*, void*, const char*, int, int)>(
        R("?NewFromUtf8@String@v8@@SA?AV?$MaybeLocal@VString@v8@@@2@PEAVIsolate@2@PEBDW4NewStringType@2@H@Z"));
    g_v8.Compile = reinterpret_cast<void(*)(void*, void*, void*, void*)>(
        R("?Compile@Script@v8@@SA?AV?$MaybeLocal@VScript@v8@@@2@V?$Local@VContext@v8@@@2@"
          "V?$Local@VString@v8@@@2@PEAVScriptOrigin@2@@Z"));
    g_v8.Run = reinterpret_cast<void(*)(void*, void*, void*)>(
        R("?Run@Script@v8@@QEAA?AV?$MaybeLocal@VValue@v8@@@2@V?$Local@VContext@v8@@@2@@Z"));
    g_v8.HandleScopeCtor = reinterpret_cast<void(*)(void*, void*)>(R("??0HandleScope@v8@@QEAA@PEAVIsolate@1@@Z"));
    g_v8.HandleScopeDtor = reinterpret_cast<void(*)(void*)>(R("??1HandleScope@v8@@QEAA@XZ"));
    g_v8.TryCatchCtor = reinterpret_cast<void(*)(void*, void*)>(R("??0TryCatch@v8@@QEAA@PEAVIsolate@1@@Z"));
    g_v8.TryCatchDtor = reinterpret_cast<void(*)(void*)>(R("??1TryCatch@v8@@QEAA@XZ"));
    g_v8.ContextEnter = reinterpret_cast<void(*)(void*)>(R("?Enter@Context@v8@@QEAAXXZ"));
    g_v8.ContextExit = reinterpret_cast<void(*)(void*)>(R("?Exit@Context@v8@@QEAAXXZ"));
    g_v8.ok = g_v8.GetCurrent && g_v8.GetCurrentContext && g_v8.NewFromUtf8 &&
              g_v8.Compile && g_v8.Run && g_v8.HandleScopeCtor && g_v8.HandleScopeDtor;
    Log("[uiwatch] v8probe: resolved=%d (GetCurrent=%p Compile=%p Run=%p HandleScope=%p)",
        g_v8.ok ? 1 : 0, g_v8.GetCurrent, g_v8.Compile, g_v8.Run, g_v8.HandleScopeCtor);
}

// Engine thread, valid only while a panel's V8 context is live (i.e. from
// inside the GetJSArgs hook during a dispatch). Compiles and runs `source` in
// that context. Every V8 handle is null-checked, so a missing isolate/context
// ends in a log line, not a crash.
void RunJs(const char* source, const char* label) {
    ResolveV8();
    if (!g_v8.ok) { Log("[uiwatch] runjs: V8 entry points missing"); return; }

    void* iso = g_v8.GetCurrent();
    if (!iso) { Log("[uiwatch] runjs(%s): no current isolate", label); return; }

    alignas(16) uint8_t hs[64];
    g_v8.HandleScopeCtor(hs, iso);

    // Local<Context>/MaybeLocal<T> come back through a caller-provided 8-byte
    // sret buffer; empty == nullptr.
    void* ctx = nullptr;
    g_v8.GetCurrentContext(iso, &ctx);
    if (!ctx && g_v8.GetEnteredOrMicrotaskContext)
        g_v8.GetEnteredOrMicrotaskContext(iso, &ctx);
    if (!ctx) {
        Log("[uiwatch] runjs(%s): no live panel context", label);
        g_v8.HandleScopeDtor(hs);
        return;
    }

    bool entered = false;
    if (g_v8.ContextEnter) { g_v8.ContextEnter(ctx); entered = true; }

    alignas(16) uint8_t tc[128];
    bool have_tc = g_v8.TryCatchCtor && g_v8.TryCatchDtor;
    if (have_tc) g_v8.TryCatchCtor(tc, iso);

    void* src = nullptr;
    g_v8.NewFromUtf8(&src, iso, source, 0 /*kNormal*/, -1);
    void* script = nullptr;
    if (src) g_v8.Compile(&script, ctx, src, nullptr);
    void* result = nullptr;
    if (script) g_v8.Run(script, &result, ctx);

    Log("[uiwatch] runjs(%s): %s (compile=%s run=%s)", label,
        result ? "ok" : "FAILED", script ? "y" : "n", result ? "y" : "n");

    if (have_tc) g_v8.TryCatchDtor(tc);
    if (entered) g_v8.ContextExit(ctx);
    g_v8.HandleScopeDtor(hs);
}

// The panel's V8 context is only entered *during* dispatch, while the engine
// marshals the event's arguments into JS. GetJSArgs (event vtable[3]) is called
// at exactly that moment, so hooking it for the length of our own dispatch
// gives us a live panel context. The hook drains any pending server-pushed JS
// there, then forwards to the original unchanged.
using GetJSArgsFn = void*(__fastcall*)(void*, void*, void*);
GetJSArgsFn g_orig_getjsargs = nullptr;

// Engine-thread only (the netmessage handler and the dispatch both run there),
// so no locking is needed.
std::vector<std::string> g_pending_js;

// The hooked slot lives in the *shared class vtable*, so it is patched for every
// event of that type, not just ours. Two guards keep that safe:
//   - the hook is installed only when there is JS to run (not on every emit), so
//     the window is rare and short instead of twice a second;
//   - this flag stops a nested dispatch from re-hooking, which would otherwise
//     record our own hook as "the original" and recurse until the stack died.
bool g_getjsargs_hooked = false;
bool g_in_runjs = false;

void* __fastcall GetJSArgsHook(void* self, void* a, void* b) {
    // Re-entrancy: pushed JS can itself dispatch events (SendToServer does),
    // which lands back here. Never run JS from inside JS.
    if (!g_in_runjs && !g_pending_js.empty()) {
        std::vector<std::string> batch;
        batch.swap(g_pending_js);
        g_in_runjs = true;
        for (const std::string& js : batch)
            RunJs(js.c_str(), "server");   // context is live right here
        g_in_runjs = false;
    }
    return g_orig_getjsargs ? g_orig_getjsargs(self, a, b) : nullptr;
}

// Engine thread. Dispatches a DWData event carrying `encoded`, hooking the
// event's GetJSArgs across the dispatch so any pending server JS runs in the
// live panel context. DispatchEvent takes ownership of the event on every
// path, so there is nothing to release here.
void DispatchUiData(const std::string& encoded) {
    auto** vt = *reinterpret_cast<void***>(g_engine);

    // Base64 has no quotes, commas or parentheses, so it survives the argument
    // parser without escaping - which is the reason for encoding it at all.
    const std::string expression = std::string(kUiDataEvent) + "('" + encoded + "')";

    void* event = nullptr;
    uint64_t scratch = 0;   // builder out-parameter; the call is refused if null
    reinterpret_cast<CreateEventFromStringFn>(vt[kCreateEventFromStringIndex])(
        g_engine, &event, nullptr, expression.c_str(), &scratch);
    if (!event) {
        Log("[uiwatch] uidata: the argument builder refused '%.48s...'", expression.c_str());
        return;
    }

    // Hook GetJSArgs (vtable[3]) across the dispatch ONLY when there is JS
    // waiting to run. A plain data emit needs no hook, so the shared class
    // vtable is left alone for the vast majority of dispatches. g_getjsargs_hooked
    // stops a nested dispatch from hooking a slot that already holds our hook.
    void** ev_vt = *reinterpret_cast<void***>(event);
    void* saved_slot = nullptr;
    bool hooked = false;
    DWORD old = 0;
    if (!g_pending_js.empty() && !g_getjsargs_hooked &&
        VirtualProtect(&ev_vt[3], sizeof(void*), PAGE_EXECUTE_READWRITE, &old)) {
        saved_slot = ev_vt[3];
        g_orig_getjsargs = reinterpret_cast<GetJSArgsFn>(saved_slot);
        ev_vt[3] = reinterpret_cast<void*>(&GetJSArgsHook);
        VirtualProtect(&ev_vt[3], sizeof(void*), old, &old);
        hooked = true;
        g_getjsargs_hooked = true;
    }

    reinterpret_cast<DispatchEventFn>(vt[kDispatchEventIndex])(g_engine, &event);

    if (hooked) {
        if (VirtualProtect(&ev_vt[3], sizeof(void*), PAGE_EXECUTE_READWRITE, &old)) {
            ev_vt[3] = saved_slot;
            VirtualProtect(&ev_vt[3], sizeof(void*), old, &old);
        }
        g_getjsargs_hooked = false;
    }
}

// Queues server-pushed JS and dispatches a DWData event to obtain a live panel
// context, in which the GetJSArgs hook runs it. The payload is a marker the
// panel side ignores; the JS runs natively, not through the panel handler.
void RunServerJs(const std::string& js) {
    if (js.empty()) return;
    g_pending_js.push_back(js);
    DispatchUiData(Base64Encode("v=1\nevent=dw.ui.script\n"));
}

// Free __fastcall handler; the delegate is { &g_handler_anchor, &this }.
void __fastcall OnCustomGameEvent(void* self, void* net_message) {
    InterlockedExchange64(&g_last_netmsg_ms, static_cast<LONG64>(GetTickCount64()));
    if (!net_message) return;

    auto** mvt = *reinterpret_cast<void***>(net_message);
    void* serializer = reinterpret_cast<GetSerializerPBFn>(
        mvt[kNetMessageGetSerializerIndex])(net_message);
    auto** nvt = *reinterpret_cast<void***>(g_net_messages);
    auto* info = static_cast<uint8_t*>(reinterpret_cast<GetNetMessageInfoFn>(
        nvt[kGetNetMessageInfoIndex])(g_net_messages, serializer));
    if (!info) return;

    void* binding = *reinterpret_cast<void**>(info + kInfoBindingOffset);
    const char* group = *reinterpret_cast<const char* const*>(info + kInfoGroupOffset);
    const int id = *reinterpret_cast<const int*>(info + kInfoMessageIdOffset);

    // Liveness only. Returns before counting or logging - this arrives about
    // sixty times a second and would drown everything else.
    if (id == g_heartbeat_id) {
        if (InterlockedExchange(&g_heartbeat_seen, 1) == 0)
            Log("[uiwatch] heartbeat: connection is live (msg %d); idle-eject armed", id);
        return;
    }

    const LONG n = InterlockedIncrement(&g_netmsg_count);
    const char* name = "unknown";
    if (binding)
        name = (*reinterpret_cast<GetBindingNameFn**>(binding))[kBindingGetNameIndex](binding);

    Log("[uiwatch] netmsg #%ld: %s (id %d, group '%s') self=%p msg=%p tid=%lu",
        n, name, id, group ? group : "", self, net_message, GetCurrentThreadId());

    bool wanted = false;
    for (size_t i = 0; i < g_netmsg_id_count; ++i)
        if (g_netmsgs[i].id == id) { wanted = g_netmsgs[i].dump; break; }
    if (!wanted || !binding) return;
    if (!ValidateBindingLayout(binding, group)) return;

    // CUtlString is just a char*. Oversized and zeroed anyway in case it isn't.
    // Whatever it allocates leaks, no way to call Purge from out here, hence
    // dumpids being opt-in rather than everything we subscribe to.
    unsigned char out[32] = {};
    const char* text = reinterpret_cast<BindingToStringFn>(
        (*reinterpret_cast<void***>(binding))[kBindingToStringIndex])(binding, net_message, out);
    if (!IsReadableString(text)) {
        Log("[uiwatch] netmsg #%ld: ToString returned nothing readable", n);
        return;
    }
    // A pack is hundreds of chunks; dumping each would swamp the log.
    if (!strstr(text, "event_name: \"dw.ui.pack\""))
        Log("[uiwatch] netmsg #%ld payload: %.1000s", n, text);

    // --- manifest and data handling ---
    std::string event_name, payload;
    if (!ExtractQuoted(text, "event_name", event_name)) return;
    if (event_name != "dw.ui.bundle" && event_name != "dw.ui.revoke" &&
        event_name != "dw.ui.emit" && event_name != "dw.ui.script" &&
        event_name != "dw.ui.pack" &&
        event_name != "dw.input.bind" && event_name != "dw.input.unbind")
        return;
    if (!ExtractQuoted(text, "data", payload)) {
        Log("[uiwatch] %s carried no data field", event_name.c_str());
        return;
    }

    // This connection belongs to a Deadworks server. That, not the presence of
    // a connection, is what keeps the module loaded - see the eject logic.
    if (InterlockedExchange(&g_deadworks_seen, 1) == 0)
        Log("[uiwatch] server: Deadworks traffic on this connection - staying loaded");

    // Live data for a panel. This handler already runs on the engine thread -
    // the same one the reload poll uses - so the dispatch happens inline; if
    // that ever stopped being true DispatchEvent would drop the event rather
    // than misbehave.
    if (event_name == "dw.ui.emit") {
        if (InterlockedCompareExchange(&g_ui_event_stage, 0, 0) != 1) {
            if (InterlockedExchange(&g_emit_drop_warned, 1) == 0)
                Log("[uiwatch] uidata: dropping emits, '%s' is not registered", kUiDataEvent);
            return;
        }
        const std::string encoded = Base64Encode(payload);
        if (encoded.size() > kMaxEmitBytes) {
            Log("[uiwatch] uidata: dropping a %zu byte emit, the cap is %zu",
                encoded.size(), kMaxEmitBytes);
            return;
        }
        DispatchUiData(encoded);
        return;
    }

    // Raw server-pushed JavaScript, run natively in the panel's V8 context.
    // The JS arrives base64-encoded so it survives the ToString scrape intact
    // (arbitrary quotes/newlines otherwise would not).
    if (event_name == "dw.ui.script") {
        if (InterlockedCompareExchange(&g_ui_event_stage, 0, 0) != 1) {
            if (InterlockedExchange(&g_emit_drop_warned, 1) == 0)
                Log("[uiwatch] uidata: dropping script, '%s' is not registered", kUiDataEvent);
            return;
        }
        std::string js = Base64Decode(payload);
        if (js.empty()) {
            Log("[uiwatch] uidata: dw.ui.script carried no decodable JS");
            return;
        }
        if (js.size() > kMaxEmitBytes) {
            Log("[uiwatch] uidata: dropping a %zu byte script, the cap is %zu",
                js.size(), kMaxEmitBytes);
            return;
        }
        Log("[uiwatch] uidata: running %zu bytes of server JS", js.size());
        RunServerJs(js);
        return;
    }

    // An images-pack chunk (see HandlePackChunk). Mounting happens inline on
    // the final chunk - this handler already runs on the engine thread.
    if (event_name == "dw.ui.pack") {
        HandlePackChunk(payload);
        return;
    }

    // Key registration from the server. Handled natively; never dispatched to
    // a panel.
    if (event_name == "dw.input.bind" || event_name == "dw.input.unbind") {
        HandleInputBind(payload, event_name == "dw.input.bind");
        return;
    }

    if (event_name == "dw.ui.revoke") {
        std::string id;
        if (!ParseRevoke(payload, id)) return;
        if (!g_bundle_mounted) {
            Log("[uiwatch] bundle: revoke '%s' ignored, nothing is mounted", id.c_str());
            return;
        }
        if (id != g_bundle_current.id) {
            Log("[uiwatch] bundle: revoke '%s' ignored, '%s' is what is mounted",
                id.c_str(), g_bundle_current.id.c_str());
            return;
        }
        Log("[uiwatch] bundle: revoking '%s'", id.c_str());
        InterlockedExchange(&g_bundle_revoke, 1);
        return;
    }

    Manifest m;
    if (!ParseManifest(payload, m)) return;

    if (InterlockedCompareExchange(&g_bundle_stage, 0, 0) !=
        static_cast<LONG>(BundleStage::Idle)) {
        Log("[uiwatch] bundle: '%s' ignored, another bundle is still in flight", m.id.c_str());
        return;
    }

    Log("[uiwatch] bundle: '%s' -> %s", m.id.c_str(), m.url.c_str());
    Log("[uiwatch] bundle:   expect sha256=%s, %zu cache key(s)", m.sha256.c_str(), m.keys.size());
    g_bundle_pending = m;
    InterlockedExchange(&g_bundle_stage, static_cast<LONG>(BundleStage::Fetching));
    SetEvent(g_bundle_event);
}

// Fetch and verify only, never touches the filesystem interface.
DWORD WINAPI BundleWorker(LPVOID) {
    for (;;) {
        WaitForSingleObject(g_bundle_event, INFINITE);
        if (InterlockedCompareExchange(&g_shutdown, 0, 0) == 1) return 0;
        const Manifest m = g_bundle_pending;

        std::vector<uint8_t> bytes;
        std::string err;
        const ULONGLONG t0 = GetTickCount64();
        if (!HttpsGet(m.url, bytes, err)) {
            Log("[uiwatch] bundle: download failed - %s", err.c_str());
            InterlockedExchange(&g_bundle_stage, static_cast<LONG>(BundleStage::Idle));
            continue;
        }
        Log("[uiwatch] bundle: downloaded %zu bytes in %llu ms", bytes.size(),
            GetTickCount64() - t0);

        std::string got;
        if (!Sha256Hex(bytes, got)) {
            Log("[uiwatch] bundle: could not hash the download");
            InterlockedExchange(&g_bundle_stage, static_cast<LONG>(BundleStage::Idle));
            continue;
        }
        if (_stricmp(got.c_str(), m.sha256.c_str()) != 0) {
            Log("[uiwatch] bundle: REJECTED - sha256 mismatch");
            Log("[uiwatch] bundle:   expected %s", m.sha256.c_str());
            Log("[uiwatch] bundle:   actual   %s", got.c_str());
            InterlockedExchange(&g_bundle_stage, static_cast<LONG>(BundleStage::Idle));
            continue;
        }
        Log("[uiwatch] bundle: sha256 verified");

        if (!WriteAllBytes(g_bundle_vpk + ".new", bytes)) {
            Log("[uiwatch] bundle: could not stage %s.new (%lu)", g_bundle_vpk.c_str(),
                GetLastError());
            InterlockedExchange(&g_bundle_stage, static_cast<LONG>(BundleStage::Idle));
            continue;
        }

        g_bundle_ready = m;
        InterlockedExchange(&g_bundle_stage, static_cast<LONG>(BundleStage::ReadyMount));
    }
}

// Engine thread. Unmount -> replace -> remount, same as the hot-reload path,
// because a mounted VPK is held open and can't be overwritten in place.
void MountBundleAndReload() {
    const Manifest m = g_bundle_ready;

    if (g_bundle_mounted) UnmountVpk(g_bundle_vpk);
    if (!MoveFileExA((g_bundle_vpk + ".new").c_str(), g_bundle_vpk.c_str(),
                     MOVEFILE_REPLACE_EXISTING)) {
        Log("[uiwatch] bundle: could not replace the vpk (%lu)", GetLastError());
        if (g_bundle_mounted) MountVpk(g_bundle_vpk);
        return;
    }
    MountVpk(g_bundle_vpk);
    g_bundle_mounted = true;
    g_bundle_current = m;
    Log("[uiwatch] bundle: mounted %s at head of GAME", g_bundle_vpk.c_str());

    if (m.keys.empty()) {
        Log("[uiwatch] bundle: no cache keys, content applies on next panel load");
        return;
    }
    if (!g_layout_manager) {
        // Captured only when the reload driver invalidates something, which needs
        // one edit under the watched source dir. Until then the mount is live but
        // already-loaded panels keep their cached copy.
        Log("[uiwatch] bundle: mounted, but no layout manager captured yet - save any "
            "watched .xml/.css/.js once to prime it, then republish");
        return;
    }
    for (const auto& k : m.keys) {
        g_original_invalidate(g_layout_manager, k.c_str());
        RememberOverride(k);
        Log("[uiwatch] bundle:   reloaded %s", k.c_str());
    }
}

// Engine thread. Invalidates the keys we recorded at mount time, not anything
// the revoke message names - a message with no bundle attached has no business
// picking arbitrary panels to reload.
void RevokeBundleAndReload() {
    UnmountVpk(g_bundle_vpk);
    g_bundle_mounted = false;
    Log("[uiwatch] bundle: unmounted %s", g_bundle_vpk.c_str());

    if (g_layout_manager) {
        for (const auto& k : g_bundle_current.keys) {
            g_original_invalidate(g_layout_manager, k.c_str());
            Log("[uiwatch] bundle:   reverted %s", k.c_str());
        }
    } else if (!g_bundle_current.keys.empty()) {
        Log("[uiwatch] bundle: unmounted, but no layout manager - panels keep the "
            "replaced content until they next load");
    }
    g_bundle_current = Manifest();
}

// Resolves the interfaces and the message pointer. Lookups only, no
// registration - safe to run on the injected thread.
bool ResolveNetMsg() {
    HMODULE engine2 = GetModuleHandleW(L"engine2.dll");
    HMODULE netsys = GetModuleHandleW(L"networksystem.dll");
    if (!engine2 || !netsys) {
        Log("[uiwatch] netmsg: engine2/networksystem not loaded"); return false;
    }
    auto ci_engine = reinterpret_cast<CreateInterfaceFn>(GetProcAddress(engine2, "CreateInterface"));
    auto ci_net = reinterpret_cast<CreateInterfaceFn>(GetProcAddress(netsys, "CreateInterface"));
    if (!ci_engine || !ci_net) {
        Log("[uiwatch] netmsg: missing CreateInterface export"); return false;
    }

    g_game_event_system = ci_engine("GameEventSystemClientV001", nullptr);
    if (!g_game_event_system) {
        Log("[uiwatch] netmsg: GameEventSystemClientV001 returned null"); return false;
    }
    uint8_t* text = nullptr; size_t text_size = 0;
    GetSection(engine2, ".text", &text, &text_size);
    auto** evt = *reinterpret_cast<void***>(g_game_event_system);
    void* reg = evt[kRegisterGameEventHandlerIndex];
    if (text && (reg < text || reg >= text + text_size)) {
        Log("[uiwatch] netmsg: event vtable[%zu] = %p outside engine2 .text - ABORT",
            kRegisterGameEventHandlerIndex, reg);
        return false;
    }
    Log("[uiwatch] netmsg: event system %p, RegisterGameEventHandlerAbstract %p",
        g_game_event_system, reg);

    g_net_messages = ci_net("NetworkMessagesVersion001", nullptr);
    if (!g_net_messages) {
        Log("[uiwatch] netmsg: NetworkMessagesVersion001 returned null"); return false;
    }
    uint8_t* data = nullptr; size_t data_size = 0;
    if (GetSection(engine2, ".data", &data, &data_size)) {
        auto* slot = reinterpret_cast<uint8_t*>(engine2) + kEngineNetMessagesRva;
        if (slot >= data && slot + sizeof(void*) <= data + data_size) {
            void* cached = *reinterpret_cast<void**>(slot);
            if (cached && cached != g_net_messages)
                Log("[uiwatch] netmsg: WARNING - engine2 uses %p but CreateInterface gave %p; "
                    "message lookups may not match", cached, g_net_messages);
        }
    }

    uint8_t* ntext = nullptr; size_t ntext_size = 0;
    GetSection(netsys, ".text", &ntext, &ntext_size);
    auto** nvt = *reinterpret_cast<void***>(g_net_messages);
    for (size_t i : {kGetNetMessageInfoIndex, kFindNetworkMessageByIdIndex}) {
        void* p = nvt[i];
        if (ntext && (p < ntext || p >= ntext + ntext_size)) {
            Log("[uiwatch] netmsg: netmessages vtable[%zu] = %p outside .text - ABORT", i, p);
            return false;
        }
    }

    // Resolve each id, then feed the result back through GetNetMessageInfo
    // (whose index we're sure of) to confirm it really is the message we asked
    // for. If slot 31 were wrong we'd catch the garbage here instead of
    // registering a handler against it.
    size_t resolved = 0;
    for (size_t i = 0; i < g_netmsg_id_count; ++i) {
        SubscribedMsg& m = g_netmsgs[i];
        void* found = reinterpret_cast<FindNetworkMessageByIdFn>(
            nvt[kFindNetworkMessageByIdIndex])(g_net_messages, m.id);
        if (!found) {
            Log("[uiwatch] netmsg: FindNetworkMessageById(%d) returned null - skipping", m.id);
            continue;
        }
        auto* info = static_cast<uint8_t*>(reinterpret_cast<GetNetMessageInfoFn>(
            nvt[kGetNetMessageInfoIndex])(g_net_messages, found));
        if (!info) {
            Log("[uiwatch] netmsg: id %d gave no NetMessageInfo_t - skipping", m.id);
            continue;
        }
        const int id = *reinterpret_cast<const int*>(info + kInfoMessageIdOffset);
        void* binding = *reinterpret_cast<void**>(info + kInfoBindingOffset);
        const char* name = binding
            ? (*reinterpret_cast<GetBindingNameFn**>(binding))[kBindingGetNameIndex](binding)
            : nullptr;
        Log("[uiwatch] netmsg: id %d resolved to %p -> '%s' (id %d)",
            m.id, found, name ? name : "?", id);
        if (id != m.id) {
            Log("[uiwatch] netmsg: id mismatch - skipping"); continue;
        }
        strcpy_s(m.name, sizeof(m.name), name ? name : "?");
        m.internal = found;
        ++resolved;
    }
    return resolved > 0;
}

// Poll (engine) thread only - the event system is driven from there.
void RegisterCustomGameEventHandler() {
    g_ui_delegate.pthis = &g_handler_anchor;
    g_ui_delegate.fn = reinterpret_cast<void*>(&OnCustomGameEvent);
    Log("[uiwatch] netmsg: registering on tid %lu, delegate { %p, %p }",
        GetCurrentThreadId(), g_ui_delegate.pthis, g_ui_delegate.fn);
    auto** vt = *reinterpret_cast<void***>(g_game_event_system);
    auto reg = reinterpret_cast<RegisterGameEventHandlerFn>(vt[kRegisterGameEventHandlerIndex]);
    for (size_t i = 0; i < g_netmsg_id_count; ++i) {
        const SubscribedMsg& m = g_netmsgs[i];
        if (!m.internal) continue;
        reg(g_game_event_system, nullptr, &g_ui_delegate, m.internal, 0);
        Log("[uiwatch] netmsg: subscribed to %s%s", m.name, m.heartbeat ? "  [heartbeat]" : "");
    }
}

// ---------------------------------------------------------------------------
// Change handling
// ---------------------------------------------------------------------------

// The cache is keyed by source path with backslashes, so a watched source file
// maps straight to its key with no extension rewriting:
//   "...\content\<addon>\panorama\layout\hud_health.xml"
//     -> "panorama\layout\hud_health.xml"
bool CacheKeyFor(const char* absolute, std::string& out) {
    const char* tail = strstr(absolute, "\\panorama\\");
    if (!tail) return false;
    out.assign(tail + 1);  // keep backslashes
    const size_t dot = out.rfind('.');
    if (dot == std::string::npos) return true;
    // Tolerate being pointed at compiled files too.
    const std::string ext = out.substr(dot + 1);
    if (ext.size() > 3 && ext.front() == 'v' && ext.compare(ext.size() - 2, 2, "_c") == 0)
        out = out.substr(0, dot) + "." + ext.substr(1, ext.size() - 3);
    return true;
}

bool IsCompilableSource(const std::string& path) {
    const size_t dot = path.rfind('.');
    if (dot == std::string::npos) return false;
    const std::string ext = path.substr(dot + 1);
    return ext == "xml" || ext == "css" || ext == "js";
}

// Runs the CSDK resource compiler over the given sources. Blocking - worker
// thread only.
bool CompileSources(const std::vector<std::string>& sources) {
    if (g_compiler.empty() || sources.empty()) return true;

    std::string cmd = "\"" + g_compiler + "\" -nop4 -f -game \"" + g_gamedir + "\"";
    for (const auto& s : sources) cmd += " -i \"" + s + "\"";

    STARTUPINFOA si = {};
    si.cb = sizeof(si);
    si.dwFlags = STARTF_USESHOWWINDOW;
    si.wShowWindow = SW_HIDE;
    PROCESS_INFORMATION pi = {};
    std::vector<char> mutable_cmd(cmd.begin(), cmd.end());
    mutable_cmd.push_back('\0');

    if (!CreateProcessA(nullptr, mutable_cmd.data(), nullptr, nullptr, FALSE,
                        CREATE_NO_WINDOW, nullptr, nullptr, &si, &pi)) {
        Log("[uiwatch] could not launch the compiler (%lu)", GetLastError());
        return false;
    }
    WaitForSingleObject(pi.hProcess, 120000);
    DWORD code = 1;
    GetExitCodeProcess(pi.hProcess, &code);
    CloseHandle(pi.hThread);
    CloseHandle(pi.hProcess);
    if (code != 0) {
        Log("[uiwatch] compile FAILED (exit %lu) - fix the source and save again", code);
        return false;
    }
    return true;
}

// Worker: compile, then pack the compiler's output into a staged VPK.
DWORD WINAPI BuildWorker(LPVOID) {
    for (;;) {
        WaitForSingleObject(g_work_event, INFINITE);
        if (InterlockedCompareExchange(&g_shutdown, 0, 0) == 1) return 0;
        Log("[uiwatch] --- compiling %zu file(s) ---", g_building_sources.size());

        if (!CompileSources(g_building_sources)) {
            InterlockedExchange(&g_stage, static_cast<LONG>(Stage::Idle));
            continue;
        }
        const int packed = BuildVpk(g_output_dir, g_live_vpk + ".new");
        if (packed <= 0) {
            Log("[uiwatch] pack produced nothing (%d) - is [output] correct?", packed);
            InterlockedExchange(&g_stage, static_cast<LONG>(Stage::Idle));
            continue;
        }
        Log("[uiwatch] compiled and packed %d file(s)", packed);
        InterlockedExchange(&g_stage, static_cast<LONG>(Stage::ReadySwap));
    }
}

// Engine thread: swap the VPK and reload. Fast - no process spawn, no packing.
void SwapAndReload(void* layout_manager) {
    UnmountLiveVpk();
    const std::string staged = g_live_vpk + ".new";
    if (!MoveFileExA(staged.c_str(), g_live_vpk.c_str(), MOVEFILE_REPLACE_EXISTING)) {
        Log("[uiwatch] could not replace the VPK (%lu) - remounting previous", GetLastError());
        MountLiveVpk();
        return;
    }
    MountLiveVpk();
    for (const auto& key : g_building_keys) {
        g_original_invalidate(layout_manager, key.c_str());
        RememberOverride(key);
        Log("[uiwatch]   reloaded %s", key.c_str());
    }
    g_building_keys.clear();
    g_building_sources.clear();
}

void __fastcall InvalidateProbe(void* layout_manager, const char* path) {
    if (!g_original_invalidate || !path) return;
    g_layout_manager = layout_manager;
    g_original_invalidate(layout_manager, path);

    // Our own priming touch - we wanted the pointer above, nothing actually
    // changed, so don't queue a rebuild off the back of it.
    if (InterlockedExchange(&g_prime_pending, 0) == 1) {
        Log("[uiwatch] layout manager captured (%p) - panel reloads are available", layout_manager);
        return;
    }

    // No compiler in client mode and nothing local to rebuild anyway.
    if (!g_dev_mode) return;

    if (!IsCompilableSource(path)) return;
    std::string key;
    if (!CacheKeyFor(path, key)) return;

    for (const auto& k : g_pending_keys) if (k == key) return;
    g_pending_keys.push_back(key);
    g_pending_sources.push_back(path);
    g_last_change_ms = GetTickCount64();
    g_dirty = true;
    Log("[uiwatch] edited: %s", key.c_str());
}

bool __fastcall GetChangedFileProbe(void* watcher, void* out_buffer) {
    bool seen = false;
    for (size_t i = 0; i < g_rearmed_count; ++i)
        if (g_rearmed[i] == watcher) { seen = true; break; }

    if (!seen && g_rearmed_count < kMaxWatchers) {
        if (g_rearmed_count == 0)
            Log("[uiwatch] poll thread is tid %lu (injected on tid %lu)",
                GetCurrentThreadId(), g_inject_tid);
        g_rearmed[g_rearmed_count++] = watcher;
        const char* dir = *reinterpret_cast<const char* const*>(
            reinterpret_cast<const uint8_t*>(watcher) + kWatcherDirOffset);
        if (g_set_dir_to_watch && dir)
            g_set_dir_to_watch(watcher, dir, kWatchFlags, true, 0, 0);
    }

    InterlockedIncrement(&g_poll_count);

    // On the way out: undo everything here, then stay out of the way. The
    // watchdog does the actual unload.
    if (InterlockedCompareExchange(&g_eject_requested, 0, 0) == 1) {
        if (InterlockedCompareExchange(&g_unhooked, 0, 0) == 0) UnhookAndUnmount();
        return g_original_get_changed ? g_original_get_changed(watcher, out_buffer) : false;
    }

    // The connection itself is the liveness signal: the client's netchannel
    // exists while connected and is gone the moment it is not. Arming only
    // after one live sighting keeps a module injected at the main menu from
    // ejecting before the player has joined anything.
    if (g_eject_idle_ms != 0) {
        const uint64_t now = GetTickCount64();
        if (ClientConnected()) {
            if (InterlockedExchange(&g_channel_seen, 1) == 0)
                Log("[uiwatch] netchannel: connection is live; idle-eject armed");
            InterlockedExchange64(&g_channel_lost_ms, 0);

            // A different connection object means a different server: the last
            // one's Deadworks traffic says nothing about this one.
            void* conn = GetClientConnection();
            if (conn != g_conn_object) {
                g_conn_object = conn;
                g_session_ingame_ms = 0;
                if (InterlockedExchange(&g_deadworks_seen, 0) == 1)
                    Log("[uiwatch] server: new connection - watching for Deadworks traffic again");
            }

            // Only judge a connection once it is actually in a game; joining,
            // loading and hero select are all too early to expect anything.
            if (InterlockedCompareExchange(&g_signon_state, 0, 0) >= kSignonInGame) {
                if (g_session_ingame_ms == 0)
                    g_session_ingame_ms = now;
                if (InterlockedCompareExchange(&g_deadworks_seen, 0, 0) == 0
                    && now - g_session_ingame_ms > kNoDeadworksMs) {
                    RequestEject("no Deadworks traffic on this server");
                }
            }
        } else if (InterlockedCompareExchange(&g_channel_seen, 0, 0) == 1) {
            const LONG64 lost = InterlockedCompareExchange64(&g_channel_lost_ms, 0, 0);
            if (lost == 0) {
                InterlockedExchange64(&g_channel_lost_ms, static_cast<LONG64>(now));
                // Usually just a loading screen: Deadlock reconnects to its
                // own lobby within seconds, and the timer is cancelled above.
                Log("[uiwatch] netchannel: gone - ejecting in %llus unless it comes back",
                    g_eject_idle_ms / 1000);
            } else if (now - static_cast<uint64_t>(lost) > g_eject_idle_ms) {
                RequestEject("disconnected");
            }
        } else if (InterlockedCompareExchange(&g_channel_warned, 0, 0) == 0
                   && g_started_ms != 0 && now - g_started_ms > kChannelWarnAfterMs) {
            // Never resolved after minutes of polling: almost always the client
            // service RVA drifting after a game patch. Say so once - silence
            // here is what made the last no-eject take a log dig to explain.
            InterlockedExchange(&g_channel_warned, 1);
            static const char* kStageNames[] = {
                "the service slot was never found (name-anchored search failed too)",
                "the service pointer is null",
                "the connection object is null (never joined a game?)",
                "the connection has no netchannel",
                "live",
            };
            const LONG stage = InterlockedCompareExchange(&g_channel_stage, 0, 0);
            Log("[uiwatch] netchannel: never resolved after %llus - %s. Idle-eject cannot arm; "
                "delete '%s' to unload by hand.",
                kChannelWarnAfterMs / 1000,
                kStageNames[(stage >= 0 && stage <= 4) ? stage : 0],
                g_eject_marker.c_str());
        }
    }

    // Manual eject. The module writes DELETE-TO-EJECT.eject at startup and
    // watches for it to GO AWAY - deleting a file that is already sitting
    // there is easier to explain (and harder to get wrong) than creating one
    // with an exact name, which Explorer likes to save as .eject.txt. Only
    // armed if the marker was actually written, so a folder we cannot write to
    // never reads as "deleted".
    if (g_eject_marker_written
        && (g_last_eject_file_check == 0 || GetTickCount64() - g_last_eject_file_check > 1000)) {
        g_last_eject_file_check = GetTickCount64();
        if (GetFileAttributesA(g_eject_marker.c_str()) == INVALID_FILE_ATTRIBUTES)
            RequestEject("marker deleted");
    }

    // The event system is driven from this thread; register here, not on the
    // injected thread (same lesson as arming the dir watch).
    if (InterlockedCompareExchange(&g_netmsg_stage, 0, 0) == 1) {
        RegisterCustomGameEventHandler();
        InterlockedExchange(&g_netmsg_stage, 2);
    }

    // Register the data event on the first tick. A panel that calls
    // $.RegisterForUnhandledEvent before the type exists throws and gives up for
    // good, so this has to be in place well before a bundle loads one.
    if (g_engine && InterlockedCompareExchange(&g_ui_event_stage, 0, 0) == 0)
        InterlockedExchange(&g_ui_event_stage, RegisterUiDataEvent() ? 1 : 2);

    const LONG stage = InterlockedCompareExchange(&g_stage, 0, 0);

    // Writes have settled and no build is in flight - hand it to the worker.
    if (g_dirty && stage == static_cast<LONG>(Stage::Idle) &&
        GetTickCount64() - g_last_change_ms >= kDebounceMsRuntime) {
        g_dirty = false;
        g_building_sources = g_pending_sources;
        g_building_keys = g_pending_keys;
        g_pending_sources.clear();
        g_pending_keys.clear();
        InterlockedExchange(&g_stage, static_cast<LONG>(Stage::Building));
        SetEvent(g_work_event);
    }

    // Worker finished; do the swap here because it must happen on this thread.
    if (stage == static_cast<LONG>(Stage::ReadySwap)) {
        if (g_layout_manager) SwapAndReload(g_layout_manager);
        InterlockedExchange(&g_stage, static_cast<LONG>(Stage::Idle));
    }

    // Bundle finished downloading. Mount here for the same reason as above -
    // the filesystem swap belongs on this thread.
    if (InterlockedCompareExchange(&g_bundle_stage, 0, 0) ==
        static_cast<LONG>(BundleStage::ReadyMount)) {
        MountBundleAndReload();
        InterlockedExchange(&g_bundle_stage, static_cast<LONG>(BundleStage::Idle));
    }

    if (InterlockedExchange(&g_bundle_revoke, 0) == 1)
        RevokeBundleAndReload();

    // Key presses queued by the window thread go out from here.
    FlushInputReports();

    return g_original_get_changed ? g_original_get_changed(watcher, out_buffer) : false;
}

// ---------------------------------------------------------------------------
// Server-driven key input: capture, block, and report
//
// The server registers the keys it cares about (dw.input.bind / dw.input.unbind
// over message 148) and the module enforces that policy locally, in the game
// window's WndProc, the instant a key is pressed - blocking cannot wait for a
// round trip because by then the engine and Panorama have already acted on it.
// Presses on listening keys are reported back as dw.input.key over message 280.
//
// Why a WndProc subclass: input is SDL3-owned, and SDL3 owns the top-level game
// window, so a subclass front-runs SDL's own window proc and sees WM_KEYDOWN
// (and WM_MBUTTON/WM_XBUTTON/WM_MOUSEWHEEL) before SDL turns them into SDL
// events - which is also why blocking the wheel stops the game scrolling. Raw
// keyboard is opt-in in SDL (SDL_WINDOWS_RAW_KEYBOARD, default off) and even
// then targets a separate message-only window without RIDEV_NOLEGACY, so the
// legacy messages still arrive. All confirmed live.
//
// Typing is never eaten: while SDL reports text input active (chat, console, a
// Panorama TextEntry focused) registered keys pass straight through.
//
// The 280 send chain, all re-verified 2026-08-14 (see the design spec):
//   service = *(client.dll+0x3989858)   CNetworkClientService*  (DRIFTS - re-derive per patch)
//   conn    = *(service+0xA0)           CNetworkGameClient*     (engine2 ConnectClient)
//   netchan = *(conn+0xF0)              INetChannel*            (engine2 SendClientInfo)
//   binding = FindNetworkMessageById(280)
//   msg     = binding->vtable[6]()      AllocateMessage         (networksystem symbols)
//   msg->AsMessage()->ParseFromArray(...)
//   netchan->vtable[39](netchan, msg, bufType)
//   msg->vtable[0](msg)                 release
// ---------------------------------------------------------------------------

// --- native 280 send -------------------------------------------------------

// Cached CNetworkClientService* inside client.dll. Re-derive per patch from the
// interface-import descriptor table (name strings at client.dll+0x2932aa8/ac0/af8);
// each entry is { name ptr, cached-instance ptr }.
constexpr uintptr_t kClientServiceRva = 0x3989858;
constexpr size_t kServiceConnOffset = 0xA0;      // CNetworkGameClient*
constexpr size_t kConnNetChanOffset = 0xF0;      // INetChannel*

// Signon state on CNetworkGameClient, which is what the engine itself tests
// for "connected" - read out of its own vtable accessors in engine2.dll:
//   vtable[14]  cmp [rcx+230h], 6 ; setnl al    -> IsInGame     (>= 6)
//   vtable[15]  cmp [rcx+230h], 2 ; setnl al    -> IsConnected  (>= 2)
// The pointers alone are useless for liveness: the engine keeps the connection
// object (and a stale channel pointer) after a disconnect, which is why the
// first netchannel-based build armed in a match and then never saw it end.
constexpr size_t kConnSignonOffset = 0x230;
constexpr int kSignonConnected = 2;
constexpr int kSignonMax = 8;                    // range check: a sane state is 0..8
constexpr size_t kSendNetMessageIndex = 39;      // netchannel vtable +0x138
constexpr size_t kAllocateMessageIndex = 6;      // CProtobufBinding vtable
constexpr size_t kNetMessageAsMessageIndex = 2;  // CNetMessage vtable
constexpr int kClientCustomGameEventId = 280;
constexpr int kNetChanBufReliable = 0;

using AllocateMessageFn = void*(__fastcall*)(void*);
using AsMessageFn = void*(__fastcall*)(void*);
using SendNetMessageFn = void*(__fastcall*)(void*, void*, int);
using NetMessageDtorFn = void(__fastcall*)(void*, unsigned int);

// --- resolving the client service ------------------------------------------
//
// kClientServiceRva is where client.dll caches the resolved
// INetworkClientService pointer, and it moves with every game patch - it has
// already moved once (0x3987658 -> 0x3989858), and a stale one silently reads
// as "never connected", which is what stopped the module ejecting. So the RVA
// is only a first guess now: if it does not validate, the slot is found by
// anchoring on the interface's own name string, which does not move with the
// build.

void** g_client_service_slot = nullptr;

uint8_t* ModuleBounds(const wchar_t* name, size_t* size) {
    HMODULE module = GetModuleHandleW(name);
    if (!module) return nullptr;
    auto* base = reinterpret_cast<uint8_t*>(module);
    auto* dos = reinterpret_cast<IMAGE_DOS_HEADER*>(base);
    if (dos->e_magic != IMAGE_DOS_SIGNATURE) return nullptr;
    auto* nt = reinterpret_cast<IMAGE_NT_HEADERS64*>(base + dos->e_lfanew);
    if (nt->Signature != IMAGE_NT_SIGNATURE) return nullptr;
    if (size) *size = nt->OptionalHeader.SizeOfImage;
    return base;
}

bool PointsInto(const uint8_t* base, size_t size, const void* p) {
    if (!base || !p) return false;
    const auto v = reinterpret_cast<uintptr_t>(p);
    const auto b = reinterpret_cast<uintptr_t>(base);
    return v >= b && v < b + size;
}

/// The service is engine2-defined and client.dll only caches the pointer, so a
/// candidate whose vtable lives in engine2 is the real thing. Cheap, and it
/// rejects whatever unrelated data a stale offset happens to point at.
bool LooksLikeEngineObject(void* candidate) {
    size_t engine_size = 0;
    uint8_t* engine = ModuleBounds(L"engine2.dll", &engine_size);
    if (!engine || !candidate) return false;
    void* vtable = nullptr;
    __try {
        vtable = *reinterpret_cast<void**>(candidate);
    }
    __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
    return PointsInto(engine, engine_size, vtable);
}

const uint8_t* FindBytes(const uint8_t* begin, size_t size, const void* needle, size_t needle_size) {
    if (size < needle_size) return nullptr;
    for (size_t i = 0; i + needle_size <= size; ++i)
        if (memcmp(begin + i, needle, needle_size) == 0)
            return begin + i;
    return nullptr;
}

/// Finds the global client.dll keeps the service pointer in: locate the
/// interface name string, then the import descriptor that references it. The
/// descriptor holds either the slot address or the instance itself, depending
/// on the build, so both shapes are accepted - and only if the result passes
/// the engine2 vtable check.
void** FindClientServiceSlot() {
    size_t client_size = 0;
    uint8_t* client = ModuleBounds(L"client.dll", &client_size);
    if (!client) return nullptr;

    // The known offset first: right until a patch moves it, and free to check.
    if (kClientServiceRva + sizeof(void*) < client_size) {
        auto** slot = reinterpret_cast<void**>(client + kClientServiceRva);
        if (LooksLikeEngineObject(*slot)) {
            Log("[uiwatch] netchannel: service slot at the known rva +0x%llX", (unsigned long long)kClientServiceRva);
            return slot;
        }
    }

    static const char kServiceName[] = "NetworkClientService_001";
    uint8_t* rdata = nullptr;
    size_t rdata_size = 0;
    if (!GetSection(reinterpret_cast<HMODULE>(client), ".rdata", &rdata, &rdata_size))
        return nullptr;

    const uint8_t* name = FindBytes(rdata, rdata_size, kServiceName, sizeof(kServiceName));
    if (!name) {
        Log("[uiwatch] netchannel: '%s' is not in client.dll .rdata", kServiceName);
        return nullptr;
    }

    // A pointer to that string, then the slot/instance a few qwords behind it.
    const char* sections[] = {".rdata", ".data"};
    for (const char* section : sections) {
        uint8_t* begin = nullptr;
        size_t size = 0;
        if (!GetSection(reinterpret_cast<HMODULE>(client), section, &begin, &size))
            continue;

        for (size_t off = 0; off + sizeof(void*) <= size; off += sizeof(void*)) {
            auto** entry = reinterpret_cast<void**>(begin + off);
            if (*entry != name) continue;

            for (int step = 1; step <= 6 && off + (step + 1) * sizeof(void*) <= size; ++step) {
                void* value = entry[step];
                if (PointsInto(client, client_size, value)) {
                    auto** slot = reinterpret_cast<void**>(value);
                    if (LooksLikeEngineObject(*slot)) {
                        Log("[uiwatch] netchannel: service slot found by name in %s at +0x%llX",
                            section, (unsigned long long)(reinterpret_cast<uint8_t*>(slot) - client));
                        return slot;
                    }
                } else if (LooksLikeEngineObject(value)) {
                    Log("[uiwatch] netchannel: service instance found by name in %s at +0x%llX",
                        section, (unsigned long long)(reinterpret_cast<uint8_t*>(entry + step) - client));
                    return entry + step;
                }
            }
        }
    }

    Log("[uiwatch] netchannel: found the interface name but no usable descriptor");
    return nullptr;
}

/// The connection object the service holds, or null. Its signon state is the
/// liveness signal; its netchannel is what messages are sent on.
void* GetClientConnection() {
    if (!g_client_service_slot) {
        g_client_service_slot = FindClientServiceSlot();
        if (!g_client_service_slot) {
            InterlockedExchange(&g_channel_stage, static_cast<LONG>(ChannelStage::NoSlot));
            return nullptr;
        }
    }

    void* service = *g_client_service_slot;
    if (!service) {
        InterlockedExchange(&g_channel_stage, static_cast<LONG>(ChannelStage::NoService));
        return nullptr;
    }

    void* conn = *reinterpret_cast<void**>(
        reinterpret_cast<uint8_t*>(service) + kServiceConnOffset);
    if (!conn)
        InterlockedExchange(&g_channel_stage, static_cast<LONG>(ChannelStage::NoConn));
    return conn;
}

// Walks the service -> conn -> netchannel chain. Null until connected.
void* GetClientNetChannel() {
    void* conn = GetClientConnection();
    if (!conn)
        return nullptr;

    void* netchan = *reinterpret_cast<void**>(reinterpret_cast<uint8_t*>(conn) + kConnNetChanOffset);
    InterlockedExchange(&g_channel_stage, static_cast<LONG>(
        netchan ? ChannelStage::Live : ChannelStage::NoChannel));
    return netchan;
}

// Whether the client currently has a netchannel, i.e. is in a game. Polled
// several times a second from the reload probe, so it is wrapped in SEH: the
// service offset is a hardcoded RVA that drifts across game patches, and a
// stale one would otherwise turn a liveness check into an access violation.
// A drifted offset reads as "not connected", which never arms the eject -
// exactly the fail-safe direction.
bool ClientConnected() {
    __try {
        void* conn = GetClientConnection();
        if (!conn)
            return false;

        // The engine's own test. Range-checked, so a moved field reads as
        // "unknown" and falls back to the old pointer check rather than
        // ejecting on garbage.
        const int signon = *reinterpret_cast<const int*>(
            reinterpret_cast<const uint8_t*>(conn) + kConnSignonOffset);
        if (signon >= 0 && signon <= kSignonMax) {
            if (signon != InterlockedExchange(&g_signon_state, signon))
                Log("[uiwatch] netchannel: signon state %d%s", signon,
                    signon >= kSignonConnected ? " (connected)" : " (not connected)");
            return signon >= kSignonConnected;
        }

        if (InterlockedExchange(&g_signon_bogus_logged, 1) == 0)
            Log("[uiwatch] netchannel: signon field at +0x%llX reads %d - out of range, "
                "falling back to the netchannel pointer (offset moved?)",
                (unsigned long long)kConnSignonOffset, signon);
        return GetClientNetChannel() != nullptr;
    }
    __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
}

// Sends one custom game event to the server. Engine thread only.
bool SendClientEvent(const std::string& event_name, const std::string& data) {
    if (!g_net_messages) { Log("[uiwatch] input: no INetworkMessages"); return false; }

    void* netchan = GetClientNetChannel();
    if (!netchan) { Log("[uiwatch] input: no netchannel (not connected?)"); return false; }

    auto** nvt = *reinterpret_cast<void***>(g_net_messages);
    void* binding = reinterpret_cast<FindNetworkMessageByIdFn>(
        nvt[kFindNetworkMessageByIdIndex])(g_net_messages, kClientCustomGameEventId);
    if (!binding) { Log("[uiwatch] input: FindNetworkMessageById(280) null"); return false; }

    auto** bvt = *reinterpret_cast<void***>(binding);
    void* msg = reinterpret_cast<AllocateMessageFn>(bvt[kAllocateMessageIndex])(binding);
    if (!msg) { Log("[uiwatch] input: AllocateMessage returned null"); return false; }

    auto** mvt = *reinterpret_cast<void***>(msg);
    void* pb = reinterpret_cast<AsMessageFn>(mvt[kNetMessageAsMessageIndex])(msg);
    if (!pb) {
        Log("[uiwatch] input: AsMessage returned null");
        reinterpret_cast<NetMessageDtorFn>(mvt[0])(msg, 1);
        return false;
    }

    // Hand-encoded CClientMsg_CustomGameEvent: field 1 (event_name) and
    // field 2 (data), both length-delimited. Parsed into the engine's own
    // message object by its own protobuf, so no protobuf is linked here.
    std::string wire;
    auto put_varint = [&wire](size_t v) {
        while (v >= 0x80) { wire.push_back(static_cast<char>((v & 0x7F) | 0x80)); v >>= 7; }
        wire.push_back(static_cast<char>(v));
    };
    wire.push_back('\x0A'); put_varint(event_name.size()); wire += event_name;
    wire.push_back('\x12'); put_varint(data.size()); wire += data;

    // Parsed by protobuf's own ParseFromArray, dispatching onto the ENGINE's
    // message object. Only the lite runtime is linked and no generated message
    // is used: registering our own CClientMsg_CustomGameEvent descriptor
    // alongside client.dll's would be a duplicate-registration conflict.
    if (!reinterpret_cast<google::protobuf::MessageLite*>(pb)
             ->ParseFromArray(wire.data(), static_cast<int>(wire.size()))) {
        Log("[uiwatch] input: parse into the engine message failed");
        reinterpret_cast<NetMessageDtorFn>(mvt[0])(msg, 1);
        return false;
    }

    reinterpret_cast<SendNetMessageFn>(
        (*reinterpret_cast<void***>(netchan))[kSendNetMessageIndex])(
            netchan, msg, kNetChanBufReliable);
    reinterpret_cast<NetMessageDtorFn>(mvt[0])(msg, 1);

    Log("[uiwatch] input: sent 280 '%s' (%zu byte payload) up the netchannel",
        event_name.c_str(), data.size());
    return true;
}

// Policy for a registered key, matching the server's KeyPolicy enum.
enum class KeyPolicy : int { Listen = 0, Block = 1, BlockAndListen = 2 };

// Windows has no virtual key for the wheel, so these are Deadworks codes past
// the end of the VK range (0x01..0xFF) where they cannot collide with a real
// key. They must match Key.WheelUp/Down/Left/Right on the server.
constexpr int kWheelUp = 0x100;
constexpr int kWheelDown = 0x101;
constexpr int kWheelLeft = 0x102;
constexpr int kWheelRight = 0x103;
constexpr int kMaxKeyCode = 0x103;

// A flick of the wheel can arrive as one message carrying many notches; cap
// what a single message can queue so a spun wheel cannot flood the channel.
constexpr int kMaxWheelNotches = 8;

// The server's registration table: VK code -> policy. Written by the 148
// handler on the engine thread, read by the WndProc on the window thread, so
// every access takes the lock. Small and rarely changed, so a plain mutex over
// a flat map costs nothing on the input path.
std::unordered_map<int, KeyPolicy> g_key_policies;
CRITICAL_SECTION g_key_lock;
bool g_key_lock_ready = false;

// Keys currently held, so a key that is released after its bind was removed
// (or while typing started) cannot strand a "down" with no "up".
std::unordered_set<int> g_keys_down;

// Reports waiting to go out; filled from the window thread, drained on the
// engine thread (the netchannel send does not belong on the window thread).
struct KeyReport { int vk; int edge; int mods; };
std::vector<KeyReport> g_pending_reports;

WNDPROC g_orig_wndproc = nullptr;
HWND g_input_window = nullptr;

using SdlGetKeyboardFocusFn = void*(__cdecl*)();
using SdlTextInputActiveFn = int(__cdecl*)(void*);   // bool SDL_TextInputActive(SDL_Window*)
SdlGetKeyboardFocusFn g_sdl_get_kbd_focus = nullptr;
SdlTextInputActiveFn g_sdl_text_input_active = nullptr;
bool g_sdl_probe_resolved = false;

void ResolveSdlTyping() {
    if (g_sdl_probe_resolved) return;
    g_sdl_probe_resolved = true;
    HMODULE sdl = GetModuleHandleW(L"SDL3.dll");
    if (!sdl) { Log("[uiwatch] input: SDL3.dll not loaded - typing suppression off"); return; }
    g_sdl_get_kbd_focus =
        reinterpret_cast<SdlGetKeyboardFocusFn>(GetProcAddress(sdl, "SDL_GetKeyboardFocus"));
    g_sdl_text_input_active =
        reinterpret_cast<SdlTextInputActiveFn>(GetProcAddress(sdl, "SDL_TextInputActive"));
    Log("[uiwatch] input: SDL typing signal %s (focus=%p active=%p)",
        (g_sdl_get_kbd_focus && g_sdl_text_input_active) ? "resolved" : "MISSING",
        g_sdl_get_kbd_focus, g_sdl_text_input_active);
}

// True while a text field has focus (chat, console, a Panorama TextEntry).
// Fails open (false) if SDL is unavailable, so keys still work.
bool IsTypingActive() {
    if (!g_sdl_get_kbd_focus || !g_sdl_text_input_active) return false;
    void* win = g_sdl_get_kbd_focus();
    return win && g_sdl_text_input_active(win) != 0;
}

// Modifiers held right now, matching the server's KeyModifiers flags.
int CurrentModifiers() {
    int mods = 0;
    if (GetKeyState(VK_SHIFT) & 0x8000) mods |= 1;
    if (GetKeyState(VK_CONTROL) & 0x8000) mods |= 2;
    if (GetKeyState(VK_MENU) & 0x8000) mods |= 4;
    return mods;
}

LRESULT CALLBACK InputWndProc(HWND h, UINT msg, WPARAM w, LPARAM l) {
    int vk = -1;
    bool down = false, is_input = true, repeat = false;
    // A wheel notch is not a key: it has no release and no held state, so it
    // never touches g_keys_down and only ever reports a "down" edge. One
    // message can carry several notches on a fast flick (delta is a multiple
    // of WHEEL_DELTA), and each is reported separately.
    bool wheel = false;
    int notches = 0;
    switch (msg) {
        case WM_KEYDOWN: case WM_SYSKEYDOWN:
            vk = (int)w; down = true; repeat = (l & (1 << 30)) != 0; break;
        case WM_KEYUP: case WM_SYSKEYUP:
            vk = (int)w; down = false; break;
        case WM_MOUSEWHEEL: case WM_MOUSEHWHEEL: {
            const int delta = GET_WHEEL_DELTA_WPARAM(w);
            if (delta == 0) { is_input = false; break; }
            const bool horizontal = (msg == WM_MOUSEHWHEEL);
            // Horizontal deltas are positive to the RIGHT, vertical positive UP.
            vk = horizontal ? (delta > 0 ? kWheelRight : kWheelLeft)
                            : (delta > 0 ? kWheelUp : kWheelDown);
            int steps = (delta < 0 ? -delta : delta) / WHEEL_DELTA;
            if (steps < 1) steps = 1;              // sub-notch precision wheels
            if (steps > kMaxWheelNotches) steps = kMaxWheelNotches;
            wheel = true; notches = steps; down = true;
            break;
        }
        case WM_MBUTTONDOWN: vk = VK_MBUTTON; down = true; break;
        case WM_MBUTTONUP:   vk = VK_MBUTTON; down = false; break;
        case WM_XBUTTONDOWN:
            vk = (HIWORD(w) == XBUTTON1) ? VK_XBUTTON1 : VK_XBUTTON2; down = true; break;
        case WM_XBUTTONUP:
            vk = (HIWORD(w) == XBUTTON1) ? VK_XBUTTON1 : VK_XBUTTON2; down = false; break;
        default: is_input = false; break;
    }

    // OS auto-repeat is not an event: only true press and release edges are
    // reported, and a repeat must never re-trigger a block decision either.
    if (is_input && !(down && repeat) && g_key_lock_ready) {
        bool have_policy = false;
        KeyPolicy policy = KeyPolicy::Listen;
        bool was_down = false;

        EnterCriticalSection(&g_key_lock);
        auto it = g_key_policies.find(vk);
        if (it != g_key_policies.end()) { have_policy = true; policy = it->second; }
        was_down = g_keys_down.count(vk) != 0;
        LeaveCriticalSection(&g_key_lock);

        if (have_policy) {
            // While the player is typing, a registered key is neither blocked
            // nor reported - it belongs to the text box. A key already held
            // when typing started still gets its release, so no press is left
            // dangling server-side.
            const bool typing = IsTypingActive();
            if (!typing || (!down && was_down)) {
                const bool listens = policy != KeyPolicy::Block;
                const bool blocks = policy != KeyPolicy::Listen;
                const int mods = CurrentModifiers();

                EnterCriticalSection(&g_key_lock);
                if (wheel) {
                    // No held state to track - one report per notch.
                    if (listens)
                        for (int i = 0; i < notches; i++)
                            g_pending_reports.push_back({vk, 0, mods});
                } else {
                    if (down) g_keys_down.insert(vk); else g_keys_down.erase(vk);
                    if (listens)
                        g_pending_reports.push_back({vk, down ? 0 : 1, mods});
                }
                LeaveCriticalSection(&g_key_lock);

                if (blocks)
                    return 0;   // swallowed: the game never sees it
            }
        }
    }

    return CallWindowProcW(g_orig_wndproc, h, msg, w, l);
}

struct InputWindowSearch { DWORD pid; HWND best; LONG bestArea; };

BOOL CALLBACK InputEnumWindows(HWND h, LPARAM param) {
    auto* s = reinterpret_cast<InputWindowSearch*>(param);
    DWORD pid = 0;
    GetWindowThreadProcessId(h, &pid);
    if (pid != s->pid || !IsWindowVisible(h)) return TRUE;
    RECT r;
    if (!GetWindowRect(h, &r)) return TRUE;
    const LONG area = (r.right - r.left) * (r.bottom - r.top);
    if (area > s->bestArea) { s->bestArea = area; s->best = h; }
    return TRUE;
}

void InstallInputHook() {
    if (g_orig_wndproc) return;
    ResolveSdlTyping();
    if (!g_key_lock_ready) {
        InitializeCriticalSection(&g_key_lock);
        g_key_lock_ready = true;
    }
    InputWindowSearch s{ GetCurrentProcessId(), nullptr, 0 };
    EnumWindows(InputEnumWindows, reinterpret_cast<LPARAM>(&s));
    if (!s.best) { Log("[uiwatch] input: no visible top-level window - key input off"); return; }
    g_input_window = s.best;
    g_orig_wndproc = reinterpret_cast<WNDPROC>(
        SetWindowLongPtrW(g_input_window, GWLP_WNDPROC, reinterpret_cast<LONG_PTR>(&InputWndProc)));
    wchar_t title[128] = {};
    GetWindowTextW(g_input_window, title, 127);
    Log("[uiwatch] input: watching window %p ('%ls') for server-registered keys",
        g_input_window, title);
}

void RemoveInputHook() {
    if (!g_orig_wndproc || !g_input_window) return;
    // Only unsubclass if we are still the installed proc; something else
    // subclassing after us would otherwise be torn out from under it.
    auto current = reinterpret_cast<WNDPROC>(GetWindowLongPtrW(g_input_window, GWLP_WNDPROC));
    if (current == &InputWndProc)
        SetWindowLongPtrW(g_input_window, GWLP_WNDPROC, reinterpret_cast<LONG_PTR>(g_orig_wndproc));
    else
        Log("[uiwatch] input: another wndproc is installed on top - leaving the chain alone");
    Log("[uiwatch] input: released the window hook");
    g_orig_wndproc = nullptr;
    g_input_window = nullptr;

    if (g_key_lock_ready) {
        EnterCriticalSection(&g_key_lock);
        g_key_policies.clear();
        g_keys_down.clear();
        g_pending_reports.clear();
        LeaveCriticalSection(&g_key_lock);
    }
}

// Engine thread. Applies a dw.input.bind / dw.input.unbind control message.
void HandleInputBind(const std::string& payload, bool bind) {
    size_t start = 0;
    std::string version, key_text, policy_text;
    while (start <= payload.size()) {
        size_t nl = payload.find('\n', start);
        if (nl == std::string::npos) nl = payload.size();
        const std::string line = payload.substr(start, nl - start);
        const bool last = (nl == payload.size());
        start = nl + 1;
        if (!line.empty()) {
            const size_t eq = line.find('=');
            if (eq != std::string::npos) {
                const std::string k = line.substr(0, eq);
                if (k == "v") version = line.substr(eq + 1);
                else if (k == "key") key_text = line.substr(eq + 1);
                else if (k == "policy") policy_text = line.substr(eq + 1);
            }
        }
        if (last) break;
    }
    if (version != "1" || key_text.empty()) return;

    const int vk = atoi(key_text.c_str());
    if (vk <= 0 || vk > kMaxKeyCode) return;
    if (!g_key_lock_ready) return;

    EnterCriticalSection(&g_key_lock);
    if (bind) {
        const int p = policy_text.empty() ? 0 : atoi(policy_text.c_str());
        g_key_policies[vk] = static_cast<KeyPolicy>(p < 0 || p > 2 ? 0 : p);
    } else {
        g_key_policies.erase(vk);
        g_keys_down.erase(vk);
    }
    const size_t count = g_key_policies.size();
    LeaveCriticalSection(&g_key_lock);

    Log("[uiwatch] input: %s vk=0x%02X (%zu key(s) registered)",
        bind ? "bound" : "unbound", vk, count);
}

// Engine thread. Ships any key reports the window thread queued.
void FlushInputReports() {
    if (!g_key_lock_ready) return;

    std::vector<KeyReport> batch;
    EnterCriticalSection(&g_key_lock);
    batch.swap(g_pending_reports);
    LeaveCriticalSection(&g_key_lock);

    for (const auto& r : batch) {
        char body[96];
        sprintf_s(body, "v=1\nkey=%d\nedge=%d\nmods=%d\n", r.vk, r.edge, r.mods);
        SendClientEvent("dw.input.key", body);
    }
}

// ---------------------------------------------------------------------------
// Teardown
// ---------------------------------------------------------------------------

// Panels the host bundle built at runtime do NOT live under the layout it
// replaced: the runtime hoists its overlay into HudCore for z-order, so it is a
// child of the HUD root, not of hud_health. Unmounting and evicting the layout
// therefore reloads a panel that never owned those panels, and the pushed UI
// stays on screen with nothing left to update it. Deleting the overlay is the
// only thing that takes it away - and it has to happen while a panel context
// still exists, i.e. before the unmount.
constexpr char kTeardownJs[] =
    "(function(){try{"
    "var p=$.GetContextPanel();if(!p)return;"
    "var top=p;while(top.GetParent&&top.GetParent())top=top.GetParent();"
    "var names=['DwHostOverlay','DwOverlayHost'];"
    "for(var i=0;i<names.length;i++){"
    "var n=top.FindChildTraverse&&top.FindChildTraverse(names[i]);"
    "if(n&&n.DeleteAsync)n.DeleteAsync(0);}"
    "}catch(e){}})();";

// Engine thread only. Unmount first, then undo the hooks - that way the
// filesystem is left clean even if something below goes wrong.
void UnhookAndUnmount() {
    // Before anything else, while the panel and its V8 context are still
    // alive: take down the server-built UI. After the unmount there is no
    // script left to do it, and after the hooks are gone we cannot run any.
    if (InterlockedCompareExchange(&g_ui_event_stage, 0, 0) == 1) {
        RunServerJs(kTeardownJs);
        Log("[uiwatch] eject: asked the panel to remove the server-built UI");
    }

    RemoveInputHook();   // release the window hook before anything else
    if (g_bundle_mounted) {
        UnmountVpk(g_bundle_vpk);
        g_bundle_mounted = false;
        Log("[uiwatch] eject: unmounted the server bundle");
    }
    if (g_images_mounted) {
        UnmountVpk(g_images_vpk);
        g_images_mounted = false;
        Log("[uiwatch] eject: unmounted the images pack");
    }
    if (g_filesystem && g_dev_mode && !g_live_vpk.empty()) {
        UnmountLiveVpk();
        Log("[uiwatch] eject: unmounted the hot-reload vpk");
    }

    // Unmounting alone doesn't revert anything - a loaded panel keeps its cached
    // copy and carries on showing our content. Evicting the keys is what puts
    // the UI back, and it has to come after the unmount so the re-read picks up
    // the game's own pak. Safe to do mid-teardown since g_original_invalidate
    // points into panorama, not us.
    if (!g_overridden_keys.empty()) {
        if (g_layout_manager) {
            for (const auto& k : g_overridden_keys) {
                g_original_invalidate(g_layout_manager, k.c_str());
                Log("[uiwatch] eject:   reverted %s", k.c_str());
            }
        } else {
            Log("[uiwatch] eject: no layout manager - panels keep the replaced "
                "content until they next load");
        }
    }
    g_overridden_keys.clear();
    g_bundle_current = Manifest();

    if (g_panorama_base && g_original_get_changed) {
        HookIat(g_panorama_base, kGetChangedFileIatRva,
                reinterpret_cast<void*>(g_original_get_changed));
        Log("[uiwatch] eject: restored the GetChangedFile IAT entry");
    }

    if (g_invalidate_site && g_original_invalidate) {
        DWORD old = 0;
        VirtualProtect(g_invalidate_site, 5, PAGE_EXECUTE_READWRITE, &old);
        *reinterpret_cast<int32_t*>(g_invalidate_site + 1) = static_cast<int32_t>(
            reinterpret_cast<uint8_t*>(g_original_invalidate) - (g_invalidate_site + 5));
        VirtualProtect(g_invalidate_site, 5, old, &old);
        FlushInstructionCache(GetCurrentProcess(), g_invalidate_site, 5);
        Log("[uiwatch] eject: restored the patched call site");
    }

    InterlockedExchange(&g_unhooked, 1);
}

// Own thread, so the final FreeLibrary isn't running out of the image it's
// freeing.
DWORD WINAPI EjectWatchdog(LPVOID) {
    while (InterlockedCompareExchange(&g_unhooked, 0, 0) == 0)
        Sleep(100);

    // The engine might be inside our probe at the exact moment we unhook.
    // Nothing new gets in after that, and the reload tick is 0.2s, so this is
    // plenty of slack for whatever was already in there to finish.
    Sleep(2000);

    if (g_stub) {
        VirtualFree(g_stub, 0, MEM_RELEASE);
        g_stub = nullptr;
    }

    InterlockedExchange(&g_shutdown, 1);
    if (g_work_event) SetEvent(g_work_event);
    if (g_bundle_event) SetEvent(g_bundle_event);
    Sleep(300);

    // Leave the folder as we found it; the next injection writes it again.
    if (!g_eject_marker.empty()) DeleteFileA(g_eject_marker.c_str());

    Log("[uiwatch] eject: unloading");
    FreeLibraryAndExitThread(g_self, 0);
    return 0;
}

void RequestEject(const char* why) {
    if (InterlockedExchange(&g_eject_requested, 1) == 1) return;
    Log("[uiwatch] eject: requested (%s)", why);
    CreateThread(nullptr, 0, EjectWatchdog, nullptr, 0, nullptr);
}

// ---------------------------------------------------------------------------
// Setup
// ---------------------------------------------------------------------------

// Creates every level of a path, ignoring levels that already exist.
bool EnsureDir(const std::string& path) {
    for (size_t i = 0; i <= path.size(); ++i) {
        if (i != path.size() && path[i] != '\\') continue;
        if (i < 3) continue;  // skip the "C:\" root
        const std::string part = path.substr(0, i);
        if (!CreateDirectoryA(part.c_str(), nullptr) &&
            GetLastError() != ERROR_ALREADY_EXISTS)
            return false;
    }
    return GetFileAttributesA(path.c_str()) != INVALID_FILE_ATTRIBUTES;
}

// Client mode has no config, so everything comes from the running game.
// deadlock.exe lives at <game>\bin\win64\, so walk back up from there.
bool DeriveClientPaths() {
    char exe[MAX_PATH] = {};
    if (GetModuleFileNameA(nullptr, exe, MAX_PATH) == 0) return false;

    std::string dir(exe);
    for (int i = 0; i < 4; ++i) {  // strip deadlock.exe, win64, bin, game
        const size_t slash = dir.find_last_of('\\');
        if (slash == std::string::npos) return false;
        dir = dir.substr(0, slash);
    }
    // dir is now <...>\Deadlock\game's parent-of-bin, i.e. the game root.
    const std::string addons = dir + "\\game\\citadel\\addons_dev";

    // Somewhere of our own to watch. GetChangedFile is our only engine-thread
    // callback and the engine calls it once per registered watcher, so we need
    // at least one watched directory even with nothing to hot reload.
    g_source_dir = addons + "\\dwclient";
    const std::string layout = g_source_dir + "\\panorama\\layout";
    if (!EnsureDir(layout)) {
        Log("[uiwatch] could not create %s", layout.c_str());
        return false;
    }

    // A file to touch so the reload driver hands us the layout manager. Named
    // like a real panorama source since that's the only shape we've seen
    // actually produce an invalidation.
    const std::string prime = layout + "\\dwprime.xml";
    if (GetFileAttributesA(prime.c_str()) == INVALID_FILE_ATTRIBUTES) {
        HANDLE h = CreateFileA(prime.c_str(), GENERIC_WRITE, 0, nullptr, CREATE_ALWAYS,
                               FILE_ATTRIBUTE_NORMAL, nullptr);
        if (h == INVALID_HANDLE_VALUE) {
            Log("[uiwatch] could not create %s", prime.c_str());
            return false;
        }
        const char* body = "<root></root>\n";
        DWORD written = 0;
        WriteFile(h, body, static_cast<DWORD>(strlen(body)), &written, nullptr);
        CloseHandle(h);
    }

    g_bundle_vpk = addons + "\\dwbundle.vpk";
    g_images_vpk = addons + "\\dwimages.vpk";
    g_live_vpk.clear();   // no hot-reload vpk in client mode
    return true;
}

// Reads uiwatch.ini from next to the DLL.
bool LoadConfig() {
    char ini[MAX_PATH];
    if (GetModuleFileNameA(g_self, ini, MAX_PATH) == 0) return false;
    char* slash = strrchr(ini, '\\');
    if (!slash) return false;
    strcpy_s(slash + 1, MAX_PATH - (slash + 1 - ini), "uiwatch.ini");

    // The manual-eject marker lives beside the ini, whether or not there is one.
    g_eject_marker.assign(ini, slash + 1 - ini);
    g_eject_marker += "DELETE-TO-EJECT.eject";
    g_started_ms = GetTickCount64();

    FILE* marker = nullptr;
    if (fopen_s(&marker, g_eject_marker.c_str(), "w") == 0 && marker) {
        fputs("Delete this file to unload the Deadworks UI module from the running game.\r\n"
              "It is recreated the next time the module is injected.\r\n", marker);
        fclose(marker);
        g_eject_marker_written = true;
        Log("[uiwatch] eject: delete '%s' to unload", g_eject_marker.c_str());
    } else {
        Log("[uiwatch] eject: could not write '%s' - manual eject unavailable",
            g_eject_marker.c_str());
    }

    const bool have_ini = GetFileAttributesA(ini) != INVALID_FILE_ATTRIBUTES;
    if (!have_ini)
        Log("[uiwatch] no uiwatch.ini next to the DLL - client mode (server bundles only)");

    auto read = [&](const char* key, std::string& out) {
        char buf[MAX_PATH] = {};
        GetPrivateProfileStringA("paths", key, "", buf, MAX_PATH, ini);
        out = buf;
        // GetPrivateProfileString keeps trailing spaces from "key = value".
        while (!out.empty() && (out.back() == ' ' || out.back() == '\t')) out.pop_back();
        return !out.empty();
    };

    // Dev mode wants the whole [paths] set. Anything short of that and we run
    // as the shipped client, which needs no config at all.
    bool complete = have_ini;
    complete &= read("compiler", g_compiler);
    complete &= read("gamedir", g_gamedir);
    complete &= read("source", g_source_dir);
    complete &= read("output", g_output_dir);
    complete &= read("livevpk", g_live_vpk);

    if (have_ini && !complete)
        Log("[uiwatch] uiwatch.ini is missing one or more [paths] keys - client mode");

    g_dev_mode = complete;

    // Own mount for server bundles so they never fight the hot-reload vpk.
    // Sits next to livevpk unless told otherwise.
    if (!read("bundlevpk", g_bundle_vpk)) {
        const size_t slash = g_live_vpk.find_last_of('\\');
        g_bundle_vpk = (slash == std::string::npos ? std::string() : g_live_vpk.substr(0, slash + 1)) +
                       "dwbundle.vpk";
    }

    // The images pack gets its own slot beside the bundle (client mode derives
    // both in DeriveClientPaths below).
    {
        const size_t slash = g_bundle_vpk.find_last_of('\\');
        g_images_vpk = (slash == std::string::npos ? std::string() : g_bundle_vpk.substr(0, slash + 1)) +
                       "dwimages.vpk";
    }

    kDebounceMsRuntime = GetPrivateProfileIntA("options", "debounce", 400, ini);

    // Seconds without a heartbeat before we assume they left and unload.
    // Long enough to survive a map load, where it genuinely pauses. 0 keeps us
    // resident, handy while working on the module itself.
    g_eject_idle_ms = static_cast<uint64_t>(
        GetPrivateProfileIntA("options", "ejectidle", 30, ini)) * 1000ull;
    Log("[uiwatch] self-eject after %llus without a heartbeat%s",
        g_eject_idle_ms / 1000, g_eject_idle_ms ? "" : " (disabled)");

    char ids[256] = {};
    GetPrivateProfileStringA("options", "netmsgid", kDefaultNetMsgIds, ids, sizeof(ids), ini);
    char* ctx = nullptr;
    for (char* tok = strtok_s(ids, ", \t", &ctx); tok && g_netmsg_id_count < kMaxNetMsgIds;
         tok = strtok_s(nullptr, ", \t", &ctx)) {
        const int id = atoi(tok);
        if (id > 0) g_netmsgs[g_netmsg_id_count++].id = id;
    }
    // Liveness subscription, added on top of the logged ids.
    g_heartbeat_id = GetPrivateProfileIntA("options", "heartbeatid", kDefaultHeartbeatId, ini);
    if (g_heartbeat_id > 0 && g_netmsg_id_count < kMaxNetMsgIds) {
        bool present = false;
        for (size_t i = 0; i < g_netmsg_id_count; ++i)
            if (g_netmsgs[i].id == g_heartbeat_id) { g_netmsgs[i].heartbeat = true; present = true; }
        if (!present) {
            g_netmsgs[g_netmsg_id_count].id = g_heartbeat_id;
            g_netmsgs[g_netmsg_id_count].heartbeat = true;
            ++g_netmsg_id_count;
        }
    }

    if (g_netmsg_id_count == 0)
        Log("[uiwatch] netmsg: no usable ids in [options] netmsgid - not subscribing");

    // Opt-in per id, since decoding leaks a string we can't free. Turning it on
    // for something chatty like 207 would flood the log and the heap both.
    char dump[256] = {};
    GetPrivateProfileStringA("options", "dumpids", "148", dump, sizeof(dump), ini);
    char* dctx = nullptr;
    for (char* tok = strtok_s(dump, ", \t", &dctx); tok; tok = strtok_s(nullptr, ", \t", &dctx)) {
        const int id = atoi(tok);
        for (size_t i = 0; i < g_netmsg_id_count; ++i)
            if (g_netmsgs[i].id == id) g_netmsgs[i].dump = true;
    }

    if (!g_dev_mode) {
        // Nothing usable configured, so derive it all from the game.
        if (!DeriveClientPaths()) return false;
        Log("[uiwatch] mode    : client (server bundles only)");
        Log("[uiwatch] workdir : %s", g_source_dir.c_str());
        Log("[uiwatch] bundle  : %s", g_bundle_vpk.c_str());
        Log("[uiwatch] images  : %s", g_images_vpk.c_str());
        return true;
    }

    Log("[uiwatch] mode    : developer (hot reload + server bundles)");
    Log("[uiwatch] compiler: %s", g_compiler.c_str());
    Log("[uiwatch] source  : %s", g_source_dir.c_str());
    Log("[uiwatch] output  : %s", g_output_dir.c_str());
    Log("[uiwatch] live vpk: %s", g_live_vpk.c_str());
    Log("[uiwatch] bundle  : %s", g_bundle_vpk.c_str());
    Log("[uiwatch] images  : %s", g_images_vpk.c_str());

    if (GetFileAttributesA(g_compiler.c_str()) == INVALID_FILE_ATTRIBUTES) {
        Log("[uiwatch] compiler not found at that path"); return false;
    }
    if (GetFileAttributesA(g_source_dir.c_str()) == INVALID_FILE_ATTRIBUTES) {
        Log("[uiwatch] source dir does not exist"); return false;
    }
    return true;
}

void WatchTree(void* engine, void* add_dir_watch, const std::string& root, int depth) {
    reinterpret_cast<AddDirWatchFn>(add_dir_watch)(engine, root.c_str());
    Log("[uiwatch] watching %s", root.c_str());
    if (depth > 6) return;
    WIN32_FIND_DATAA fd;
    HANDLE h = FindFirstFileA((root + "\\*").c_str(), &fd);
    if (h == INVALID_HANDLE_VALUE) return;
    do {
        if (fd.cFileName[0] == '.') continue;
        if (fd.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY)
            WatchTree(engine, add_dir_watch, root + "\\" + fd.cFileName, depth + 1);
    } while (FindNextFileA(h, &fd));
    FindClose(h);
}

// Bumps the mtime on any watched source file. Content is untouched so the
// reload is a no-op; we only care that the driver hands us the layout manager
// on its way through.
bool TouchAnyWatchedSource(const std::string& root, int depth = 0) {
    WIN32_FIND_DATAA fd;
    HANDLE h = FindFirstFileA((root + "\\*").c_str(), &fd);
    if (h == INVALID_HANDLE_VALUE) return false;
    std::vector<std::string> subdirs;
    bool done = false;
    do {
        if (fd.cFileName[0] == '.') continue;
        const std::string full = root + "\\" + fd.cFileName;
        if (fd.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) {
            if (depth < 6) subdirs.push_back(full);
            continue;
        }
        if (!IsCompilableSource(full)) continue;

        HANDLE f = CreateFileA(full.c_str(), FILE_WRITE_ATTRIBUTES,
                               FILE_SHARE_READ | FILE_SHARE_WRITE, nullptr,
                               OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
        if (f == INVALID_HANDLE_VALUE) continue;
        FILETIME now;
        GetSystemTimeAsFileTime(&now);
        const bool ok = SetFileTime(f, nullptr, nullptr, &now) != 0;
        CloseHandle(f);
        if (ok) {
            Log("[uiwatch] priming the layout manager via %s", full.c_str());
            done = true;
            break;
        }
    } while (FindNextFileA(h, &fd));
    FindClose(h);

    if (done) return true;
    for (const auto& d : subdirs)
        if (TouchAnyWatchedSource(d, depth + 1)) return true;
    return false;
}

template <typename P>
bool WaitFor(P ready, int timeout_ms) {
    for (int w = 0; w < timeout_ms; w += 250) {
        if (ready()) return true;
        Sleep(250);
    }
    return ready();
}

DWORD WINAPI Run(LPVOID) {
    InitLog();
    g_inject_tid = GetCurrentThreadId();
    // Build stamp rather than a hand-written version. Spent a while once
    // reading a log that claimed to be v15 while actually being v16.
    Log("[uiwatch] starting (built " __DATE__ " " __TIME__ ")");

    HMODULE panorama = nullptr;
    if (!WaitFor([&] { return (panorama = GetModuleHandleW(L"panorama.dll")) != nullptr; }, 120000)) {
        Log("[uiwatch] panorama.dll never loaded"); return 0;
    }
    auto* base = reinterpret_cast<uint8_t*>(panorama);
    g_panorama_base = base;

    uint8_t* text = nullptr; size_t text_size = 0;
    if (!GetSection(panorama, ".text", &text, &text_size)) { Log("[uiwatch] no .text"); return 0; }

    uint8_t* match = FindPattern(text, text_size, kEnginePattern, kEngineMask);
    if (!match) { Log("[uiwatch] engine signature not found - resig needed"); return 0; }
    auto** engine_global = reinterpret_cast<void**>(match + 7 + *reinterpret_cast<int32_t*>(match + 3));

    if (!WaitFor([&] { return (g_engine = *engine_global) != nullptr; }, 120000)) {
        Log("[uiwatch] engine never became non-null"); return 0;
    }
    Log("[uiwatch] engine %p", g_engine);

    auto** vt = *reinterpret_cast<void***>(g_engine);
    void* add_dir_watch = vt[kAddDirWatchIndex];
    if (add_dir_watch < text || add_dir_watch >= text + text_size) {
        Log("[uiwatch] AddDirWatch slot outside .text - ABORT"); return 0;
    }

    // Probe on the driver's per-file invalidation call.
    uint8_t* site = base + kInvalidateCallRva;
    if (memcmp(site, kInvalidateCallBytes, sizeof(kInvalidateCallBytes)) != 0) {
        Log("[uiwatch] invalidate call site changed - ABORT"); return 0;
    }
    g_original_invalidate = reinterpret_cast<InvalidateFn>(
        site + 5 + *reinterpret_cast<int32_t*>(site + 1));
    uint8_t* stub = AllocStubNear(site, reinterpret_cast<void*>(&InvalidateProbe));
    if (!stub) { Log("[uiwatch] no stub in range - ABORT"); return 0; }
    g_invalidate_site = site;
    g_stub = stub;
    DWORD old = 0;
    VirtualProtect(site, 5, PAGE_EXECUTE_READWRITE, &old);
    *reinterpret_cast<int32_t*>(site + 1) = static_cast<int32_t>(stub - (site + 5));
    VirtualProtect(site, 5, old, &old);
    FlushInstructionCache(GetCurrentProcess(), site, 5);

    g_set_dir_to_watch = *reinterpret_cast<SetDirToWatchFn*>(base + kSetDirToWatchIatRva);
    g_original_get_changed = reinterpret_cast<GetChangedFileFn>(
        HookIat(base, kGetChangedFileIatRva, reinterpret_cast<void*>(&GetChangedFileProbe)));

    if (!LoadConfig()) return 0;
    if (!ResolveFilesystem()) return 0;

    g_bundle_event = CreateEventA(nullptr, FALSE, FALSE, nullptr);
    CreateThread(nullptr, 0, BundleWorker, nullptr, 0, nullptr);

    if (g_dev_mode) {
        g_work_event = CreateEventA(nullptr, FALSE, FALSE, nullptr);
        CreateThread(nullptr, 0, BuildWorker, nullptr, 0, nullptr);

        const int packed = BuildVpk(g_output_dir, g_live_vpk);
        Log("[uiwatch] initial pack from %s: %d file(s)", g_output_dir.c_str(), packed);
        MountLiveVpk();
        Log("[uiwatch] mounted %s at head of GAME", g_live_vpk.c_str());
    }

    // Both modes need this - it's what gets the engine calling our poll probe,
    // and the poll probe is the only place we're allowed to mount anything.
    WatchTree(g_engine, add_dir_watch, g_source_dir, 0);
    Log("[uiwatch] ready - %s", g_dev_mode
        ? "edit .xml/.css/.js under the source dir"
        : "waiting for server bundles");

    // Watchers don't get armed until the poll thread has been through
    // GetChangedFile once, so wait for that before touching anything.
    if (WaitFor([] { return g_rearmed_count > 0; }, 15000)) {
        InterlockedExchange(&g_prime_pending, 1);
        if (!TouchAnyWatchedSource(g_source_dir))
            Log("[uiwatch] no .xml/.css/.js under the source dir to prime with - "
                "server bundles will mount but not reload live panels");
        else if (!WaitFor([] { return g_layout_manager != nullptr; }, 10000))
            Log("[uiwatch] priming touch did not produce an invalidation - "
                "server bundles will mount but not reload live panels");
    } else {
        Log("[uiwatch] watchers never armed - cannot prime the layout manager");
    }

    if (ResolveNetMsg()) {
        InterlockedExchange(&g_netmsg_stage, 1);
        if (!WaitFor([] { return InterlockedCompareExchange(&g_netmsg_stage, 0, 0) == 2; }, 30000))
            Log("[uiwatch] netmsg: poll thread never picked up the registration");
    }

    InstallInputHook();   // server-registered key capture / blocking

    for (int i = 0; i < 240; ++i) {
        for (int s = 0; s < 30; ++s) {
            if (InterlockedCompareExchange(&g_eject_requested, 0, 0) == 1) return 0;
            Sleep(1000);
        }
        Log("[uiwatch] heartbeat: %ld polls, %ld netmsg(s)", g_poll_count, g_netmsg_count);
    }
    return 0;
}

}  // namespace

BOOL APIENTRY DllMain(HMODULE module, DWORD reason, LPVOID) {
    if (reason == DLL_PROCESS_ATTACH) {
        g_self = module;
        DisableThreadLibraryCalls(module);
        CreateThread(nullptr, 0, Run, nullptr, 0, nullptr);
    }
    return TRUE;
}
