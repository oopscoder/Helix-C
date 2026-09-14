#pragma once
#include "ReflectionTypes.h"

namespace HelixRuntime {
    struct GCObject {
        void* ptr; uint64_t typeHash; bool marked; bool immortal; bool isRemote;
        uint32_t targetPid; void* stackFrame; void (*dtor)(void*, uint32_t);
    };

    struct LocalContext {
        HVector<GCObject> heap;
        HMap<void*, int> roots;
        HMutex lock;
        inline void Lock(uintptr_t& s) { lock.Acquire(s); }
        inline void Unlock(uintptr_t s) { lock.Release(s); }
    };

    inline LocalContext** GetShardsPtrRef() { static LocalContext* p = nullptr; return &p; }

    inline LocalContext* GetShards() {
        LocalContext** pp = GetShardsPtrRef();
        if (!*pp) {
            *pp = (LocalContext*)OSAlloc(sizeof(LocalContext) * 256);
            for (int i = 0; i < 256; i++) new(&(*pp)[i]) LocalContext();
        }
        return *pp;
    }

    inline LocalContext& GetLocalContext() {
        uint64_t tid = HAL::GetPlatform()->GetSystemThreadId();
        return GetShards()[(tid ^ (tid >> 8)) & 0xFF];
    }

    class GC {
    public:
        struct GlobalHeap : HVector<GCObject> {};
        struct GlobalRoots : HVector<void**> {};
        struct ActiveExterns : HVector<void**> {};
        struct GlobalLock : HMutex {};

        static GlobalHeap& globalHeap() { return GetInstance<GlobalHeap>(); }
        static GlobalRoots& globalRoots() { return GetInstance<GlobalRoots>(); }
        static ActiveExterns& activeExterns() { return GetInstance<ActiveExterns>(); }
        static GlobalLock& globalLock() { return GetInstance<GlobalLock>(); }

        static void AddRoot(void* ptr) {
            if (!ptr) return;
            auto& ctx = GetLocalContext();
            uintptr_t state; ctx.Lock(state); ctx.roots[ptr]++; ctx.Unlock(state);
        }
        static void RemoveRoot(void* ptr) {
            if (!ptr) return;
            auto& ctx = GetLocalContext();
            uintptr_t state; ctx.Lock(state);
            auto it = ctx.roots.find(ptr);
            if (it != ctx.roots.end()) { it->second--; if (it->second <= 0) ctx.roots.erase(ptr); }
            ctx.Unlock(state);
        }
        static void RegisterGlobalRoot(void** addr) {
            if (!addr) return;
            uintptr_t state; globalLock().Acquire(state); globalRoots().push_back(addr); globalLock().Release(state);
        }

        static void PromoteToGlobal(void* ptr) {
            if (!ptr) return;
            auto& ctx = GetLocalContext();
            uintptr_t lState; ctx.Lock(lState);
            for (auto it = ctx.heap.begin(); it != ctx.heap.end(); ++it) {
                if (it->ptr == ptr) {
                    it->immortal = true; GCObject obj = *it; ctx.heap.erase(it); ctx.Unlock(lState);
                    uintptr_t gState; globalLock().Acquire(gState); globalHeap().push_back(obj); globalLock().Release(gState);
                    if (!obj.isRemote) {
                        auto metaIt = GetRegistry().find(obj.typeHash);
                        if (metaIt != GetRegistry().end()) {
                            for (const auto& field : metaIt->second.Fields) {
                                if (!field.isPointer) continue;
                                void* childPtr = *(void**)((char*)ptr + field.offset);
                                PromoteToGlobal(childPtr);
                            }
                        }
                    }
                    return;
                }
            }
            ctx.Unlock(lState);
            uintptr_t gState2; globalLock().Acquire(gState2);
            for (auto& obj : globalHeap()) { if (obj.ptr == ptr) { obj.immortal = true; break; } }
            globalLock().Release(gState2);
        }

        static void MakeImmortal(void* ptr) { PromoteToGlobal(ptr); }

        static void MarkLocal(void* ptr, LocalContext& ctx) {
            if (!ptr) return;
            for (auto& obj : ctx.heap) {
                if (obj.ptr == ptr) {
                    if (obj.marked) return;
                    obj.marked = true;
                    if (obj.isRemote) return;
                    auto metaIt = GetRegistry().find(obj.typeHash);
                    if (metaIt != GetRegistry().end()) {
                        for (const auto& field : metaIt->second.Fields) {
                            if (!field.isPointer) continue;
                            void* childPtr = *(void**)((char*)ptr + field.offset);
                            MarkLocal(childPtr, ctx);
                        }
                    }
                    break;
                }
            }
        }

        static void CollectLocal() {
            auto& ctx = GetLocalContext();
            uintptr_t state; ctx.Lock(state);
            for (auto& obj : ctx.heap) obj.marked = false;
            for (auto& pair : ctx.roots) MarkLocal((void*)pair.first, ctx);

            uintptr_t gState; globalLock().Acquire(gState);
            for (void** extPtr : activeExterns()) { if (extPtr && *extPtr) MarkLocal(*extPtr, ctx); }
            for (void** gRoot : globalRoots()) { if (gRoot && *gRoot) MarkLocal(*gRoot, ctx); }
            globalLock().Release(gState);

            void** scanStart = (void**)HELIX_GET_FRAME();
            void** scanEnd = (void**)HAL::GetPlatform()->GetThreadStackBase();

            if (scanStart < scanEnd) {
                size_t maxScanWords = 8192;
                size_t words = scanEnd - scanStart;
                if (words > maxScanWords) scanEnd = scanStart + maxScanWords;
                for (void** p = scanStart; p < scanEnd; ++p) {
                    void* val = *p;
                    if (val) {
                        for (auto& obj : ctx.heap) {
                            if (!obj.marked && obj.ptr == val) MarkLocal(obj.ptr, ctx);
                        }
                    }
                }
            }

            for (auto it = ctx.heap.begin(); it != ctx.heap.end(); ) {
                if (!it->marked && !it->immortal) {
                    if (it->isRemote && it->stackFrame) {
                        if ((uintptr_t)scanStart < (uintptr_t)it->stackFrame) { ++it; continue; }
                    }
                    it->dtor(it->ptr, it->targetPid); it = ctx.heap.erase(it);
                }
                else { ++it; }
            }
            ctx.Unlock(state);
        }

        static void Shutdown() {
            LocalContext** ppShards = GetShardsPtrRef();
            if (*ppShards) {
                for (int i = 0; i < 256; i++) {
                    uintptr_t s; (*ppShards)[i].Lock(s);
                    for (auto& obj : (*ppShards)[i].heap) if (obj.ptr) obj.dtor(obj.ptr, obj.targetPid);
                    (*ppShards)[i].heap.clear(); (*ppShards)[i].roots.clear();
                    (*ppShards)[i].Unlock(s); (*ppShards)[i].~LocalContext();
                }
                OSFree(*ppShards); *ppShards = nullptr;
            }
            uintptr_t state; globalLock().Acquire(state);
            globalRoots().clear(); activeExterns().clear();
            for (auto& obj : globalHeap()) { if (obj.ptr) obj.dtor(obj.ptr, obj.targetPid); }
            globalHeap().clear(); globalLock().Release(state);

            FreeInstance<GlobalHeap>(); FreeInstance<GlobalRoots>();
            FreeInstance<ActiveExterns>(); FreeInstance<GlobalLock>();
            FreeInstance<MetaRegistry>();
        }
    };

    template<typename T>
    struct ExternParamGuard {
        T ptr;
        ExternParamGuard(T p) : ptr(p) {
            if (ptr) {
                uintptr_t state; GC::globalLock().Acquire(state);
                GC::activeExterns().push_back((void**)ptr); GC::globalLock().Release(state);
            }
        }
        ~ExternParamGuard() {
            if (ptr) {
                uintptr_t state; GC::globalLock().Acquire(state);
                if (*ptr) {
                    GC::globalLock().Release(state); GC::PromoteToGlobal((void*)*ptr); GC::globalLock().Acquire(state);
                }
                for (auto it = GC::activeExterns().begin(); it != GC::activeExterns().end(); ++it) {
                    if (*it == (void**)ptr) { GC::activeExterns().erase(it); break; }
                }
                GC::globalLock().Release(state);
            }
        }
    };

    template<typename T>
    class HelixRef {
        T* ptr;
    public:
        HelixRef(T* p = nullptr) : ptr(p) { GC::AddRoot(ptr); }
        HelixRef(const HelixRef& other) : ptr(other.ptr) { GC::AddRoot(ptr); }
        HelixRef& operator=(const HelixRef& other) {
            if (ptr != other.ptr) { GC::RemoveRoot(ptr); ptr = other.ptr; GC::AddRoot(ptr); }
            return *this;
        }
        ~HelixRef() { GC::RemoveRoot(ptr); GC::CollectLocal(); }
        T* operator->() const { return ptr; }
        T& operator*() const { return *ptr; }
        T* operator&() const { return ptr; }
        operator T* () const { return ptr; }
        operator intptr_t() const { return (intptr_t)ptr; }
        operator uint64_t() const { return (uint64_t)ptr; }
        operator uint32_t() const { return (uint32_t)(uintptr_t)ptr; }
        operator int() const { return (int)(intptr_t)ptr; }
        operator void* () const { return (void*)ptr; }
        bool operator!=(decltype(nullptr)) const { return ptr != nullptr; }
        bool operator==(decltype(nullptr)) const { return ptr == nullptr; }
    };

    template<>
    class HelixRef<void*> {
        void* ptr;
    public:
        HelixRef(void* p = nullptr) : ptr(p) { GC::AddRoot(ptr); }
        HelixRef(const HelixRef<void*>& other) : ptr(other.ptr) { GC::AddRoot(ptr); }
        HelixRef& operator=(const HelixRef<void*>& other) {
            if (ptr != other.ptr) { GC::RemoveRoot(ptr); ptr = other.ptr; GC::AddRoot(ptr); }
            return *this;
        }
        ~HelixRef() { GC::RemoveRoot(ptr); GC::CollectLocal(); }
        template<typename U> operator U* () const { return (U*)ptr; }
        operator void* () const { return ptr; }
        operator intptr_t() const { return (intptr_t)ptr; }
        operator uint64_t() const { return (uint64_t)ptr; }
        operator uint32_t() const { return (uint32_t)(uintptr_t)ptr; }
        operator int() const { return (int)(intptr_t)ptr; }
        bool operator!=(decltype(nullptr)) const { return ptr != nullptr; }
        bool operator==(decltype(nullptr)) const { return ptr == nullptr; }
    };

    template<typename T, typename... Args> HelixRef<T> GCAllocate(Args&&... args) {
        void* mem = OSAlloc(sizeof(T));
        if (!mem) return HelixRef<T>(nullptr);
        HelixRuntime::HMemSet(mem, 0, sizeof(T));
        T* obj = new(mem) T(static_cast<Args&&>(args)...);
        auto& ctx = GetLocalContext();
        uintptr_t state; ctx.Lock(state);
        ctx.heap.push_back({ obj, GetTypeHash<T>(), false, false, false, 0, nullptr, [](void* p, uint32_t) { static_cast<T*>(p)->~T(); OSFree(p); } });
        ctx.Unlock(state);
        return HelixRef<T>(obj);
    }
}