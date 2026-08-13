using Nyx.Desktop.Core.PublisherMaintenance;
using Nyx.Desktop.Infrastructure.PublisherMaintenance;

namespace Nyx.Desktop.Tests.PublisherMaintenance;

public sealed class HoyoBranchResponseParserTests
{
    [Fact]
    public void Sanitized_three_game_batch_projects_only_reviewed_fields()
    {
        var parser = new HoyoBranchResponseParser();

        var parsed = parser.TryParse(SanitizedHoyoFixtures.Utf8(SanitizedHoyoFixtures.ValidBatch), out var batch);

        Assert.True(parsed);
        Assert.NotNull(batch);
        Assert.Equal(["genshin", "hsr", "zzz"], batch.Games.Keys.OrderBy(key => key, StringComparer.Ordinal));
        Assert.Equal("4.3.0", batch.Games["hsr"].LiveVersion.ToString());
        Assert.Equal("4.4.0", batch.Games["hsr"].PreDownloadVersion?.ToString());
        Assert.Equal(PublisherPreDownloadState.Offered, batch.Games["hsr"].PreDownload);
        Assert.Equal(PublisherOptionalSignal.Advertised, batch.Games["hsr"].IncrementalPathAdvertised);
        Assert.DoesNotContain("redacted-fixture", batch.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("ignored-fixture", batch.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Entry_order_is_non_authoritative()
    {
        var json = SanitizedHoyoFixtures.Batch(
            SanitizedHoyoFixtures.ZzzEntry,
            SanitizedHoyoFixtures.GenshinEntry,
            SanitizedHoyoFixtures.HsrEntry);

        Assert.True(new HoyoBranchResponseParser().TryParse(SanitizedHoyoFixtures.Utf8(json), out _));
    }

    [Theory]
    [MemberData(nameof(InvalidIdentityBatches))]
    public void Missing_extra_duplicate_or_wrong_identity_rejects_entire_batch(string json)
    {
        Assert.False(new HoyoBranchResponseParser().TryParse(SanitizedHoyoFixtures.Utf8(json), out var batch));
        Assert.Null(batch);
    }

    public static IEnumerable<object[]> InvalidIdentityBatches()
    {
        yield return [SanitizedHoyoFixtures.Batch(SanitizedHoyoFixtures.GenshinEntry, SanitizedHoyoFixtures.HsrEntry)];
        yield return [SanitizedHoyoFixtures.Batch(
            SanitizedHoyoFixtures.GenshinEntry,
            SanitizedHoyoFixtures.HsrEntry,
            SanitizedHoyoFixtures.ZzzEntry,
            SanitizedHoyoFixtures.ZzzEntry.Replace("U5hbdsT9W7", "extra-id", StringComparison.Ordinal))];
        yield return [SanitizedHoyoFixtures.Batch(
            SanitizedHoyoFixtures.GenshinEntry,
            SanitizedHoyoFixtures.HsrEntry,
            SanitizedHoyoFixtures.HsrEntry)];
        yield return [SanitizedHoyoFixtures.ValidBatch.Replace("hkrpg_global", "nap_global", StringComparison.Ordinal)];
    }

    [Theory]
    [InlineData("release", "4.3.0")]
    [InlineData("main", "4.3")]
    [InlineData("main", "4.3.0.0")]
    [InlineData("main", "v4.3.0")]
    [InlineData("main", "04.3.0")]
    [InlineData("main", "2147483648.3.0")]
    public void Main_branch_and_version_are_exact(string branch, string tag)
    {
        var hsr = SanitizedHoyoFixtures.HsrEntry
            .Replace("\"branch\": \"main\"", $"\"branch\": \"{branch}\"", StringComparison.Ordinal)
            .Replace("\"tag\": \"4.3.0\"", $"\"tag\": \"{tag}\"", StringComparison.Ordinal);

        var parsed = new HoyoBranchResponseParser().TryParse(
            SanitizedHoyoFixtures.Utf8(SanitizedHoyoFixtures.Batch(
                SanitizedHoyoFixtures.GenshinEntry,
                hsr,
                SanitizedHoyoFixtures.ZzzEntry)),
            out _);

        Assert.False(parsed);
    }

    [Theory]
    [InlineData("preview", "4.4.0")]
    [InlineData("predownload", "4.3.0")]
    [InlineData("predownload", "4.2.9")]
    [InlineData("predownload", "4.4")]
    public void Invalid_or_not_newer_pre_download_is_unknown_without_erasing_valid_main(
        string branch,
        string tag)
    {
        var hsr = SanitizedHoyoFixtures.HsrEntry
            .Replace("\"branch\": \"predownload\"", $"\"branch\": \"{branch}\"", StringComparison.Ordinal)
            .Replace("\"tag\": \"4.4.0\"", $"\"tag\": \"{tag}\"", StringComparison.Ordinal);
        var json = SanitizedHoyoFixtures.Batch(
            SanitizedHoyoFixtures.GenshinEntry,
            hsr,
            SanitizedHoyoFixtures.ZzzEntry);

        Assert.True(new HoyoBranchResponseParser().TryParse(SanitizedHoyoFixtures.Utf8(json), out var batch));
        Assert.Equal("4.3.0", batch!.Games["hsr"].LiveVersion.ToString());
        Assert.Equal(PublisherPreDownloadState.Unknown, batch.Games["hsr"].PreDownload);
        Assert.Null(batch.Games["hsr"].PreDownloadVersion);
    }

    [Fact]
    public void Capability_true_with_null_pre_download_is_not_an_offer()
    {
        Assert.True(new HoyoBranchResponseParser().TryParse(
            SanitizedHoyoFixtures.Utf8(SanitizedHoyoFixtures.ValidBatch),
            out var batch));

        var genshin = batch!.Games["genshin"];
        Assert.Equal(PublisherOptionalSignal.Advertised, genshin.BasePackagePreDownloadCapability);
        Assert.Equal(PublisherPreDownloadState.NotOffered, genshin.PreDownload);
        Assert.Null(genshin.PreDownloadVersion);
    }

    [Fact]
    public void Malformed_optional_diff_tags_only_make_incremental_detail_unknown()
    {
        var hsr = SanitizedHoyoFixtures.HsrEntry.Replace(
            "\"diff_tags\": [\"4.3.0\"]",
            "\"diff_tags\": { \"unexpected\": true }",
            StringComparison.Ordinal);
        var json = SanitizedHoyoFixtures.Batch(
            SanitizedHoyoFixtures.GenshinEntry,
            hsr,
            SanitizedHoyoFixtures.ZzzEntry);

        Assert.True(new HoyoBranchResponseParser().TryParse(SanitizedHoyoFixtures.Utf8(json), out var batch));
        var status = batch!.Games["hsr"];
        Assert.Equal("4.3.0", status.LiveVersion.ToString());
        Assert.Equal(PublisherPreDownloadState.Offered, status.PreDownload);
        Assert.Equal(PublisherOptionalSignal.Unknown, status.IncrementalPathAdvertised);
    }

    [Fact]
    public void Malformed_optional_base_capability_only_makes_that_detail_unknown()
    {
        var genshin = SanitizedHoyoFixtures.GenshinEntry.Replace(
            "\"enable_base_pkg_predownload\": true",
            "\"enable_base_pkg_predownload\": { \"unexpected\": true }",
            StringComparison.Ordinal);
        var json = SanitizedHoyoFixtures.Batch(
            genshin,
            SanitizedHoyoFixtures.HsrEntry,
            SanitizedHoyoFixtures.ZzzEntry);

        Assert.True(new HoyoBranchResponseParser().TryParse(SanitizedHoyoFixtures.Utf8(json), out var batch));
        var status = batch!.Games["genshin"];
        Assert.Equal("6.7.0", status.LiveVersion.ToString());
        Assert.Equal(PublisherPreDownloadState.NotOffered, status.PreDownload);
        Assert.Equal(PublisherOptionalSignal.Unknown, status.BasePackagePreDownloadCapability);
    }

    [Theory]
    [MemberData(nameof(MalformedCriticalPayloads))]
    public void Malformed_duplicate_deep_or_oversized_json_is_rejected(ReadOnlyMemory<byte> body)
    {
        Assert.False(new HoyoBranchResponseParser().TryParse(body, out var batch));
        Assert.Null(batch);
    }

    public static IEnumerable<object[]> MalformedCriticalPayloads()
    {
        yield return [SanitizedHoyoFixtures.Utf8("{")];
        yield return [SanitizedHoyoFixtures.Utf8(
            SanitizedHoyoFixtures.ValidBatch.Replace(
                "\"retcode\": 0",
                "\"retcode\": 0, \"retcode\": 0",
                StringComparison.Ordinal))];
        yield return [SanitizedHoyoFixtures.Utf8(
            SanitizedHoyoFixtures.ValidBatch.Replace(
                "\"tag\": \"4.3.0\"",
                "\"tag\": \"4.3.0\", \"tag\": \"4.3.0\"",
                StringComparison.Ordinal))];
        yield return [SanitizedHoyoFixtures.Utf8(
            "{\"retcode\":0,\"data\":{\"game_branches\":[],\"unknown\":"
            + new string('[', 20)
            + "0"
            + new string(']', 20)
            + "}}")];
        yield return [new byte[HoyoBranchResponseParser.MaximumResponseBytes + 1]];
    }

    [Fact]
    public void Nonzero_retcode_is_rejected()
    {
        var json = SanitizedHoyoFixtures.ValidBatch.Replace(
            "\"retcode\": 0",
            "\"retcode\": -1",
            StringComparison.Ordinal);

        Assert.False(new HoyoBranchResponseParser().TryParse(SanitizedHoyoFixtures.Utf8(json), out _));
    }

    [Fact]
    public void Multi_megabyte_and_pathological_versions_are_rejected_by_cheap_length_bound()
    {
        var multiMegabyteDigits = new string('9', 4 * 1024 * 1024);
        var pathologicalSegments = "1." + new string('7', 4 * 1024 * 1024) + ".0";

        Assert.False(StrictVersion.TryParse(multiMegabyteDigits, out _));
        Assert.False(StrictVersion.TryParse(pathologicalSegments, out _));
        Assert.Equal(32, StrictVersion.MaximumTextLength);
        Assert.Equal(10, StrictVersion.MaximumSegmentLength);
        Assert.True(StrictVersion.TryParse("2147483647.0.0", out _));
        Assert.False(StrictVersion.TryParse("2147483648.0.0", out _));
    }
}
