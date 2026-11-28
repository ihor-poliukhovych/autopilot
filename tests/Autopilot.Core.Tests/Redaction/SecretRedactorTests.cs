using System.Diagnostics;
using Autopilot.Core.Redaction;

namespace Autopilot.Core.Tests.Redaction;

public class SecretRedactorTests
{
    private static string R(string? s) => SecretRedactor.Redact(s);

    private static void AssertGone(string input, params string[] secrets)
    {
        var output = R(input);
        foreach (var s in secrets) Assert.DoesNotContain(s, output);
        Assert.Equal(output, R(output)); // idempotent
    }

    [Fact]
    public void Null_and_empty_are_safe()
    {
        Assert.Equal("", R(null));
        Assert.Equal("", R(""));
        Assert.Equal("", SecretRedactor.RedactException(null));
        Assert.Null(SecretRedactor.RedactReport(null));
    }

    [Theory]
    [InlineData("Build finished in 3 seconds.")]
    [InlineData("Task 307 moved to Review; 12:30:45 elapsed, version 1.2.3, System.Net.Http.HttpClient")]
    [InlineData("Hello wörld, ключ — 日本語 text. design: simple, path of least resistance")]
    [InlineData("The basic authentication bearer concept is explained in the docs.")]
    [InlineData("at Autopilot.Core.Redaction.SecretRedactor.Redact(String text)")]
    public void Innocent_text_is_unchanged(string text)
    {
        Assert.Equal(text, R(text));
    }

    [Fact]
    public void Github_tokens_are_masked()
    {
        var t = "ghp_" + new string('a', 36);
        Assert.Equal("token [REDACTED:token] end", R($"token {t} end"));
        AssertGone("x github_pat_11ABCDEFG0abcdefghijklmnop_qrstuvwxyz0123456789 y", "11ABCDEFG0");
    }

    [Fact]
    public void Azure_devops_pat_aws_and_jwt_are_masked()
    {
        var pat = new string('a', 26) + new string('7', 26);
        Assert.Equal(52, pat.Length);
        AssertGone($"pat {pat}!", pat);
        AssertGone("key AKIAIOSFODNN7EXAMPLE here", "AKIAIOSFODNN7EXAMPLE");
        const string jwt = "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.SflKxwRJSMeKKF2QT4fwpMeJf36POk6yJV_adQssw5c";
        Assert.Equal("jwt=[REDACTED:token]", R("jwt=" + jwt));
        AssertGone($"see ({jwt}).", "SflKxwRJ");
    }

    [Fact]
    public void Bearer_and_authorization_headers_are_masked()
    {
        Assert.Equal("Authorization: [REDACTED:token]", R("Authorization: Bearer abcdef1234567890XYZ"));
        Assert.Equal("authorization: [REDACTED:token]", R("authorization: basic dXNlcjpwYXNzd29yZA=="));
        Assert.Equal("curl -H Bearer [REDACTED:token]", R("curl -H Bearer abcdef1234567890XYZ"));
        AssertGone("BEARER abcdef1234567890XYZ", "abcdef1234567890XYZ");
    }

    [Fact]
    public void Private_key_blocks_are_masked_including_multiline_and_truncated()
    {
        var pem = "-----BEGIN RSA PRIVATE KEY-----\nMIIEowIBAAKCAQEA1234\nabcdEFGH5678\n-----END RSA PRIVATE KEY-----";
        Assert.Equal("before\n[REDACTED:private-key]\nafter", R($"before\n{pem}\nafter"));
        AssertGone("-----BEGIN PRIVATE KEY-----\nMIIEvQIBADANBgkqhkiG9w0BAQ", "MIIEvQ");
        AssertGone("-----begin openssh private key-----\nb3BlbnNzaC1rZXk\n-----end openssh private key-----", "b3BlbnNz");
    }

    [Theory]
    [InlineData("password=hunter2", "hunter2")]
    [InlineData("PASSWORD: hunter2", "hunter2")]
    [InlineData("Pwd=hunter2;Other=1", "hunter2")]
    [InlineData("client_secret = 's3cr3t value'", "s3cr3t")]
    [InlineData("api-key=abc123xyz", "abc123xyz")]
    [InlineData("https://x.test/cb?token=abc123xyz&a=1", "abc123xyz")]
    [InlineData("{\"password\":\"hunter2\",\"user\":\"bob\"}", "hunter2")]
    [InlineData("{ \"Password\" : \"hun ter2\" }", "hun ter2")]
    [InlineData("{\\\"password\\\":\\\"hunter2\\\"}", "hunter2")]
    [InlineData("MySecret=hunter2", "hunter2")]
    public void Key_value_secrets_are_masked(string input, string secret)
    {
        AssertGone(input, secret);
        Assert.Contains("[REDACTED:secret]", R(input));
    }

    [Fact]
    public void Json_structure_is_preserved()
    {
        Assert.Equal("{\"password\":\"[REDACTED:secret]\",\"user\":\"bob\"}", R("{\"password\":\"hunter2\",\"user\":\"bob\"}"));
    }

    [Fact]
    public void Connection_strings_are_masked()
    {
        var r = R("Server=tcp:db1.contoso.com,1433;Database=app;User Id=sa;Password=P@ss;w0rd;Encrypt=true");
        Assert.DoesNotContain("db1.contoso.com", r);
        Assert.DoesNotContain("P@ss", r);
        Assert.Contains("Database=app", r);
        AssertGone("Server=myhost;Password=\"a;b c\"", "a;b c", "myhost");
        AssertGone("AccountKey=abcDEF123+/==;EndpointSuffix=core.windows.net", "abcDEF123");
    }

    [Fact]
    public void Url_credentials_are_masked()
    {
        var r = R("clone https://bob:s3cret@git.example.com/repo.git now");
        Assert.Equal("clone https://[REDACTED:credentials]@[REDACTED:host]/repo.git now", r);
        AssertGone("postgres://admin:p%40ss@10.0.0.5:5432/db", "p%40ss", "admin", "10.0.0.5");
    }

    [Fact]
    public void Emails_are_masked()
    {
        Assert.Equal("mail [REDACTED:email], and [REDACTED:email].", R("mail Bob.Smith+tag@Example.COM, and jörg@bücher.de."));
    }

    [Fact]
    public void Paths_are_masked()
    {
        AssertGone(@"Error in C:\Users\JohnDoe\source\repo\a.cs:line 12", "JohnDoe", "repo");
        Assert.Contains(":line 12", R(@"in C:\Users\JohnDoe\a.cs:line 12"));
        AssertGone("failed at /home/alice/work/x.txt", "alice");
        AssertGone("failed at /Users/alice/Library/x", "alice");
        AssertGone("cfg ~/.ssh/id_rsa", "id_rsa");
        AssertGone(@"share \\fileserver\team\docs\a.docx", "fileserver", "team");
        AssertGone("file:///C:/Users/Bob/x.txt", "Bob");
        AssertGone(@"D:/data/secret-project/a.txt", "secret-project");
        AssertGone("(/var/lib/autopilot/state.db)", "autopilot/state");
    }

    [Fact]
    public void Hosts_and_ips_are_masked()
    {
        AssertGone("connect to db1.corp.contoso.com failed", "contoso");
        AssertGone("connect to DB1.CONTOSO.COM failed", "CONTOSO");
        AssertGone("ip 192.168.1.20 and 10.0.0.1.", "192.168.1.20", "10.0.0.1");
        AssertGone("ipv6 2001:db8:85a3:0:0:8a2e:370:7334 and fe80::1 and ::1", "2001:db8", "fe80", "::1");
        AssertGone("machine DESKTOP-AB12CD3 down", "AB12CD3");
        AssertGone("GET https://api.example.org:8443/v1/x", "example.org");
        Assert.Equal("System.Net.Http failed at 12:30:45", R("System.Net.Http failed at 12:30:45"));
    }

    [Fact]
    public void Mixed_case_keys_are_masked()
    {
        AssertGone("PaSsWoRd=Hunter2 TOKEN=abc12345 ApiKey=zzz999", "Hunter2", "abc12345", "zzz999");
    }

    [Fact]
    public void Secrets_next_to_punctuation_are_masked()
    {
        AssertGone("(password=hunter2)", "hunter2");
        AssertGone("[ghp_" + new string('b', 30) + "],", "bbbbbbbbbb");
        AssertGone("<bob@example.com>;", "bob@");
        AssertGone("\"token\": \"abcdef123456\",", "abcdef123456");
        AssertGone("a,password=hunter2,b", "hunter2");
    }

    [Fact]
    public void Several_secrets_in_one_line_are_all_masked()
    {
        var line = "u=bob@example.com p=/home/bob/x token=tok123 ip=10.1.2.3 password=pw1 AKIAIOSFODNN7EXAMPLE";
        AssertGone(line, "bob", "tok123", "10.1.2.3", "pw1", "AKIAIOSFODNN7EXAMPLE");
    }

    [Fact]
    public void Multiline_quoted_values_are_masked()
    {
        AssertGone("secret = \"line1\nline2 hunter2\nline3\" after", "hunter2", "line1", "line3");
    }

    [Fact]
    public void Unicode_text_around_secrets_is_kept_and_secret_masked()
    {
        var r = R("пароль ключ: password=секрет123 — готово 🎉");
        Assert.DoesNotContain("секрет123", r);
        Assert.Contains("готово 🎉", r);
    }

    [Fact]
    public void Redaction_is_idempotent_for_all_placeholders()
    {
        var text = string.Join("\n",
            "password=hunter2", "\"secret\":\"x y\"", "bob@example.com", @"C:\Users\bob\a.cs", "/home/bob/a",
            "https://u:p@host.example.com/a", "10.0.0.1", "Authorization: Bearer abcdefghijklmnop1234",
            "-----BEGIN PRIVATE KEY-----\nAAAA\n-----END PRIVATE KEY-----", "Server=abc;Password=xyz",
            "ghp_" + new string('c', 36), "host db.example.com");
        var once = R(text);
        Assert.Equal(once, R(once));
        Assert.Equal(once, R(R(once)));
    }

    [Fact]
    public void Very_long_input_does_not_backtrack_catastrophically()
    {
        var inputs = new[]
        {
            new string('a', 200_000),
            string.Concat(Enumerable.Repeat("password", 20_000)),
            string.Concat(Enumerable.Repeat("a.", 50_000)),
            string.Concat(Enumerable.Repeat("a:", 50_000)),
            "-----BEGIN PRIVATE KEY-----" + new string('A', 200_000),
            string.Concat(Enumerable.Repeat("https://", 20_000)),
            string.Concat(Enumerable.Repeat("x@", 50_000)),
            "password=\"" + new string('b', 200_000),
            new string('/', 100_000),
            new string(' ', 100_000) + "token=abc",
        };
        var sw = Stopwatch.StartNew();
        foreach (var input in inputs)
        {
            var r = R(input);
            Assert.False(string.IsNullOrEmpty(r));
        }
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(60), $"took {sw.Elapsed}");
    }

    [Fact]
    public void Exception_report_masks_message_stack_trace_and_inner_exceptions()
    {
        Exception ex;
        try
        {
            try { throw new InvalidOperationException(@"inner: password=hunter2 at C:\Users\bob\x.cs"); }
            catch (Exception inner) { throw new ApplicationException("outer user bob@example.com host db.example.com", inner); }
        }
        catch (Exception e) { ex = e; }

        var text = SecretRedactor.RedactException(ex);
        Assert.DoesNotContain("hunter2", text);
        Assert.DoesNotContain("bob", text);
        Assert.DoesNotContain("db.example.com", text);
        Assert.Contains("ApplicationException", text);
        Assert.Contains("InvalidOperationException", text);

        var report = SecretRedactor.RedactReport(ex)!;
        Assert.Single(report.Inner);
        Assert.Contains("[REDACTED:secret]", report.Inner[0].Message);
        Assert.Equal(text, SecretRedactor.Redact(text));
    }

    [Fact]
    public void Aggregate_exception_inner_exceptions_are_all_masked()
    {
        var agg = new AggregateException(new Exception("a token=t0k3n1"), new Exception("b password=pw22"));
        var text = SecretRedactor.RedactException(agg);
        Assert.DoesNotContain("t0k3n1", text);
        Assert.DoesNotContain("pw22", text);
    }

    [Fact]
    public void Deep_exception_chain_is_bounded()
    {
        Exception e = new("password=deep1");
        for (var i = 0; i < 200; i++) e = new Exception("level " + i, e);
        var text = SecretRedactor.RedactException(e);
        Assert.DoesNotContain("deep1", text);
    }
}
