namespace WMT.Core.Vba;

/// <summary>Minimal read-only MS-CFB (OLE2 compound file) reader. Never executes anything.</summary>
public sealed class CompoundFile
{
    const uint EndOfChain = 0xFFFFFFFE, FreeSect = 0xFFFFFFFF;
    static readonly byte[] Signature = { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 };

    public sealed class Entry
    {
        public int Index; public string Name = ""; public byte Type; public int Left = -1, Right = -1, Child = -1;
        public uint StartSector; public long Size;
        public bool IsStorage => Type == 1 || Type == 5;
        public bool IsStream => Type == 2;
    }

    readonly byte[] data;
    readonly int sectorSize, miniSectorSize;
    readonly uint miniCutoff;
    readonly List<uint> fat = new();
    readonly List<uint> miniFat = new();
    readonly byte[] miniStream;
    public IReadOnlyList<Entry> Entries { get; }

    public static bool HasSignature(byte[] bytes) => bytes.Length >= 512 && bytes.AsSpan(0, 8).SequenceEqual(Signature);

    public CompoundFile(byte[] bytes)
    {
        data = bytes;
        if (!HasSignature(bytes)) throw new InvalidDataException("Not a compound file");
        int sectorShift = U16(0x1E), miniShift = U16(0x20);
        if (sectorShift is not (9 or 12) || miniShift != 6) throw new InvalidDataException("Unsupported sector size");
        sectorSize = 1 << sectorShift; miniSectorSize = 1 << miniShift;
        uint numFat = U32(0x2C), firstDir = U32(0x30);
        miniCutoff = U32(0x38);
        uint firstMiniFat = U32(0x3C), numMiniFat = U32(0x40), firstDifat = U32(0x44), numDifat = U32(0x48);

        var fatSectors = new List<uint>();
        for (int i = 0; i < 109 && fatSectors.Count < numFat; i++)
        {
            var s = U32(0x4C + i * 4);
            if (s != FreeSect) fatSectors.Add(s);
        }
        uint difat = firstDifat; int guard = 0;
        while (fatSectors.Count < numFat && difat != EndOfChain && difat != FreeSect && guard++ < numDifat + 1)
        {
            int off = Offset(difat);
            int per = sectorSize / 4 - 1;
            for (int i = 0; i < per && fatSectors.Count < numFat; i++)
            {
                var s = U32(off + i * 4);
                if (s != FreeSect) fatSectors.Add(s);
            }
            difat = U32(off + per * 4);
        }
        foreach (var s in fatSectors)
        {
            int off = Offset(s);
            for (int i = 0; i < sectorSize / 4; i++) fat.Add(U32(off + i * 4));
        }
        if (numMiniFat > 0 && firstMiniFat != EndOfChain)
        {
            var mf = ReadChain(firstMiniFat, long.MaxValue);
            for (int i = 0; i + 4 <= mf.Length; i += 4) miniFat.Add(BitConverter.ToUInt32(mf, i));
        }

        var dirBytes = ReadChain(firstDir, long.MaxValue);
        var list = new List<Entry>();
        for (int i = 0; i + 128 <= dirBytes.Length; i += 128)
        {
            int nameLen = BitConverter.ToUInt16(dirBytes, i + 0x40);
            var e = new Entry
            {
                Index = list.Count,
                Name = nameLen >= 2 && nameLen <= 64 ? System.Text.Encoding.Unicode.GetString(dirBytes, i, nameLen - 2) : "",
                Type = dirBytes[i + 0x42],
                Left = (int)BitConverter.ToUInt32(dirBytes, i + 0x44),
                Right = (int)BitConverter.ToUInt32(dirBytes, i + 0x48),
                Child = (int)BitConverter.ToUInt32(dirBytes, i + 0x4C),
                StartSector = BitConverter.ToUInt32(dirBytes, i + 0x74),
                Size = (long)BitConverter.ToUInt64(dirBytes, i + 0x78)
            };
            if (sectorSize == 512) e.Size &= 0xFFFFFFFF;
            list.Add(e);
        }
        Entries = list;
        if (list.Count == 0 || list[0].Type != 5) throw new InvalidDataException("Root entry missing");
        miniStream = list[0].Size > 0 ? ReadChain(list[0].StartSector, list[0].Size) : Array.Empty<byte>();
    }

    int U16(int o) => BitConverter.ToUInt16(data, o);
    uint U32(int o) => BitConverter.ToUInt32(data, o);
    int Offset(uint sector)
    {
        long off = ((long)sector + 1) * sectorSize;
        if (off + sectorSize > data.Length) throw new InvalidDataException("Sector out of range");
        return (int)off;
    }

    byte[] ReadChain(uint start, long size)
    {
        using var ms = new MemoryStream();
        uint s = start; int guard = 0;
        while (s != EndOfChain && s != FreeSect && ms.Length < size)
        {
            if (guard++ > fat.Count + 1 && fat.Count > 0) throw new InvalidDataException("FAT loop");
            ms.Write(data, Offset(s), sectorSize);
            if (fat.Count == 0) break;
            if (s >= fat.Count) throw new InvalidDataException("FAT index out of range");
            s = fat[(int)s];
        }
        var r = ms.ToArray();
        return size < r.Length ? r.AsSpan(0, (int)size).ToArray() : r;
    }

    byte[] ReadMiniChain(uint start, long size)
    {
        var r = new byte[size];
        uint s = start; long pos = 0; int guard = 0;
        while (s != EndOfChain && pos < size)
        {
            if (guard++ > miniFat.Count + 1) throw new InvalidDataException("Mini FAT loop");
            long off = (long)s * miniSectorSize;
            int n = (int)Math.Min(miniSectorSize, size - pos);
            if (off + n > miniStream.Length) throw new InvalidDataException("Mini sector out of range");
            Array.Copy(miniStream, off, r, pos, n);
            pos += n;
            if (s >= miniFat.Count) break;
            s = miniFat[(int)s];
        }
        return r;
    }

    public byte[] ReadStream(Entry e)
    {
        if (!e.IsStream) throw new InvalidOperationException("Not a stream");
        if (e.Size == 0) return Array.Empty<byte>();
        return e.Size < miniCutoff ? ReadMiniChain(e.StartSector, e.Size) : ReadChain(e.StartSector, e.Size);
    }

    /// <summary>Children of a storage (red-black tree walk, loop-safe).</summary>
    public List<Entry> Children(Entry storage)
    {
        var result = new List<Entry>();
        var seen = new HashSet<int>();
        void Walk(int i)
        {
            if (i < 0 || i >= Entries.Count || !seen.Add(i)) return;
            var e = Entries[i];
            Walk(e.Left); result.Add(e); Walk(e.Right);
        }
        Walk(storage.Child);
        return result;
    }

    public Entry Root => Entries[0];

    public Entry? Find(Entry storage, string name) =>
        Children(storage).FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Finds a storage named <paramref name="name"/> anywhere (depth-first), e.g. "VBA".</summary>
    public IEnumerable<(Entry Storage, Entry Parent)> FindStorages(string name)
    {
        var stack = new Stack<Entry>(); stack.Push(Root);
        var seen = new HashSet<int>();
        while (stack.Count > 0)
        {
            var s = stack.Pop();
            if (!seen.Add(s.Index)) continue;
            foreach (var c in Children(s))
            {
                if (!c.IsStorage) continue;
                if (c.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) yield return (c, s);
                stack.Push(c);
            }
        }
    }
}
