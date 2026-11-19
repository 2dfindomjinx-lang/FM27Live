using System.Buffers.Binary;
using System.Text;
using Fm27Editor.Core.Memory;

namespace Fm27Editor.Core.Offsets;

/// <summary>
/// A copy of the mapped module image (or its string/data segments) taken from the running game, indexed by RVA.
/// Unread pages stay zero so callers can scan safely.
/// </summary>
public sealed class ModuleImage
{
    public ulong ModuleBase { get; }
    public byte[] Bytes { get; }
    public IReadOnlyList<(int Start, int End)> WritableDataRanges { get; }
    public IReadOnlyList<(int Start, int End)> ConstRanges { get; }
    public IReadOnlyList<(int Start, int End)> StringRanges { get; }
    public uint? PeTimestamp { get; }

    ModuleImage(ulong moduleBase, byte[] bytes,
        IReadOnlyList<(int Start, int End)>? writableDataRanges = null,
        IReadOnlyList<(int Start, int End)>? constRanges = null,
        IReadOnlyList<(int Start, int End)>? stringRanges = null,
        uint? peTimestamp = null)
    {
        ModuleBase = moduleBase;
        Bytes = bytes;
        var full = new (int, int)[] { (0, bytes.Length) };
        WritableDataRanges = writableDataRanges is { Count: > 0 } ? writableDataRanges : full;
        ConstRanges = constRanges is { Count: > 0 } ? constRanges : full;
        StringRanges = stringRanges is { Count: > 0 } ? stringRanges : full;
        PeTimestamp = peTimestamp;
    }

    public static ModuleImage FromBytes(ulong moduleBase, byte[] bytes,
        IReadOnlyList<(int Start, int End)>? writableDataRanges = null,
        IReadOnlyList<(int Start, int End)>? constRanges = null,
        IReadOnlyList<(int Start, int End)>? stringRanges = null)
        => new(moduleBase, bytes, writableDataRanges, constRanges, stringRanges);

    public static ModuleImage Read(IProcessMemory memory, ulong moduleBase, int size)
    {
        if (size <= 0 || size > 512 * 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(size));
        var bytes = new byte[size];
        const int chunk = 1 << 18; // 256 KB chunks reduce helper IPC round-trips
        for (int offset = 0; offset < size; offset += chunk)
            memory.TryRead(moduleBase + (ulong)offset, bytes.AsSpan(offset, Math.Min(chunk, size - offset)));
        return ParseMetadata(moduleBase, bytes);
    }

    /// <summary>
    /// Reads headers plus string/const/data sections needed for RTTI, build string, and root discovery,
    /// skipping executable code pages (<c>__TEXT.__text</c> / <c>.text</c>) to cut discovery latency by ~10x.
    /// </summary>
    public static ModuleImage ReadForDiscovery(IProcessMemory memory, ulong moduleBase, int size)
    {
        if (size <= 0 || size > 512 * 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(size));
        var bytes = new byte[size];
        int headerLen = Math.Min(0x4000, size);
        if (!memory.TryRead(moduleBase, bytes.AsSpan(0, headerLen)))
            return new ModuleImage(moduleBase, bytes);

        if (TryParseMachOLayout(bytes, size, out var writable, out var consts, out var strings, out var extra))
        {
            foreach (var (s, e) in MergeRanges([.. strings, .. consts, .. writable, .. extra]))
                ReadSpan(memory, moduleBase, bytes, s, e - s);
            return new ModuleImage(moduleBase, bytes, writable, consts, strings);
        }

        if (TryParsePeLayout(bytes, size, out writable, out consts, out strings, out uint? peTs))
        {
            foreach (var (s, e) in MergeRanges([.. strings, .. consts, .. writable]))
                ReadSpan(memory, moduleBase, bytes, s, e - s);
            return new ModuleImage(moduleBase, bytes, writable, consts, strings, peTs);
        }

        return Read(memory, moduleBase, size);
    }

    static ModuleImage ParseMetadata(ulong moduleBase, byte[] bytes)
    {
        if (TryParseMachOLayout(bytes, bytes.Length, out var writable, out var consts, out var strings, out _))
            return new ModuleImage(moduleBase, bytes, writable, consts, strings);
        if (TryParsePeLayout(bytes, bytes.Length, out writable, out consts, out strings, out uint? peTs))
            return new ModuleImage(moduleBase, bytes, writable, consts, strings, peTs);
        return new ModuleImage(moduleBase, bytes);
    }

    static void ReadSpan(IProcessMemory memory, ulong moduleBase, byte[] bytes, int start, int length)
    {
        const int chunk = 1 << 18;
        int end = Math.Min(bytes.Length, start + length);
        for (int off = Math.Max(0, start); off < end; off += chunk)
            memory.TryRead(moduleBase + (ulong)off, bytes.AsSpan(off, Math.Min(chunk, end - off)));
    }

    static List<(int Start, int End)> MergeRanges(List<(int Start, int End)> ranges)
    {
        if (ranges.Count == 0) return [];
        var sorted = ranges.Where(r => r.End > r.Start).OrderBy(r => r.Start).ToList();
        if (sorted.Count == 0) return [];
        var merged = new List<(int Start, int End)> { sorted[0] };
        for (int i = 1; i < sorted.Count; i++)
        {
            var last = merged[^1];
            if (sorted[i].Start <= last.End)
                merged[^1] = (last.Start, Math.Max(last.End, sorted[i].End));
            else
                merged.Add(sorted[i]);
        }
        return merged;
    }

    internal static bool TryParseMachOLayout(ReadOnlySpan<byte> header, int imageSize,
        out List<(int Start, int End)> writableData,
        out List<(int Start, int End)> constSections,
        out List<(int Start, int End)> stringSections,
        out List<(int Start, int End)> otherDataSections)
    {
        writableData = [];
        constSections = [];
        stringSections = [];
        otherDataSections = [];
        if (header.Length < 32 || BinaryPrimitives.ReadUInt32LittleEndian(header) != 0xFEEDFACFu)
            return false;

        uint ncmds = BinaryPrimitives.ReadUInt32LittleEndian(header[16..]);
        uint cmdsSize = BinaryPrimitives.ReadUInt32LittleEndian(header[20..]);
        if (cmdsSize == 0 || 32 + (long)cmdsSize > header.Length) return false;

        ulong textVm = 0;
        bool hasText = false;
        int off = 32;
        int cmdsEnd = 32 + (int)cmdsSize;
        var parsedSegs = new List<(string Name, ulong Vm, ulong Size, uint InitProt)>();
        var parsedSects = new List<(string Seg, string Sect, ulong Addr, ulong Size)>();

        for (uint i = 0; i < ncmds && off + 8 <= cmdsEnd; i++)
        {
            uint cmd = BinaryPrimitives.ReadUInt32LittleEndian(header[off..]);
            uint sz = BinaryPrimitives.ReadUInt32LittleEndian(header[(off + 4)..]);
            if (sz < 8 || off + (long)sz > cmdsEnd) break;
            if (cmd == 0x19 && sz >= 72) // LC_SEGMENT_64
            {
                string segName = ReadFixedAscii(header.Slice(off + 8, 16));
                if (segName != "__PAGEZERO")
                {
                    ulong vm = BinaryPrimitives.ReadUInt64LittleEndian(header[(off + 24)..]);
                    ulong vmsize = BinaryPrimitives.ReadUInt64LittleEndian(header[(off + 32)..]);
                    uint initProt = BinaryPrimitives.ReadUInt32LittleEndian(header[(off + 60)..]);
                    uint nsects = BinaryPrimitives.ReadUInt32LittleEndian(header[(off + 64)..]);
                    if (segName == "__TEXT" && !hasText) { textVm = vm; hasText = true; }
                    parsedSegs.Add((segName, vm, vmsize, initProt));
                    int so = off + 72;
                    for (uint s = 0; s < nsects && so + 80 <= off + (int)sz; s++, so += 80)
                    {
                        string sname = ReadFixedAscii(header.Slice(so, 16));
                        string sseg = ReadFixedAscii(header.Slice(so + 16, 16));
                        ulong saddr = BinaryPrimitives.ReadUInt64LittleEndian(header[(so + 32)..]);
                        ulong ssize = BinaryPrimitives.ReadUInt64LittleEndian(header[(so + 40)..]);
                        parsedSects.Add((sseg, sname, saddr, ssize));
                    }
                }
            }
            off += (int)sz;
        }
        if (!hasText) return false;

        foreach (var (name, vm, vmsize, initProt) in parsedSegs)
        {
            long rva = (long)(vm - textVm);
            if (rva < 0 || rva >= imageSize) continue;
            int start = (int)rva;
            int end = (int)Math.Min(imageSize, rva + (long)vmsize);
            if (name == "__DATA" || (initProt & 2) != 0 && name != "__TEXT")
                writableData.Add((start, end));
            else if (name == "__DATA_CONST")
                otherDataSections.Add((start, end));
        }

        foreach (var (seg, sect, addr, ssize) in parsedSects)
        {
            long rva = (long)(addr - textVm);
            if (rva < 0 || rva >= imageSize || ssize == 0) continue;
            int start = (int)rva;
            int end = (int)Math.Min(imageSize, rva + (long)ssize);
            if (seg == "__TEXT" && sect is "__cstring" or "__const" or "__ustring")
                stringSections.Add((start, end));
            else if (seg is "__DATA_CONST" or "__DATA" && sect == "__const")
                constSections.Add((start, end));
            else if (seg is "__DATA_CONST" or "__DATA")
                otherDataSections.Add((start, end));
        }
        return true;
    }

    static bool TryParsePeLayout(ReadOnlySpan<byte> header, int imageSize,
        out List<(int Start, int End)> writableData,
        out List<(int Start, int End)> constSections,
        out List<(int Start, int End)> stringSections,
        out uint? peTimestamp)
    {
        writableData = [];
        constSections = [];
        stringSections = [];
        peTimestamp = null;
        if (header.Length < 64 || header[0] != (byte)'M' || header[1] != (byte)'Z') return false;
        uint peOff = BinaryPrimitives.ReadUInt32LittleEndian(header[0x3C..]);
        if (peOff + 24 > (uint)header.Length) return false;
        if (BinaryPrimitives.ReadUInt32LittleEndian(header[(int)peOff..]) != 0x00004550u) return false;
        ushort numSections = BinaryPrimitives.ReadUInt16LittleEndian(header[((int)peOff + 6)..]);
        peTimestamp = BinaryPrimitives.ReadUInt32LittleEndian(header[((int)peOff + 8)..]);
        ushort optSize = BinaryPrimitives.ReadUInt16LittleEndian(header[((int)peOff + 20)..]);
        int sectOff = (int)peOff + 24 + optSize;
        if (sectOff < 0 || sectOff + numSections * 40 > header.Length) return false;

        for (int i = 0; i < numSections; i++, sectOff += 40)
        {
            string name = ReadFixedAscii(header.Slice(sectOff, 8));
            uint vsize = BinaryPrimitives.ReadUInt32LittleEndian(header[(sectOff + 8)..]);
            uint rva = BinaryPrimitives.ReadUInt32LittleEndian(header[(sectOff + 12)..]);
            uint chars = BinaryPrimitives.ReadUInt32LittleEndian(header[(sectOff + 36)..]);
            int start = (int)Math.Min(imageSize, (long)rva);
            int end = (int)Math.Min(imageSize, (long)rva + vsize);
            if (end <= start) continue;
            bool isExec = (chars & 0x20000000u) != 0;
            bool isWrite = (chars & 0x80000000u) != 0;
            if (isExec) continue;
            if (isWrite) writableData.Add((start, end));
            else
            {
                constSections.Add((start, end));
                stringSections.Add((start, end));
            }
        }
        return writableData.Count > 0 || constSections.Count > 0;
    }

    static string ReadFixedAscii(ReadOnlySpan<byte> bytes)
    {
        int end = bytes.IndexOf((byte)0);
        if (end < 0) end = bytes.Length;
        return Encoding.ASCII.GetString(bytes[..end]);
    }

    public uint U32(int offset) => BitConverter.ToUInt32(Bytes, offset);
    public ulong U64(int offset) => BitConverter.ToUInt64(Bytes, offset);
    public bool InImage(ulong address) => address >= ModuleBase && address - ModuleBase < (ulong)Bytes.Length;
    public int RvaOf(ulong address) => checked((int)(address - ModuleBase));
}

public sealed record RttiVtableEntry(uint VtableRva, string CanonicalName, string MangledName, int SubobjectOffset);

/// <summary>
/// Universal RTTI index of the module (Itanium C++ ABI on macOS ARM64/x64 and MSVC on Windows x64):
/// maps between class names (canonical or mangled), subobject offsets, and vtable RVAs.
/// </summary>
public sealed class RttiIndex
{
    const ulong Arm64NonUniqueBit = 1UL << 63;

    readonly Dictionary<string, List<uint>> byMangledName;
    readonly Dictionary<string, List<RttiVtableEntry>> byCanonicalName;
    readonly Dictionary<uint, RttiVtableEntry> byVtable;
    readonly List<RttiVtableEntry> allEntries;

    RttiIndex(
        Dictionary<string, List<uint>> byMangledName,
        Dictionary<string, List<RttiVtableEntry>> byCanonicalName,
        Dictionary<uint, RttiVtableEntry> byVtable,
        List<RttiVtableEntry> allEntries)
    {
        this.byMangledName = byMangledName;
        this.byCanonicalName = byCanonicalName;
        this.byVtable = byVtable;
        this.allEntries = allEntries;
    }

    public IReadOnlyDictionary<string, List<uint>> ByName => byMangledName;
    public IReadOnlyList<RttiVtableEntry> AllEntries => allEntries;

    public RttiVtableEntry? EntryOf(uint vtableRva) => byVtable.GetValueOrDefault(vtableRva);

    /// <summary>Mangled class name of one vtable RVA, or null when the slot does not carry a decodable RTTI locator.</summary>
    public string? NameOf(uint vtableRva) => byVtable.GetValueOrDefault(vtableRva)?.MangledName;

    public string? CanonicalNameOf(uint vtableRva) => byVtable.GetValueOrDefault(vtableRva)?.CanonicalName;

    public IReadOnlyList<uint> Vtables(string className)
    {
        if (byMangledName.TryGetValue(className, out var exact)) return exact;
        string canonical = Canonicalize(className);
        return byCanonicalName.TryGetValue(canonical, out var entries)
            ? entries.Select(e => e.VtableRva).Distinct().OrderBy(v => v).ToArray()
            : [];
    }

    public IReadOnlyList<uint> Vtables(string className, int subobjectOffset)
    {
        string canonical = Canonicalize(className);
        if (!byCanonicalName.TryGetValue(canonical, out var entries)) return [];
        return entries
            .Where(e => subobjectOffset < 0 || e.SubobjectOffset == subobjectOffset)
            .Select(e => e.VtableRva)
            .Distinct()
            .OrderBy(v => v)
            .ToArray();
    }

    public static RttiIndex Build(ModuleImage image)
    {
        if (image.Bytes.Length >= 4 && BitConverter.ToUInt32(image.Bytes, 0) == 0xFEEDFACFu)
            return BuildItaniumFromMappedImage(image);
        return BuildMsvc(image);
    }

    public static RttiIndex BuildMsvc(ModuleImage image)
    {
        var bytes = image.Bytes;
        // 1. Type descriptors: ".?AV<name>\0" or ".?AU<name>\0" with a 16-byte header in front.
        var descriptors = new Dictionary<uint, string>();
        foreach (var (rangeStart, rangeEnd) in image.StringRanges)
        {
            int start = Math.Max(16, rangeStart);
            int end = Math.Min(bytes.Length, rangeEnd);
            for (int i = start; i + 8 < end; i++)
            {
                if (bytes[i] != (byte)'.' || bytes[i + 1] != (byte)'?' || bytes[i + 2] != (byte)'A' ||
                    (bytes[i + 3] != (byte)'V' && bytes[i + 3] != (byte)'U')) continue;
                int length = 4;
                while (length < 250 && i + length < end && bytes[i + length] != 0) length++;
                if (length >= 250 || i + length >= end) continue;
                descriptors.TryAdd((uint)(i - 16), Encoding.ASCII.GetString(bytes, i, length));
                i += length;
            }
        }
        // 2. Complete object locators: { signature 1, offset, cdOffset, typeDescriptor, classDescriptor, self }.
        var locators = new Dictionary<uint, (string Name, int Offset)>();
        foreach (var (rangeStart, rangeEnd) in image.ConstRanges)
        {
            int start = Math.Max(12, rangeStart);
            int end = Math.Min(bytes.Length, rangeEnd);
            for (int p = start; p + 12 <= end; p += 4)
            {
                if (!descriptors.TryGetValue(BitConverter.ToUInt32(bytes, p), out string? name)) continue;
                int col = p - 12;
                if (col + 24 > bytes.Length) continue;
                if (BitConverter.ToUInt32(bytes, col) != 1) continue;
                if (BitConverter.ToUInt32(bytes, col + 0x14) != (uint)col) continue;
                int subobjectOffset = (int)BitConverter.ToUInt32(bytes, col + 4);
                locators[(uint)col] = (name, subobjectOffset);
            }
        }
        // 3. Vtables: the slot right before the first virtual method points at the locator.
        var entries = new List<RttiVtableEntry>();
        foreach (var (rangeStart, rangeEnd) in image.ConstRanges)
        {
            int start = Math.Max(0, rangeStart & ~7);
            int end = Math.Min(bytes.Length, rangeEnd);
            for (int q = start; q + 8 <= end; q += 8)
            {
                ulong value = BitConverter.ToUInt64(bytes, q);
                if (value < image.ModuleBase) continue;
                ulong rva = value - image.ModuleBase;
                if (rva > uint.MaxValue || !locators.TryGetValue((uint)rva, out var col)) continue;
                uint vtable = (uint)(q + 8);
                entries.Add(new RttiVtableEntry(vtable, DemangleMsvc(col.Name), col.Name, col.Offset));
            }
        }
        return CreateIndex(entries);
    }

    public static RttiIndex BuildItaniumFromMappedImage(ModuleImage image)
    {
        var bytes = image.Bytes;
        ulong moduleBase = image.ModuleBase;
        ulong moduleEnd = moduleBase + (ulong)bytes.Length;

        bool InStringSection(int rva) => image.StringRanges.Any(r => rva >= r.Start && rva < r.End);

        string? TryReadMangled(int rva)
        {
            if (rva < 0 || rva >= bytes.Length || !InStringSection(rva)) return null;
            int len = 0;
            while (len < 250 && rva + len < bytes.Length && bytes[rva + len] != 0)
            {
                byte b = bytes[rva + len];
                if (b < 0x20 || b >= 0x7F) return null;
                len++;
            }
            if (len < 2 || len >= 250 || rva + len >= bytes.Length) return null;
            byte first = bytes[rva];
            if (first != (byte)'N' && first != (byte)'Z' && (first < (byte)'1' || first > (byte)'9')) return null;
            return Encoding.ASCII.GetString(bytes, rva, len);
        }

        // Pass 1: type_info structs in __DATA_CONST.__const / __DATA.__const.
        // Word 0: external pointer to libc++abi type_info vtable (outside module, nonzero).
        // Word 1: pointer to mangled string in __TEXT (ARM64 may set bit 63 NON_UNIQUE).
        var typeInfos = new Dictionary<uint, string>();
        foreach (var (rangeStart, rangeEnd) in image.ConstRanges)
        {
            int start = Math.Max(0, rangeStart & ~7);
            int end = Math.Min(bytes.Length, rangeEnd);
            for (int off = start; off + 16 <= end; off += 8)
            {
                ulong w0 = BitConverter.ToUInt64(bytes, off);
                if (w0 == 0 || (w0 >= moduleBase && w0 < moduleEnd)) continue;
                ulong w1 = BitConverter.ToUInt64(bytes, off + 8) & ~Arm64NonUniqueBit;
                if (w1 < moduleBase || w1 >= moduleEnd) continue;
                int nameRva = (int)(w1 - moduleBase);
                if (TryReadMangled(nameRva) is { } mangled)
                    typeInfos[(uint)off] = mangled;
            }
        }

        // Pass 2: vtable headers { offset_to_top (i64), type_info* } followed by vtable address point.
        var entries = new List<RttiVtableEntry>();
        foreach (var (rangeStart, rangeEnd) in image.ConstRanges)
        {
            int start = Math.Max(16, (rangeStart + 7) & ~7);
            int end = Math.Min(bytes.Length, rangeEnd);
            for (int vtOff = start; vtOff + 8 <= end; vtOff += 8)
            {
                ulong tiPtr = BitConverter.ToUInt64(bytes, vtOff - 8);
                if (tiPtr < moduleBase || tiPtr >= moduleEnd) continue;
                uint tiRva = (uint)(tiPtr - moduleBase);
                if (!typeInfos.TryGetValue(tiRva, out string? mangled)) continue;
                long top = BitConverter.ToInt64(bytes, vtOff - 16);
                if (top is < -0x20000 or > 0) continue;
                entries.Add(new RttiVtableEntry((uint)vtOff, DemangleItanium(mangled), mangled, (int)(-top)));
            }
        }
        return CreateIndex(entries);
    }

    /// <summary>
    /// Parses Itanium RTTI from an on-disk Mach-O (thin or universal ARM64 slice) with chained fixups.
    /// Reads only string and __const sections via FileStream (~6 MB instead of ~95 MB).
    /// </summary>
    public static RttiIndex FromMachOFile(string path)
    {
        using var fs = File.OpenRead(path);
        using var br = new BinaryReader(fs);
        byte[] prefix = br.ReadBytes(512);
        if (prefix.Length < 32) throw new InvalidDataException($"File too small: {path}");

        long sliceOffset = 0;
        uint magicLe = BinaryPrimitives.ReadUInt32LittleEndian(prefix);
        uint magicBe = BinaryPrimitives.ReadUInt32BigEndian(prefix);
        if (magicBe == 0xCAFEBABEu)
        {
            uint nfat = BinaryPrimitives.ReadUInt32BigEndian(prefix.AsSpan(4));
            bool found = false;
            for (int i = 0; i < nfat && 8 + (i + 1) * 20 <= prefix.Length; i++)
            {
                uint cputype = BinaryPrimitives.ReadUInt32BigEndian(prefix.AsSpan(8 + i * 20));
                if (cputype == 0x0100000Cu) // CPU_TYPE_ARM64
                {
                    sliceOffset = BinaryPrimitives.ReadUInt32BigEndian(prefix.AsSpan(8 + i * 20 + 8));
                    found = true;
                    break;
                }
            }
            if (!found) throw new InvalidDataException($"No ARM64 slice in universal Mach-O: {path}");
        }
        else if (magicLe != 0xFEEDFACFu)
            throw new InvalidDataException($"Unsupported Mach-O header magic 0x{magicLe:X8}: {path}");

        fs.Position = sliceOffset;
        byte[] header = br.ReadBytes(0x4000);
        if (header.Length < 32 || BinaryPrimitives.ReadUInt32LittleEndian(header) != 0xFEEDFACFu)
            throw new InvalidDataException($"Invalid 64-bit Mach-O slice at 0x{sliceOffset:X}");

        uint ncmds = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(16));
        uint cmdsSize = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(20));
        if (32 + cmdsSize > header.Length)
        {
            fs.Position = sliceOffset;
            header = br.ReadBytes(checked((int)(32 + cmdsSize)));
        }

        ulong textVm = 0;
        bool hasText = false;
        var textStringSects = new List<(ulong Rva, ulong Size, ulong FileOff)>();
        var dataConstSects = new List<(ulong Rva, ulong Size, ulong FileOff)>();

        int off = 32;
        int cmdsEnd = 32 + (int)cmdsSize;
        for (uint i = 0; i < ncmds && off + 8 <= cmdsEnd; i++)
        {
            uint cmd = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(off));
            uint sz = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(off + 4));
            if (sz < 8 || off + (long)sz > cmdsEnd) break;
            if (cmd == 0x19 && sz >= 72)
            {
                string segName = Encoding.ASCII.GetString(header, off + 8, 16).TrimEnd('\0');
                ulong vm = BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(off + 24));
                uint nsects = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(off + 64));
                if (segName == "__TEXT" && !hasText) { textVm = vm; hasText = true; }
                int so = off + 72;
                for (uint s = 0; s < nsects && so + 80 <= off + (int)sz; s++, so += 80)
                {
                    string sname = Encoding.ASCII.GetString(header, so, 16).TrimEnd('\0');
                    string sseg = Encoding.ASCII.GetString(header, so + 16, 16).TrimEnd('\0');
                    ulong saddr = BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(so + 32));
                    ulong ssize = BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(so + 40));
                    uint sfo = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(so + 48));
                    ulong rva = saddr - textVm;
                    if (sseg == "__TEXT" && sname is "__cstring" or "__const")
                        textStringSects.Add((rva, ssize, checked((ulong)(sliceOffset + sfo))));
                    else if (sseg is "__DATA_CONST" or "__DATA" && sname == "__const")
                        dataConstSects.Add((rva, ssize, checked((ulong)(sliceOffset + sfo))));
                }
            }
            off += (int)sz;
        }
        if (!hasText) throw new InvalidDataException($"No __TEXT segment found in {path}");

        var stringBlobs = textStringSects.Select(s =>
        {
            fs.Position = (long)s.FileOff;
            return (s.Rva, s.Size, Data: br.ReadBytes(checked((int)s.Size)));
        }).ToList();

        var constBlobs = dataConstSects.Select(s =>
        {
            fs.Position = (long)s.FileOff;
            return (s.Rva, s.Size, Data: br.ReadBytes(checked((int)s.Size)));
        }).ToList();

        string? ReadStringAtRva(ulong rva)
        {
            foreach (var (sRva, sSize, data) in stringBlobs)
            {
                if (rva < sRva || rva >= sRva + sSize) continue;
                int at = (int)(rva - sRva);
                int end = Array.IndexOf(data, (byte)0, at);
                if (end <= at || end - at >= 250) return null;
                byte first = data[at];
                if (first != (byte)'N' && first != (byte)'Z' && (first < (byte)'1' || first > (byte)'9')) return null;
                for (int k = at; k < end; k++)
                    if (data[k] < 0x20 || data[k] >= 0x7F) return null;
                return Encoding.ASCII.GetString(data, at, end - at);
            }
            return null;
        }

        ulong DecodeRebase(ulong raw)
        {
            if ((raw >> 63) != 0) return ulong.MaxValue;
            ulong target = raw & 0xFFFFFFFFFUL;
            return target >= textVm ? target - textVm : target;
        }

        // Pass 1: chained bind at +0, chained rebase at +8 -> __TEXT mangled string.
        var typeInfos = new Dictionary<ulong, string>();
        foreach (var (cRva, _, data) in constBlobs)
        {
            for (int pos = 0; pos + 16 <= data.Length; pos += 8)
            {
                ulong w0 = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(pos));
                ulong w1 = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(pos + 8));
                if ((w0 >> 63) == 0 || (w1 >> 63) != 0) continue;
                ulong nameRva = DecodeRebase(w1);
                if (ReadStringAtRva(nameRva) is { } mangled)
                    typeInfos[cRva + (ulong)pos] = mangled;
            }
        }

        // Pass 2: { offset_to_top (i64), type_info* } before vtable.
        var entries = new List<RttiVtableEntry>();
        foreach (var (cRva, _, data) in constBlobs)
        {
            for (int pos = 16; pos + 8 <= data.Length; pos += 8)
            {
                ulong wTi = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(pos - 8));
                if ((wTi >> 63) != 0) continue;
                ulong tiRva = DecodeRebase(wTi);
                if (!typeInfos.TryGetValue(tiRva, out string? mangled)) continue;
                long top = BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(pos - 16));
                if (top is < -0x20000 or > 0) continue;
                entries.Add(new RttiVtableEntry((uint)(cRva + (ulong)pos), DemangleItanium(mangled), mangled, (int)(-top)));
            }
        }
        return CreateIndex(entries);
    }

    /// <summary>
    /// Reads RTTI metadata directly from a live process for a single vtable pointer (handles both MSVC PE and Itanium Mach-O).
    /// </summary>
    public static bool TryReadClass(IProcessMemory memory, ulong moduleBase, ulong moduleEnd, ulong vtableAbs,
        out RttiVtableEntry entry)
    {
        entry = default!;
        if (vtableAbs < moduleBase + 16 || vtableAbs >= moduleEnd) return false;
        Span<byte> head = stackalloc byte[16];
        if (!memory.TryRead(vtableAbs - 16, head)) return false;
        ulong colOrTi = BinaryPrimitives.ReadUInt64LittleEndian(head[8..]);
        if (colOrTi < moduleBase || colOrTi >= moduleEnd) return false;
        uint vtRva = (uint)(vtableAbs - moduleBase);

        // Try MSVC COL first: { signature == 1, offset, cdOffset, typeDescriptorRva, classHierarchyRva, selfRva }
        Span<byte> colBuf = stackalloc byte[24];
        if (memory.TryRead(colOrTi, colBuf))
        {
            uint sig = BinaryPrimitives.ReadUInt32LittleEndian(colBuf);
            uint selfRva = BinaryPrimitives.ReadUInt32LittleEndian(colBuf[20..]);
            if (sig == 1 && (ulong)selfRva == colOrTi - moduleBase)
            {
                int subOffset = (int)BinaryPrimitives.ReadUInt32LittleEndian(colBuf[4..]);
                uint typeRva = BinaryPrimitives.ReadUInt32LittleEndian(colBuf[12..]);
                if (ReadCString(memory, moduleBase + typeRva + 16, 200) is { } msvc &&
                    (msvc.StartsWith(".?AV", StringComparison.Ordinal) || msvc.StartsWith(".?AU", StringComparison.Ordinal)))
                {
                    entry = new RttiVtableEntry(vtRva, DemangleMsvc(msvc), msvc, subOffset);
                    return true;
                }
            }
        }

        // Itanium ABI: { offset_to_top (i64), type_info* }; type_info+8 points to mangled name (bit 63 = NON_UNIQUE).
        long top = BinaryPrimitives.ReadInt64LittleEndian(head);
        if (top is >= -0x20000 and <= 0)
        {
            Span<byte> tiBuf = stackalloc byte[16];
            if (memory.TryRead(colOrTi, tiBuf))
            {
                ulong w0 = BinaryPrimitives.ReadUInt64LittleEndian(tiBuf);
                ulong namePtr = BinaryPrimitives.ReadUInt64LittleEndian(tiBuf[8..]) & ~Arm64NonUniqueBit;
                if (w0 != 0 && !(w0 >= moduleBase && w0 < moduleEnd) && namePtr >= moduleBase && namePtr < moduleEnd &&
                    ReadCString(memory, namePtr, 240) is { } itanium && itanium.Length >= 2)
                {
                    entry = new RttiVtableEntry(vtRva, DemangleItanium(itanium), itanium, (int)(-top));
                    return true;
                }
            }
        }
        return false;
    }

    public static string? ReadClassName(IProcessMemory memory, ulong moduleBase, ulong moduleEnd, ulong vtableAbs) =>
        TryReadClass(memory, moduleBase, moduleEnd, vtableAbs, out var entry) ? entry.CanonicalName : null;

    static string? ReadCString(IProcessMemory memory, ulong address, int maxLen)
    {
        var buf = new byte[maxLen];
        if (!memory.TryRead(address, buf)) return null;
        int end = Array.IndexOf(buf, (byte)0);
        if (end <= 0) return null;
        for (int i = 0; i < end; i++)
            if (buf[i] < 0x20 || buf[i] >= 0x7F) return null;
        return Encoding.ASCII.GetString(buf, 0, end);
    }

    static RttiIndex CreateIndex(List<RttiVtableEntry> entries)
    {
        var byMangled = new Dictionary<string, List<uint>>(StringComparer.Ordinal);
        var byCanonical = new Dictionary<string, List<RttiVtableEntry>>(StringComparer.Ordinal);
        var byVt = new Dictionary<uint, RttiVtableEntry>();
        foreach (var e in entries)
        {
            if (!byMangled.TryGetValue(e.MangledName, out var ml)) byMangled[e.MangledName] = ml = [];
            ml.Add(e.VtableRva);
            if (!byCanonical.TryGetValue(e.CanonicalName, out var cl)) byCanonical[e.CanonicalName] = cl = [];
            cl.Add(e);
            byVt.TryAdd(e.VtableRva, e);
        }
        return new RttiIndex(byMangled, byCanonical, byVt, entries);
    }

    public static string Canonicalize(string name)
    {
        if (name.StartsWith(".?AV", StringComparison.Ordinal) || name.StartsWith(".?AU", StringComparison.Ordinal))
            return DemangleMsvc(name);
        if (name.Contains("::", StringComparison.Ordinal)) return name;
        if (name.Length >= 2 && (name[0] == 'N' && name[^1] == 'E' || char.IsAsciiDigit(name[0])))
            return DemangleItanium(name);
        return name;
    }

    public static string DemangleItanium(string mangled)
    {
        if (string.IsNullOrEmpty(mangled)) return mangled;
        if (mangled[0] == 'N')
        {
            int pos = 1;
            var parts = new List<string>();
            while (pos < mangled.Length && mangled[pos] != 'E')
            {
                if (!TryReadLengthPrefixed(mangled, ref pos, out string part)) return mangled;
                parts.Add(part);
            }
            if (pos == mangled.Length - 1 && mangled[pos] == 'E' && parts.Count > 0)
                return string.Join("::", parts);
            return mangled;
        }
        if (char.IsAsciiDigit(mangled[0]))
        {
            int pos = 0;
            if (TryReadLengthPrefixed(mangled, ref pos, out string single) && pos == mangled.Length)
                return single;
        }
        return mangled;
    }

    static bool TryReadLengthPrefixed(string s, ref int pos, out string part)
    {
        part = "";
        int start = pos;
        while (pos < s.Length && char.IsAsciiDigit(s[pos])) pos++;
        if (pos == start || !int.TryParse(s.AsSpan(start, pos - start), out int len) || len <= 0 || pos + len > s.Length)
            return false;
        part = s.Substring(pos, len);
        pos += len;
        return true;
    }

    public static string DemangleMsvc(string mangled)
    {
        if (string.IsNullOrEmpty(mangled)) return mangled;
        string body = mangled;
        if (body.StartsWith(".?AV", StringComparison.Ordinal) || body.StartsWith(".?AU", StringComparison.Ordinal))
            body = body[4..];
        if (body.EndsWith("@@", StringComparison.Ordinal))
            body = body[..^2];
        if (body.Contains("?$", StringComparison.Ordinal)) return mangled;
        var parts = body.Split('@', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return mangled;
        Array.Reverse(parts);
        return string.Join("::", parts);
    }

    public static string ToItanium(string canonical)
    {
        if (string.IsNullOrWhiteSpace(canonical)) return canonical;
        var parts = canonical.Split("::", StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 1) return $"{parts[0].Length}{parts[0]}";
        var sb = new StringBuilder("N");
        foreach (var p in parts) sb.Append(p.Length).Append(p);
        sb.Append('E');
        return sb.ToString();
    }

    public static string ToMsvc(string canonical)
    {
        if (string.IsNullOrWhiteSpace(canonical)) return canonical;
        var parts = canonical.Split("::", StringSplitOptions.RemoveEmptyEntries);
        Array.Reverse(parts);
        return $".?AV{string.Join('@', parts)}@@";
    }
}
