using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Benchmark.Util;

public static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower) },
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T Deserialize<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, Options) ?? throw new InvalidDataException("JSON document was null.");

    public static T Read<T>(string path) => Deserialize<T>(File.ReadAllText(path));

    public static void Write<T>(string path, T value) => Fs.WriteAtomic(path, Serialize(value) + "\n");

    public static JsonNode? TryParseNode(string line)
    {
        try { return JsonNode.Parse(line); }
        catch (JsonException) { return null; }
    }
}

public static class Fs
{
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    /// <summary>Write via temp file + move so a crash never leaves a half-written evidence file.</summary>
    public static void WriteAtomic(string path, string content)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(dir);
        var tmp = Path.Combine(dir, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        File.WriteAllText(tmp, content, Utf8NoBom);
        File.Move(tmp, path, overwrite: true);
    }

    public static void AppendLine(string path, string line)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
        var bytes = Utf8NoBom.GetBytes(line + "\n");
        stream.Write(bytes);
        stream.Flush(true);
    }

    public static string Sha256File(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    public static string Sha256Text(string text) => Convert.ToHexStringLower(SHA256.HashData(Utf8NoBom.GetBytes(text)));

    /// <summary>Normalises CRLF so the same prompt hashes identically regardless of checkout line endings.</summary>
    public static string NormalizeNewlines(string text) => text.Replace("\r\n", "\n").Replace('\r', '\n');

    private static readonly string[] IgnoredDirs = ["bin", "obj", ".vs", "TestResults", ".git"];

    /// <summary>Deterministic hash of a directory tree (relative path + content hash), ignoring build output.</summary>
    public static string Sha256Directory(string root)
    {
        var entries = EnumerateFilesSorted(root)
            .Select(f => $"{RelativeUnix(root, f)}:{Sha256File(f)}");
        return Sha256Text(string.Join("\n", entries));
    }

    public static IEnumerable<string> EnumerateFilesSorted(string root) =>
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(f => !RelativeUnix(root, f).Split('/').Any(seg => IgnoredDirs.Contains(seg, StringComparer.OrdinalIgnoreCase)))
            .OrderBy(f => RelativeUnix(root, f), StringComparer.Ordinal);

    public static string RelativeUnix(string root, string path) =>
        Path.GetRelativePath(root, path).Replace('\\', '/');

    public static void CopyDirectory(string source, string destination)
    {
        foreach (var file in EnumerateFilesSorted(source))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    public static bool IsUnder(string path, string root)
    {
        var p = Path.GetFullPath(path).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        var r = Path.GetFullPath(root).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        return p.StartsWith(r, StringComparison.OrdinalIgnoreCase);
    }
}
