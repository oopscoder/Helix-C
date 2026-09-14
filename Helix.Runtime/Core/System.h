#pragma once
#include "GC.h"

namespace HelixRuntime {
    namespace HelixSystem {
        using CpuInfo = HAL::CpuInfo;
        using SystemInfo = HAL::SystemInfo;
        using MacInfo = HAL::MacInfo;
        using DiskInfo = HAL::DiskInfo;

        inline HelixRef<void*> AllocRemote(uint32_t pid, size_t size, void* frame) {
            if (size == 0) return HelixRef<void*>(nullptr);
            void* address = HAL::GetPlatform()->AllocRemote(pid, size);
            if (address) {
                auto& ctx = GetLocalContext(); uintptr_t state; ctx.Lock(state);
                ctx.heap.push_back({ address, 0, false, false, true, pid, frame, [](void* p, uint32_t targetPid) {
                    if (p && targetPid > 0) HAL::GetPlatform()->FreeRemote(targetPid, p);
                } });
                ctx.Unlock(state);
            }
            return HelixRef<void*>(address);
        }

        template<typename T>
        inline typename ReturnTypeTrait<T>::type Read(uint32_t pid, void* address) {
            typename ReturnTypeTrait<T>::type result;
            HMemSet(&result, 0, sizeof(result));
            HAL::GetPlatform()->ReadRemoteRaw(pid, address, &result, sizeof(result));
            return result;
        }

        inline int WriteRaw(uint32_t pid, void* address, void* buffer, size_t size) {
            return HAL::GetPlatform()->WriteRemoteRaw(pid, address, buffer, size);
        }
        inline int Write(uint32_t pid, void* address, void* buffer, size_t size) { return WriteRaw(pid, address, buffer, size); }
        template<typename T> inline int Write(uint32_t pid, void* address, void* buffer) { return WriteRaw(pid, address, buffer, sizeof(T)); }
        template<typename T, typename enable_if<!is_pointer<T>::value && !is_helix_ref<T>::value, int>::type = 0>
        inline int Write(uint32_t pid, void* address, const T& data) { return WriteRaw(pid, address, (void*)&data, sizeof(T)); }

        inline HelixRef<SystemInfo> System() {
            auto info = GCAllocate<SystemInfo>();
            HAL::GetPlatform()->QuerySystemInfo(&(*info));
            return info;
        }

        inline HelixRef<CpuInfo> Cpu() {
            auto info = GCAllocate<CpuInfo>();
            HAL::GetPlatform()->QueryCpuInfo(&(*info));
            return info;
        }

        inline HelixRef<HVector<MacInfo>> Mac() {
            auto list = GCAllocate<HVector<MacInfo>>();
            auto cb = [](const MacInfo& m, void* ctx) { ((HVector<MacInfo>*)ctx)->push_back(m); };
            HAL::GetPlatform()->QueryMacInfo(cb, &(*list));
            if (list->size() == 0) {
                MacInfo m; HMemSet(&m, 0, sizeof(m));
                const char* defMac = "00:1A:2B:3C:4D:5E"; for (int i = 0; defMac[i]; i++) m.MacAddress[i] = defMac[i];
                const char* defDesc = "Helix Virtual Adapter"; for (int i = 0; defDesc[i]; i++) m.Description[i] = defDesc[i];
                list->push_back(m);
            }
            return list;
        }

        inline HelixRef<HVector<DiskInfo>> Disk(const char* driveLetter = nullptr) {
            auto list = GCAllocate<HVector<DiskInfo>>();
            auto cb = [](const DiskInfo& d, void* ctx) { ((HVector<DiskInfo>*)ctx)->push_back(d); };
            HAL::GetPlatform()->QueryDiskInfo(driveLetter, cb, &(*list));
            if (list->size() == 0) {
                DiskInfo d; HMemSet(&d, 0, sizeof(d));
                d.DriveLetter[0] = driveLetter && driveLetter[0] ? driveLetter[0] : 'C';
                d.DriveLetter[1] = ':'; d.DriveLetter[2] = '\\'; d.TotalSize = 500ULL << 30; d.FreeSpace = 250ULL << 30;
                const char* dModel = "Helix Virtual Storage"; for (int i = 0; dModel[i]; i++) d.Model[i] = dModel[i];
                const char* fallback = "00000000"; for (int i = 0; fallback[i]; i++) d.SerialNumber[i] = fallback[i];
                list->push_back(d);
            }
            return list;
        }
    }
}