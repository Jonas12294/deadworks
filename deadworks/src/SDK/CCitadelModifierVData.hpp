#pragma once

#include "Schema/Schema.hpp"
#include <tier0/utlstring.h>
#include <tier1/utlvector.h>

class CCitadelModifierVData {
    DECLARE_SCHEMA_CLASS(CCitadelModifierVData);
    SCHEMA_FIELD(CUtlVector<CUtlString>, m_vecAutoRegisterModifierValueFromAbilityPropertyName);
};
