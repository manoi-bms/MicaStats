using System;
using System.Collections.Generic;
using System.Runtime.Intrinsics.X86;
using System.Text;

namespace Kil0bitSystemMonitor.Services.HardwareInfo
{
    /// <summary>One CPUID register set (EAX..EDX) for a given leaf/subleaf.</summary>
    public readonly record struct CpuIdRegs(uint Eax, uint Ebx, uint Ecx, uint Edx);

    /// <summary>
    /// Source of raw CPUID data. The production implementation executes the CPUID instruction
    /// directly — the same primary source CPU-Z reads — while tests substitute canned register
    /// dumps, which is what makes the decoder below verifiable without real silicon.
    /// </summary>
    public interface ICpuIdSource
    {
        bool TryRead(uint leaf, uint subleaf, out CpuIdRegs regs);
    }

    /// <summary>
    /// Executes CPUID through the .NET <see cref="X86Base.CpuId"/> intrinsic. Unlike MSR or
    /// SMBus access this needs no kernel driver: CPUID is unprivileged, so ring 3 sees exactly
    /// the identity data CPU-Z reports. Unavailable on ARM64, where the caller must fall back
    /// to registry strings.
    /// </summary>
    public sealed class X86CpuIdSource : ICpuIdSource
    {
        public static bool IsSupported => X86Base.IsSupported;

        public bool TryRead(uint leaf, uint subleaf, out CpuIdRegs regs)
        {
            if (!X86Base.IsSupported) { regs = default; return false; }
            var (eax, ebx, ecx, edx) = X86Base.CpuId(unchecked((int)leaf), unchecked((int)subleaf));
            regs = new CpuIdRegs((uint)eax, (uint)ebx, (uint)ecx, (uint)edx);
            return true;
        }
    }

    /// <summary>Decoded CPU identity — a pure function of CPUID register values.</summary>
    public sealed record CpuIdentity(
        string Vendor,
        string BrandString,
        uint Family,
        uint Model,
        uint Stepping,
        IReadOnlyList<string> Features,
        bool HypervisorPresent,
        bool HybridFlag);

    /// <summary>
    /// Decodes CPUID leaves into a <see cref="CpuIdentity"/>. All methods are pure over an
    /// <see cref="ICpuIdSource"/>; nothing here touches the OS.
    /// </summary>
    public static class CpuIdDecoder
    {
        public static CpuIdentity? Decode(ICpuIdSource src)
        {
            if (!src.TryRead(0, 0, out var leaf0)) return null;

            string vendor = DecodeVendor(leaf0);
            uint maxLeaf = leaf0.Eax;

            uint family = 0, model = 0, stepping = 0;
            bool hypervisor = false, hybrid = false;
            var features = new List<string>();

            if (maxLeaf >= 1 && src.TryRead(1, 0, out var leaf1))
            {
                (family, model, stepping) = DecodeSignature(leaf1.Eax);
                hypervisor = Bit(leaf1.Ecx, 31);
            }
            else
            {
                leaf1 = default;
            }

            src.TryRead(0x80000000, 0, out var extMax);
            if (!(extMax.Eax >= 0x80000001 && src.TryRead(0x80000001, 0, out var ext1)))
                ext1 = default;

            CpuIdRegs leaf7 = default;
            if (maxLeaf >= 7 && src.TryRead(7, 0, out var l7))
            {
                leaf7 = l7;
                hybrid = Bit(l7.Edx, 15); // Intel hybrid-architecture flag
            }

            features.AddRange(DecodeFeatures(leaf1, leaf7, ext1));

            string brand = DecodeBrand(src, extMax.Eax);

            return new CpuIdentity(vendor, brand, family, model, stepping, features, hypervisor, hybrid);
        }

        /// <summary>Leaf 0 vendor string: 12 ASCII chars packed as EBX, EDX, ECX (in that order).</summary>
        public static string DecodeVendor(CpuIdRegs leaf0)
        {
            var sb = new StringBuilder(12);
            AppendAscii(sb, leaf0.Ebx);
            AppendAscii(sb, leaf0.Edx);
            AppendAscii(sb, leaf0.Ecx);
            return sb.ToString().Trim('\0', ' ');
        }

        /// <summary>
        /// Leaves 0x80000002..0x80000004 hold the 48-byte marketing brand string, all four
        /// registers of each leaf in order. Absent on very old parts, hence the max-leaf gate.
        /// </summary>
        public static string DecodeBrand(ICpuIdSource src, uint maxExtLeaf)
        {
            if (maxExtLeaf < 0x80000004) return "";
            var sb = new StringBuilder(48);
            for (uint leaf = 0x80000002; leaf <= 0x80000004; leaf++)
            {
                if (!src.TryRead(leaf, 0, out var r)) return "";
                AppendAscii(sb, r.Eax);
                AppendAscii(sb, r.Ebx);
                AppendAscii(sb, r.Ecx);
                AppendAscii(sb, r.Edx);
            }
            return sb.ToString().Trim('\0', ' ');
        }

        /// <summary>
        /// Leaf 1 EAX signature. Display family adds the extended field only when the base
        /// family is 0xF; display model prepends the extended field for family 6 and 0xF —
        /// the composition rule both Intel and AMD document and CPU-Z displays.
        /// </summary>
        public static (uint Family, uint Model, uint Stepping) DecodeSignature(uint eax)
        {
            uint stepping = eax & 0xF;
            uint model = (eax >> 4) & 0xF;
            uint family = (eax >> 8) & 0xF;
            uint extModel = (eax >> 16) & 0xF;
            uint extFamily = (eax >> 20) & 0xFF;

            uint displayFamily = family == 0xF ? family + extFamily : family;
            uint displayModel = (family == 6 || family == 0xF) ? (extModel << 4) + model : model;
            return (displayFamily, displayModel, stepping);
        }

        /// <summary>
        /// Instruction-set extensions in rough historical order. Kept to the set CPU-Z headlines;
        /// virtualization capability (VT-x / AMD-V) is included because it is the flag people
        /// actually open CPU-Z to check.
        /// </summary>
        public static List<string> DecodeFeatures(CpuIdRegs leaf1, CpuIdRegs leaf7, CpuIdRegs ext1)
        {
            var f = new List<string>();
            if (Bit(leaf1.Edx, 23)) f.Add("MMX");
            if (Bit(leaf1.Edx, 25)) f.Add("SSE");
            if (Bit(leaf1.Edx, 26)) f.Add("SSE2");
            if (Bit(leaf1.Ecx, 0)) f.Add("SSE3");
            if (Bit(leaf1.Ecx, 9)) f.Add("SSSE3");
            if (Bit(leaf1.Ecx, 19)) f.Add("SSE4.1");
            if (Bit(leaf1.Ecx, 20)) f.Add("SSE4.2");
            if (Bit(ext1.Edx, 29)) f.Add("x86-64");
            if (Bit(leaf1.Ecx, 25)) f.Add("AES-NI");
            if (Bit(leaf1.Ecx, 28)) f.Add("AVX");
            if (Bit(leaf1.Ecx, 12)) f.Add("FMA3");
            if (Bit(leaf7.Ebx, 5)) f.Add("AVX2");
            if (Bit(leaf7.Ebx, 3)) f.Add("BMI1");
            if (Bit(leaf7.Ebx, 8)) f.Add("BMI2");
            if (Bit(leaf7.Ebx, 29)) f.Add("SHA");
            if (Bit(leaf7.Ebx, 16)) f.Add("AVX-512");
            if (Bit(leaf1.Ecx, 5)) f.Add("VT-x");
            if (Bit(ext1.Ecx, 2)) f.Add("AMD-V");
            return f;
        }

        private static bool Bit(uint value, int bit) => (value & (1u << bit)) != 0;

        private static void AppendAscii(StringBuilder sb, uint reg)
        {
            sb.Append((char)(reg & 0xFF));
            sb.Append((char)((reg >> 8) & 0xFF));
            sb.Append((char)((reg >> 16) & 0xFF));
            sb.Append((char)((reg >> 24) & 0xFF));
        }
    }
}
