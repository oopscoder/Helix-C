#pragma once
#include "Collections.h"

namespace HelixRuntime {
    struct HashStringProxy {
        uint64_t hash;

        bool operator==(const char* str) const { return hash == ConstexprFnv1a(str); }
        bool operator==(const HString& str) const { return hash == ConstexprFnv1a(str.c_str()); }
    };

    struct FieldMeta { uint64_t nameHash; uint64_t typeHash; size_t offset; bool isExtern; bool isPointer; };

    struct MethodMeta { uint64_t nameHash; void* methodPtr; };

    struct ClassMeta { uint64_t classHash; HVector<FieldMeta> Fields; HVector<MethodMeta> Methods; };

    struct MetaRegistry : HMap<uint64_t, ClassMeta> {};
    inline MetaRegistry& GetRegistry() { return GetInstance<MetaRegistry>(); }
    inline void RegisterMeta(uint64_t typeHash, const ClassMeta& meta) { GetRegistry()[typeHash] = meta; }
}