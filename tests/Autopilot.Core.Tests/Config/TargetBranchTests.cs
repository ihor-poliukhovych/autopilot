using Autopilot.Core.Config;
using Autopilot.Core.Git;

namespace Autopilot.Core.Tests.Config;

public class TargetBranchTests
{
    private const string Template = "release/release-{major}.{minor}.*";

    [Fact]
    public void Newest_matching_branch_wins_by_numeric_version()
    {
        string[] branches = ["main", "release/release-7.14.2", "release/release-7.14.10", "release/release-7.14.3", "release/release-7.13.9", "release/release-7.140.1"];

        var result = TargetBranchResolver.Resolve("Go-OPS\\2026\\7.14", Template, branches);

        Assert.True(result.Found);
        Assert.Equal("release/release-7.14.10", result.Branch);
    }

    [Fact]
    public void Three_over_two_when_both_match()
    {
        var result = TargetBranchResolver.Resolve("Go-OPS\\2026\\7.14", Template, ["release/release-7.14.2", "release/release-7.14.3"]);

        Assert.Equal("release/release-7.14.3", result.Branch);
    }

    [Fact]
    public void No_matching_branch_is_a_no_match_not_main()
    {
        var result = TargetBranchResolver.Resolve("Go-OPS\\2026\\7.15", Template, ["main", "release/release-7.14.2"]);

        Assert.False(result.Found);
        Assert.Null(result.Branch);
        Assert.Contains("7.15", result.Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Go-OPS\\2026\\Backlog")]
    [InlineData("")]
    [InlineData(null)]
    public void Iteration_without_a_version_is_a_no_match(string? iteration)
    {
        var result = TargetBranchResolver.Resolve(iteration, Template, ["main", "release/release-7.14.2"]);

        Assert.False(result.Found);
        Assert.NotNull(result.Reason);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("  ")]
    public void Unset_template_is_a_no_match(string? template)
    {
        var result = TargetBranchResolver.Resolve("Go-OPS\\2026\\7.14", template, ["release/release-7.14.2"]);

        Assert.False(result.Found);
    }

    [Fact]
    public void Template_without_wildcard_matches_the_exact_branch()
    {
        var result = TargetBranchResolver.Resolve("7.14", "release/{major}.{minor}", ["release/7.14", "release/7.14.1"]);

        Assert.Equal("release/7.14", result.Branch);
    }

    [Fact]
    public void Config_without_the_settings_loads_unchanged_and_serializes_without_them()
    {
        var config = ConfigLoader.Parse("""{ "baseBranch": "dev" }""");

        Assert.Null(config.TargetBranchTemplate);
        Assert.Null(config.TargetBranchField);
        Assert.Empty(ConfigLoader.Validate(config));
        var json = ConfigLoader.Serialize(config);
        Assert.DoesNotContain("targetBranch", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Config_round_trips_with_the_settings()
    {
        var config = new AutopilotConfig { TargetBranchTemplate = Template, TargetBranchField = "iteration" };

        var back = ConfigLoader.Parse(ConfigLoader.Serialize(config));

        Assert.Equal(Template, back.TargetBranchTemplate);
        Assert.Equal("iteration", back.TargetBranchField);
        Assert.Empty(ConfigLoader.Validate(back));
    }

    [Theory]
    [InlineData("release/release-7.14")]
    [InlineData("")]
    public void Template_without_wildcard_or_placeholder_is_rejected(string template)
    {
        var problems = ConfigLoader.Validate(new AutopilotConfig { TargetBranchTemplate = template });

        Assert.Single(problems, p => p.Contains("targetBranchTemplate", StringComparison.Ordinal));
    }
}
