using System.Text;
using System.Text.RegularExpressions;

namespace Autopilot.Core.Redaction;

/// <summary>
/// Masks secrets and identifying details in free text before it may leave the computer.
/// Pure string processing: no I/O, no network, no logging. Detected values are never
/// stored or written anywhere; they are only replaced with stable placeholders such as
/// <c>[REDACTED:token]</c>. Redaction is idempotent and fails closed: if a pattern runs
/// into its match timeout, the whole text is replaced by a single placeholder.
/// </summary>
public static class SecretRedactor
{
    /// <summary>Placeholder used when the text could not be processed safely.</summary>
    public const string UnprocessablePlaceholder = "[REDACTED:unprocessable]";

    private const int MaxExceptionDepth = 32;
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(2);

    private const RegexOptions Ci = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
    private const RegexOptions Cs = RegexOptions.CultureInvariant;

    // Characters that end a path or URL part.
    private const string PathChars = @"[^\s""'<>|*?:,;()\[\]{}]";

    private sealed record Rule(Regex Regex, MatchEvaluator Evaluator);

    private static string P(string kind) => "[REDACTED:" + kind + "]";

    private static Rule Simple(string pattern, RegexOptions options, string kind)
    {
        var placeholder = P(kind);
        return new Rule(new Regex(pattern, options, MatchTimeout), _ => placeholder);
    }

    private static Rule WithPrefix(string pattern, RegexOptions options, string kind)
    {
        var placeholder = P(kind);
        return new Rule(new Regex(pattern, options, MatchTimeout), m => m.Groups["pre"].Value + placeholder);
    }

    private static readonly string SecretKeys =
        @"(?:passw(?:or)?d|passphrase|pwd|secret|token|api[_-]?key|apikey|access[_-]?key|account[_-]?key|" +
        @"shared[_-]?access[_-]?key|private[_-]?key|client[_-]?secret|credentials?|signature|\b(?:sig|pat)\b)";

    // Ordered: specific token shapes first, generic key=value, then identifying details.
    private static readonly Rule[] Rules =
    [
        // Private key blocks (also truncated ones).
        Simple(@"-----BEGIN [A-Z0-9 ]{0,40}PRIVATE KEY(?: BLOCK)?-----(?:[\s\S]*?-----END [A-Z0-9 ]{0,40}PRIVATE KEY(?: BLOCK)?-----|[A-Za-z0-9+/=\s:,\-]*)",
            Ci, "private-key"),

        // JWT.
        Simple(@"\beyJ[A-Za-z0-9_\-]{5,}\.[A-Za-z0-9_\-]{5,}\.[A-Za-z0-9_\-]*", Cs, "token"),

        // GitHub, Slack, Stripe, Google, npm, AWS access key ids.
        Simple(@"\bgh[pousr]_[A-Za-z0-9]{20,}", Cs, "token"),
        Simple(@"\bgithub_pat_[A-Za-z0-9_]{20,}", Cs, "token"),
        Simple(@"\bxox[abprs]-[A-Za-z0-9\-]{10,}", Cs, "token"),
        Simple(@"\b[sprk]k_(?:live|test)_[A-Za-z0-9]{16,}", Cs, "token"),
        Simple(@"\bAIza[A-Za-z0-9_\-]{35}\b", Cs, "token"),
        Simple(@"\bnpm_[A-Za-z0-9]{36}\b", Cs, "token"),
        Simple(@"\b(?:AKIA|ASIA|AGPA|AIDA|AROA)[A-Z0-9]{16}\b", Cs, "token"),

        // Azure DevOps personal access token: 52 base32 characters.
        Simple(@"(?<![A-Za-z0-9])[a-z2-7]{52}(?![A-Za-z0-9])", Cs, "token"),

        // Authorization header value (any scheme).
        WithPrefix(@"(?<pre>\bAuthorization[""']?\s*[:=]\s*)(?:(?:Bearer|Basic|Token|Digest|Negotiate)\s+)?[^\s""',;]+", Ci, "token"),

        // Bearer tokens elsewhere.
        WithPrefix(@"(?<pre>\bBearer\s+)[A-Za-z0-9._~+/=\-]{16,}", Ci, "token"),

        // URLs with user:pass@.
        new Rule(new Regex(@"(?<scheme>\b[a-z][a-z0-9+.\-]{1,20}://)[^\s/?#@:]+:[^\s/?#@]*@", Ci, MatchTimeout),
            m => m.Groups["scheme"].Value + "[REDACTED:credentials]@"),

        // Secret key=value / "key": "value" pairs (also inside connection strings and JSON).
        new Rule(new Regex(
            @"(?<pre>" + SecretKeys + @"[\w.\-]{0,32}\\?[""']?\s*[:=]\s*)" +
            @"(?:\\""(?<v4>(?:(?!\\"")[^\r\n])*)\\""|""(?<v1>(?:[^""\\]|\\.)*)""|'(?<v2>[^']*)'|(?<v3>[^\s""';,&\\]+))",
            Ci, MatchTimeout),
            m =>
            {
                var pre = m.Groups["pre"].Value;
                var ph = P("secret");
                if (m.Groups["v4"].Success) return pre + "\\\"" + ph + "\\\"";
                if (m.Groups["v1"].Success) return pre + "\"" + ph + "\"";
                if (m.Groups["v2"].Success) return pre + "'" + ph + "'";
                return pre + ph;
            }),

        // Host-like connection string keys.
        WithPrefix(@"(?<pre>\b(?:server|data\s+source|host(?:name)?|network\s+address|address|addr|endpoint)\s*=\s*)[^;\s""']+", Ci, "host"),

        // E-mail addresses.
        Simple(@"[\p{L}\p{N}._%+\-]{1,64}@[\p{L}\p{N}\-]{1,63}(?:\.[\p{L}\p{N}\-]{1,63})+", Ci, "email"),

        // Windows paths: UNC, then drive paths.
        Simple(@"\\\\[\w.\-]+\\" + PathChars + @"*", Cs, "path"),
        Simple(@"(?<![A-Za-z0-9_])[A-Za-z]:[\\/]" + PathChars.Replace(@"[^", @"[^\\/") + @"*(?:[\\/]" + PathChars + @"*)*", Cs, "path"),

        // Unix paths under well-known roots, and ~/ home paths.
        Simple(@"(?<![\w.:/\]\[\-])(?:~|/(?:home|Users|root|var|etc|tmp|opt|usr|mnt|srv|private|Volumes|proc|run|workspace|workspaces|app|data))(?:/" + PathChars + @"+)+", Cs, "path"),
        Simple(@"(?<![\w.:/\]\[\-])~/" + PathChars + @"+", Cs, "path"),

        // Host part of URLs.
        new Rule(new Regex(@"(?<pre>://(?:\[REDACTED:credentials\]@)?)[^\s/?#:@""'<>\[\]]+", Ci, MatchTimeout),
            m => m.Groups["pre"].Value + "[REDACTED:host]"),

        // IPv4 and IPv6.
        Simple(@"(?<![\w.])(?:(?:25[0-5]|2[0-4]\d|1?\d?\d)\.){3}(?:25[0-5]|2[0-4]\d|1?\d?\d)(?![\w])", Cs, "ip"),
        Simple(@"(?<![\w:])(?:[0-9a-f]{1,4}:){7}[0-9a-f]{1,4}(?![\w:])", Ci, "ip"),
        Simple(@"(?<![\w:])(?:[0-9a-f]{1,4}:){1,6}:(?:[0-9a-f]{1,4}(?::[0-9a-f]{1,4}){0,5})?(?![\w:])", Ci, "ip"),
        Simple(@"(?<![\w:])::(?:[0-9a-f]{1,4}(?::[0-9a-f]{1,4}){0,6})?(?![\w:])", Ci, "ip"),

        // Bare host names (lowercase or upper-case well-known suffixes only, to spare namespaces like System.Net).
        Simple(@"(?<![\w.\-])(?:[A-Za-z0-9\-]{1,63}\.){1,10}(?:(?-i:com|net|org|io|local|internal|corp|lan|intranet|edu|gov|info|biz|COM|ORG|LOCAL|CORP|LAN|INTERNAL))(?![\w\-])",
            Ci, "host"),
        Simple(@"\b(?:DESKTOP|LAPTOP|WIN)-[A-Z0-9]{5,}\b", Cs, "host"),
    ];

    /// <summary>
    /// Replaces sensitive values in <paramref name="text"/> with placeholders.
    /// Returns an empty string for null.
    /// </summary>
    public static string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        try
        {
            var current = text;
            foreach (var rule in Rules)
            {
                current = rule.Regex.Replace(current, rule.Evaluator);
            }
            return current;
        }
        catch (RegexMatchTimeoutException)
        {
            return UnprocessablePlaceholder;
        }
    }

    /// <summary>
    /// Builds a redacted copy of an exception (type, message, stack trace, inner exceptions).
    /// Returns null for null.
    /// </summary>
    public static RedactedErrorReport? RedactReport(Exception? exception) => Build(exception, 0);

    /// <summary>Formats a redacted error report as text. Returns an empty string for null.</summary>
    public static string RedactException(Exception? exception) => RedactReport(exception)?.ToString() ?? string.Empty;

    private static RedactedErrorReport? Build(Exception? ex, int depth)
    {
        if (ex is null) return null;
        var inner = new List<RedactedErrorReport>();
        if (depth < MaxExceptionDepth)
        {
            if (ex is AggregateException agg)
            {
                foreach (var e in agg.InnerExceptions)
                {
                    var r = Build(e, depth + 1);
                    if (r is not null) inner.Add(r);
                }
            }
            else
            {
                var r = Build(ex.InnerException, depth + 1);
                if (r is not null) inner.Add(r);
            }
        }
        return new RedactedErrorReport(
            Redact(ex.GetType().FullName),
            Redact(ex.Message),
            Redact(ex.StackTrace),
            inner);
    }
}

/// <summary>A redacted view of an exception tree; contains no original secret values.</summary>
public sealed record RedactedErrorReport(
    string Type,
    string Message,
    string StackTrace,
    IReadOnlyList<RedactedErrorReport> Inner)
{
    public override string ToString()
    {
        var sb = new StringBuilder();
        Append(sb, 0);
        return sb.ToString();
    }

    private void Append(StringBuilder sb, int level)
    {
        if (level > 0) sb.AppendLine().Append(' ', level * 2).Append("---> ");
        sb.Append(Type).Append(": ").Append(Message);
        if (StackTrace.Length > 0) sb.AppendLine().Append(StackTrace);
        foreach (var i in Inner) i.Append(sb, level + 1);
    }
}
