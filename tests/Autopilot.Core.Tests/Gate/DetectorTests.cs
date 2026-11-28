using Autopilot.Core.Gate.Detectors;

namespace Autopilot.Core.Tests.Gate;

public class DetectorTests
{
    private static DetectorInput In(params (string path, string[] lines)[] files) =>
        new(files.Select(f => new ChangedFile(f.path, f.lines)).ToList());

    [Theory]
    [InlineData("-----BEGIN RSA PRIVATE KEY-----")]
    [InlineData("var k = \"AKIAABCDEFGHIJKLMNOP\";")]
    [InlineData("var password = \"hunter2hunter2\";")]
    [InlineData("\"ApiKey\": \"abcd1234efgh\"")]
    public void Secret_detector_flags_secrets(string line)
    {
        var f = new SecretDetector().Detect(In(("src/A.cs", new[] { line })));
        var finding = Assert.Single(f);
        Assert.Equal("secret", finding.DetectorId);
        Assert.Equal("src/A.cs", finding.FilePath);
        Assert.False(string.IsNullOrEmpty(finding.Message));
    }

    [Theory]
    [InlineData("var password = config[\"Db:Password\"];")]
    [InlineData("var token = Environment.GetEnvironmentVariable(\"TOKEN\");")]
    [InlineData("var x = 1;")]
    public void Secret_detector_ignores_config_reads(string line)
    {
        Assert.Empty(new SecretDetector().Detect(In(("src/A.cs", new[] { line }))));
    }

    [Fact]
    public void Dependency_detector_flags_unlisted_package()
    {
        var f = new RiskyDependencyDetector(new[] { "Good.Pkg" })
            .Detect(In(("a.csproj", new[] { "<PackageReference Include=\"Evil.Pkg\" Version=\"1.0.0\" />" })));
        Assert.Single(f);
        Assert.Equal("risky-dependency", f[0].DetectorId);
    }

    [Theory]
    [InlineData("<PackageReference Include=\"Good.Pkg\" Version=\"*\" />")]
    [InlineData("<PackageReference Include=\"Good.Pkg\" Version=\"1.*\" />")]
    [InlineData("<PackageReference Include=\"Good.Pkg\" Version=\"[1.0,)\" />")]
    public void Dependency_detector_flags_floating_version(string line)
    {
        var f = new RiskyDependencyDetector(new[] { "Good.Pkg" }).Detect(In(("a.csproj", new[] { line })));
        Assert.Single(f);
    }

    [Fact]
    public void Dependency_detector_ignores_allowlisted_pinned()
    {
        var f = new RiskyDependencyDetector(new[] { "Good.Pkg" })
            .Detect(In(("a.csproj", new[] { "<PackageReference Include=\"Good.Pkg\" Version=\"2.3.1\" />" })));
        Assert.Empty(f);
    }

    [Fact]
    public void Controller_detector_flags_without_access_test()
    {
        var f = new ControllerTestDetector().Detect(In(
            ("src/Web/OrdersController.cs", new[] { "// x" }),
            ("tests/OrdersTests.cs", new[] { "Assert.True(true);" })));
        var finding = Assert.Single(f);
        Assert.Equal("src/Web/OrdersController.cs", finding.FilePath);
    }

    [Fact]
    public void Controller_detector_quiet_when_access_test_changed()
    {
        var f = new ControllerTestDetector().Detect(In(
            ("src/Web/OrdersController.cs", new[] { "// x" }),
            ("tests/OrdersTests.cs", new[] { "public void Other_user_gets_403_on_ownership() {}" })));
        Assert.Empty(f);
    }

    [Fact]
    public void Controller_detector_quiet_without_controller()
    {
        Assert.Empty(new ControllerTestDetector().Detect(In(("src/A.cs", new[] { "x" }))));
    }
}
