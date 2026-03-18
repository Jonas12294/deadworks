#pragma once

#include "Schema/Schema.hpp"
#include "CitadelAbilityProperty.hpp"
#include <tier1/utlmap.h>

using AbilityPropertyMap_t = CUtlOrderedMap<CUtlString, CitadelAbilityProperty_t>;

class CitadelAbilityVData {
    DECLARE_SCHEMA_CLASS(CitadelAbilityVData);
    SCHEMA_FIELD_POINTER(AbilityPropertyMap_t, m_mapAbilityProperties);

};
