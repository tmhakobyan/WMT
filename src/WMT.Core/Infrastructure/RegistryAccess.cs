using Microsoft.Win32;

namespace WMT.Core;

public enum RegView { Default, Reg64, Reg32 }

public sealed record RegValue(string Kind, object? Data)
{
    public string? AsString() => Data switch
    {
        null => null,
        string s => s,
        string[] a => string.Join("; ", a),
        byte[] b => Convert.ToHexString(b),
        _ => Convert.ToString(Data, System.Globalization.CultureInfo.InvariantCulture)
    };
}

/// <summary>Read-only registry access (abstracted so providers are testable without touching the real registry).</summary>
public interface IRegistryReader
{
    IReadOnlyList<string> SubKeys(string hive, string path, RegView view = RegView.Default);
    IReadOnlyList<string> ValueNames(string hive, string path, RegView view = RegView.Default);
    RegValue? Value(string hive, string path, string name, RegView view = RegView.Default);
    bool KeyExists(string hive, string path, RegView view = RegView.Default);
}

/// <summary>Used only by the confirmed, journaled registry restore (REG-003).</summary>
public interface IRegistryWriter
{
    void SetValue(string hive, string path, string name, string kind, object? data);
    void DeleteValue(string hive, string path, string name);
}

public sealed class WindowsRegistry : IRegistryReader, IRegistryWriter
{
    static RegistryKey? Open(string hive, string path, RegView view, bool writable = false)
    {
        if (!OperatingSystem.IsWindows()) return null;
        var h = hive.ToUpperInvariant() switch
        {
            "HKLM" => RegistryHive.LocalMachine,
            "HKCU" => RegistryHive.CurrentUser,
            "HKU" => RegistryHive.Users,
            "HKCR" => RegistryHive.ClassesRoot,
            _ => throw new ArgumentException("Unknown hive " + hive)
        };
        var v = view switch { RegView.Reg64 => RegistryView.Registry64, RegView.Reg32 => RegistryView.Registry32, _ => RegistryView.Default };
        try
        {
            var b = RegistryKey.OpenBaseKey(h, v);
            if (string.IsNullOrEmpty(path)) return b;
            var k = writable ? b.CreateSubKey(path, true) : b.OpenSubKey(path, false);
            b.Dispose();
            return k;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return null; }
    }

    public IReadOnlyList<string> SubKeys(string hive, string path, RegView view = RegView.Default)
    {
        using var k = Open(hive, path, view);
        try { return k?.GetSubKeyNames() ?? Array.Empty<string>(); } catch { return Array.Empty<string>(); }
    }

    public IReadOnlyList<string> ValueNames(string hive, string path, RegView view = RegView.Default)
    {
        using var k = Open(hive, path, view);
        try { return k?.GetValueNames() ?? Array.Empty<string>(); } catch { return Array.Empty<string>(); }
    }

    public RegValue? Value(string hive, string path, string name, RegView view = RegView.Default)
    {
        using var k = Open(hive, path, view);
        if (k == null) return null;
        try
        {
            var data = k.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            if (data == null) return null;
            return new RegValue(k.GetValueKind(name).ToString(), data);
        }
        catch { return null; }
    }

    public bool KeyExists(string hive, string path, RegView view = RegView.Default)
    {
        using var k = Open(hive, path, view);
        return k != null;
    }

    public void SetValue(string hive, string path, string name, string kind, object? data)
    {
        using var k = Open(hive, path, RegView.Default, writable: true) ?? throw new InvalidOperationException($"Cannot open {hive}\\{path} for writing");
        var rk = Enum.Parse<RegistryValueKind>(kind, true);
        k.SetValue(name, data ?? "", rk);
    }

    public void DeleteValue(string hive, string path, string name)
    {
        using var k = Open(hive, path, RegView.Default, writable: true);
        k?.DeleteValue(name, false);
    }
}

/// <summary>In-memory registry for tests and dry runs.</summary>
public sealed class MemoryRegistry : IRegistryReader, IRegistryWriter
{
    readonly Dictionary<string, Dictionary<string, RegValue>> keys = new(StringComparer.OrdinalIgnoreCase);
    static string K(string hive, string path, RegView view) => $"{hive}|{view}|{path.Trim('\\')}";

    public void Set(string hive, string path, string name, string kind, object? data, RegView view = RegView.Default)
    {
        var segs = path.Trim('\\').Split('\\');
        for (int i = 1; i <= segs.Length; i++)
        {
            var kk = K(hive, string.Join('\\', segs.Take(i)), view);
            if (!keys.ContainsKey(kk)) keys[kk] = new(StringComparer.OrdinalIgnoreCase);
        }
        keys[K(hive, path, view)][name] = new RegValue(kind, data);
    }

    public void AddKey(string hive, string path, RegView view = RegView.Default)
    {
        var segs = path.Trim('\\').Split('\\');
        for (int i = 1; i <= segs.Length; i++)
        {
            var kk = K(hive, string.Join('\\', segs.Take(i)), view);
            if (!keys.ContainsKey(kk)) keys[kk] = new(StringComparer.OrdinalIgnoreCase);
        }
    }

    public IReadOnlyList<string> SubKeys(string hive, string path, RegView view = RegView.Default)
    {
        var prefix = K(hive, path, view) + "\\";
        if (string.IsNullOrEmpty(path.Trim('\\'))) prefix = $"{hive}|{view}|";
        return keys.Keys.Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(k => k[prefix.Length..]).Where(r => r.Length > 0 && !r.Contains('\\')).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public IReadOnlyList<string> ValueNames(string hive, string path, RegView view = RegView.Default) =>
        keys.TryGetValue(K(hive, path, view), out var v) ? v.Keys.ToList() : new List<string>();

    public RegValue? Value(string hive, string path, string name, RegView view = RegView.Default) =>
        keys.TryGetValue(K(hive, path, view), out var v) && v.TryGetValue(name, out var r) ? r : null;

    public bool KeyExists(string hive, string path, RegView view = RegView.Default) => keys.ContainsKey(K(hive, path, view));

    public void SetValue(string hive, string path, string name, string kind, object? data) => Set(hive, path, name, kind, data);

    public void DeleteValue(string hive, string path, string name)
    {
        if (keys.TryGetValue(K(hive, path, RegView.Default), out var v)) v.Remove(name);
    }
}
