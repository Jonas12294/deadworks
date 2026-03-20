#include "AddModifier.hpp"

#include "../Deadworks.hpp"

namespace deadworks {
namespace hooks {

void *__fastcall Hook_CModifierProperty_AddModifier(void *thisptr, CBaseEntity *pCaster, uint32_t hAbility, int iTeam,
                                                     CModifierVData *vdata, KeyValues3 *pParams, KeyValues3 *pKV) {
    if (g_Deadworks.OnPre_AddModifier(thisptr, pCaster, hAbility, iTeam, vdata, pParams, pKV))
        return nullptr;

    return g_CModifierProperty_AddModifier.thiscall<void *>(thisptr, pCaster, hAbility, iTeam, vdata, pParams, pKV);
}

} // namespace hooks
} // namespace deadworks
