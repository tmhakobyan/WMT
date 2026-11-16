using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WMT.Core;

public static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Converters = { new JsonStringEnumConverter() },
        PropertyNameCaseInsensitive = true
    };

    static readonly UTF8Encoding Utf8NoBom = new(false);

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T? Deserialize<T>(string text) => JsonSerializer.Deserialize<T>(text, Options);

    /// <summary>Writes UTF-8 (no BOM) via temp file + atomic replace.</summary>
    public static void WriteFile<T>(string path, T value)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, Serialize(value), Utf8NoBom);
        File.Move(tmp, path, overwrite: true);
    }

    public static T ReadFile<T>(string path)
    {
        var v = Deserialize<T>(File.ReadAllText(path, Encoding.UTF8));
        return v ?? throw new InvalidDataException("Empty or invalid JSON: " + path);
    }

    public static void WriteText(string path, string text)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, text, Utf8NoBom);
        File.Move(tmp, path, overwrite: true);
    }
}
