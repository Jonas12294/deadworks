#pragma once

#include "Schema/Schema.hpp"
#include "CBaseEntity.hpp"

#include "../Memory/MemoryDataLoader.hpp"

class CCitadelBaseAbility : public CBaseEntity {
    DECLARE_SCHEMA_CLASS(CCitadelBaseAbility);
    SCHEMA_FIELD(uint16_t, m_eAbilitySlot);

    void ToggleActivate(char activate) {
        static const auto fn = reinterpret_cast<void(__fastcall *)(void *, char)>(
            deadworks::MemoryDataLoader::Get().GetOffset("CCitadelBaseAbility::ToggleActivate").value());
        fn(this, activate);
    }
};
