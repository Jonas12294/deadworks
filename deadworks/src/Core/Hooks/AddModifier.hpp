#pragma once

#include <safetyhook.hpp>

class CBaseEntity;
struct CModifierVData;
class KeyValues3;

namespace deadworks {
namespace hooks {

inline safetyhook::InlineHook g_CModifierProperty_AddModifier;
void *__fastcall Hook_CModifierProperty_AddModifier(void *thisptr, CBaseEntity *pCaster, uint32_t hAbility, int iTeam,
                                                     CModifierVData *vdata, KeyValues3 *pParams, KeyValues3 *pKV);

} // namespace hooks
} // namespace deadworks
