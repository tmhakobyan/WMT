using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace WMT.Core;

/// <summary>Reads an installed keyboard layout DLL (its KBDTABLES, the same data Windows uses) and writes an
/// MSKLC-compatible .klc source — the equivalent of MSKLC "File → Load Existing Keyboard → Save As".
/// The DLL is only asked for its table pointer; nothing in it is changed.</summary>
public static class KlcExporter
{
    const ushort WCH_NONE = 0xF000, WCH_DEAD = 0xF001, WCH_LGTR = 0xF002;
    const byte CAPLOK = 0x01, SGCAPS = 0x02, CAPLOKALTGR = 0x04;
    const byte SHFT_INVALID = 0x0F;
    const uint KLLF_SHIFTLOCK = 0x0002, KLLF_LRM_RLM = 0x0004;

    /// <summary>Scan codes MSKLC lists in the LAYOUT section (the editable typing keys).</summary>
    static readonly HashSet<int> EditableScanCodes = new(
        Enumerable.Range(0x02, 12).Concat(Enumerable.Range(0x10, 12)).Concat(Enumerable.Range(0x1E, 12)).Concat(Enumerable.Range(0x2B, 11)).Concat(new[] { 0x39, 0x53, 0x56 }));

    static readonly Dictionary<int, string> VkNames = new()
    {
        [0x20] = "SPACE", [0x6E] = "DECIMAL", [0xBA] = "OEM_1", [0xBB] = "OEM_PLUS", [0xBC] = "OEM_COMMA", [0xBD] = "OEM_MINUS", [0xBE] = "OEM_PERIOD",
        [0xBF] = "OEM_2", [0xC0] = "OEM_3", [0xC1] = "ABNT_C1", [0xC2] = "ABNT_C2", [0xDB] = "OEM_4", [0xDC] = "OEM_5", [0xDD] = "OEM_6", [0xDE] = "OEM_7",
        [0xDF] = "OEM_8", [0xE2] = "OEM_102", [0x6F] = "DIVIDE", [0x6A] = "MULTIPLY", [0x6D] = "SUBTRACT", [0x6B] = "ADD",
    };

    public static string VkName(int vk) =>
        vk is >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A ? ((char)vk).ToString() :
        VkNames.TryGetValue(vk, out var n) ? n : vk.ToString("x2");

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    delegate IntPtr KbdLayerDescriptorFn();

    public sealed record ExportResult(string Klc, int KeyRows, int DeadKeys, int ShiftStates, List<string> Warnings);

    public static ExportResult Export(string dllPath, string kbdName, string description, string klid, string? company = null, string? copyright = null, string? version = null)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Keyboard DLLs can only be read on Windows.");
        var lib = NativeLibrary.Load(dllPath);
        try
        {
            var fn = Marshal.GetDelegateForFunctionPointer<KbdLayerDescriptorFn>(NativeLibrary.GetExport(lib, "KbdLayerDescriptor"));
            var tables = fn();
            if (tables == IntPtr.Zero) throw new InvalidDataException("KbdLayerDescriptor returned no tables.");
            return Build(tables, kbdName, description, klid, company, copyright, version);
        }
        finally { NativeLibrary.Free(lib); }
    }

    static int Align(int v, int a) => (v + a - 1) / a * a;
    static IntPtr P(IntPtr b, int off) => Marshal.ReadIntPtr(b, off);
    static string? WStr(IntPtr p) => p == IntPtr.Zero ? null : Marshal.PtrToStringUni(p);
    static string Hex(int c) => c.ToString("x4");

    static ExportResult Build(IntPtr t, string kbdName, string description, string klid, string? company, string? copyright, string? version)
    {
        int ps = IntPtr.Size;
        var warnings = new List<string>();
        IntPtr pMods = P(t, 0), pVkTables = P(t, ps), pDead = P(t, 2 * ps), pNames = P(t, 3 * ps), pNamesExt = P(t, 4 * ps), pNamesDead = P(t, 5 * ps), pVscToVk = P(t, 6 * ps);
        int maxVsc = Marshal.ReadByte(t, 7 * ps);
        int offFlags = 10 * ps;
        uint flags = (uint)Marshal.ReadInt32(t, offFlags);
        int nLgMax = Marshal.ReadByte(t, offFlags + 4), cbLg = Marshal.ReadByte(t, offFlags + 5);
        IntPtr pLig = P(t, Align(offFlags + 6, ps));

        // ---- shift states: bit combination → column
        int maxBits = (ushort)Marshal.ReadInt16(pMods, ps);
        var columns = new SortedDictionary<int, int>();
        for (int b = 0; b <= maxBits; b++)
        {
            int col = Marshal.ReadByte(pMods, ps + 2 + b);
            if (col != SHFT_INVALID && !columns.ContainsKey(col)) columns[col] = b;
        }
        var colBits = columns.Values.ToList();
        int nCols = colBits.Count;

        // ---- scan code ↔ VK
        var vkToSc = new Dictionary<int, int>();
        for (int sc = 0; sc < maxVsc; sc++)
        {
            int raw = (ushort)Marshal.ReadInt16(pVscToVk, sc * 2), vk = raw & 0xFF;
            if (sc == 0x53 && (raw & 0x0800) != 0) vk = 0x6E; // numpad Del/. key: characters live under VK_DECIMAL
            if (vk != 0 && vk != 0xFF && EditableScanCodes.Contains(sc) && !vkToSc.ContainsKey(vk)) vkToSc[vk] = sc;
        }

        // ---- characters
        var rows = new List<(int Sc, int Vk, byte Attr, ushort[] Chars, ushort[]? DeadChars, ushort[]? SgChars)>();
        int tableSize = Align(ps + 2, ps);
        for (int ti = 0; ; ti++)
        {
            var entry = pVkTables + ti * tableSize;
            var pChars = P(entry, 0);
            if (pChars == IntPtr.Zero) break;
            int nMods = Marshal.ReadByte(entry, ps), cb = Marshal.ReadByte(entry, ps + 1);
            if (cb < 2 || nMods == 0) continue;
            for (int i = 0; ; i++)
            {
                var r = pChars + i * cb;
                int vk = Marshal.ReadByte(r, 0);
                if (vk == 0) break;
                if (vk == 0xFF) continue; // follow-up row consumed below
                byte attr = Marshal.ReadByte(r, 1);
                var chars = Enumerable.Range(0, nMods).Select(k => (ushort)Marshal.ReadInt16(r, 2 + 2 * k)).ToArray();
                ushort[]? dead = null, sg = null;
                int next = i + 1;
                if (chars.Contains(WCH_DEAD) && Marshal.ReadByte(pChars + next * cb, 0) == 0xFF)
                { dead = Enumerable.Range(0, nMods).Select(k => (ushort)Marshal.ReadInt16(pChars + next * cb, 2 + 2 * k)).ToArray(); next++; }
                if ((attr & SGCAPS) != 0 && Marshal.ReadByte(pChars + next * cb, 0) == 0xFF)
                    sg = Enumerable.Range(0, nMods).Select(k => (ushort)Marshal.ReadInt16(pChars + next * cb, 2 + 2 * k)).ToArray();
                if (!vkToSc.TryGetValue(vk, out var sc)) continue;
                rows.Add((sc, vk, attr, chars, dead, sg));
            }
        }
        rows = rows.GroupBy(r => r.Sc).Select(g => g.First()).OrderBy(r => r.Sc).ToList();
        if (rows.Count == 0) warnings.Add("No typing keys were found in the layout tables.");

        string Cell(ushort[] chars, ushort[]? dead, int col)
        {
            if (col >= chars.Length) return "-1";
            var c = chars[col];
            if (c == WCH_NONE) return "-1";
            if (c == WCH_LGTR) return "%%";
            if (c == WCH_DEAD) return dead != null && col < dead.Length ? Hex(dead[col]) + "@" : "-1";
            return Hex(c);
        }

        var sb = new StringBuilder();
        void L(string s = "") => sb.Append(s).Append("\r\n");
        static string Q(string s) => "\"" + s.Replace("\"", "'") + "\"";
        var lcid = klid.Length >= 4 ? klid[^4..].ToLowerInvariant() : "0409";
        string culture, langName;
        try { var ci = CultureInfo.GetCultureInfo(Convert.ToInt32(lcid, 16)); culture = ci.Name; langName = ci.EnglishName; }
        catch { culture = "en-US"; langName = "Unknown"; warnings.Add("Language of KLID could not be resolved."); }

        L($"KBD\t{kbdName}\t{Q(description)}"); L();
        L($"COPYRIGHT\t{Q(string.IsNullOrWhiteSpace(copyright) ? "(c) " + DateTime.Now.Year : copyright!)}"); L();
        L($"COMPANY\t{Q(string.IsNullOrWhiteSpace(company) ? "Unknown" : company!)}"); L();
        L($"LOCALENAME\t{Q(culture)}"); L();
        L($"LOCALEID\t\"0000{lcid}\""); L();
        var ver = Version.TryParse(version ?? "", out var v) ? $"{v.Major}.{Math.Max(0, v.Minor)}" : "1.0";
        L($"VERSION\t{ver}"); L();
        if ((flags & (KLLF_SHIFTLOCK | KLLF_LRM_RLM)) != 0)
        {
            L("ATTRIBUTES");
            if ((flags & KLLF_SHIFTLOCK) != 0) L("SHIFTLOCK");
            if ((flags & KLLF_LRM_RLM) != 0) L("LRM_RLM");
            L();
        }
        L("SHIFTSTATE"); L();
        for (int c = 0; c < nCols; c++)
        {
            int b = colBits[c];
            var label = (b & 1) != 0 ? "Shft " : "     ";
            label += (b & 2) != 0 ? " Ctrl" : "     ";
            label += (b & 4) != 0 ? " Alt" : "";
            L($"{b}\t//Column {c + 4} : {label}".TrimEnd());
        }
        L();
        L("LAYOUT\t\t;an extra '@' at the end is a dead key"); L();
        L("//SC\tVK_\t\tCap\t" + string.Join("\t", colBits)); L("//--\t----\t\t----\t" + string.Join("\t", colBits.Select(_ => "----"))); L();
        foreach (var r in rows)
        {
            string cap = (r.Attr & SGCAPS) != 0 ? "SGCap" : (((r.Attr & CAPLOK) != 0 ? 1 : 0) + ((r.Attr & CAPLOKALTGR) != 0 ? 4 : 0)).ToString();
            var cells = Enumerable.Range(0, nCols).Select(c => Cell(r.Chars, r.DeadChars, c));
            L($"{r.Sc:x2}\t{VkName(r.Vk)}\t\t{cap}\t{string.Join("\t", cells)}");
            if (r.SgChars != null)
                L($"-1\t-1\t\t0\t{string.Join("\t", Enumerable.Range(0, Math.Min(2, nCols)).Select(c => Cell(r.SgChars, null, c)))}");
        }
        L();

        // ---- ligatures
        if (pLig != IntPtr.Zero && cbLg > 0 && nLgMax > 0)
        {
            var ligs = new List<string>();
            for (int i = 0; ; i++)
            {
                var e = pLig + i * cbLg;
                int vk = Marshal.ReadByte(e, 0);
                if (vk == 0) break;
                int mod = (ushort)Marshal.ReadInt16(e, 2);
                var chars = Enumerable.Range(0, nLgMax).Select(k => (ushort)Marshal.ReadInt16(e, 4 + 2 * k)).TakeWhile(ch => ch != WCH_NONE && ch != 0).Select(ch => Hex(ch));
                ligs.Add($"{VkName(vk)}\t{mod}\t{string.Join("\t", chars)}");
            }
            if (ligs.Count > 0)
            {
                L("LIGATURE"); L();
                L("//VK_\tMod#\tChar0\tChar1\tChar2\tChar3"); L("//----\t\t----\t----\t----\t----"); L();
                foreach (var l in ligs) L(l);
                L();
            }
        }

        // ---- dead keys
        int deadCount = 0;
        if (pDead != IntPtr.Zero)
        {
            var groups = new List<(int Accent, List<string> Lines)>();
            for (int i = 0; ; i++)
            {
                var e = pDead + i * 8;
                uint both = (uint)Marshal.ReadInt32(e, 0);
                if (both == 0) break;
                int accent = (int)(both >> 16), baseCh = (int)(both & 0xFFFF);
                int composed = (ushort)Marshal.ReadInt16(e, 4), dkf = (ushort)Marshal.ReadInt16(e, 6);
                var g = groups.FirstOrDefault(x => x.Accent == accent);
                if (g.Lines == null) { g = (accent, new List<string>()); groups.Add(g); }
                g.Lines.Add($"{Hex(baseCh)}\t{Hex(composed)}{((dkf & 1) != 0 ? "@" : "")}");
                deadCount++;
            }
            foreach (var g in groups)
            {
                L($"DEADKEY\t{Hex(g.Accent)}"); L();
                foreach (var line in g.Lines) L(line);
                L();
            }
        }

        void Names(string title, IntPtr list)
        {
            if (list == IntPtr.Zero) return;
            var lines = new List<string>();
            for (int i = 0; ; i++)
            {
                var e = list + i * 2 * ps;
                int vsc = Marshal.ReadByte(e, 0);
                if (vsc == 0) break;
                var name = WStr(P(e, ps)) ?? "";
                lines.Add($"{vsc:x2}\t{(name.Contains(' ') ? Q(name) : name)}");
            }
            if (lines.Count == 0) return;
            L(title); L();
            foreach (var l in lines) L(l);
            L();
        }
        Names("KEYNAME", pNames);
        Names("KEYNAME_EXT", pNamesExt);
        if (pNamesDead != IntPtr.Zero)
        {
            var lines = new List<string>();
            for (int i = 0; ; i++)
            {
                var s = WStr(P(pNamesDead, i * ps));
                if (s == null) break;
                if (s.Length >= 1) lines.Add($"{Hex(s[0])}\t{Q(s[1..])}");
            }
            if (lines.Count > 0) { L("KEYNAME_DEAD"); L(); foreach (var l in lines) L(l); L(); }
        }
        L("DESCRIPTIONS"); L();
        L($"0409\t{description}"); L();
        L("LANGUAGENAMES"); L();
        L($"0409\t{langName}"); L();
        L("ENDKBD");
        return new ExportResult(sb.ToString(), rows.Count, deadCount, nCols, warnings);
    }
}
