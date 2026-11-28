using System.Globalization;
using System.Text.RegularExpressions;

namespace Autopilot.Web.Tests;

public sealed class PaletteTests
{
    private static string Css()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var path = Path.Combine(dir.FullName, "src", "Autopilot.Web", "wwwroot", "app.css");
            if (File.Exists(path)) return File.ReadAllText(path);
            dir = dir.Parent;
        }
        throw new FileNotFoundException("wwwroot/app.css not found above " + AppContext.BaseDirectory);
    }

    // The three token blocks: light :root, the prefers-color-scheme dark block, and :root[data-theme="dark"].
    private static (string Light, string AutoDark, string ForcedDark) Blocks()
    {
        var css = Css();
        var light = Regex.Match(css, @"^:root\s*\{(.*?)\n\}", RegexOptions.Singleline | RegexOptions.Multiline).Groups[1].Value;
        var auto = Regex.Match(css, @"@media \(prefers-color-scheme: dark\)\s*\{\s*:root:not\(\[data-theme=""light""\]\)\s*\{(.*?)\}", RegexOptions.Singleline).Groups[1].Value;
        var forced = Regex.Match(css, @":root\[data-theme=""dark""\]\s*\{(.*?)\}", RegexOptions.Singleline).Groups[1].Value;
        Assert.NotEmpty(light);
        Assert.NotEmpty(auto);
        Assert.NotEmpty(forced);
        return (light, auto, forced);
    }

    private static string Token(string block, string name)
    {
        var m = Regex.Match(block, @"(?<![\w-])" + Regex.Escape(name) + @":\s*(#[0-9a-fA-F]{6})");
        Assert.True(m.Success, "token " + name + " missing");
        return m.Groups[1].Value.ToLowerInvariant();
    }

    private static string Raw(string block, string name)
    {
        var m = Regex.Match(block, @"(?<![\w-])" + Regex.Escape(name) + @":\s*([^;]+);");
        Assert.True(m.Success, "token " + name + " missing");
        return m.Groups[1].Value.Trim();
    }

    [Fact]
    public void Dark_theme_uses_the_compact_warm_tokens_in_both_dark_blocks()
    {
        var (_, auto, forced) = Blocks();
        var expected = new Dictionary<string, string>
        {
            ["--bg"] = "#161412", ["--surface"] = "#1d1b18", ["--surface-2"] = "#25221f", ["--surface-3"] = "#2f2b26",
            ["--border"] = "#38342e", ["--border-strong"] = "#4a453d", ["--text"] = "#ebe6de", ["--text-2"] = "#cbc4b9",
            ["--muted"] = "#a29a8e", ["--accent"] = "#6ba4de", ["--link"] = "#86b6e8", ["--ok"] = "#62b57f",
            ["--warn"] = "#e2ac58", ["--bad"] = "#e2685d",
        };
        foreach (var block in new[] { auto, forced })
            foreach (var (name, value) in expected)
                Assert.Equal(value, Token(block, name));
    }

    [Fact]
    public void Light_theme_uses_the_compact_warm_tokens()
    {
        var (light, _, _) = Blocks();
        var expected = new Dictionary<string, string>
        {
            ["--bg"] = "#f3f0ea", ["--surface"] = "#fbfaf7", ["--surface-2"] = "#eeeae3", ["--surface-3"] = "#e4dfd6",
            ["--border"] = "#d9d3c8", ["--text"] = "#24201b", ["--muted"] = "#665d51", ["--accent"] = "#2b6cb0",
            ["--ok"] = "#22703c", ["--warn"] = "#85550a", ["--bad"] = "#ad2f27",
        };
        foreach (var (name, value) in expected)
            Assert.Equal(value, Token(light, name));
    }

    [Fact]
    public void Spacing_sizes_radius_and_type_tokens_are_defined()
    {
        var (light, _, _) = Blocks();
        var expected = new Dictionary<string, string>
        {
            ["--sp-2"] = "2px", ["--sp-4"] = "4px", ["--sp-6"] = "6px", ["--sp-8"] = "8px", ["--sp-10"] = "10px",
            ["--sp-12"] = "12px", ["--sp-16"] = "16px", ["--sp-20"] = "20px",
            ["--gutter"] = "10px", ["--left-col"] = "340px", ["--radius-panel"] = "8px", ["--radius-control"] = "6px",
            ["--fs-body"] = "13px", ["--fs-label"] = "10.5px",
            ["--h-control"] = "32px", ["--h-row-control"] = "24px", ["--h-hit"] = "32px", ["--h-chip"] = "28px",
            ["--h-topbar"] = "40px", ["--h-tab"] = "34px",
        };
        foreach (var (name, value) in expected)
            Assert.Equal(value, Raw(light, name));
        var css = Css();
        Assert.Matches(@"\.topbar\s*\{[^}]*min-height: var\(--h-topbar\)", css);
        Assert.Matches(@"\.tabs a\s*\{[^}]*min-height: var\(--h-tab\)", css);
        Assert.Matches(@"\.chip\s*\{[^}]*min-height: var\(--h-chip\)", css);
        Assert.Matches(@"button, \.btn\s*\{[^}]*min-height: var\(--h-control\)", css);
        Assert.Matches(@"input, select, textarea\s*\{[^}]*min-height: var\(--h-control\)", css);
        Assert.Matches(@"button\.sm, \.btn\.sm\s*\{[^}]*min-height: var\(--h-row-control\)", css);
        Assert.Matches(@"button\.sm::after, \.btn\.sm::after\s*\{[^}]*height: var\(--h-hit\)", css);
    }

    [Fact]
    public void Touch_and_phone_keep_44px_controls_and_a_14px_body()
    {
        var css = Css();
        var m = Regex.Match(css, @"@media \(pointer: coarse\), \(max-width: 640px\)\s*\{\s*:root\s*\{([^}]*)\}");
        Assert.True(m.Success, "touch token block missing");
        var block = m.Groups[1].Value;
        Assert.Equal("14px", Raw(block, "--fs-body"));
        Assert.Equal("44px", Raw(block, "--h-control"));
        Assert.Equal("44px", Raw(block, "--h-row-control"));
        Assert.Equal("44px", Raw(block, "--h-hit"));
    }

    [Fact]
    public void Gutter_token_is_used_for_panel_and_column_gaps()
    {
        var css = Css();
        Assert.Matches(@"\.app-layout\s*\{[^}]*gap: var\(--gutter\)", css);
        Assert.Matches(@"\.split\s*\{[^}]*gap: var\(--gutter\)", css);
        Assert.Matches(@"\.pool-main\s*\{[^}]*gap: var\(--gutter\)", css);
        Assert.Matches(@"\.pool-right\s*\{[^}]*gap: var\(--gutter\)", css);
        Assert.Matches(@"\.pool-live\s*\{[^}]*gap: var\(--gutter\)", css);
    }

    [Fact]
    public void Check_rows_and_icon_buttons_use_the_height_tokens_not_fixed_pixels()
    {
        var css = Css();
        Assert.Matches(@"\.check-row\s*\{[^}]*min-height: var\(--h-control\)", css);
        Assert.Matches(@"\.icon-btn\s*\{[^}]*min-height: var\(--h-control\)[^}]*width: var\(--h-control\)", css);
    }

    [Fact]
    public void Touch_query_is_exactly_coarse_pointer_or_phone_width()
    {
        // Intended OR: a coarse pointer on a wide tablet and a narrow desktop window both need 44px targets.
        var css = Css();
        Assert.Single(Regex.Matches(css, @"@media \(pointer: coarse\)[^{]*\{"));
        Assert.Contains("@media (pointer: coarse), (max-width: 640px) {", css);
    }

    [Fact]
    public void Docs_record_the_computed_accent_on_accent_soft_ratio_for_both_themes()
    {
        var (light, auto, _) = Blocks();
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "docs", "ui-tokens.md"))) dir = dir.Parent;
        Assert.NotNull(dir);
        var doc = File.ReadAllText(Path.Combine(dir!.FullName, "docs", "ui-tokens.md"));
        var row = doc.Split('\n').Single(l => l.StartsWith("| `--accent` on `--accent-soft`"));
        var dark = Contrast(Token(auto, "--accent"), Token(auto, "--accent-soft")).ToString("F2", CultureInfo.InvariantCulture);
        var lt = Contrast(Token(light, "--accent"), Token(light, "--accent-soft")).ToString("F2", CultureInfo.InvariantCulture);
        Assert.Contains(dark, row);
        Assert.Contains(lt, row);
    }

    [Fact]
    public void Every_var_reference_resolves_to_a_defined_token()
    {
        var wwwroot = Path.GetDirectoryName(FindApp())!;
        var files = Directory.EnumerateFiles(wwwroot, "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".css") || f.EndsWith(".html") || f.EndsWith(".js")).ToArray();
        var text = string.Concat(files.Select(File.ReadAllText));
        var defined = Regex.Matches(Css(), @"(?<![\w-])(--[\w-]+)\s*:").Select(m => m.Groups[1].Value).ToHashSet();
        var used = Regex.Matches(text, @"var\((--[\w-]+)").Select(m => m.Groups[1].Value).ToHashSet();
        var missing = used.Where(u => !defined.Contains(u)).ToArray();
        Assert.True(missing.Length == 0, "undefined tokens: " + string.Join(", ", missing));
        foreach (var name in new[] { "--s1", "--s2", "--s3", "--s4", "--s5", "--s6", "--radius", "--radius-sm", "--fs-xs", "--fs-sm", "--fs-md", "--fs-lg", "--fs-xl", "--fs-stat",
                     "--accent-text", "--accent-soft", "--ok-soft", "--warn-soft", "--bad-soft", "--run", "--run-soft", "--code-bg", "--shadow" })
            Assert.Contains(name, defined);
    }

    private static string FindApp()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var path = Path.Combine(dir.FullName, "src", "Autopilot.Web", "wwwroot", "app.css");
            if (File.Exists(path)) return path;
            dir = dir.Parent;
        }
        throw new FileNotFoundException("wwwroot/app.css not found");
    }

    [Theory]
    [InlineData("light")]
    [InlineData("auto-dark")]
    [InlineData("forced-dark")]
    public void Text_muted_and_status_colours_meet_AA_on_bg_and_surface(string theme)
    {
        var (light, auto, forced) = Blocks();
        var block = theme switch { "light" => light, "auto-dark" => auto, _ => forced };
        foreach (var bgName in new[] { "--bg", "--surface" })
            foreach (var fg in new[] { "--text", "--muted", "--accent", "--link", "--ok", "--warn", "--bad" })
            {
                var ratio = Contrast(Token(block, fg), Token(block, bgName));
                Assert.True(ratio >= 4.5, $"{theme}: {fg} on {bgName} is {ratio:F2}:1");
            }
        foreach (var (fg, soft) in new[] { ("--ok", "--ok-soft"), ("--warn", "--warn-soft"), ("--bad", "--bad-soft"), ("--accent", "--accent-soft") })
        {
            var ratio = Contrast(Token(block, fg), Token(block, soft));
            Assert.True(ratio >= 4.5, $"{theme}: {fg} on {soft} is {ratio:F2}:1");
        }
        var onAccent = Contrast(Token(block, "--accent-text"), Token(block, "--accent"));
        Assert.True(onAccent >= 4.5, $"{theme}: accent-text on accent is {onAccent:F2}:1");
    }

    private static (int R, int G, int B) Rgb(string hex) =>
        (int.Parse(hex[1..3], NumberStyles.HexNumber), int.Parse(hex[3..5], NumberStyles.HexNumber), int.Parse(hex[5..7], NumberStyles.HexNumber));

    private static double Luminance(string hex)
    {
        var (r, g, b) = Rgb(hex);
        static double C(int v) { var s = v / 255.0; return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4); }
        return 0.2126 * C(r) + 0.7152 * C(g) + 0.0722 * C(b);
    }

    private static double Contrast(string a, string b)
    {
        var (x, y) = (Luminance(a), Luminance(b));
        return (Math.Max(x, y) + 0.05) / (Math.Min(x, y) + 0.05);
    }
}
