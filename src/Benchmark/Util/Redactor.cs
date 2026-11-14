using System.Text.RegularExpressions;

namespace Benchmark.Util;

/// <summary>
/// Removes credential-shaped substrings from captured evidence while preserving JSON structure.
/// Redaction is pattern-based and therefore best-effort; it is applied to every transcript and
/// stream capture before it is written to the evidence store unless explicitly disabled.
/// </summary>
public static partial class Redactor
{
    public const string Marker = "[REDACTED]";

    [GeneratedRegex(@"sk-ant-[A-Za-z0-9_\-]{8,}")] private static partial Regex AnthropicKey();
    [GeneratedRegex(@"(?i)\bBearer\s+[A-Za-z0-9._\-~+/]{16,}=*")] private static partial Regex BearerToken();
    [GeneratedRegex(@"\bgh[pousr]_[A-Za-z0-9]{20,}")] private static partial Regex GitHubToken();
    [GeneratedRegex(@"\bAKIA[0-9A-Z]{16}\b")] private static partial Regex AwsKey();
    [GeneratedRegex(@"(?i)((?:password|pwd|secret|api[_-]?key|access[_-]?token|client[_-]?secret)\s*[=:]\s*)(""?)[^;""\s,}]{4,}")] private static partial Regex KeyValueSecret();
    [GeneratedRegex(@"(""(?:accountUuid|organizationUuid|oauthAccount|emailAddress|credential_expires_at)""\s*:\s*)""[^""]*""")] private static partial Regex AccountJsonField();
    [GeneratedRegex(@"-----BEGIN [A-Z ]*PRIVATE KEY-----[\s\S]*?-----END [A-Z ]*PRIVATE KEY-----")] private static partial Regex PrivateKey();

    public static string Redact(string text, IEnumerable<string>? extraLiterals = null)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var result = AnthropicKey().Replace(text, Marker);
        result = BearerToken().Replace(result, "Bearer " + Marker);
        result = GitHubToken().Replace(result, Marker);
        result = AwsKey().Replace(result, Marker);
        result = PrivateKey().Replace(result, Marker);
        result = AccountJsonField().Replace(result, m => m.Groups[1].Value + "\"" + Marker + "\"");
        result = KeyValueSecret().Replace(result, m => m.Groups[1].Value + m.Groups[2].Value + Marker);
        if (extraLiterals is not null)
        {
            foreach (var literal in extraLiterals.Where(l => !string.IsNullOrWhiteSpace(l) && l.Length >= 6))
                result = result.Replace(literal, Marker, StringComparison.Ordinal);
        }
        return result;
    }

    /// <summary>Values of secret-looking environment variables, so they can be scrubbed literally.</summary>
    public static IReadOnlyList<string> SecretEnvironmentValues()
    {
        var names = new[] { "KEY", "TOKEN", "SECRET", "PASSWORD", "CREDENTIAL" };
        var values = new List<string>();
        foreach (System.Collections.DictionaryEntry e in Environment.GetEnvironmentVariables())
        {
            var name = e.Key?.ToString() ?? "";
            if (names.Any(n => name.Contains(n, StringComparison.OrdinalIgnoreCase)) && e.Value?.ToString() is { Length: >= 6 } v)
                values.Add(v);
        }
        return values;
    }

    public static void RedactFile(string source, string destination, bool redact)
    {
        var text = File.ReadAllText(source);
        Fs.WriteAtomic(destination, redact ? Redact(text, SecretEnvironmentValues()) : text);
    }
}
