using System.Text;

// RE helper: snapshot every object that starts with one vtable (heap scan by 8-byte value), then diff by address.
// Used to find which object family FMRTE writes when the field is not in the competition object itself.
//   memscan objsnap <file> <vtableRvaHex> <size>     e.g. objsnap C:\Temp\stage.tsv 4D7C7B8 464
//   memscan objdiff <file> <vtableRvaHex> <size>
static partial class Program
{
    static List<(ulong Address, byte[] Bytes)> ObjectsWithVtable(int vtableRva, int size)
    {
        ulong wanted = ModBase + (ulong)vtableRva;
        var found = new List<(ulong, byte[])>();
        foreach (var (baseAddr, d) in Regions(false))
            for (int o = 0; o + size <= d.Length; o += 8)
                if (BitConverter.ToUInt64(d, o) == wanted)
                    found.Add((baseAddr + (ulong)o, d.AsSpan(o, size).ToArray()));
        return found;
    }

    // findbytes <hexPattern> [maxHits]: every heap occurrence of an exact byte pattern, e.g. a packed game date.
    static void FindBytes(string hex, int maxHits = 40)
    {
        // '?' bytes are wildcards, so a masked signature can match flag bits.
        var needle = new byte[hex.Length / 2];
        var mask = new bool[needle.Length];
        for (int i = 0; i < needle.Length; i++)
        {
            string pair = hex.Substring(i * 2, 2);
            if (pair == "??") { mask[i] = true; continue; }
            needle[i] = Convert.ToByte(pair, 16);
        }
        bool Match(byte[] d, int at)
        {
            for (int i = 0; i < needle.Length; i++)
                if (!mask[i] && d[at + i] != needle[i]) return false;
            return true;
        }
        int hits = 0;
        foreach (var (baseAddr, d) in Regions(false))
        {
            for (int at = 0; at + needle.Length <= d.Length; at++)
            {
                if (!Match(d, at)) continue;
                Console.WriteLine($"0x{baseAddr + (ulong)at:X}");
                if (++hits >= maxHits) return;
            }
        }
        Console.WriteLine($"{hits} hits");
    }

    // link <compAddrHex> <targetLoHex> <targetHiHex> <window>: find qwords holding compAddr whose surrounding window
    // also holds a pointer into [lo,hi). Prints the containing range for both.
    static void FindLink(ulong comp, ulong lo, ulong hi, int window)
    {
        int hits = 0;
        foreach (var (baseAddr, d) in Regions(false))
            for (int o = 0; o + 8 <= d.Length; o += 8)
            {
                if (BitConverter.ToUInt64(d, o) != comp) continue;
                int start = Math.Max(0, o - window), end = Math.Min(d.Length, o + window);
                for (int k = start; k + 8 <= end; k += 8)
                {
                    ulong q = BitConverter.ToUInt64(d, k);
                    if (q < lo || q >= hi) continue;
                    Console.WriteLine($"comp at 0x{baseAddr + (ulong)o:X} (object-ish 0x{baseAddr + (ulong)o - (ulong)(o % 8):X}) target 0x{q:X} at {k - o:+#;-#;0} (0x{baseAddr + (ulong)k:X})");
                    if (++hits >= 40) return;
                }
            }
        Console.WriteLine($"{hits} links");
    }

    static void ObjSnap(string file, int vtableRva, int size)    {
        var objects = ObjectsWithVtable(vtableRva, size);
        using var w = new StreamWriter(file, false, Encoding.ASCII);
        foreach (var (address, bytes) in objects) w.WriteLine($"{address:X}\t{Convert.ToHexString(bytes)}");
        Console.WriteLine($"{objects.Count} objects with vtable module+0x{vtableRva:X} -> {file}");
    }

    static void ObjDiff(string file, int vtableRva, int size)
    {
        var before = new Dictionary<ulong, byte[]>();
        foreach (var line in File.ReadLines(file))
        {
            var parts = line.Split('\t');
            if (parts.Length == 2 && ulong.TryParse(parts[0], System.Globalization.NumberStyles.HexNumber, null, out ulong address))
                before[address] = Convert.FromHexString(parts[1]);
        }
        var now = ObjectsWithVtable(vtableRva, size).ToDictionary(x => x.Address, x => x.Bytes);
        int changed = 0;
        foreach (var (address, bytes) in now)
        {
            if (!before.TryGetValue(address, out var old)) { Console.WriteLine($"new object 0x{address:X}"); continue; }
            var diffs = Enumerable.Range(0, old.Length).Where(i => old[i] != bytes[i]).ToArray();
            if (diffs.Length == 0) continue;
            changed++;
            Console.WriteLine($"0x{address:X}: {diffs.Length} byte(s) differ");
            int at = 0;
            while (at < diffs.Length)
            {
                int start = diffs[at], end = start;
                while (at + 1 < diffs.Length && diffs[at + 1] == end + 1) { at++; end++; }
                at++;
                Console.WriteLine($"  +0x{start:X3} ({end - start + 1}) {Convert.ToHexString(old, start, end - start + 1)} -> {Convert.ToHexString(bytes, start, end - start + 1)}");
            }
        }
        int vanished = before.Keys.Count(a => !now.ContainsKey(a));
        Console.WriteLine($"{changed} objects changed, {vanished} gone, {now.Count} now");
    }

    // vtptr <vtableRvaHex> <size>: per-qword statistics over every object that starts with the vtable. Counts zero,
    // module pointers, heap pointers and the pointers that themselves point at a (begin, end, capacity) vector header,
    // plus offsets whose own three qwords form a vector. Read only; used to tell a contract's owned lists from its fields.
    static void VtPtrStats(int vtableRva, int size)
    {
        var objects = ObjectsWithVtable(vtableRva, size);
        int words = size / 8;
        var zero = new int[words];
        var module = new int[words];
        var heap = new int[words];
        var other = new int[words];
        var vectorHeader = new int[words];
        var vectorTriple = new int[words];
        var sample = new ulong[words];

        bool InModule(ulong v) => v >= ModBase && v < ModEnd;
        bool IsPointer(ulong v) => v > 0x10000 && v < 0x7FFFFFFFFFFF && !InModule(v);
        bool TryRead(ulong address, byte[] buffer) =>
            Native.ReadProcessMemory(H, (nint)address, buffer, buffer.Length, out var read) && read == buffer.Length;
        bool VectorAt(byte[] d, int at)
        {
            if (at + 24 > d.Length) return false;
            ulong begin = BitConverter.ToUInt64(d, at), end = BitConverter.ToUInt64(d, at + 8), cap = BitConverter.ToUInt64(d, at + 16);
            if (begin == 0) return end == 0 && cap == 0;
            if (!IsPointer(begin) || end < begin || cap < end) return false;
            return cap - begin <= 0x1000000 && (end - begin) % 4 == 0 && (cap - begin) % 4 == 0;
        }
        bool HeaderAt(ulong address)
        {
            if (!IsPointer(address)) return false;
            var b = new byte[24];
            return TryRead(address, b) && VectorAt(b, 0);
        }

        foreach (var (_, d) in objects)
        {
            for (int w = 0; w < words; w++)
            {
                ulong v = BitConverter.ToUInt64(d, w * 8);
                if (v == 0) zero[w]++;
                else if (InModule(v)) module[w]++;
                else if (IsPointer(v)) { heap[w]++; if (HeaderAt(v)) vectorHeader[w]++; }
                else other[w]++;
                if (v != 0 && sample[w] == 0) sample[w] = v;
            }
            for (int w = 0; w + 3 <= words; w++)
                if (VectorAt(d, w * 8)) vectorTriple[w]++;
        }

        Console.WriteLine($"objects={objects.Count} vtable module+0x{vtableRva:X} size 0x{size:X}");
        Console.WriteLine($"{"off",-6} {"zero",6} {"module",6} {"heap",6} {"other",6} {"vecHdr",7} {"vecTriple",9}  sample");
        for (int w = 0; w < words; w++)
        {
            if (heap[w] == 0 && module[w] == 0 && vectorHeader[w] == 0 && vectorTriple[w] == 0) continue;
            Console.WriteLine($"+0x{w * 8:X3} {zero[w],6} {module[w],6} {heap[w],6} {other[w],6} {vectorHeader[w],7} {vectorTriple[w],9}  0x{sample[w]:X}");
        }
    }
}
