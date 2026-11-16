using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace WMT.Core.Vba;

/// <summary>MS-OVBA 2.4.1 decompression.</summary>
public static class OvbaCompression
{
    public static byte[] Decompress(byte[] src, int start = 0)
    {
        if (src.Length <= start || src[start] != 0x01) throw new InvalidDataException("Bad compressed container signature");
        var output = new List<byte>(src.Length * 3);
        int pos = start + 1;
        while (pos + 2 <= src.Length)
        {
            int header = src[pos] | (src[pos + 1] << 8);
            int chunkSize = (header & 0x0FFF) + 3;
            bool compressed = (header & 0x8000) != 0;
            int chunkEnd = Math.Min(src.Length, pos + chunkSize);
            pos += 2;
            int chunkStart = output.Count;
            if (!compressed)
            {
                int n = Math.Min(4096, src.Length - pos);
                for (int i = 0; i < n; i++) output.Add(src[pos + i]);
                pos += 4096;
                continue;
            }
            while (pos < chunkEnd)
            {
                byte flags = src[pos++];
                for (int bit = 0; bit < 8 && pos < chunkEnd; bit++)
                {
                    if ((flags & (1 << bit)) == 0) { output.Add(src[pos++]); continue; }
                    if (pos + 2 > chunkEnd) { pos = chunkEnd; break; }
                    int token = src[pos] | (src[pos + 1] << 8);
                    pos += 2;
                    int difference = output.Count - chunkStart;
                    int bitCount = 4;
                    while ((1 << bitCount) < difference) bitCount++;
                    int lengthMask = 0xFFFF >> bitCount;
                    int offsetMask = ~lengthMask & 0xFFFF;
                    int length = (token & lengthMask) + 3;
                    int offset = ((token & offsetMask) >> (16 - bitCount)) + 1;
                    int from = output.Count - offset;
                    if (from < 0) throw new InvalidDataException("Copy token points before start");
                    for (int i = 0; i < length; i++) output.Add(output[from + i]);
                }
            }
            pos = chunkEnd;
        }
        return output.ToArray();
    }
}

public sealed class VbaReference
{
    public string Kind { get; set; } = "";
    public string Name { get; set; } = "";
    public string Libid { get; set; } = "";
    public string? Path { get; set; }
    public string? Guid { get; set; }
}

public sealed class VbaModule
{
    public string Name { get; set; } = "";
    public string StreamName { get; set; } = "";
    public string Source { get; set; } = "";
}

public sealed class DeclareInfo
{
    public string Module { get; set; } = "";
    public string Procedure { get; set; } = "";
    public string Library { get; set; } = "";
    public bool PtrSafe { get; set; }
}

public sealed class VbaAnalysis
{
    public string File { get; set; } = "";
    public bool HasVbaProject { get; set; }
    public List<VbaReference> References { get; set; } = new();
    public List<VbaModule> Modules { get; set; } = new();
    public List<DeclareInfo> Declares { get; set; } = new();
    public List<string> CreateObjectProgIds { get; set; } = new();
    public List<string> ExternalPaths { get; set; } = new();
    public List<string> ActiveXClassIds { get; set; } = new();
    public List<string> ExternalRelationships { get; set; } = new();
    public bool UsesConditionalBitness { get; set; }
    public bool UsesShell { get; set; }
    public List<string> Errors { get; set; } = new();
}

/// <summary>OFF-005: static VBA/COM dependency inventory. Macros are never executed.</summary>
public static class VbaProjectReader
{
    static readonly Regex DeclareRx = new(@"^\s*(?:(?:Public|Private|Friend)\s+)?Declare\s+(PtrSafe\s+)?(?:Function|Sub)\s+(\w+)\s+Lib\s+""([^""]+)""",
        RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Compiled);
    static readonly Regex CreateObjRx = new(@"\b(?:CreateObject|GetObject)\s*\(\s*""([^""]+)""", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex PathRx = new(@"""((?:[A-Za-z]:\\|\\\\[^""\\\s]+\\)[^""]{2,})""", RegexOptions.Compiled);
    static readonly Regex BitnessRx = new(@"^\s*#If\s+.*\b(VBA7|Win64)\b", RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Compiled);
    static readonly Regex ShellRx = new(@"\bShell\s*\(|WScript\.Shell", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    static VbaProjectReader()
    {
        try { Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); } catch { }
    }

    public static VbaAnalysis AnalyzeFile(string path)
    {
        var a = new VbaAnalysis { File = path };
        try
        {
            byte[] head = new byte[8];
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                if (fs.Length > 200L * 1024 * 1024) { a.Errors.Add("File too large for VBA analysis (>200 MB)."); return a; }
                fs.ReadExactly(head, 0, Math.Min(8, (int)fs.Length));
            }
            var bytes = File.ReadAllBytes(path);
            if (head[0] == 0x50 && head[1] == 0x4B) AnalyzeOoxml(bytes, a);
            else if (CompoundFile.HasSignature(bytes)) AnalyzeCompound(bytes, a);
            else if (head[0] == 0x4D && head[1] == 0x5A) a.Errors.Add("Binary add-in (PE file); VBA analysis not applicable.");
            else a.Errors.Add("Unknown file format.");
        }
        catch (Exception ex) { a.Errors.Add(ex.GetType().Name + ": " + ex.Message); }
        return a;
    }

    static void AnalyzeOoxml(byte[] bytes, VbaAnalysis a)
    {
        using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        foreach (var e in zip.Entries)
        {
            var n = e.FullName.Replace('\\', '/');
            if (n.EndsWith("vbaProject.bin", StringComparison.OrdinalIgnoreCase))
            {
                using var s = e.Open(); using var ms = new MemoryStream(); s.CopyTo(ms);
                AnalyzeCompound(ms.ToArray(), a);
            }
            else if (n.Contains("/activeX/", StringComparison.OrdinalIgnoreCase) && n.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    using var s = e.Open();
                    var x = XDocument.Load(s);
                    var cls = x.Root?.Attributes().FirstOrDefault(at => at.Name.LocalName.Equals("classid", StringComparison.OrdinalIgnoreCase))?.Value;
                    if (!string.IsNullOrEmpty(cls) && !a.ActiveXClassIds.Contains(cls, StringComparer.OrdinalIgnoreCase)) a.ActiveXClassIds.Add(cls);
                }
                catch (Exception ex) { a.Errors.Add("ActiveX part unreadable: " + ex.Message); }
            }
            else if (n.EndsWith(".rels", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    using var s = e.Open();
                    var x = XDocument.Load(s);
                    foreach (var rel in x.Root?.Elements() ?? Enumerable.Empty<XElement>())
                    {
                        if (!string.Equals((string?)rel.Attribute("TargetMode"), "External", StringComparison.OrdinalIgnoreCase)) continue;
                        var type = ((string?)rel.Attribute("Type") ?? "").Split('/').Last();
                        var target = (string?)rel.Attribute("Target") ?? "";
                        if (type is "hyperlink") continue;
                        a.ExternalRelationships.Add($"{type}: {target}");
                    }
                }
                catch { }
            }
        }
    }

    public static void AnalyzeCompound(byte[] bytes, VbaAnalysis a)
    {
        var cf = new CompoundFile(bytes);
        foreach (var (vba, _) in cf.FindStorages("VBA"))
        {
            var dirEntry = cf.Find(vba, "dir");
            if (dirEntry == null || !dirEntry.IsStream) continue;
            a.HasVbaProject = true;
            var dir = OvbaCompression.Decompress(cf.ReadStream(dirEntry));
            var (codePage, refs, modules) = ParseDir(dir);
            a.References.AddRange(refs);
            var enc = GetEncoding(codePage);
            foreach (var m in modules)
            {
                var st = cf.Find(vba, m.StreamName);
                if (st == null || !st.IsStream) { a.Errors.Add($"Module stream '{m.StreamName}' missing"); continue; }
                try
                {
                    var raw = cf.ReadStream(st);
                    if (m.TextOffset < raw.Length) m.Module.Source = enc.GetString(OvbaCompression.Decompress(raw, (int)m.TextOffset));
                }
                catch (Exception ex) { a.Errors.Add($"Module '{m.Module.Name}' source unreadable: {ex.Message}"); }
                a.Modules.Add(m.Module);
            }
        }
        foreach (var m in a.Modules) ScanSource(m, a);
    }

    static Encoding GetEncoding(int cp)
    {
        try { return cp > 0 ? Encoding.GetEncoding(cp) : Encoding.Latin1; } catch { return Encoding.Latin1; }
    }

    sealed record ModuleDef(VbaModule Module, string StreamName, uint TextOffset);

    static (int CodePage, List<VbaReference> Refs, List<ModuleDef> Modules) ParseDir(byte[] d)
    {
        int pos = 0, codePage = 1252;
        var refs = new List<VbaReference>();
        var mods = new List<ModuleDef>();
        string pendingRefName = "";
        VbaModule? cur = null; string curStream = ""; uint curOffset = 0;
        Encoding enc = Encoding.Latin1;
        while (pos + 6 <= d.Length)
        {
            int id = BitConverter.ToUInt16(d, pos);
            int size = (int)BitConverter.ToUInt32(d, pos + 2);
            pos += 6;
            if (id == 0x0009) size = 6;
            if (size < 0 || pos + size > d.Length) break;
            var body = d.AsSpan(pos, size);
            switch (id)
            {
                case 0x0003: codePage = BitConverter.ToUInt16(body); enc = GetEncoding(codePage); break;
                case 0x0016: pendingRefName = enc.GetString(body); break;
                case 0x000D:
                {
                    int l = (int)BitConverter.ToUInt32(body);
                    var libid = enc.GetString(body.Slice(4, Math.Min(l, size - 4)));
                    refs.Add(MakeRef("Registered", pendingRefName, libid));
                    break;
                }
                case 0x000E:
                {
                    int l = (int)BitConverter.ToUInt32(body);
                    var abs = enc.GetString(body.Slice(4, Math.Min(l, size - 4)));
                    refs.Add(new VbaReference { Kind = "Project", Name = pendingRefName, Libid = abs, Path = ExtractProjectPath(abs) });
                    break;
                }
                case 0x002F:
                {
                    int l = (int)BitConverter.ToUInt32(body);
                    var libid = enc.GetString(body.Slice(4, Math.Min(l, size - 4)));
                    refs.Add(MakeRef("Control", pendingRefName, libid));
                    break;
                }
                case 0x0019:
                    if (cur != null) mods.Add(new ModuleDef(cur, curStream, curOffset));
                    cur = new VbaModule { Name = enc.GetString(body) }; curStream = cur.Name; curOffset = 0; break;
                case 0x001A: curStream = enc.GetString(body); if (cur != null) cur.StreamName = curStream; break;
                case 0x0031: curOffset = BitConverter.ToUInt32(body); break;
                case 0x002B:
                    if (cur != null) { cur.StreamName = curStream; mods.Add(new ModuleDef(cur, curStream, curOffset)); cur = null; }
                    break;
                case 0x0010: pos = d.Length; continue;
            }
            pos += size;
        }
        if (cur != null) mods.Add(new ModuleDef(cur, curStream, curOffset));
        return (codePage, refs, mods);
    }

    static VbaReference MakeRef(string kind, string name, string libid)
    {
        // Libid: *\G{GUID}#maj.min#lcid#path#description
        var r = new VbaReference { Kind = kind, Name = name, Libid = libid };
        var m = Regex.Match(libid, @"\{[0-9A-Fa-f\-]{36}\}");
        if (m.Success) r.Guid = m.Value.ToUpperInvariant();
        var parts = libid.Split('#');
        if (parts.Length >= 4 && parts[3].Length > 0) r.Path = parts[3];
        if (string.IsNullOrEmpty(r.Name) && parts.Length >= 5) r.Name = parts[4];
        return r;
    }

    static string? ExtractProjectPath(string abs)
    {
        var s = abs.StartsWith("*\\C", StringComparison.Ordinal) ? abs[3..] : abs;
        return s.Length > 0 ? s : null;
    }

    static void ScanSource(VbaModule m, VbaAnalysis a)
    {
        var src = m.Source;
        foreach (Match d in DeclareRx.Matches(src))
            a.Declares.Add(new DeclareInfo { Module = m.Name, Procedure = d.Groups[2].Value, Library = d.Groups[3].Value, PtrSafe = d.Groups[1].Success });
        foreach (Match c in CreateObjRx.Matches(src))
            if (!a.CreateObjectProgIds.Contains(c.Groups[1].Value, StringComparer.OrdinalIgnoreCase)) a.CreateObjectProgIds.Add(c.Groups[1].Value);
        foreach (Match p in PathRx.Matches(src))
            if (!a.ExternalPaths.Contains(p.Groups[1].Value, StringComparer.OrdinalIgnoreCase)) a.ExternalPaths.Add(p.Groups[1].Value);
        if (BitnessRx.IsMatch(src)) a.UsesConditionalBitness = true;
        if (ShellRx.IsMatch(src)) a.UsesShell = true;
    }
}
