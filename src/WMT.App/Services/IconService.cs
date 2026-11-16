using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace WMT.App.Services;

/// <summary>UI-004: Windows system icons per file type (cached per extension and size; files are never opened).</summary>
public static class IconService
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct SHFILEINFO
    {
        public IntPtr hIcon; public int iIcon; public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }
    const uint SHGFI_ICON = 0x100, SHGFI_SMALLICON = 0x1, SHGFI_LARGEICON = 0x0, SHGFI_USEFILEATTRIBUTES = 0x10, SHGFI_SYSICONINDEX = 0x4000, SHGFI_TYPENAME = 0x400;
    const uint FILE_ATTRIBUTE_NORMAL = 0x80, FILE_ATTRIBUTE_DIRECTORY = 0x10;
    const int SHIL_JUMBO = 4, SHIL_EXTRALARGE = 2, ILD_TRANSPARENT = 1;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr SHGetFileInfo(string pszPath, uint attr, ref SHFILEINFO psfi, uint cb, uint flags);
    [DllImport("shell32.dll")]
    static extern int SHGetImageList(int iImageList, ref Guid riid, out IntPtr ppv);
    [DllImport("comctl32.dll")]
    static extern IntPtr ImageList_GetIcon(IntPtr himl, int i, int flags);
    [DllImport("user32.dll")]
    static extern bool DestroyIcon(IntPtr h);

    static readonly ConcurrentDictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);
    static readonly ConcurrentDictionary<string, string> TypeNames = new(StringComparer.OrdinalIgnoreCase);
    static Guid IID_IImageList = new("46EB5926-582E-4017-9FDF-E8998DAA0950");

    static string Probe(string ext, bool folder) => folder ? "folder" : "file" + (string.IsNullOrEmpty(ext) ? "" : ext);

    public static ImageSource? Get(string extension, bool folder, int size)
    {
        int bucket = size <= 16 ? 16 : size <= 32 ? 32 : 256;
        var key = (folder ? "<dir>" : extension.ToLowerInvariant()) + "|" + bucket;
        return Cache.GetOrAdd(key, _ => Load(extension, folder, bucket));
    }

    public static string TypeName(string extension)
    {
        if (string.IsNullOrEmpty(extension)) return "File";
        return TypeNames.GetOrAdd(extension, e =>
        {
            try
            {
                var info = new SHFILEINFO();
                SHGetFileInfo(Probe(e, false), FILE_ATTRIBUTE_NORMAL, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), SHGFI_TYPENAME | SHGFI_USEFILEATTRIBUTES);
                return string.IsNullOrWhiteSpace(info.szTypeName) ? e.TrimStart('.').ToUpperInvariant() + " file" : info.szTypeName;
            }
            catch { return e.TrimStart('.').ToUpperInvariant() + " file"; }
        });
    }

    static ImageSource? Load(string ext, bool folder, int bucket)
    {
        try
        {
            var info = new SHFILEINFO();
            uint attr = folder ? FILE_ATTRIBUTE_DIRECTORY : FILE_ATTRIBUTE_NORMAL;
            IntPtr hIcon = IntPtr.Zero;
            if (bucket == 256)
            {
                SHGetFileInfo(Probe(ext, folder), attr, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), SHGFI_SYSICONINDEX | SHGFI_USEFILEATTRIBUTES);
                if (SHGetImageList(SHIL_JUMBO, ref IID_IImageList, out var himl) == 0 && himl != IntPtr.Zero)
                    hIcon = ImageList_GetIcon(himl, info.iIcon, ILD_TRANSPARENT);
                if (hIcon == IntPtr.Zero && SHGetImageList(SHIL_EXTRALARGE, ref IID_IImageList, out himl) == 0 && himl != IntPtr.Zero)
                    hIcon = ImageList_GetIcon(himl, info.iIcon, ILD_TRANSPARENT);
            }
            if (hIcon == IntPtr.Zero)
            {
                SHGetFileInfo(Probe(ext, folder), attr, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(),
                    SHGFI_ICON | SHGFI_USEFILEATTRIBUTES | (bucket == 16 ? SHGFI_SMALLICON : SHGFI_LARGEICON));
                hIcon = info.hIcon;
            }
            if (hIcon == IntPtr.Zero) return null;
            try
            {
                var src = Imaging.CreateBitmapSourceFromHIcon(hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                src.Freeze();
                return src;
            }
            finally { DestroyIcon(hIcon); }
        }
        catch { return null; }
    }
}

/// <summary>UI-004: real thumbnails for image files, decoded off the UI thread at the needed size only; frozen, no open file handles.</summary>
public static class ThumbnailService
{
    static readonly HashSet<string> ImageExts = new(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tif", ".tiff", ".ico", ".jfif" };
    public static bool IsImage(string path) => ImageExts.Contains(Path.GetExtension(path));

    public static ImageSource? Load(string path, int pixelWidth, CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            var fi = new FileInfo(path);
            if (!fi.Exists || fi.Length > 80L * 1024 * 1024) return null;
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bmp.DecodePixelWidth = pixelWidth;
            bmp.StreamSource = fs;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch (OperationCanceledException) { return null; }
        catch { return null; }
    }
}
