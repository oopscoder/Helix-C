#pragma once
#if defined(_KERNEL_MODE)
#include <ntifs.h> // Helix Auto-Wrapped
#endif
#if defined(_KERNEL_MODE)
#include <ntstrsafe.h> // Helix Auto-Wrapped
#endif
#include <intrin.h>
#include <stdarg.h>
#include "../IPlatformProvider.h"

extern "C" NTSTATUS NTAPI MmCopyVirtualMemory(
    PEPROCESS FromProcess, PVOID FromAddress, PEPROCESS ToProcess, PVOID ToAddress,
    SIZE_T BufferSize, KPROCESSOR_MODE PreviousMode, PSIZE_T NumberOfBytesCopied
);

extern "C" PVOID NTAPI IoGetInitialStack();

namespace HelixRuntime {
    namespace HAL {

        class WinKernelProvider : public IPlatformProvider {
        private:
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
                        bool match = true; const char* s1 = name; const char* s2 = funcName;
                        while (*s1 && *s2) { if (*s1 != *s2) { match = false; break; } s1++; s2++; }
                        if (match && *s1 == *s2) {
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

        public:
            void* MemAlloc(size_t size) override { return ExAllocatePool2(POOL_FLAG_NON_PAGED, size, 'XLEH'); }
            void MemFree(void* ptr) override { if (ptr) ExFreePoolWithTag(ptr, 'XLEH'); }

            uint64_t GetSystemThreadId() override { return (uint64_t)PsGetCurrentThreadId(); }

            void* CreateHMutex() override {
                KSPIN_LOCK* lock = (KSPIN_LOCK*)MemAlloc(sizeof(KSPIN_LOCK));
                if (lock) KeInitializeSpinLock(lock);
                return lock;
            }
            void LockHMutex(void* mutex, uintptr_t& state) override {
                KIRQL irql;
                KeAcquireSpinLock((PKSPIN_LOCK)mutex, &irql);
                state = irql;
            }
            void UnlockHMutex(void* mutex, uintptr_t state) override {
                KeReleaseSpinLock((PKSPIN_LOCK)mutex, (KIRQL)state);
            }
            void DestroyHMutex(void* mutex) override { MemFree(mutex); }

            void* GetThreadStackBase() override {
                return IoGetInitialStack();
            }

            void InstallHook(void* target, void* detour) override {
                if (!target || !detour) return;
                uint8_t* p = (uint8_t*)target;
                if (p[0] == 0xE9) { target = (void*)(p + 5 + *(int32_t*)&p[1]); p = (uint8_t*)target; }
                PMDL mdl = IoAllocateMdl(target, 5, FALSE, FALSE, NULL);
                if (mdl) {
                    __try {
                        MmProbeAndLockPages(mdl, KernelMode, IoReadAccess);
                        void* mapping = MmMapLockedPagesSpecifyCache(mdl, KernelMode, MmCached, NULL, FALSE, NormalPagePriority);
                        if (mapping) {
                            KIRQL irql = KeRaiseIrqlToDpcLevel();
                            uint8_t* rwPtr = (uint8_t*)mapping;
                            rwPtr[0] = 0xE9;
                            *(int32_t*)(&rwPtr[1]) = (int32_t)((intptr_t)detour - (intptr_t)target - 5);
                            KeLowerIrql(irql);
                            MmUnmapLockedPages(mapping, mdl);
                        }
                        MmUnlockPages(mdl);
                    }
                    __except (EXCEPTION_EXECUTE_HANDLER) {}
                    IoFreeMdl(mdl);
                }
            }

            void* GetHModuleBase(const char* dllName, void* context = nullptr) override {
                if (!context || !dllName) return nullptr;
                uint8_t* driverSection = (uint8_t*)((PDRIVER_OBJECT)context)->DriverSection;
                if (driverSection) {
                    uint8_t* curr = driverSection;
                    for (int i = 0; i < 512; i++) {
                        void* dllBase = *(void**)(curr + 0x30);
                        UNICODE_STRING* baseDllName = (UNICODE_STRING*)(curr + 0x58);

                        if (dllBase && baseDllName && baseDllName->Buffer && baseDllName->Length > 0 && baseDllName->Length < 512) {
                            bool match = true;
                            size_t len = baseDllName->Length / 2;
                            for (size_t j = 0; j < len && dllName[j] != '\0'; j++) {
                                wchar_t c1 = baseDllName->Buffer[j];
                                char c2 = dllName[j];
                                if (c1 >= L'A' && c1 <= L'Z') c1 += 32;
                                if (c2 >= 'A' && c2 <= 'Z') c2 += 32;
                                if (c1 != (wchar_t)c2) { match = false; break; }
                            }
                            size_t dllNameLen = 0; while (dllName[dllNameLen]) dllNameLen++;
                            if (match && len == dllNameLen) return dllBase;
                        }
                        curr = (uint8_t*)(*(void**)curr);
                        if (curr == driverSection) break;
                    }
                }
                return nullptr;
            }

            void* GetHExportAddress(void* moduleBase, const char* funcName) override {
                return ParseEAT(moduleBase, funcName);
            }

            void* AllocRemote(uint32_t pid, size_t size) override {
                void* address = nullptr;
                if (pid == (uint32_t)-1 || pid == (uint32_t)(uintptr_t)PsGetCurrentProcessId()) {
                    SIZE_T allocSize = size;
                    ZwAllocateVirtualMemory(NtCurrentProcess(), &address, 0, &allocSize, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
                    return address;
                }

                PEPROCESS process = nullptr;
                if (NT_SUCCESS(PsLookupProcessByProcessId((HANDLE)(uintptr_t)pid, &process))) {
                    KAPC_STATE apc; KeStackAttachProcess(process, &apc);
                    SIZE_T allocSize = size;
                    ZwAllocateVirtualMemory(NtCurrentProcess(), &address, 0, &allocSize, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
                    KeUnstackDetachProcess(&apc);
                    ObDereferenceObject(process);
                }
                return address;
            }

            void FreeRemote(uint32_t pid, void* ptr) override {
                if (pid == (uint32_t)-1 || pid == (uint32_t)(uintptr_t)PsGetCurrentProcessId()) {
                    SIZE_T freeSize = 0;
                    ZwFreeVirtualMemory(NtCurrentProcess(), &ptr, &freeSize, MEM_RELEASE);
                    return;
                }

                PEPROCESS tProc = nullptr;
                if (NT_SUCCESS(PsLookupProcessByProcessId((HANDLE)(uintptr_t)pid, &tProc))) {
                    KAPC_STATE tApc; KeStackAttachProcess(tProc, &tApc);
                    SIZE_T freeSize = 0;
                    ZwFreeVirtualMemory(NtCurrentProcess(), &ptr, &freeSize, MEM_RELEASE);
                    KeUnstackDetachProcess(&tApc);
                    ObDereferenceObject(tProc);
                }
            }

            int ReadRemoteRaw(uint32_t pid, void* address, void* buffer, size_t size) override {
                if (pid == (uint32_t)-1 || pid == (uint32_t)(uintptr_t)PsGetCurrentProcessId()) {
                    __try {
                        RtlCopyMemory(buffer, address, size);
                        return 1;
                    }
                    __except (EXCEPTION_EXECUTE_HANDLER) {
                        return 0;
                    }
                }

                int status = 0; PEPROCESS process = nullptr;
                if (NT_SUCCESS(PsLookupProcessByProcessId((HANDLE)(uintptr_t)pid, &process))) {
                    SIZE_T bytesRead = 0;
                    if (NT_SUCCESS(MmCopyVirtualMemory(process, address, PsGetCurrentProcess(), buffer, size, KernelMode, &bytesRead))) {
                        status = 1;
                    }
                    ObDereferenceObject(process);
                }
                return status;
            }

            int WriteRemoteRaw(uint32_t pid, void* address, void* buffer, size_t size) override {
                if (pid == (uint32_t)-1 || pid == (uint32_t)(uintptr_t)PsGetCurrentProcessId()) {
                    __try {
                        RtlCopyMemory(address, buffer, size);
                        return 1;
                    }
                    __except (EXCEPTION_EXECUTE_HANDLER) {
                        return 0;
                    }
                }

                int status = 0; PEPROCESS process = nullptr;
                if (NT_SUCCESS(PsLookupProcessByProcessId((HANDLE)(uintptr_t)pid, &process))) {
                    SIZE_T bytesWritten = 0;
                    if (NT_SUCCESS(MmCopyVirtualMemory(PsGetCurrentProcess(), buffer, process, address, size, KernelMode, &bytesWritten))) {
                        status = 1;
                    }
                    ObDereferenceObject(process);
                }
                return status;
            }

            void QuerySystemInfo(SystemInfo* outInfo) override {
                RTL_OSVERSIONINFOW osInfo = { 0 };
                osInfo.dwOSVersionInfoSize = sizeof(osInfo);
                RtlGetVersion(&osInfo);
                outInfo->MajorVersion = osInfo.dwMajorVersion;
                outInfo->MinorVersion = osInfo.dwMinorVersion;
                outInfo->BuildNumber = osInfo.dwBuildNumber;

                const char* osName = "Windows (Unknown) x64";
                if (outInfo->BuildNumber >= 22000) osName = "Windows 11 x64";
                else if (outInfo->BuildNumber >= 19041) osName = "Windows 10 x64";
                else if (outInfo->BuildNumber >= 9200) osName = "Windows 8.1 x64";
                else if (outInfo->BuildNumber >= 7600) osName = "Windows 7 x64";

                int i = 0; while (osName[i] && i < 63) { outInfo->OSName[i] = osName[i]; i++; }
                outInfo->OSName[i] = '\0';

                WCHAR guidBuf[128] = { 0 };
                UNICODE_STRING guidStr; guidStr.Buffer = guidBuf; guidStr.Length = 0; guidStr.MaximumLength = sizeof(guidBuf);
                RTL_QUERY_REGISTRY_TABLE queryTable[2] = { 0 };
                queryTable[0].Flags = RTL_QUERY_REGISTRY_DIRECT; queryTable[0].Name = (PWSTR)L"MachineGuid";
                queryTable[0].EntryContext = &guidStr; queryTable[0].DefaultType = REG_NONE;

                if (NT_SUCCESS(RtlQueryRegistryValues(RTL_REGISTRY_ABSOLUTE, (PWSTR)L"\\Registry\\Machine\\SOFTWARE\\Microsoft\\Cryptography", queryTable, NULL, NULL))) {
                    ANSI_STRING ansiGuid;
                    if (NT_SUCCESS(RtlUnicodeStringToAnsiString(&ansiGuid, &guidStr, TRUE))) {
                        int j = 0; while (ansiGuid.Buffer[j] && j < 63) { outInfo->SerialNumber[j] = ansiGuid.Buffer[j]; j++; }
                        outInfo->SerialNumber[j] = '\0'; RtlFreeAnsiString(&ansiGuid);
                    }
                }
            }

            void QueryMacInfo(void (*callback)(const MacInfo&, void*), void* context) override {
                if (KeGetCurrentIrql() == PASSIVE_LEVEL) {
                    for (int i = 0; i <= 32; i++) {
                        WCHAR keyPath[256];
                        RtlStringCchPrintfW(keyPath, 256, L"\\Registry\\Machine\\SYSTEM\\CurrentControlSet\\Control\\Class\\{4d36e972-e325-11ce-bfc1-08002be10318}\\%04d", i);

                        WCHAR descBuf[256] = { 0 };
                        UNICODE_STRING descStr;
                        descStr.Buffer = descBuf; descStr.Length = 0; descStr.MaximumLength = sizeof(descBuf);

                        WCHAR netCfgBuf[128] = { 0 };
                        UNICODE_STRING netCfgStr;
                        netCfgStr.Buffer = netCfgBuf; netCfgStr.Length = 0; netCfgStr.MaximumLength = sizeof(netCfgBuf);

                        RTL_QUERY_REGISTRY_TABLE queryTable[3] = { 0 };
                        queryTable[0].Flags = RTL_QUERY_REGISTRY_DIRECT;
                        queryTable[0].Name = (PWSTR)L"DriverDesc";
                        queryTable[0].EntryContext = &descStr;
                        queryTable[0].DefaultType = REG_NONE;

                        queryTable[1].Flags = RTL_QUERY_REGISTRY_DIRECT;
                        queryTable[1].Name = (PWSTR)L"NetCfgInstanceId";
                        queryTable[1].EntryContext = &netCfgStr;
                        queryTable[1].DefaultType = REG_NONE;

                        if (NT_SUCCESS(RtlQueryRegistryValues(RTL_REGISTRY_ABSOLUTE, keyPath, queryTable, NULL, NULL))) {
                            if (descStr.Length > 0 && netCfgStr.Length > 0) {
                                WCHAR devNameBuf[256];
                                RtlStringCchPrintfW(devNameBuf, 256, L"\\Device\\%wZ", &netCfgStr);
                                UNICODE_STRING devName; RtlInitUnicodeString(&devName, devNameBuf);
                                OBJECT_ATTRIBUTES objAttr; InitializeObjectAttributes(&objAttr, &devName, OBJ_CASE_INSENSITIVE | OBJ_KERNEL_HANDLE, NULL, NULL);

                                HANDLE hDevice; IO_STATUS_BLOCK ioStatus;
                                bool macFound = false;
                                uint8_t mac[6] = { 0 };

                                // 穿透 NDIS 驱动栈，获取真实物理 MAC 地址，规避注册表 REG_BINARY 导致的解析截断
                                if (NT_SUCCESS(ZwOpenFile(&hDevice, GENERIC_READ | SYNCHRONIZE, &objAttr, &ioStatus, FILE_SHARE_READ | FILE_SHARE_WRITE, FILE_SYNCHRONOUS_IO_NONALERT))) {
                                    uint32_t oid = 0x01010102; // OID_802_3_CURRENT_ADDRESS
                                    // 0x170002 对应 IOCTL_NDIS_QUERY_GLOBAL_STATS
                                    if (NT_SUCCESS(ZwDeviceIoControlFile(hDevice, NULL, NULL, NULL, &ioStatus, 0x170002, &oid, sizeof(oid), mac, sizeof(mac)))) {
                                        if (mac[0] || mac[1] || mac[2] || mac[3] || mac[4] || mac[5]) {
                                            macFound = true;
                                        }
                                    }
                                    ZwClose(hDevice);
                                }

                                if (macFound) {
                                    MacInfo m = { 0 };
                                    char hexMap[] = "0123456789ABCDEF"; int k = 0;
                                    for (int j = 0; j < 6; j++) {
                                        m.MacAddress[k++] = hexMap[(mac[j] >> 4) & 0xF];
                                        m.MacAddress[k++] = hexMap[mac[j] & 0xF];
                                        if (j < 5) m.MacAddress[k++] = ':';
                                    }
                                    m.MacAddress[17] = '\0';

                                    ANSI_STRING ansiDesc;
                                    if (NT_SUCCESS(RtlUnicodeStringToAnsiString(&ansiDesc, &descStr, TRUE))) {
                                        int c = 0; while (ansiDesc.Buffer[c] && c < 127) { m.Description[c] = ansiDesc.Buffer[c]; c++; }
                                        m.Description[c] = '\0';
                                        RtlFreeAnsiString(&ansiDesc);
                                    }
                                    callback(m, context);
                                }
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

                if (family == 0xF) family += (cpuInfo[0] >> 20) & 0xFF;
                if (family == 0x6 || family == 0xF) model += ((cpuInfo[0] >> 16) & 0xF) << 4;

                RtlStringCbPrintfA(outInfo->ModelName, sizeof(outInfo->ModelName), "Family %u Model %u Stepping %u", family, model, stepping);

                char hexMap[] = "0123456789ABCDEF";
                unsigned int serialHi = cpuInfo[0], serialLo = cpuInfo[3];
                for (int i = 0; i < 8; i++) {
                    outInfo->SerialNumber[i] = hexMap[(serialHi >> ((7 - i) * 4)) & 0xF];
                    outInfo->SerialNumber[8 + i] = hexMap[(serialLo >> ((7 - i) * 4)) & 0xF];
                }
            }

            void QueryDiskInfo(const char* driveLetter, void (*callback)(const DiskInfo&, void*), void* context) override {
                if (KeGetCurrentIrql() == PASSIVE_LEVEL) {
                    char startDrive = 'C';
                    char endDrive = 'Z';

                    if (driveLetter && driveLetter[0]) {
                        char c = driveLetter[0];
                        if (c >= 'a' && c <= 'z') c -= 32;
                        if (c >= 'A' && c <= 'Z') {
                            startDrive = c;
                            endDrive = c;
                        }
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

                    for (char c = startDrive; c <= endDrive; c++) {
                        DiskInfo d = { 0 };
                        d.DriveLetter[0] = c;
                        d.DriveLetter[1] = ':'; d.DriveLetter[2] = '\\'; d.DriveLetter[3] = '\0';

                        const char* fallbackModel = "Unknown Disk Model";
                        int i = 0; while (fallbackModel[i] && i < 63) { d.Model[i] = fallbackModel[i]; i++; }
                        d.Model[i] = '\0';

                        // 打开无反斜杠的“卷设备”，用于抓取硬盘固件物理型号
                        WCHAR volNameBuf[32] = L"\\DosDevices\\X:";
                        volNameBuf[12] = c;
                        UNICODE_STRING volName; RtlInitUnicodeString(&volName, volNameBuf);
                        OBJECT_ATTRIBUTES volAttr; InitializeObjectAttributes(&volAttr, &volName, OBJ_CASE_INSENSITIVE | OBJ_KERNEL_HANDLE, NULL, NULL);
                        HANDLE hVol; IO_STATUS_BLOCK ioStatus;

                        if (NT_SUCCESS(ZwOpenFile(&hVol, FILE_READ_ATTRIBUTES | SYNCHRONIZE, &volAttr, &ioStatus, FILE_SHARE_READ | FILE_SHARE_WRITE, FILE_SYNCHRONOUS_IO_NONALERT))) {
                            HELIX_STORAGE_PROPERTY_QUERY query = { 0, 0, 0 };
                            uint8_t buffer[512] = { 0 };
                            if (NT_SUCCESS(ZwDeviceIoControlFile(hVol, NULL, NULL, NULL, &ioStatus, 0x2D1400, &query, sizeof(query), buffer, sizeof(buffer)))) {
                                HELIX_STORAGE_DEVICE_DESCRIPTOR* devDesc = (HELIX_STORAGE_DEVICE_DESCRIPTOR*)buffer;
                                if (devDesc->ProductIdOffset > 0 && devDesc->ProductIdOffset < sizeof(buffer)) {
                                    char* pModel = (char*)(buffer + devDesc->ProductIdOffset);
                                    int mIdx = 0;
                                    while (pModel[mIdx] && mIdx < 63) { d.Model[mIdx] = pModel[mIdx]; mIdx++; }
                                    d.Model[mIdx] = '\0';
                                    while (mIdx > 0 && d.Model[mIdx - 1] == ' ') { d.Model[mIdx - 1] = '\0'; mIdx--; }
                                }
                            }
                            ZwClose(hVol);
                        }

                        // 打开带反斜杠的“文件系统设备”，用于抓取文件系统容量信息
                        WCHAR fsNameBuf[32] = L"\\DosDevices\\X:\\";
                        fsNameBuf[12] = c;
                        UNICODE_STRING fsName; RtlInitUnicodeString(&fsName, fsNameBuf);
                        OBJECT_ATTRIBUTES fsAttr; InitializeObjectAttributes(&fsAttr, &fsName, OBJ_CASE_INSENSITIVE | OBJ_KERNEL_HANDLE, NULL, NULL);
                        HANDLE hFs;
                        if (NT_SUCCESS(ZwOpenFile(&hFs, FILE_READ_ATTRIBUTES | SYNCHRONIZE, &fsAttr, &ioStatus, FILE_SHARE_READ | FILE_SHARE_WRITE, FILE_SYNCHRONOUS_IO_NONALERT))) {
                            FILE_FS_VOLUME_INFORMATION volInfo;
                            if (NT_SUCCESS(ZwQueryVolumeInformationFile(hFs, &ioStatus, &volInfo, sizeof(volInfo), FileFsVolumeInformation))) {
                                char hexMap[] = "0123456789ABCDEF"; unsigned int serial = volInfo.VolumeSerialNumber;
                                for (int j = 0; j < 8; j++) { d.SerialNumber[j] = hexMap[(serial >> ((7 - j) * 4)) & 0xF]; }
                                d.SerialNumber[8] = '\0';
                            }

                            FILE_FS_SIZE_INFORMATION sizeInfo;
                            if (NT_SUCCESS(ZwQueryVolumeInformationFile(hFs, &ioStatus, &sizeInfo, sizeof(sizeInfo), FileFsSizeInformation))) {
                                uint64_t bytesPerCluster = (uint64_t)sizeInfo.SectorsPerAllocationUnit * sizeInfo.BytesPerSector;
                                d.TotalSize = sizeInfo.TotalAllocationUnits.QuadPart * bytesPerCluster;
                                d.FreeSpace = sizeInfo.AvailableAllocationUnits.QuadPart * bytesPerCluster;
                            }

                            ZwClose(hFs);

                            // 如果成功拿到真实容量，才认为是有效磁盘并回调
                            if (d.TotalSize > 0) {
                                callback(d, context);
                            }
                        }
                    }
                }
            }

            void LogPrint(const char* format, ...) override {
                va_list args;
                va_start(args, format);
                vDbgPrintEx(DPFLTR_IHVDRIVER_ID, DPFLTR_ERROR_LEVEL, format, args);
                va_end(args);
            }
        };

    }
}