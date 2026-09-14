#pragma once
#ifndef NOMINMAX
#define NOMINMAX
#endif
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#if !defined(_KERNEL_MODE)
#include <Windows.h> // Helix Auto-Wrapped
#endif
#include <intrin.h>
#include <mutex>
#include <cstdarg>
#include <cstdio>
#include "../IPlatformProvider.h"

namespace HelixRuntime {
    namespace HAL {

        class WinUserProvider : public IPlatformProvider {
        private:
            static bool StrCmp(const char* s1, const char* s2) {
                while (*s1 && *s2) { if (*s1 != *s2) return false; s1++; s2++; }
                return *s1 == *s2;
            }

            void* ParseEAT(void* moduleBase, const char* funcName) {
                if (!moduleBase) return nullptr;
                uint8_t* base = (uint8_t*)moduleBase;
                uint32_t peOffset = *(uint32_t*)(base + 0x3C);
                uint8_t* ntHeaders = base + peOffset;

                uint16_t magic = *(uint16_t*)(ntHeaders + 0x18);
                uint32_t exportDirRva = 0;
                uint32_t exportDirSize = 0;
                if (magic == 0x20B) {
                    exportDirRva = *(uint32_t*)(ntHeaders + 0x88);
                    exportDirSize = *(uint32_t*)(ntHeaders + 0x8C);
                }
                else if (magic == 0x10B) {
                    exportDirRva = *(uint32_t*)(ntHeaders + 0x78);
                    exportDirSize = *(uint32_t*)(ntHeaders + 0x7C);
                }
                if (exportDirRva == 0) return nullptr;

                uint8_t* exportDir = base + exportDirRva;
                uint32_t numberOfNames = *(uint32_t*)(exportDir + 0x18);
                uint32_t* addressOfFunctions = (uint32_t*)(base + *(uint32_t*)(exportDir + 0x1C));
                uint32_t* addressOfNames = (uint32_t*)(base + *(uint32_t*)(exportDir + 0x20));
                uint16_t* addressOfNameOrdinals = (uint16_t*)(base + *(uint32_t*)(exportDir + 0x24));

                uint32_t funcRva = 0;

                if ((uintptr_t)funcName <= 0xFFFF) {
                    uint32_t ordinalBase = *(uint32_t*)(exportDir + 0x10);
                    uint32_t ordinal = (uint32_t)(uintptr_t)funcName;
                    if (ordinal < ordinalBase) return nullptr;
                    uint32_t index = ordinal - ordinalBase;
                    uint32_t numberOfFunctions = *(uint32_t*)(exportDir + 0x14);
                    if (index < numberOfFunctions) funcRva = addressOfFunctions[index];
                }
                else {
                    for (uint32_t i = 0; i < numberOfNames; i++) {
                        char* name = (char*)(base + addressOfNames[i]);
                        if (StrCmp(name, funcName)) {
                            uint16_t ordinal = addressOfNameOrdinals[i];
                            funcRva = addressOfFunctions[ordinal];
                            break;
                        }
                    }
                }

                if (funcRva == 0) return nullptr;

                if (funcRva >= exportDirRva && funcRva < exportDirRva + exportDirSize) {
                    char fwd[256] = { 0 };
                    char* fwdPtr = (char*)(base + funcRva);
                    int i = 0; while (fwdPtr[i] && i < 255) { fwd[i] = fwdPtr[i]; i++; }
                    char* dot = nullptr;
                    for (int j = 0; j < i; j++) if (fwd[j] == '.') { dot = &fwd[j]; break; }
                    if (dot) {
                        *dot = '\0';
                        char dllName[256];
                        int k = 0; while (fwd[k]) { dllName[k] = fwd[k]; k++; }
                        dllName[k++] = '.'; dllName[k++] = 'd'; dllName[k++] = 'l'; dllName[k++] = 'l'; dllName[k] = '\0';
                        void* hMod = GetHModuleBase(dllName, nullptr);
                        if (hMod) return ParseEAT(hMod, dot + 1);
                    }
                    return nullptr;
                }

                return base + funcRva;
            }

            bool GetIndirectSyscallStub(const char* funcName, unsigned char* outStub) {
                HMODULE hNtdll = GetModuleHandleA("ntdll.dll");
                if (!hNtdll) return false;
                unsigned char* funcAddr = (unsigned char*)GetProcAddress(hNtdll, funcName);
                if (!funcAddr) return false;

                uint32_t ssn = 0;
                bool found = false;

                for (int idx = 0; idx < 500; idx++) {
                    unsigned char* pDown = funcAddr + idx * 32;
                    if (pDown[0] == 0x4C && pDown[1] == 0x8B && pDown[2] == 0xD1 && pDown[3] == 0xB8) {
                        ssn = *(uint32_t*)(&pDown[4]) - idx;
                        found = true; break;
                    }
                    unsigned char* pUp = funcAddr - idx * 32;
                    if (pUp[0] == 0x4C && pUp[1] == 0x8B && pUp[2] == 0xD1 && pUp[3] == 0xB8) {
                        ssn = *(uint32_t*)(&pUp[4]) + idx;
                        found = true; break;
                    }
                }
                if (!found) return false;

                uintptr_t gadget = 0;
                for (int i = 0; i < 0x1000; i++) {
                    if (funcAddr[i] == 0x0F && funcAddr[i + 1] == 0x05 && funcAddr[i + 2] == 0xC3) {
                        gadget = (uintptr_t)&funcAddr[i]; break;
                    }
                }
                if (!gadget) return false;

                outStub[0] = 0x4C; outStub[1] = 0x8B; outStub[2] = 0xD1;
                outStub[3] = 0xB8; *(uint32_t*)(&outStub[4]) = ssn;
                outStub[8] = 0x49; outStub[9] = 0xBB; *(uintptr_t*)(&outStub[10]) = gadget;
                outStub[18] = 0x41; outStub[19] = 0xFF; outStub[20] = 0xE3;
                return true;
            }

        public:
            void* MemAlloc(size_t size) override { return std::malloc(size); }
            void MemFree(void* ptr) override { std::free(ptr); }

            uint64_t GetSystemThreadId() override { return ::GetCurrentThreadId(); }

            void* CreateHMutex() override { return new std::mutex(); }
            void LockHMutex(void* mutex, uintptr_t& state) override { ((std::mutex*)mutex)->lock(); }
            void UnlockHMutex(void* mutex, uintptr_t state) override { ((std::mutex*)mutex)->unlock(); }
            void DestroyHMutex(void* mutex) override { delete (std::mutex*)mutex; }

            void* GetThreadStackBase() override {
#ifdef _M_X64
                return (void*)__readgsqword(0x08);
#else
                return (void*)__readfsdword(0x04);
#endif
            }

            void InstallHook(void* target, void* detour) override {
                if (!target || !detour) return;
                uint8_t* p = (uint8_t*)target;
                if (p[0] == 0xE9) { target = (void*)(p + 5 + *(int32_t*)&p[1]); p = (uint8_t*)target; }
                DWORD oldProtect; VirtualProtect(target, 5, PAGE_EXECUTE_READWRITE, &oldProtect);
                p[0] = 0xE9; *(int32_t*)(&p[1]) = (int32_t)((intptr_t)detour - (intptr_t)target - 5);
                VirtualProtect(target, 5, oldProtect, &oldProtect);
            }

            void* GetHModuleBase(const char* moduleName, void* context = nullptr) override {
                if (StrCmp(moduleName, "ntoskrnl.exe") || StrCmp(moduleName, "hal.dll")) return nullptr;
                HMODULE hMod = GetModuleHandleA(moduleName);
                if (!hMod) hMod = LoadLibraryA(moduleName);
                return (void*)hMod;
            }

            void* GetHExportAddress(void* moduleBase, const char* funcName) override {
                return ParseEAT(moduleBase, funcName);
            }

            void* AllocRemote(uint32_t pid, size_t size) override {
                if (pid == (uint32_t)-1 || pid == ::GetCurrentProcessId()) {
                    return VirtualAlloc(nullptr, size, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
                }

                void* address = nullptr;
                HANDLE hProcess = OpenProcess(PROCESS_ALL_ACCESS, FALSE, pid);
                if (hProcess) {
                    unsigned char stub[21];
                    if (GetIndirectSyscallStub("NtAllocateVirtualMemory", stub)) {
                        void* mem = VirtualAlloc(nullptr, sizeof(stub), MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
                        if (mem) {
                            memcpy(mem, stub, sizeof(stub));
                            DWORD oldProtect = 0; VirtualProtect(mem, sizeof(stub), PAGE_EXECUTE_READ, &oldProtect);
                            using NtAllocFn = long(__stdcall*)(void*, void**, size_t, size_t*, ULONG, ULONG);
                            size_t allocSize = size;
                            ((NtAllocFn)mem)((void*)hProcess, &address, 0, &allocSize, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
                            VirtualFree(mem, 0, MEM_RELEASE);
                        }
                    }
                    if (!address) address = VirtualAllocEx(hProcess, nullptr, size, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
                    CloseHandle(hProcess);
                }
                return address;
            }

            void FreeRemote(uint32_t pid, void* ptr) override {
                if (pid == (uint32_t)-1 || pid == ::GetCurrentProcessId()) {
                    VirtualFree(ptr, 0, MEM_RELEASE);
                    return;
                }

                HANDLE tHandle = OpenProcess(PROCESS_ALL_ACCESS, FALSE, pid);
                if (tHandle) {
                    VirtualFreeEx(tHandle, ptr, 0, MEM_RELEASE);
                    CloseHandle(tHandle);
                }
            }

            int ReadRemoteRaw(uint32_t pid, void* address, void* buffer, size_t size) override {
                if (pid == (uint32_t)-1 || pid == ::GetCurrentProcessId()) {
                    __try {
                        std::memcpy(buffer, address, size);
                        return 1;
                    }
                    __except (EXCEPTION_EXECUTE_HANDLER) {
                        return 0;
                    }
                }

                int status = 0;
                HANDLE hProcess = OpenProcess(PROCESS_ALL_ACCESS, FALSE, pid);
                if (hProcess) {
                    __try {
                        unsigned char stub[21]; bool success = false;
                        if (GetIndirectSyscallStub("NtReadVirtualMemory", stub)) {
                            void* mem = VirtualAlloc(nullptr, sizeof(stub), MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
                            if (mem) {
                                memcpy(mem, stub, sizeof(stub));
                                DWORD oldProtect = 0; VirtualProtect(mem, sizeof(stub), PAGE_EXECUTE_READ, &oldProtect);
                                using NtReadVirtualMemoryFn = long(__stdcall*)(void*, void*, void*, size_t, size_t*);
                                size_t bytesRead = 0;
                                if (((NtReadVirtualMemoryFn)mem)((void*)hProcess, address, buffer, size, &bytesRead) >= 0) {
                                    success = true; status = 1;
                                }
                                VirtualFree(mem, 0, MEM_RELEASE);
                            }
                        }
                        if (!success) {
                            SIZE_T bytesRead = 0;
                            if (ReadProcessMemory(hProcess, address, buffer, size, &bytesRead)) status = 1;
                        }
                    }
                    __except (EXCEPTION_EXECUTE_HANDLER) {
                        status = 0; 
                    }
                    CloseHandle(hProcess); 
                }
                return status;
            }

            int WriteRemoteRaw(uint32_t pid, void* address, void* buffer, size_t size) override {
                if (pid == (uint32_t)-1 || pid == ::GetCurrentProcessId()) {
                    __try {
                        std::memcpy(address, buffer, size);
                        return 1;
                    }
                    __except (EXCEPTION_EXECUTE_HANDLER) {
                        return 0;
                    }
                }

                int status = 0;
                HANDLE hProcess = OpenProcess(PROCESS_ALL_ACCESS, FALSE, pid);
                if (hProcess) {
                    __try {
                        unsigned char stub[21]; bool success = false;
                        if (GetIndirectSyscallStub("NtWriteVirtualMemory", stub)) {
                            void* mem = VirtualAlloc(nullptr, sizeof(stub), MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
                            if (mem) {
                                memcpy(mem, stub, sizeof(stub));
                                DWORD oldProtect = 0; VirtualProtect(mem, sizeof(stub), PAGE_EXECUTE_READ, &oldProtect);
                                using NtWriteVirtualMemoryFn = long(__stdcall*)(void*, void*, void*, size_t, size_t*);
                                size_t bytesWritten = 0;
                                if (((NtWriteVirtualMemoryFn)mem)((void*)hProcess, address, buffer, size, &bytesWritten) >= 0) {
                                    success = true; status = 1;
                                }
                                VirtualFree(mem, 0, MEM_RELEASE);
                            }
                        }
                        if (!success) {
                            SIZE_T bytesWritten = 0;
                            if (WriteProcessMemory(hProcess, address, buffer, size, &bytesWritten)) status = 1;
                        }
                    }
                    __except (EXCEPTION_EXECUTE_HANDLER) {
                        status = 0;
                    }
                    CloseHandle(hProcess);
                }
                return status;
            }

            void QuerySystemInfo(SystemInfo* outInfo) override {
                uint8_t* peb = (uint8_t*)__readgsqword(0x60);
                outInfo->MajorVersion = *(uint32_t*)(peb + 0x118);
                outInfo->MinorVersion = *(uint32_t*)(peb + 0x11C);
                outInfo->BuildNumber = *(uint32_t*)(peb + 0x120);

                HKEY hKey;
                if (RegOpenKeyExA(HKEY_LOCAL_MACHINE, "SOFTWARE\\Microsoft\\Cryptography", 0, KEY_READ | KEY_WOW64_64KEY, &hKey) == ERROR_SUCCESS) {
                    DWORD type; DWORD size = 63;
                    RegQueryValueExA(hKey, "MachineGuid", NULL, &type, (unsigned char*)outInfo->SerialNumber, &size);
                    RegCloseKey(hKey);
                }

                const char* osName = "Windows (Unknown) x64";
                if (outInfo->BuildNumber >= 22000) osName = "Windows 11 x64";
                else if (outInfo->BuildNumber >= 19041) osName = "Windows 10 x64";
                else if (outInfo->BuildNumber >= 9200) osName = "Windows 8.1 x64";
                else if (outInfo->BuildNumber >= 7600) osName = "Windows 7 x64";

                int i = 0; while (osName[i] && i < 63) { outInfo->OSName[i] = osName[i]; i++; }
            }

            void QueryMacInfo(void (*callback)(const MacInfo&, void*), void* context) override {
                HMODULE hIphlp = GetModuleHandleA("iphlpapi.dll");
                if (!hIphlp) hIphlp = LoadLibraryA("iphlpapi.dll");
                if (hIphlp) {
                    using GetAdaptersInfoFn = ULONG(__stdcall*)(void*, ULONG*);
                    auto getAdaptersInfo = (GetAdaptersInfoFn)GetProcAddress(hIphlp, "GetAdaptersInfo");
                    if (getAdaptersInfo) {
                        ULONG outBufLen = 0;
                        getAdaptersInfo(nullptr, &outBufLen);
                        if (outBufLen > 0) {
                            void* pAdapterInfo = VirtualAlloc(nullptr, outBufLen, MEM_COMMIT, PAGE_READWRITE);
                            if (pAdapterInfo) {
                                if (getAdaptersInfo(pAdapterInfo, &outBufLen) == 0) {

                                    struct IP_ADAPTER_INFO_LOCAL {
                                        void* Next;
                                        uint32_t ComboIndex;
                                        char AdapterName[260];
                                        char Description[132];
                                        uint32_t AddressLength;
                                        uint8_t Address[8];
                                    };

                                    IP_ADAPTER_INFO_LOCAL* curr = (IP_ADAPTER_INFO_LOCAL*)pAdapterInfo;
                                    while (curr) {
                                        MacInfo m = { 0 };
                                        if (curr->AddressLength == 6) {
                                            char hexMap[] = "0123456789ABCDEF"; int k = 0;
                                            for (int j = 0; j < 6; j++) {
                                                m.MacAddress[k++] = hexMap[(curr->Address[j] >> 4) & 0xF];
                                                m.MacAddress[k++] = hexMap[curr->Address[j] & 0xF];
                                                if (j < 5) m.MacAddress[k++] = ':';
                                            }
                                            m.MacAddress[17] = '\0';
                                            int i = 0; while (curr->Description[i] && i < 127) { m.Description[i] = curr->Description[i]; i++; }
                                            callback(m, context);
                                        }
                                        curr = (IP_ADAPTER_INFO_LOCAL*)curr->Next;
                                    }
                                }
                                VirtualFree(pAdapterInfo, 0, MEM_RELEASE);
                            }
                        }
                    }
                }
            }

            void QueryCpuInfo(CpuInfo* outInfo) override {
                int cpuInfo[4] = { -1 };
                __cpuid(cpuInfo, 0);
                *(int*)&outInfo->Vendor[0] = cpuInfo[1]; *(int*)&outInfo->Vendor[4] = cpuInfo[3]; *(int*)&outInfo->Vendor[8] = cpuInfo[2];
                __cpuid(cpuInfo, 0x80000002);
                *(int*)&outInfo->Brand[0] = cpuInfo[0]; *(int*)&outInfo->Brand[4] = cpuInfo[1]; *(int*)&outInfo->Brand[8] = cpuInfo[2]; *(int*)&outInfo->Brand[12] = cpuInfo[3];
                __cpuid(cpuInfo, 0x80000003);
                *(int*)&outInfo->Brand[16] = cpuInfo[0]; *(int*)&outInfo->Brand[20] = cpuInfo[1]; *(int*)&outInfo->Brand[24] = cpuInfo[2]; *(int*)&outInfo->Brand[28] = cpuInfo[3];
                __cpuid(cpuInfo, 0x80000004);
                *(int*)&outInfo->Brand[32] = cpuInfo[0]; *(int*)&outInfo->Brand[36] = cpuInfo[1]; *(int*)&outInfo->Brand[40] = cpuInfo[2]; *(int*)&outInfo->Brand[44] = cpuInfo[3];

                __cpuid(cpuInfo, 1);
                unsigned int stepping = cpuInfo[0] & 0xF;
                unsigned int model = (cpuInfo[0] >> 4) & 0xF;
                unsigned int family = (cpuInfo[0] >> 8) & 0xF;

                // 动态解析扩展族和扩展型号
                if (family == 0xF) family += (cpuInfo[0] >> 20) & 0xFF;
                if (family == 0x6 || family == 0xF) model += ((cpuInfo[0] >> 16) & 0xF) << 4;

                std::snprintf(outInfo->ModelName, sizeof(outInfo->ModelName), "Family %u Model %u Stepping %u", family, model, stepping);

                char hexMap[] = "0123456789ABCDEF";
                unsigned int serialHi = cpuInfo[0], serialLo = cpuInfo[3];
                for (int i = 0; i < 8; i++) {
                    outInfo->SerialNumber[i] = hexMap[(serialHi >> ((7 - i) * 4)) & 0xF];
                    outInfo->SerialNumber[8 + i] = hexMap[(serialLo >> ((7 - i) * 4)) & 0xF];
                }
            }

            void QueryDiskInfo(const char* driveLetter, void (*callback)(const DiskInfo&, void*), void* context) override {
                DWORD drivesMask = 0;
                if (driveLetter && driveLetter[0]) {
                    char c = driveLetter[0];
                    if (c >= 'a' && c <= 'z') c -= 32;
                    if (c >= 'A' && c <= 'Z') drivesMask = 1 << (c - 'A');
                }
                else {
                    drivesMask = GetLogicalDrives();
                }

                struct HELIX_STORAGE_PROPERTY_QUERY {
                    uint32_t PropertyId;
                    uint32_t QueryType;
                    uint32_t AdditionalParameters;
                };
                struct HELIX_STORAGE_DEVICE_DESCRIPTOR {
                    uint32_t Version;
                    uint32_t Size;
                    uint8_t  DeviceType;
                    uint8_t  DeviceTypeModifier;
                    uint8_t  RemovableMedia;
                    uint8_t  CommandQueueing;
                    uint32_t VendorIdOffset;
                    uint32_t ProductIdOffset;
                    uint32_t ProductRevisionOffset;
                    uint32_t SerialNumberOffset;
                };

                for (int bit = 0; bit < 26; bit++) {
                    if (drivesMask & (1 << bit)) {
                        char c = (char)('A' + bit);
                        DiskInfo d = { 0 };
                        d.DriveLetter[0] = c;
                        d.DriveLetter[1] = ':'; d.DriveLetter[2] = '\\'; d.DriveLetter[3] = '\0';

                        char rootPath[4] = { c, ':', '\\', '\0' };

                        ULARGE_INTEGER freeBytes, totalBytes, totalFreeBytes;
                        if (GetDiskFreeSpaceExA(rootPath, &freeBytes, &totalBytes, &totalFreeBytes)) {
                            d.TotalSize = totalBytes.QuadPart;
                            d.FreeSpace = totalFreeBytes.QuadPart;

                            const char* fallbackModel = "Unknown Disk Model";
                            int i = 0; while (fallbackModel[i] && i < 63) { d.Model[i] = fallbackModel[i]; i++; }
                            d.Model[i] = '\0';

                            // 打开物理卷设备读取固件硬件名称
                            char devPath[16];
                            std::snprintf(devPath, sizeof(devPath), "\\\\.\\%c:", c);
                            HANDLE hDevice = CreateFileA(devPath, 0, FILE_SHARE_READ | FILE_SHARE_WRITE, NULL, OPEN_EXISTING, 0, NULL);
                            if (hDevice != INVALID_HANDLE_VALUE) {
                                HELIX_STORAGE_PROPERTY_QUERY query = { 0, 0, 0 };
                                uint8_t buffer[512] = { 0 };
                                DWORD bytesReturned = 0;
                                if (DeviceIoControl(hDevice, 0x2D1400, &query, sizeof(query), buffer, sizeof(buffer), &bytesReturned, NULL)) {
                                    HELIX_STORAGE_DEVICE_DESCRIPTOR* devDesc = (HELIX_STORAGE_DEVICE_DESCRIPTOR*)buffer;
                                    if (devDesc->ProductIdOffset > 0 && devDesc->ProductIdOffset < sizeof(buffer)) {
                                        char* pModel = (char*)(buffer + devDesc->ProductIdOffset);
                                        int mIdx = 0;
                                        while (pModel[mIdx] && mIdx < 63) { d.Model[mIdx] = pModel[mIdx]; mIdx++; }
                                        d.Model[mIdx] = '\0';
                                        while (mIdx > 0 && d.Model[mIdx - 1] == ' ') { d.Model[mIdx - 1] = '\0'; mIdx--; }
                                    }
                                }
                                CloseHandle(hDevice);
                            }

                            DWORD volSerial = 0;
                            if (GetVolumeInformationA(rootPath, NULL, 0, &volSerial, NULL, NULL, NULL, 0)) {
                                char hexMap[] = "0123456789ABCDEF";
                                for (int j = 0; j < 8; j++) { d.SerialNumber[j] = hexMap[(volSerial >> ((7 - j) * 4)) & 0xF]; }
                                d.SerialNumber[8] = '\0';
                            }
                            callback(d, context);
                        }
                    }
                }
            }

            void LogPrint(const char* format, ...) override {
                va_list args;
                va_start(args, format);
                std::vprintf(format, args);
                va_end(args);
            }
        };
    }
}