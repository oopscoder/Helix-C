#pragma once
#include "CoreDefines.h"

namespace HelixRuntime {
    namespace VmpCore {
        template<typename T> inline void* GetMethodAddress(T func) {
            union { T f; void* p; } u; u.p = nullptr; u.f = func; return u.p;
        }
        inline void InstallHook(void* target, void* detour) {
            HAL::GetPlatform()->InstallHook(target, detour);
        }
    }
}