# Helix-C
🧬 Helix Engine - 使用例子在https://www.cnblogs.com/dalgleish/p/22510998

Helix Engine 是一款专为极致性能与底层安全对抗打造的 C++ 静态反射与代码虚拟化（VMP）框架。

本项目自带深度定制的 Visual Studio VSIX 扩展引擎。通过在后台无感调度独立 ClangSharp AST 解析器，Helix 为 C++ 赋予了全量的高级反射与元数据操控能力，同时保持了裸指针级别的极致运行速度。完美支持 Windows 内核态（Ring 0）与用户态（Ring 3）双轨开发，是构建底层反作弊（Anti-Cheat）、终端检测响应（EDR）及高级安全分析工具的绝佳基石。

🚀 核心特性 (Core Features)
零开销静态反射 (Zero-Overhead Static Reflection)
彻底抛弃低效且极易在内核态引发蓝屏崩溃（BSOD）的 C++ 原生 RTTI 与异常机制。引擎基于 64 位 FNV-1a 强哈希算法构建类型映射，支持 O(1) 级别的成员访问、方法动态调用与跨进程反序列化。

双环架构统一 (Ring 0 & Ring 3 Unification)
通过极简的宏隔离与底层硬件抽象层（HAL），提供统一的 API 体验。业务层代码无需修改，即可在常规用户态程序（EXE/DLL）和内核驱动（.sys, KMDF/WDM）之间自由切换。

深层虚拟化保护 (VMP & Control Flow Flattening)
仅需一个 [[clang::annotate("HelixVmp")]] 标签，扩展引擎即可在编译期接管 AST 语法树，针对关键函数执行深度的控制流平坦化、状态机切片与动态垃圾汇编（Junk Code）填充，拉满逆向对抗门槛。

隐蔽寻址与特征穿透 (Stealth Interop & HWID Bypass)
内置动态 API 哈希寻址（EAT 解析），告别明文导入表。用户态默认采用动态拼装的间接系统调用（Indirect Syscalls）规避 Ring 3 Hook；内核态直接穿透 NDIS 栈与文件系统底层读取真实硬件设备特征，无视基于常规 API 的注册表或 WMI 欺骗。

极速无碎片内存 (Zero-Fragmentation GC & Pool)
舍弃臃肿的 STL。引擎内置自主研发的低延迟容器（HVector, HMap, HString）与基于 GCHandle 映射的局部自动化垃圾收集机制，提供 Any 动态神匣支持，彻底根除高频调度下的内存重分配碎片。

🛠️ VSIX 编译管线 (Toolchain Pipeline)
Helix Engine 不依赖、不妥协于 Visual Studio 脆弱的单线程 COM 代码模型。

极速增量拦截：基于文件长度与时间戳的双重哈希校验，精准拦截脏文件。

并发 AST 扫描：多线程调度 Clang 解析器，剔除冗余 I/O 轰炸。

动态 Polyfill：编译期自动补齐缺失的跨环依赖结构，并在后台无缝生成 Helix.cpp 注册表，全程对开发者透明。

⚠️ 适用场景 (Use Cases)
本框架涉及极深的系统底层操作与编译管线劫持，建议在以下场景中使用：

恶意软件逆向工程分析与数据沙箱监控。

反作弊/反木马安全模块开发。

无感知的动态调试器插件研发。

在编写 Helix 业务逻辑时，强制使用以下引擎原语（完整测试例子请去上面链接查看）：

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
