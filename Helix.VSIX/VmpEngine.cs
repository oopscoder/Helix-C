using Iced.Intel;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;

namespace Helix
{
    public class VmpEngine
    {
        private static readonly Register[] s_regs64 = {
            Register.RAX, Register.RCX, Register.RDX, Register.RBX,
            Register.RSI, Register.RDI, Register.R8, Register.R9,
            Register.R10, Register.R11, Register.R12, Register.R13,
            Register.R14, Register.R15
        };

        private static readonly Register[] s_regs32 = {
            Register.EAX, Register.ECX, Register.EDX, Register.EBX,
            Register.ESI, Register.EDI
        };
        private static readonly ThreadLocal<Random> s_rand = new(() => new Random(Guid.NewGuid().GetHashCode()));

        // 追加默认参数以保持接口兼容，支持动态架构分发与 Logger 挂载
        public static byte[] GenerateJunkCode(bool isX64 = true, Action<string> logger = null)
        {
            try
            {
                Assembler assembler = new(isX64 ? 64 : 32);
                var targetRegs = isX64 ? s_regs64 : s_regs32;

                int count = s_rand.Value.Next(3, 7);
                for (int i = 0; i < count; i++)
                {
                    Register r1 = targetRegs[s_rand.Value.Next(targetRegs.Length)];
                    Register r2 = targetRegs[s_rand.Value.Next(targetRegs.Length)];

                    switch (s_rand.Value.Next(5))
                    {
                        case 0: assembler.AddInstruction(Instruction.Create(Code.Nopd)); break;
                        case 1: assembler.AddInstruction(Instruction.Create(isX64 ? Code.Xchg_rm64_r64 : Code.Xchg_rm32_r32, r1, r2)); break;
                        case 2:
                            assembler.AddInstruction(Instruction.Create(isX64 ? Code.Push_r64 : Code.Push_r32, r1));
                            assembler.AddInstruction(Instruction.Create(isX64 ? Code.Pop_r64 : Code.Pop_r32, r1));
                            break;
                        case 3: assembler.AddInstruction(Instruction.Create(isX64 ? Code.Mov_rm64_r64 : Code.Mov_rm32_r32, r1, r1)); break;
                        case 4:
                            assembler.AddInstruction(Instruction.Create(Code.Stc));
                            assembler.AddInstruction(Instruction.Create(Code.Clc));
                            break;
                    }
                }

                assembler.AddInstruction(Instruction.Create(isX64 ? Code.Retnq : Code.Retnd));

                using MemoryStream stream = new();
                assembler.Assemble(new StreamCodeWriter(stream), 0);
                return stream.ToArray();
            }
            catch (Exception ex)
            {
#if DEBUG
                logger?.Invoke($"[VmpEngine] Iced.Intel Assembler Crash: {ex.Message}\n{ex.StackTrace}");
#endif
                return new byte[] { 0xC3 };
            }
        }
        public static string FlattenFunction(List<string> blocks, List<string> liftedDecls, List<string> refDecls)
        {
            if (blocks == null || blocks.Count == 0) return "";

            StringBuilder sb = new();

            foreach (var decl in liftedDecls) sb.AppendLine($"        {decl}");

            List<int> states = new();
            HashSet<int> usedStates = new();
            while (states.Count < blocks.Count)
            {
                int s = s_rand.Value.Next(1, 1000000); 
                if (usedStates.Add(s)) states.Add(s);
            }

            int pad1 = s_rand.Value.Next(4, 32); 
            int pad2 = s_rand.Value.Next(4, 32);
            int pad3 = s_rand.Value.Next(4, 32);

            // 还原切片语法为 Substring
            string ctxName = "_vmp_ctx_" + Guid.NewGuid().ToString("N").Substring(0, 6);

            sb.AppendLine($"        __declspec(align(32)) struct {{");
            sb.AppendLine($"            unsigned char p1[{pad1}];");
            sb.AppendLine($"            volatile unsigned int state;");
            sb.AppendLine($"            unsigned char p2[{pad2}];");
            sb.AppendLine($"            volatile unsigned int key;");
            sb.AppendLine($"            unsigned char p3[{pad3}];");
            sb.AppendLine($"        }} {ctxName};");
            sb.AppendLine($"        {ctxName}.key = (unsigned int)__rdtsc();");
            sb.AppendLine($"        {ctxName}.state = (unsigned int){states[0]} ^ {ctxName}.key;");
            sb.AppendLine($"        volatile unsigned int* _vmp_pState = &{ctxName}.state;");
            sb.AppendLine($"        volatile unsigned int* _vmp_pKey = &{ctxName}.key;");

            sb.AppendLine("        while (((*_vmp_pState) ^ (*_vmp_pKey)) != 0xFFFFFFFFu) {");
            sb.AppendLine("            switch (((*_vmp_pState) ^ (*_vmp_pKey))) {");

            Func<string> generateSourceJunk = () =>
            {
                int type = s_rand.Value.Next(4); 
                // 还原切片语法为 Substring
                string v1 = "j" + Guid.NewGuid().ToString("N").Substring(0, 6);
                string v2 = "j" + Guid.NewGuid().ToString("N").Substring(0, 6);

                int magicNum = s_rand.Value.Next(0x10000000, 0x7FFFFFFF);

                StringBuilder junkSb = new();
                junkSb.AppendLine($"volatile unsigned int {v1} = (unsigned int)__rdtsc();");
                if (type == 0)
                {
                    junkSb.AppendLine($"volatile unsigned int {v2} = {v1} ^ {s_rand.Value.Next(0x1000, 0xFFFF)};");
                    junkSb.AppendLine($"if ({v2} == 0) {{ __nop(); }}");
                }
                else if (type == 1)
                {
                    junkSb.AppendLine($"volatile unsigned int {v2} = {v1} * {s_rand.Value.Next(3, 99)};");
                    junkSb.AppendLine($"while ({v2} == 0x{magicNum:X8}) {{ _mm_pause(); break; }}");
                }
                else if (type == 2)
                {
                    junkSb.AppendLine($"__nop(); __nop();");
                    junkSb.AppendLine($"volatile unsigned int {v2} = {v1} + {s_rand.Value.Next(0x100, 0x999)};");
                    junkSb.AppendLine($"if ({v2} < {v1}) {{ _mm_pause(); }}");
                }
                else
                {
                    junkSb.AppendLine($"volatile unsigned int {v2} = {v1} & {s_rand.Value.Next(0x10, 0xFF)};");
                    junkSb.AppendLine($"for (int i = 0; i < (int)({v2} % 2); ++i) {{ __nop(); }}");
                }
                return junkSb.ToString();
            };

            List<int> indices = new();
            for (int i = 0; i < blocks.Count; i++) indices.Add(i);
            indices.Sort((a, b) => s_rand.Value.Next(-1, 2)); 

            foreach (int i in indices)
            {
                sb.AppendLine($"                case {states[i]}: {{");

                sb.AppendLine("                    {");
                sb.AppendLine(generateSourceJunk());
                sb.AppendLine("                    }");

                foreach (var rd in refDecls) sb.AppendLine($"                    {rd}");

                sb.AppendLine($"                    {blocks[i]}");

                if (i < blocks.Count - 1)
                {
                    sb.AppendLine($"                    *_vmp_pState = {states[i + 1]} ^ (*_vmp_pKey);");
                    sb.AppendLine("                    break;");
                }
                else
                {
                    sb.AppendLine("                    *_vmp_pState = 0xFFFFFFFFu ^ (*_vmp_pKey);");
                    sb.AppendLine("                    break;");
                }
                sb.AppendLine("                }");
            }

            sb.AppendLine("                default:");
            sb.AppendLine("                    *_vmp_pState = 0xFFFFFFFFu ^ (*_vmp_pKey);");
            sb.AppendLine("                    break;");
            sb.AppendLine("            }");
            sb.AppendLine("        }");
            return sb.ToString();
        }
    }

    public class StreamCodeWriter(Stream stream) : CodeWriter
    {
        private readonly Stream _stream = stream;
        public override void WriteByte(byte value) => _stream.WriteByte(value);
    }
}