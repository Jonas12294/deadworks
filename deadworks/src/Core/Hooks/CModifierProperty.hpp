#pragma once

#include <safetyhook.hpp>

class CBaseEntity;

namespace deadworks {
namespace hooks {

inline safetyhook::InlineHook g_CModifierProperty_AddModifier;
void *__fastcall Hook_CModifierProperty_AddModifier(
    void *thisptr, CBaseEntity *pCaster, uint32_t hAbility, int iTeam,
    void *vdata, void *pModifierParams, void *pKeyValues);

} // namespace hooks
} // namespace deadworks
