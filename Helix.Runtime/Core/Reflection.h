#pragma once
#include "GC.h"

namespace HelixRuntime {
    template<typename T>
    class BoundMeta {
        T* instance; ClassMeta& meta;
    public:
        const HashStringProxy className; const HVector<FieldMeta>& Fields; const HVector<MethodMeta>& Methods;
        BoundMeta(T* inst, ClassMeta& m) : instance(inst), meta(m), className{ m.classHash }, Fields(m.Fields), Methods(m.Methods) {}

        template<typename V> void SetValue(const char* name, V&& value) {
            uint64_t targetHash = ConstexprFnv1a(name);
            for (const auto& field : Fields) {
                if (field.nameHash == targetHash) { *(decay_t<V>*)((char*)instance + field.offset) = static_cast<V&&>(value); return; }
            }
        }
        template<typename V> V GetValue(const char* name) {
            uint64_t targetHash = ConstexprFnv1a(name);
            for (const auto& field : Fields) {
                if (field.nameHash == targetHash) return *(V*)((char*)instance + field.offset);
            }
            return V();
        }
        template<typename Ret = void, typename... Args> Ret Invoke(const char* name, Args... args) {
            uint64_t targetHash = ConstexprFnv1a(name);
            for (const auto& method : Methods) {
                if (method.nameHash == targetHash) {
                    using FuncType = Ret(T::*)(Args...); FuncType func;
                    HMemCpy(&func, method.methodPtr, sizeof(FuncType));
                    return (instance->*func)(static_cast<Args&&>(args)...);
                }
            }
            return Ret();
        }
    };

    template <typename> struct is_helix_ref : false_type {};
    template <typename T> struct is_helix_ref<HelixRef<T>> : true_type {};

    template<typename T> BoundMeta<T> GetMeta(HelixRef<T>& obj) { return BoundMeta<T>(&(*obj), GetRegistry()[GetTypeHash<T>()]); }
    template<typename T> BoundMeta<T> GetMeta(const HelixRef<T>& obj) { return BoundMeta<T>(&(*obj), GetRegistry()[GetTypeHash<T>()]); }
    template<typename T> BoundMeta<T> GetMeta(T* obj) { return BoundMeta<T>(obj, GetRegistry()[GetTypeHash<T>()]); }
    template<typename T, typename enable_if<!is_pointer<T>::value && !is_helix_ref<T>::value, int>::type = 0>
    BoundMeta<T> GetMeta(T& obj) { return BoundMeta<T>(&obj, GetRegistry()[GetTypeHash<T>()]); }

    template<typename T> HVector<uint8_t> DumpMemory(void* ptr) {
        HVector<uint8_t> buffer; if (!ptr) return buffer;
        buffer.reserve(sizeof(T));
        uint8_t* raw = (uint8_t*)ptr;
        for (size_t i = 0; i < sizeof(T); i++) buffer.push_back(raw[i]);
        return buffer;
    }
    template<typename T> HVector<uint8_t> DumpMemory(HelixRef<T>& obj) {
        HVector<uint8_t> buffer; if (!(&obj) || !(&*obj)) return buffer;
        buffer.reserve(sizeof(T));
        uint8_t* raw = (uint8_t*)(&(*obj));
        for (size_t i = 0; i < sizeof(T); i++) buffer.push_back(raw[i]);
        return buffer;
    }
    template<typename T, typename enable_if<!is_pointer<T>::value && !is_helix_ref<T>::value, int>::type = 0>
    HVector<uint8_t> DumpMemory(const T& obj) {
        HVector<uint8_t> buffer;
        buffer.reserve(sizeof(T));
        uint8_t* raw = (uint8_t*)&obj;
        for (size_t i = 0; i < sizeof(T); i++) buffer.push_back(raw[i]);
        return buffer;
    }

    template<typename T> typename ReturnTypeTrait<T>::type LoadMemory(const HVector<uint8_t>& data) {
        typename ReturnTypeTrait<T>::type obj; HMemSet(&obj, 0, sizeof(obj));
        if (data.size() >= sizeof(obj)) HMemCpy(&obj, &data[0], sizeof(obj));
        return obj;
    }
}