#include "CModifierProperty.hpp"

#include "../Deadworks.hpp"

namespace deadworks {
namespace hooks {

void *__fastcall Hook_CModifierProperty_AddModifier(
    void *thisptr, CBaseEntity *pCaster, uint32_t hAbility, int iTeam,
    void *vdata, void *pModifierParams, void *pKeyValues) {

    if (g_Deadworks.OnPre_CModifierProperty_AddModifier(thisptr, pCaster, hAbility, vdata, iTeam))
        return nullptr;

    return g_CModifierProperty_AddModifier.thiscall<void *>(
        thisptr, pCaster, hAbility, iTeam, vdata, pModifierParams, pKeyValues);
}

} // namespace hooks
} // namespace deadworks
