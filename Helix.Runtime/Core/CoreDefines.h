#pragma once
#include <stdint.h>
#include <stddef.h>
#include "../HAL/IPlatformProvider.h"

#ifdef _MSC_VER
extern "C" void* _AddressOfReturnAddress(void);
#pragma intrinsic(_AddressOfReturnAddress)
#define HELIX_GET_FRAME() _AddressOfReturnAddress()
#else
#define HELIX_GET_FRAME() __builtin_frame_address(0)
#endif

namespace HelixRuntime {

    inline void HMemSet(void* dest, uint8_t val, size_t size) {
        uint8_t* d = (uint8_t*)dest;
        while (size--) *d++ = val;
    }
    inline void HMemCpy(void* dest, const void* src, size_t size) {
        uint8_t* d = (uint8_t*)dest;
        const uint8_t* s = (const uint8_t*)src;
        while (size--) *d++ = *s++;
    }

    inline void* OSAlloc(size_t size) {
        if (!HAL::GetPlatform()) return nullptr;
        return HAL::GetPlatform()->MemAlloc(size);
    }
    inline void OSFree(void* ptr) {
        if (ptr && HAL::GetPlatform()) HAL::GetPlatform()->MemFree(ptr);
    }

    class HMutex {
        void* handle;
    public:
        HMutex() { handle = HAL::GetPlatform()->CreateHMutex(); }
        ~HMutex() { HAL::GetPlatform()->DestroyHMutex(handle); }
        void Acquire(uintptr_t& state) { HAL::GetPlatform()->LockHMutex(handle, state); }
        void Release(uintptr_t state) { HAL::GetPlatform()->UnlockHMutex(handle, state); }
    };

    template <bool B, class T = void> struct enable_if {};
    template <class T> struct enable_if<true, T> { typedef T type; };
    struct false_type { static constexpr bool value = false; };
    struct true_type { static constexpr bool value = true; };
    template <class T> struct is_pointer : false_type {};
    template <class T> struct is_pointer<T*> : true_type {};
    template <class T> struct is_pointer<T* const> : true_type {};
    template <class T> struct is_pointer<T* volatile> : true_type {};
    template <class T> struct is_pointer<T* const volatile> : true_type {};

    template <typename T> struct remove_reference { typedef T type; };
    template <typename T> struct remove_reference<T&> { typedef T type; };
    template <typename T> struct remove_reference<T&&> { typedef T type; };

    template <typename T> constexpr T&& forward(typename remove_reference<T>::type& arg) noexcept { return static_cast<T&&>(arg); }
    template <typename T> constexpr T&& forward(typename remove_reference<T>::type&& arg) noexcept { return static_cast<T&&>(arg); }

    template <typename T> struct remove_cv { typedef T type; };
    template <typename T> struct remove_cv<const T> { typedef T type; };
    template <typename T> struct remove_cv<volatile T> { typedef T type; };
    template <typename T> struct remove_cv<const volatile T> { typedef T type; };
    template <typename T> struct decay { typedef typename remove_cv<typename remove_reference<T>::type>::type type; };
    template <typename T> using decay_t = typename decay<T>::type;

    template<typename U, size_t N>
    struct HArray {
        U data[N];
        operator U* () { return data; }
        operator const U* () const { return data; }
        operator void* () { return data; }
        U& operator[](size_t i) { return data[i]; }
        const U& operator[](size_t i) const { return data[i]; }
    };

    template<typename T> struct ReturnTypeTrait { using type = T; };
    template<typename U, size_t N> struct ReturnTypeTrait<U[N]> { using type = HArray<U, N>; };

    constexpr uint64_t ConstexprFnv1a(const char* str) {
        uint64_t hash = 14695981039346656037ull;
        for (size_t i = 0; str[i] != '\0'; ++i) {
            hash ^= static_cast<uint8_t>(str[i]);
            hash *= 1099511628211ull;
        }
        return hash;
    }

    template<typename T>
    constexpr uint64_t GetTypeHash() {
#ifdef _MSC_VER
        return ConstexprFnv1a(__FUNCSIG__);
#else
        return ConstexprFnv1a(__PRETTY_FUNCTION__);
#endif
    }

    template <typename T>
    inline T** GetInstancePtrRef() {
        static T* p = nullptr;
        return &p;
    }

    template <typename T>
    inline T& GetInstance() {
        T** pp = GetInstancePtrRef<T>();
        if (!*pp) {
            void* mem = OSAlloc(sizeof(T));
            if (mem) {
                HMemSet(mem, 0, sizeof(T));
                *pp = new(mem) T();
            }
        }
        return **pp;
    }

    template <typename T>
    inline void FreeInstance() {
        T** pp = GetInstancePtrRef<T>();
        if (*pp) {
            (*pp)->~T();
            OSFree(*pp);
            *pp = nullptr;
        }
    }
}

#ifdef _KERNEL_MODE
extern "C" __declspec(selectany) int _fltused = 0;
extern "C" inline int __cdecl atexit(void(__cdecl* func)(void)) { return 0; }

#pragma warning(push)
#pragma warning(disable: 4595)
inline void* __cdecl operator new(size_t size) { return HelixRuntime::OSAlloc(size); }
inline void* __cdecl operator new[](size_t size) { return HelixRuntime::OSAlloc(size); }
inline void __cdecl operator delete(void* p) { HelixRuntime::OSFree(p); }
inline void __cdecl operator delete[](void* p) { HelixRuntime::OSFree(p); }
inline void __cdecl operator delete(void* p, size_t) { HelixRuntime::OSFree(p); }
inline void __cdecl operator delete[](void* p, size_t) { HelixRuntime::OSFree(p); }
#pragma warning(pop)
#endif

#if !defined(__PLACEMENT_NEW_INLINE) && !defined(_NEW_) && !defined(_NEW)
#define __PLACEMENT_NEW_INLINE
inline void* __cdecl operator new(size_t, void* p) noexcept { return p; }
inline void __cdecl operator delete(void*, void*) noexcept {}
#endif