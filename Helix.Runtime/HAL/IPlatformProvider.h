#pragma once
#include <stddef.h>
#include <stdint.h>

namespace HelixRuntime {
    namespace HAL {
        struct CpuInfo { char Vendor[16]; char Brand[64]; char ModelName[64]; char SerialNumber[64]; };
        struct SystemInfo { uint32_t MajorVersion; uint32_t MinorVersion; uint32_t BuildNumber; char OSName[64]; char SerialNumber[64]; };
        struct MacInfo { char MacAddress[20]; char Description[128]; };
        struct DiskInfo { char DriveLetter[8]; uint64_t TotalSize; uint64_t FreeSpace; char Model[64]; char SerialNumber[64]; };

        class IPlatformProvider {
        public:
            virtual ~IPlatformProvider() = default;

            virtual void* MemAlloc(size_t size) = 0;
            virtual void MemFree(void* ptr) = 0;

            virtual uint64_t GetSystemThreadId() = 0;

            virtual void* CreateHMutex() = 0;
            virtual void LockHMutex(void* mutex, uintptr_t& state) = 0;
            virtual void UnlockHMutex(void* mutex, uintptr_t state) = 0;
            virtual void DestroyHMutex(void* mutex) = 0;

            virtual void* GetThreadStackBase() = 0;
            virtual void InstallHook(void* target, void* detour) = 0;

            virtual void* GetHModuleBase(const char* moduleName, void* context = nullptr) = 0;
            virtual void* GetHExportAddress(void* moduleBase, const char* funcName) = 0;

            virtual void* AllocRemote(uint32_t pid, size_t size) = 0;
            virtual void FreeRemote(uint32_t pid, void* ptr) = 0;
            virtual int ReadRemoteRaw(uint32_t pid, void* address, void* buffer, size_t size) = 0;
            virtual int WriteRemoteRaw(uint32_t pid, void* address, void* buffer, size_t size) = 0;

            virtual void QuerySystemInfo(SystemInfo* outInfo) = 0;
            virtual void QueryCpuInfo(CpuInfo* outInfo) = 0;
            virtual void QueryMacInfo(void (*callback)(const MacInfo&, void*), void* context) = 0;
            virtual void QueryDiskInfo(const char* driveLetter, void (*callback)(const DiskInfo&, void*), void* context) = 0;

            virtual void LogPrint(const char* format, ...) = 0;
        };

        inline IPlatformProvider** GetPlatformProviderRef() {
            static IPlatformProvider* provider = nullptr;
            return &provider;
        }

        inline IPlatformProvider* GetPlatform() { return *GetPlatformProviderRef(); }
        inline void SetPlatform(IPlatformProvider* provider) { *GetPlatformProviderRef() = provider; }
    }
}