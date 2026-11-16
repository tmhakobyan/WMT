using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace WMT.Core;

public sealed record DiskCheck(bool? SamePhysicalDisk, string Detail, IReadOnlyList<int> DestinationDisks, IReadOnlyList<int> SystemDisks, bool IsNetwork);

/// <summary>NFR-009: detects whether a destination is on the same physical disk as Windows.</summary>
public static class DiskInfo
{
    const uint IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS = 0x00560000;
    const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, OPEN_EXISTING = 3;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool GetVolumePathNameW(string lpszFileName, StringBuilder lpszVolumePathName, int cchBufferLength);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool GetVolumeNameForVolumeMountPointW(string lpszVolumeMountPoint, StringBuilder lpszVolumeName, int cchBufferLength);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr sa, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool DeviceIoControl(SafeFileHandle h, uint code, IntPtr inBuf, int inSize, byte[] outBuf, int outSize, out int returned, IntPtr overlapped);

    public static IReadOnlyList<int>? PhysicalDisksOf(string path, out bool isNetwork)
    {
        isNetwork = false;
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            var full = Path.GetFullPath(path);
            if (full.StartsWith(@"\\") && !full.StartsWith(@"\\?\") && !full.StartsWith(@"\\.\")) { isNetwork = true; return Array.Empty<int>(); }
            var sb = new StringBuilder(1024);
            if (!GetVolumePathNameW(full, sb, sb.Capacity)) return null;
            var mount = sb.ToString();
            try { if (new DriveInfo(mount).DriveType == DriveType.Network) { isNetwork = true; return Array.Empty<int>(); } } catch { }
            string device;
            if (mount.Length <= 3 && mount.Length >= 2 && mount[1] == ':') device = @"\\.\" + mount[..2];
            else
            {
                var vn = new StringBuilder(128);
                if (!GetVolumeNameForVolumeMountPointW(mount.EndsWith('\\') ? mount : mount + "\\", vn, vn.Capacity)) return null;
                device = vn.ToString().TrimEnd('\\');
            }
            using var h = CreateFileW(device, 0, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            if (h.IsInvalid) return null;
            var buf = new byte[4096];
            if (!DeviceIoControl(h, IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS, IntPtr.Zero, 0, buf, buf.Length, out _, IntPtr.Zero)) return null;
            int count = BitConverter.ToInt32(buf, 0);
            var disks = new List<int>();
            for (int i = 0; i < count && 8 + i * 24 + 4 <= buf.Length; i++)
                disks.Add(BitConverter.ToInt32(buf, 8 + i * 24));
            return disks.Distinct().ToList();
        }
        catch { return null; }
    }

    public static DiskCheck Compare(string destination)
    {
        var sys = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (string.IsNullOrEmpty(sys)) sys = @"C:\Windows";
        var d = PhysicalDisksOf(destination, out var net);
        if (net) return new DiskCheck(false, "Network location: not on this computer's disks (verify it is a separate physical device).", Array.Empty<int>(), Array.Empty<int>(), true);
        var s = PhysicalDisksOf(sys, out _);
        if (d == null || s == null || d.Count == 0 || s.Count == 0)
            return new DiskCheck(null, "Could not determine the physical disk (manual confirmation required).", d ?? Array.Empty<int>(), s ?? Array.Empty<int>(), false);
        bool same = d.Intersect(s).Any();
        return new DiskCheck(same,
            same ? $"SAME physical disk as Windows (disk {string.Join(",", d)}). This is NOT an independent backup."
                 : $"Different physical disk (destination disk {string.Join(",", d)}, Windows disk {string.Join(",", s)}).",
            d, s, false);
    }
}
