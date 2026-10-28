using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;

namespace RecursiveAI.OneCompress.Filters;

/// <summary>The instruction sets the branch, call and jump filters know.</summary>
[Experimental("ONECOMPRESS006", UrlFormat = "https://github.com/recursive-ai-oss/onearchive-csharp#preview-codecs")]
public enum BranchArchitecture
{
    /// <summary>x86 CALL and JMP (xz filter ID 0x04, "BCJ").</summary>
    X86 = 0x04,

    /// <summary>PowerPC, big endian (0x05).</summary>
    PowerPc = 0x05,

    /// <summary>Itanium, 128-bit bundles (0x06).</summary>
    Ia64 = 0x06,

    /// <summary>ARM, little-endian instructions (0x07).</summary>
    Arm = 0x07,

    /// <summary>ARM Thumb, little-endian instructions (0x08).</summary>
    ArmThumb = 0x08,

    /// <summary>SPARC (0x09).</summary>
    Sparc = 0x09,

    /// <summary>ARM64 (0x0A).</summary>
    Arm64 = 0x0A,

    /// <summary>RISC-V (0x0B).</summary>
    RiscV = 0x0B,
}

/// <summary>
/// A branch/call/jump filter for executable code: it turns the relative target of branch instructions into an absolute one (when
/// encoding) or back (when decoding), so the same call target repeated from different places compresses as the same bytes. The
/// transform is done in place and never changes the size of the data.
/// </summary>
/// <remarks>
/// <para>Feed the data in pieces of any size: <see cref="Convert"/> returns how many leading bytes are final and the caller passes the
/// rest again, followed by new bytes. Bytes at the very end of the data that cannot start an instruction stay as they are. The converted
/// positions count from <see cref="StartOffset"/> and advance by what was converted, so any split gives the same bytes as one call.</para>
/// <para>Format: the xz file format specification, section 5.3.2 (filter IDs, alignment, the 4-byte start offset), and public descriptions
/// of each instruction set's branch encoding. An x86 filter keeps a little state about the previous opcode bytes.</para>
/// </remarks>
[Experimental("ONECOMPRESS006", UrlFormat = "https://github.com/recursive-ai-oss/onearchive-csharp#preview-codecs")]
public sealed class BranchFilter
{
    private static ReadOnlySpan<byte> X86MaskToBitNumber => [0, 1, 2, 2, 3, 3, 3, 3];

    private static ReadOnlySpan<bool> X86MaskAllowed => [true, true, true, false, true, false, false, false];

    private static ReadOnlySpan<byte> Ia64Templates => [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 4, 4, 6, 6, 0, 0, 7, 7, 4, 4, 0, 0, 4, 4, 0, 0];

    private readonly BranchArchitecture _architecture;
    private readonly bool _encode;
    private uint _position;
    private uint _x86State;

    /// <summary>Creates a filter.</summary>
    /// <param name="architecture">The instruction set.</param>
    /// <param name="startOffset">The address of the first byte; it must be a multiple of <see cref="GetAlignment"/>.</param>
    /// <param name="encode">True to make branch targets absolute (compression side), false to make them relative again.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="architecture"/> is not defined, or <paramref name="startOffset"/> is not aligned.</exception>
    public BranchFilter(BranchArchitecture architecture, uint startOffset = 0, bool encode = false)
    {
        int alignment = GetAlignment(architecture);
        if (startOffset % (uint)alignment != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(startOffset), $"The start offset must be a multiple of {alignment} for this architecture.");
        }

        _architecture = architecture;
        _encode = encode;
        StartOffset = startOffset;
        _position = startOffset;
    }

    /// <summary>Gets the address assigned to the first byte.</summary>
    public uint StartOffset { get; }

    /// <summary>Gets the instruction alignment of an architecture in bytes: the start offset must be a multiple of it.</summary>
    /// <param name="architecture">The instruction set.</param>
    /// <returns>1, 2, 4 or 16.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="architecture"/> is not defined.</exception>
    public static int GetAlignment(BranchArchitecture architecture) => architecture switch
    {
        BranchArchitecture.X86 => 1,
        BranchArchitecture.ArmThumb or BranchArchitecture.RiscV => 2,
        BranchArchitecture.PowerPc or BranchArchitecture.Arm or BranchArchitecture.Sparc or BranchArchitecture.Arm64 => 4,
        BranchArchitecture.Ia64 => 16,
        _ => throw new ArgumentOutOfRangeException(nameof(architecture)),
    };

    /// <summary>Starts over at <see cref="StartOffset"/> with no history.</summary>
    public void Reset()
    {
        _position = StartOffset;
        _x86State = 0;
    }

    /// <summary>Converts the branch instructions at the start of <paramref name="data"/> in place.</summary>
    /// <param name="data">The bytes; the leading ones that were converted are overwritten.</param>
    /// <returns>The number of leading bytes that are final. The remaining bytes were not looked at (an instruction may continue past the end) and must be passed again with the bytes that follow them.</returns>
    public int Convert(Span<byte> data)
    {
        int done = _architecture switch
        {
            BranchArchitecture.X86 => X86(data),
            BranchArchitecture.PowerPc => PowerPc(data),
            BranchArchitecture.Ia64 => Ia64(data),
            BranchArchitecture.Arm => Arm(data),
            BranchArchitecture.ArmThumb => ArmThumb(data),
            BranchArchitecture.Sparc => Sparc(data),
            BranchArchitecture.Arm64 => Arm64(data),
            _ => RiscV(data),
        };
        _position += (uint)done;
        return done;
    }

    private static bool IsSignByte(byte b) => b is 0 or 0xFF;

    // x86: CALL (E8) and JMP (E9) with a 32-bit displacement whose top byte is 00 or FF. The mask remembers which of the last
    // three bytes were E8/E9 candidates so that overlapping false matches are skipped the same way by both directions.
    private int X86(Span<byte> data)
    {
        if (data.Length < 5)
        {
            return 0;
        }

        uint mask = _x86State & 7;
        uint ip = _position + 5;
        int prev = -1;
        int pos = 0;
        int limit = data.Length - 4;
        while (true)
        {
            while (pos < limit && (data[pos] & 0xFE) != 0xE8)
            {
                pos++;
            }

            if (pos >= limit)
            {
                break;
            }

            int gap = pos - prev;
            if (gap > 3)
            {
                mask = 0;
            }
            else
            {
                mask = (mask << (gap - 1)) & 7;
                if (mask != 0)
                {
                    byte b = data[pos + 4 - X86MaskToBitNumber[(int)mask]];
                    if (!X86MaskAllowed[(int)mask] || IsSignByte(b))
                    {
                        prev = pos;
                        mask = ((mask << 1) & 7) | 1;
                        pos++;
                        continue;
                    }
                }
            }

            prev = pos;
            if (IsSignByte(data[pos + 4]))
            {
                uint src = BinaryPrimitives.ReadUInt32LittleEndian(data[(pos + 1)..]);
                uint dest;
                while (true)
                {
                    dest = _encode ? ip + (uint)pos + src : src - (ip + (uint)pos);
                    if (mask == 0)
                    {
                        break;
                    }

                    int index = X86MaskToBitNumber[(int)mask] * 8;
                    if (!IsSignByte((byte)(dest >> (24 - index))))
                    {
                        break;
                    }

                    src = dest ^ ((1u << (32 - index)) - 1);
                }

                data[pos + 4] = (byte)~(((dest >> 24) & 1) - 1);
                data[pos + 3] = (byte)(dest >> 16);
                data[pos + 2] = (byte)(dest >> 8);
                data[pos + 1] = (byte)dest;
                pos += 5;
            }
            else
            {
                mask = ((mask << 1) & 7) | 1;
                pos++;
            }
        }

        int tail = pos - prev;
        _x86State = tail > 3 ? 0 : (mask << (tail - 1)) & 7;
        return pos;
    }

    private int Arm(Span<byte> data)
    {
        int i = 0;
        for (; i + 4 <= data.Length; i += 4)
        {
            if (data[i + 3] != 0xEB)
            {
                continue;
            }

            uint src = ((uint)data[i + 2] << 16) | ((uint)data[i + 1] << 8) | data[i];
            src <<= 2;
            uint at = _position + (uint)i + 8;
            uint dest = _encode ? at + src : src - at;
            dest >>= 2;
            data[i + 2] = (byte)(dest >> 16);
            data[i + 1] = (byte)(dest >> 8);
            data[i] = (byte)dest;
        }

        return i;
    }

    private int ArmThumb(Span<byte> data)
    {
        int i = 0;
        for (; i + 4 <= data.Length; i += 2)
        {
            if ((data[i + 1] & 0xF8) != 0xF0 || (data[i + 3] & 0xF8) != 0xF8)
            {
                continue;
            }

            uint src = (((uint)data[i + 1] & 7) << 19) | ((uint)data[i] << 11) | (((uint)data[i + 3] & 7) << 8) | data[i + 2];
            src <<= 1;
            uint at = _position + (uint)i + 4;
            uint dest = _encode ? at + src : src - at;
            dest >>= 1;
            data[i + 1] = (byte)(0xF0 | ((dest >> 19) & 7));
            data[i] = (byte)(dest >> 11);
            data[i + 3] = (byte)(0xF8 | ((dest >> 8) & 7));
            data[i + 2] = (byte)dest;
            i += 2;
        }

        return i;
    }

    private int PowerPc(Span<byte> data)
    {
        int i = 0;
        for (; i + 4 <= data.Length; i += 4)
        {
            if ((data[i] >> 2) != 0x12 || (data[i + 3] & 3) != 1)
            {
                continue;
            }

            uint src = (((uint)data[i] & 3) << 24) | ((uint)data[i + 1] << 16) | ((uint)data[i + 2] << 8) | ((uint)data[i + 3] & ~3u);
            uint at = _position + (uint)i;
            uint dest = _encode ? at + src : src - at;
            data[i] = (byte)(0x48 | ((dest >> 24) & 3));
            data[i + 1] = (byte)(dest >> 16);
            data[i + 2] = (byte)(dest >> 8);
            data[i + 3] = (byte)(((uint)data[i + 3] & 3u) | (dest & ~3u));
        }

        return i;
    }

    private int Sparc(Span<byte> data)
    {
        int i = 0;
        for (; i + 4 <= data.Length; i += 4)
        {
            if (!((data[i] == 0x40 && (data[i + 1] & 0xC0) == 0x00) || (data[i] == 0x7F && (data[i + 1] & 0xC0) == 0xC0)))
            {
                continue;
            }

            uint src = BinaryPrimitives.ReadUInt32BigEndian(data[i..]);
            src <<= 2;
            uint at = _position + (uint)i;
            uint dest = _encode ? at + src : src - at;
            dest >>= 2;
            dest = (((0 - ((dest >> 22) & 1)) << 22) & 0x3FFFFFFF) | (dest & 0x3FFFFF) | 0x40000000;
            BinaryPrimitives.WriteUInt32BigEndian(data[i..], dest);
        }

        return i;
    }

    // Itanium: 128-bit bundles; the template in the low 5 bits says which of the three 41-bit slots hold branch units.
    private int Ia64(Span<byte> data)
    {
        int i = 0;
        for (; i + 16 <= data.Length; i += 16)
        {
            uint mask = Ia64Templates[data[i] & 0x1F];
            int bitPos = 5;
            for (int slot = 0; slot < 3; slot++, bitPos += 41)
            {
                if (((mask >> slot) & 1) == 0)
                {
                    continue;
                }

                int bytePos = bitPos >> 3;
                int bitRes = bitPos & 7;
                ulong instruction = 0;
                for (int j = 0; j < 6; j++)
                {
                    instruction |= (ulong)data[i + j + bytePos] << (8 * j);
                }

                ulong norm = instruction >> bitRes;
                if (((norm >> 37) & 0xF) != 0x5 || ((norm >> 9) & 0x7) != 0)
                {
                    continue;
                }

                uint src = (uint)((norm >> 13) & 0xFFFFF);
                src |= (uint)((norm >> 36) & 1) << 20;
                src <<= 4;
                uint at = _position + (uint)i;
                uint dest = _encode ? at + src : src - at;
                dest >>= 4;
                norm &= ~((ulong)0x8FFFFF << 13);
                norm |= (ulong)(dest & 0xFFFFF) << 13;
                norm |= (ulong)(dest & 0x100000) << (36 - 20);
                instruction &= (1UL << bitRes) - 1;
                instruction |= norm << bitRes;
                for (int j = 0; j < 6; j++)
                {
                    data[i + j + bytePos] = (byte)(instruction >> (8 * j));
                }
            }
        }

        return i;
    }

    private int Arm64(Span<byte> data)
    {
        int i = 0;
        for (; i + 4 <= data.Length; i += 4)
        {
            uint pc = _position + (uint)i;
            uint instr = BinaryPrimitives.ReadUInt32LittleEndian(data[i..]);
            if ((instr >> 26) == 0x25)
            {
                // BL: a 26-bit word offset.
                pc >>= 2;
                if (!_encode)
                {
                    pc = 0 - pc;
                }

                BinaryPrimitives.WriteUInt32LittleEndian(data[i..], 0x94000000 | ((instr + pc) & 0x03FFFFFF));
            }
            else if ((instr & 0x9F000000) == 0x90000000)
            {
                // ADRP: a 21-bit page offset, converted only when it is small enough to look like code.
                uint src = ((instr >> 29) & 3) | ((instr >> 3) & 0x001FFFFC);
                if (((src + 0x00020000) & 0x001C0000) != 0)
                {
                    continue;
                }

                pc >>= 12;
                if (!_encode)
                {
                    pc = 0 - pc;
                }

                uint dest = src + pc;
                instr &= 0x9000001F;
                instr |= (dest & 3) << 29;
                instr |= (dest & 0x0003FFFC) << 3;
                instr |= (0u - (dest & 0x00020000)) & 0x00E00000;
                BinaryPrimitives.WriteUInt32LittleEndian(data[i..], instr);
            }
        }

        return i;
    }

    // RISC-V: JAL with a link register (x1 or x5), and AUIPC followed by an instruction that uses the same register (the usual
    // way to reach a far target, with JALR, a load or an ADDI). The reference tool's encoder was observed as a black box to
    // establish the rules below. Positions are examined every two bytes while eight bytes are available; a JAL that matches
    // moves on by 4, a pair by 8, an AUIPC that is not a pair by 6 (or by 4 when its register is x0 or x2).
    //
    // A converted pair is stored as the marker "auipc x2" whose upper 20 bits carry the low 20 bits of the second instruction, followed by
    // the absolute target as a big-endian word. An original "auipc x2" that looks like such a marker is stored escaped, as an AUIPC
    // of another register followed by a word that looks like the second instruction of a pair, which no encoder output otherwise has.
    private int RiscV(Span<byte> data)
    {
        int i = 0;
        while (i + 8 <= data.Length)
        {
            uint pc = _position + (uint)i;
            byte b0 = data[i];
            if (b0 == 0xEF)
            {
                byte b1 = data[i + 1];
                if ((b1 & 0x0D) != 0)
                {
                    i += 2;
                    continue;
                }

                byte b2 = data[i + 2];
                byte b3 = data[i + 3];
                if (_encode)
                {
                    // The 21-bit JAL offset is gathered from its scattered fields, made absolute, and stored as a plain big-endian field.
                    uint addr = (((uint)b1 & 0xF0) << 8) | (((uint)b2 & 0x0F) << 16) | (((uint)b2 & 0x10) << 7) | (((uint)b2 & 0xE0) >> 4)
                        | (((uint)b3 & 0x7F) << 4) | (((uint)b3 & 0x80) << 13);
                    addr += pc;
                    data[i + 1] = (byte)(((uint)b1 & 0x0F) | ((addr >> 13) & 0xF0));
                    data[i + 2] = (byte)(addr >> 9);
                    data[i + 3] = (byte)(addr >> 1);
                }
                else
                {
                    uint addr = (((uint)b1 & 0xF0) << 13) | ((uint)b2 << 9) | ((uint)b3 << 1);
                    addr -= pc;
                    data[i + 1] = (byte)(((uint)b1 & 0x0F) | ((addr >> 8) & 0xF0));
                    data[i + 2] = (byte)(((addr >> 16) & 0x0F) | ((addr >> 7) & 0x10) | ((addr << 4) & 0xE0));
                    data[i + 3] = (byte)(((addr >> 4) & 0x7F) | ((addr >> 13) & 0x80));
                }

                i += 4;
                continue;
            }

            if ((b0 & 0x7F) != 0x17)
            {
                i += 2;
                continue;
            }

            uint inst = BinaryPrimitives.ReadUInt32LittleEndian(data[i..]);
            if ((inst & 0xE80) != 0)
            {
                // AUIPC with a register other than x0 and x2.
                uint inst2 = BinaryPrimitives.ReadUInt32LittleEndian(data[(i + 4)..]);
                if ((((inst >> 7) ^ (inst2 >> 15)) & 31) != 0 || (inst2 & 3) != 3)
                {
                    i += 6;
                    continue;
                }

                if (_encode)
                {
                    uint addr = (inst & 0xFFFFF000) + (uint)((int)inst2 >> 20) + pc;
                    BinaryPrimitives.WriteUInt32LittleEndian(data[i..], 0x117 | (inst2 << 12));
                    BinaryPrimitives.WriteUInt32BigEndian(data[(i + 4)..], addr);
                }
                else
                {
                    // Only an escaped marker looks like this.
                    uint original = (inst & 0xFFFFF000) + (inst2 >> 20);
                    BinaryPrimitives.WriteUInt32LittleEndian(data[i..], 0x117 | (inst2 << 12));
                    BinaryPrimitives.WriteUInt32LittleEndian(data[(i + 4)..], original);
                }

                i += 8;
                continue;
            }

            // AUIPC x0 or x2: only a word that looks like a marker (x2, bits 13:12 set, a source register other than x0 and x2) matters.
            if ((inst & 0x3FFF) != 0x3117 || ((inst >> 27) & 0x1D) == 0)
            {
                i += 4;
                continue;
            }

            uint rs = inst >> 27;
            if (_encode)
            {
                uint second = BinaryPrimitives.ReadUInt32LittleEndian(data[(i + 4)..]);
                BinaryPrimitives.WriteUInt32LittleEndian(data[i..], 0x17 | (rs << 7) | (second & 0xFFFFF000));
                BinaryPrimitives.WriteUInt32LittleEndian(data[(i + 4)..], (inst >> 12) | (second << 20));
            }
            else
            {
                uint addr = BinaryPrimitives.ReadUInt32BigEndian(data[(i + 4)..]) - pc;
                BinaryPrimitives.WriteUInt32LittleEndian(data[(i + 4)..], (inst >> 12) | (addr << 20));
                BinaryPrimitives.WriteUInt32LittleEndian(data[i..], 0x17 | (rs << 7) | ((addr + 0x800) & 0xFFFFF000));
            }

            i += 8;
        }

        return i;
    }
}
