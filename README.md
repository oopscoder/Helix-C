Helix Framework 跨平台大一统引擎开发指南
1. 架构总览 (Architecture Overview)
Helix 是一套深度解耦、原生跨平台（Windows Ring 0/Ring 3, Linux, macOS）的 C++ 安全开发框架。它通过 C# VSIX Clang AST 扫描器 在预编译阶段自动生成 Helix.cpp，实现无感知的垃圾回收（GC）、控制流平坦化混淆（VMP）、原生 API 导入解析以及反射注册。无额外的Runtime库，语法标准要求至少C++20。

开发原则：业务代码中只需包含 #include "Helix.h"。绝不手动干预内存释放。

统一入口：废弃传统的 main 和 DriverEntry，双端代码统一使用 int main(void** args) 作为唯一入口点。

2. 核心语法糖与安全宏 (Core Syntax & Macros)
在编写 Helix 业务逻辑时，强制使用以下引擎原语：

智能分配 (New)：取代原生的 new/malloc。自动受底层保守式 GC（栈/BSS扫描）接管。

C++
 
auto entity = New<ComplexEntity>(1024); // 返回 HelixRef<ComplexEntity>，支持&和->操作符。内存安全和原生T*指针任意转换。
混淆护盾 (Vmp)：用于函数声明前。AST 扫描器会自动将其 AST 树打碎，生成带垃圾指令（Junk Code）的状态机（switch-case）扁平化代码。

C++
 
Vmp void ExecutePayload(int param) { ... }
外部生命周期保护 (Extern)：当需要将指针导出给系统回调、外部变量时，使用此宏声明，GC 将提升其存活期（Immortal）。为兼容第三方语言。在进程结束时释放。

C++
 
Extern void* globalResource;
void Setup(Extern ComplexEntity** outNode);
动态 API 劫持 (API)：免驱解析底层导出表（兼容序号 Ordinal 与全小写调用约定）。底层自动使用 _15 映射到序号 15。

C++
 
API("ntdll.dll") NTSTATUS NTAPI _15(void* Param1);
API("ws2_32.dll") int __stdcall _115(unsigned short wVersionRequested, void* lpWSAData);
3. 反射与序列化 (Reflection & Serialization)
所有通过 New 实例化的类，或使用了引擎类型特征的数组，均自动获得反射与 SFINAE 序列化能力。

内存转储与反转储：完美支持多维嵌套结构（T[n], void*[n]）。

C++
 
auto bytes = Serialize(entity); 
auto cloned = Deserialize<ComplexEntity>(bytes);
动态反射 (Reflec)：运行时动态调用与设值。

C++
 
auto meta = Reflec(cloned);
meta.SetValue("entityId", 2048);
meta.Invoke("ProcessData", 5);
4. 系统指纹与跨进程原语 (System & Cross-Process)
抽象层（HAL）接管了所有底层探针，以下方法在 Ring 0 和 Ring 3 表现完全一致：

指纹探测：返回受 GC 保护的托管结构体。

C++
 
auto sys = System();  // OSMajor, SerialNumber等
auto cpu = Cpu();     // Vendor, Brand 等
auto macs = Mac();    // 返回 HVector<MacInfo>
auto disks = Disk();  // 返回 HVector<DiskInfo>，用法也可以是Disk("c")
跨进程高维内存读写：

C++
 
uint32_t targetPid = 1234;
void* remoteMem = Allocate(targetPid, sizeof(int));// 内存安全，用户不用的担心释放问题，Helix插件全方位管理内存泄漏。

// 写入
Write(targetPid, remoteMem, 1337); 
// 多维复杂数组注入
Write<AdvancedArrayNode[2]>(targetPid, remoteArrayAddr, localArray);

// 读取
auto val = Read<int>(targetPid, remoteMem);
auto arr = Read<AdvancedArrayNode[2]>(targetPid, remoteArrayAddr);
