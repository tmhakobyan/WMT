using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WMT.Core;

public static class Redaction
{
    static readonly Regex Secret = new(
        @"(?i)\b(password|passwd|pwd|token|secret|api[_-]?key|product[_-]?key|access[_-]?key|credential|authorization)\b(\s*[=:]\s*)(""[^""]*""|'[^']*'|\S+)",
        RegexOptions.Compiled);
    static readonly Regex UrlCredentials = new(@"(?i)(https?://)[^/\s:@]+:[^/\s@]+@", RegexOptions.Compiled);

    /// <summary>NFR-008: strips obvious secrets from text before it is logged or reported.</summary>
    public static string Apply(string? s)
    {
        if (string.IsNullOrEmpty(s)) return s ?? "";
        s = Secret.Replace(s, m => m.Groups[1].Value + m.Groups[2].Value + "[REDACTED]");
        return UrlCredentials.Replace(s, m => m.Groups[1].Value + "[REDACTED]@");
    }

    static readonly string[] SecretNameParts = { "PASSWORD", "PASSWD", "PWD", "TOKEN", "SECRET", "APIKEY", "API_KEY", "ACCESSKEY", "ACCESS_KEY", "CREDENTIAL", "AUTH", "PRIVATE", "CONNECTIONSTRING", "SAS", "COOKIE", "SESSION" };

    /// <summary>True when a variable/value name looks like it may hold a secret.</summary>
    public static bool LooksSecretName(string name)
    {
        var u = name.ToUpperInvariant().Replace("-", "_");
        return SecretNameParts.Any(p => u.Contains(p));
    }
}

/// <summary>NFR-003: append-only JSON-lines log; concurrent writers/readers; failures are counted, never silent.</summary>
public sealed class ActivityLog
{
    static readonly JsonSerializerOptions Opts = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    static readonly UTF8Encoding Utf8 = new(false);
    readonly object gate = new();
    int failures;

    public ActivityLog(string filePath) { FilePath = filePath; }
    public string FilePath { get; }
    public int FailedWrites => Volatile.Read(ref failures);
    public string? LastError { get; private set; }
    /// <summary>Optional secondary sink (e.g. the package log during a backup).</summary>
    public ActivityLog? Mirror { get; set; }

    public void Info(string evt, string message, string? op = null) => Write("info", evt, message, op);
    public void Warn(string evt, string message, string? op = null) => Write("warn", evt, message, op);
    public void Error(string evt, string message, string? op = null) => Write("error", evt, message, op);

    public void Write(string level, string evt, string message, string? operationId = null)
    {
        try
        {
            var line = JsonSerializer.Serialize(new
            {
                ts = DateTime.UtcNow.ToString("o"),
                level,
                @event = evt,
                op = operationId,
                message = Redaction.Apply(message)
            }, Opts) + "\n";
            var bytes = Utf8.GetBytes(line);
            lock (gate)
            {
                var dir = Path.GetDirectoryName(FilePath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                using var fs = new FileStream(FilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                fs.Write(bytes, 0, bytes.Length);
            }
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            Interlocked.Increment(ref failures);
        }
        Mirror?.Write(level, evt, message, operationId);
    }
}

/// <summary>Incremental reader: returns only complete lines appended since the previous call (never re-reads the whole file).</summary>
public sealed class LogTail
{
    readonly string path;
    long offset;

    public LogTail(string path, bool startAtEnd = false)
    {
        this.path = path;
        try { if (startAtEnd && File.Exists(path)) offset = new FileInfo(path).Length; } catch { }
    }

    public IReadOnlyList<string> ReadNew(int maxBytes = 1 << 20)
    {
        var result = new List<string>();
        try
        {
            if (!File.Exists(path)) return result;
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (fs.Length < offset) offset = 0;
            long available = fs.Length - offset;
            if (available <= 0) return result;
            int toRead = (int)Math.Min(available, maxBytes);
            fs.Seek(offset, SeekOrigin.Begin);
            var buf = new byte[toRead];
            int read = 0;
            while (read < toRead)
            {
                int n = fs.Read(buf, read, toRead - read);
                if (n <= 0) break;
                read += n;
            }
            if (read == 0) return result;
            int last = Array.LastIndexOf(buf, (byte)'\n', read - 1);
            if (last < 0) return result;
            offset += last + 1;
            foreach (var l in Encoding.UTF8.GetString(buf, 0, last + 1).Split('\n', StringSplitOptions.RemoveEmptyEntries))
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
                System.Globalization.DateTimeStyles.RoundtripKind, out var t) ? t.ToLocalTime().ToString("HH:mm:ss") : "--:--:--";
            return $"{ts} {r.GetProperty("level").GetString()?.ToUpperInvariant(),-5} {r.GetProperty("event").GetString()}: {r.GetProperty("message").GetString()}";
        }
        catch { return jsonLine; }
    }
}
