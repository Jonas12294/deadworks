#pragma once

#include "Schema/Schema.hpp"

class CBaseModifier {
    DECLARE_SCHEMA_CLASS(CBaseModifier);
    SCHEMA_FIELD(uint32_t, m_nAbilitySubclassID);
};
