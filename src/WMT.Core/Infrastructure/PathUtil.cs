using System.Security.Cryptography;
using System.Text;

namespace WMT.Core;

public static class PathUtil
{
    public static readonly StringComparer Comparer = StringComparer.OrdinalIgnoreCase;

    /// <summary>Canonical full path without trailing separator (except drive roots), for comparison/dedupe.</summary>
    public static string Canonical(string path)
    {
        var full = Path.GetFullPath(path.Trim().Trim('"'));
        var root = Path.GetPathRoot(full) ?? "";
        if (full.Length > root.Length) full = full.TrimEnd('\\', '/');
        return full;
    }

    public static bool SamePath(string a, string b) => Comparer.Equals(Canonical(a), Canonical(b));

    /// <summary>True when child equals parent or lies below it (case-insensitive, separator-aware).</summary>
    public static bool IsInsideOrSame(string child, string parent)
    {
        var c = Canonical(child); var p = Canonical(parent);
        if (Comparer.Equals(c, p)) return true;
        var pp = p.EndsWith('\\') || p.EndsWith('/') ? p : p + Path.DirectorySeparatorChar;
        return c.StartsWith(pp, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>PKG-003: "C:\Users\a\x.dotm" → "Data\C\Users\a\x.dotm"; "\\srv\share\x" → "Data\UNC\srv\share\x".</summary>
    public static string ToPackageDataPath(string sourceFullPath)
    {
        var full = Path.GetFullPath(sourceFullPath);
        string rel;
        if (full.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) rel = "UNC\\" + full[8..];
        else if (full.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase)) return ToPackageDataPath(full[4..]);
        else if (full.StartsWith(@"\\")) rel = "UNC\\" + full[2..];
        else if (full.Length >= 2 && full[1] == ':') rel = char.ToUpperInvariant(full[0]) + full[2..];
        else if (full.StartsWith('/')) rel = "ROOT" + full.Replace('/', '\\');
        else throw new ArgumentException("Unsupported path form: " + sourceFullPath);
        rel = rel.Replace('/', '\\').TrimStart('\\');
        var parts = rel.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        return "Data\\" + string.Join('\\', parts);
    }

    /// <summary>Inverse of <see cref="ToPackageDataPath"/> (used when no profile remapping applies).</summary>
    public static string FromPackageDataPath(string packageRelative)
    {
        var parts = packageRelative.Replace('/', '\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3 || !parts[0].Equals("Data", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Not a Data path: " + packageRelative);
        if (parts[1].Equals("UNC", StringComparison.OrdinalIgnoreCase)) return @"\\" + string.Join('\\', parts.Skip(2));
        if (parts[1].Length == 1 && char.IsLetter(parts[1][0])) return parts[1].ToUpperInvariant() + ":\\" + string.Join('\\', parts.Skip(2));
        throw new ArgumentException("Unknown root in package path: " + packageRelative);
    }

    /// <summary>NFR-007: relative path must stay inside the package: not rooted, no "..", no ":" (ADS/drive), no empty segments.</summary>
    public static bool IsSafeRelative(string rel, string? requiredFirstSegment = null)
    {
        if (string.IsNullOrWhiteSpace(rel)) return false;
        if (rel.StartsWith('\\') || rel.StartsWith('/') || rel.Contains(':')) return false;
        if (Path.IsPathRooted(rel)) return false;
        var parts = rel.Replace('/', '\\').Split('\\');
        foreach (var p in parts)
        {
            if (p.Length == 0 || p == "." || p == "..") return false;
            if (p.TrimEnd(' ', '.').Length == 0) return false;
            if (p.IndexOfAny(Path.GetInvalidFileNameChars().Where(c => c != '\\').ToArray()) >= 0) return false;
        }
        if (requiredFirstSegment != null && !parts[0].Equals(requiredFirstSegment, StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    /// <summary>Combines and re-checks that the result is inside the base directory.</summary>
    public static string SafeCombine(string baseDir, string rel)
    {
        if (!IsSafeRelative(rel)) throw new InvalidDataException("Unsafe relative path: " + rel);
        var full = Path.GetFullPath(Path.Combine(baseDir, rel));
        if (!IsInsideOrSame(full, baseDir)) throw new InvalidDataException("Path escapes base directory: " + rel);
        return full;
    }

    public static string StableId(string text)
    {
        var h = SHA256.HashData(Encoding.UTF8.GetBytes(text.ToLowerInvariant()));
        return Convert.ToHexString(h, 0, 8);
    }

    public static string ExpandProfileVar(string template, string profilePath) =>
        template.Replace("%PROFILE%", profilePath.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    public static string FormatBytes(long bytes)
    {
        string[] u = { "B", "KB", "MB", "GB", "TB" };
        double v = bytes; int i = 0;
        while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
        return i == 0 ? $"{bytes} B" : $"{v:0.##} {u[i]}";
    }
}

public static class Hashing
{
    public static string Sha256File(string path, CancellationToken ct = default)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 1 << 20, FileOptions.SequentialScan);
        return Sha256Stream(fs, ct);
    }

    public static string Sha256Stream(Stream s, CancellationToken ct = default)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buf = new byte[1 << 20];
        int n;
        while ((n = s.Read(buf, 0, buf.Length)) > 0) { ct.ThrowIfCancellationRequested(); sha.AppendData(buf, 0, n); }
        return Convert.ToHexString(sha.GetHashAndReset());
    }

    public static string Sha256Text(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}

public static class PeInfo
{
    /// <summary>Reads the PE machine type: x86 / x64 / ARM64 / Unknown. Never executes the file.</summary>
    public static string Architecture(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var br = new BinaryReader(fs);
            if (fs.Length < 0x40 || br.ReadUInt16() != 0x5A4D) return "Unknown";
            fs.Seek(0x3C, SeekOrigin.Begin);
            int peOffset = br.ReadInt32();
            if (peOffset <= 0 || peOffset > fs.Length - 6) return "Unknown";
            fs.Seek(peOffset, SeekOrigin.Begin);
            if (br.ReadUInt32() != 0x00004550) return "Unknown";
            return br.ReadUInt16() switch { 0x14c => "x86", 0x8664 => "x64", 0xAA64 => "ARM64", _ => "Unknown" };
        }
        catch { return "Unknown"; }
    }
}
