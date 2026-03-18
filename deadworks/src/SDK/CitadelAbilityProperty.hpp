#pragma once

#include "Schema/Schema.hpp"
#include <tier0/utlstring.h>

// Accessor overlay for CitadelAbilityProperty_t fields via SCHEMA_FIELD.
// This class has no storage — it's reinterpret_cast'd onto the raw bytes.
class CitadelAbilityPropertyAccessor {
    DECLARE_SCHEMA_CLASS_ALIAS(CitadelAbilityPropertyAccessor, CitadelAbilityProperty_t);
    SCHEMA_FIELD(CUtlString, m_strValue);
    SCHEMA_FIELD(CUtlString, m_strStreetBrawlValue);
    SCHEMA_FIELD(int32_t, m_eProvidedPropertyType);

    // Parsed float cache sits right after m_strStreetBrawlValue (not in schema).
    float *GetParsedFloats() {
        static auto key = schema::GetOffset(m_className, m_classNameHash,
            "m_strStreetBrawlValue", hash_32_fnv1a_const("m_strStreetBrawlValue"));
        return reinterpret_cast<float *>(reinterpret_cast<uint8_t *>(this) + key.Offset + sizeof(CUtlString));
    }
};

// Fixed-size wrapper so CUtlOrderedMap node indexing works correctly.
// Access fields via As().m_strValue, As().GetParsedFloats(), etc.
struct CitadelAbilityProperty_t {
    uint8_t _data[0xb0];

    CitadelAbilityPropertyAccessor &As() {
        return *reinterpret_cast<CitadelAbilityPropertyAccessor *>(this);
    }
};
static_assert(sizeof(CitadelAbilityProperty_t) == 0xb0);

