using Nyx.Desktop.Core.AccountStatus;

namespace Nyx.Desktop.Tests.AccountStatus;

public sealed class HoyoLabCaptureDiagnosticsTests
{
    [Theory]
    [InlineData("[12,34,56]", "reader-lines-12-34-56")]
    [InlineData("[4096]", "reader-lines-4096")]
    [InlineData("[]", "reader-validation")]
    [InlineData("[0]", "reader-validation")]
    [InlineData("[4097]", "reader-validation")]
    [InlineData("[1,2,3,4]", "reader-validation")]
    [InlineData("[1.5]", "reader-validation")]
    [InlineData("[\"sensitive\"]", "reader-validation")]
    [InlineData("{\"url\":\"https://example.invalid/account\"}", "reader-validation")]
    [InlineData(null, "reader-validation")]
    public void Only_bounded_source_line_numbers_can_reach_the_diagnostic(string? json, string expected) =>
        Assert.Equal(expected, HoyoLabCaptureDiagnostics.FromScriptFrames(json));
}
