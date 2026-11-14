using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WMT.Core;

/// <summary>Append-only structured (JSON lines) log. Safe for concurrent writers and readers.
/// A failed write is never silent: it increments FailedWrites.</summary>
public sealed class ActivityLog
{
    static readonly Regex Secret = new(
        @"(?i)(password|passwd|token|secret|api[_-]?key|product[_-]?key|credential)\s*[=:]\s*\S+",
        RegexOptions.Compiled);
    static readonly JsonSerializerOptions Json = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    readonly object gate = new();
    int failures;

    public ActivityLog(string filePath) { FilePath = filePath; }
    public string FilePath { get; }
    public int FailedWrites => Volatile.Read(ref failures);
    public bool HasFailures => FailedWrites > 0;
    public string? LastError { get; private set; }

    public static string Redact(string s) => Secret.Replace(s, m => m.Groups[1].Value + "=[REDACTED]");

    public void Write(string level, string evt, string message, Guid? correlation = null)
    {
        try
        {
            var line = JsonSerializer.Serialize(new
            {
                ts = DateTime.UtcNow.ToString("o"),
                level,
                @event = evt,
                correlation = correlation?.ToString("N"),
                message = Redact(message)
            }, Json) + "\n";
            var bytes = new UTF8Encoding(false).GetBytes(line);
            lock (gate)
            {
                var dir = Path.GetDirectoryName(FilePath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                using var fs = new FileStream(FilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                fs.Write(bytes, 0, bytes.Length);
            }
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            Interlocked.Increment(ref failures);
        }
    }
}

/// <summary>Incremental reader: each call returns only complete lines appended since the previous call.</summary>
public sealed class LogTail
{
    readonly string path;
    long offset;

    public LogTail(string path, bool startAtEnd = false)
    {
        this.path = path;
        if (startAtEnd && File.Exists(path)) offset = new FileInfo(path).Length;
    }

    public IReadOnlyList<string> ReadNew()
    {
        var result = new List<string>();
        try
        {
            if (!File.Exists(path)) return result;
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (fs.Length < offset) offset = 0;
            if (fs.Length == offset) return result;
            fs.Seek(offset, SeekOrigin.Begin);
            var buf = new byte[fs.Length - offset];
            int read = 0;
            while (read < buf.Length)
            {
                int n = fs.Read(buf, read, buf.Length - read);
                if (n <= 0) break;
                read += n;
            }
            if (read == 0) return result;
            int last = Array.LastIndexOf(buf, (byte)10, read - 1);
            if (last < 0) return result; // incomplete line: wait for the newline
            var text = Encoding.UTF8.GetString(buf, 0, last + 1);
            offset += last + 1;
            foreach (var l in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                result.Add(l.TrimEnd('\r'));
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return result;
    }

    public static string ToDisplay(string jsonLine)
    {
        try
        {
            using var d = JsonDocument.Parse(jsonLine);
            var r = d.RootElement;
            var ts = DateTime.TryParse(r.GetProperty("ts").GetString(), null,
                System.Globalization.DateTimeStyles.RoundtripKind, out var t) ? t.ToLocalTime().ToString("HH:mm:ss") : "??:??:??";
            return $"{ts} [{r.GetProperty("level").GetString()}] {r.GetProperty("event").GetString()}: {r.GetProperty("message").GetString()}";
        }
        catch { return jsonLine; }
    }
}
