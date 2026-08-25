using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Nyx.Desktop.Core.Exports;
using Nyx.Desktop.Infrastructure.Exports;

namespace Nyx.Desktop.Tests.Exports;

public sealed class ProcessAchievementHelperRunnerTests
{
    private const string JobId = "0123456789abcdef0123456789abcdef";
    private const string Proof = "00112233445566778899aabbccddeeff00112233445566778899aabbccddeeff";
    private const string ContractFixtureSha256 = "500b2e05a75bf5b721132723af1153f16d7512657cbe25ef1773747e712945dd";

    [Fact]
    public void Canonical_contract_fixture_is_hash_pinned_and_accepted_by_the_launcher()
    {
        var fixture = Path.Combine(
            AppContext.BaseDirectory,
            "Contracts",
            "pengo-achievements-v1.fixture.json");
        var bytes = File.ReadAllBytes(fixture);
        Assert.Equal(ContractFixtureSha256, Convert.ToHexStringLower(SHA256.HashData(bytes)));

        using var temp = new TemporaryDirectory();
        var gameRoot = temp.Combine("Genshin Impact");
        Directory.CreateDirectory(gameRoot);
        const string fileName = "20260717T120000Z-abc123.json";
        var acceptedBytes = Encoding.UTF8.GetBytes(
            Encoding.UTF8.GetString(bytes).Replace(
                "gi-fixture",
                AchievementCatalogVersions.Genshin,
                StringComparison.Ordinal));
        File.WriteAllBytes(Path.Combine(gameRoot, fileName), acceptedBytes);
        var artifact = ProcessAchievementHelperRunner.ValidateCompletedOutput(
            Invocation(temp.Path, "gi"),
            0,
            Status(
                "exported",
                itemCount: 2,
                outputFile: "Genshin Impact/" + fileName,
                gameId: "gi"));

        Assert.Equal("pengo-achievements-v1", artifact.Format);
        Assert.Equal(2, artifact.ItemCount);
    }

    [Fact]
    public void Readiness_is_not_published_until_authenticated_ready_state()
    {
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ownershipTransfers = 0;
        var sequence = new ProcessAchievementHelperRunner.StatusSequence(
            ready,
            () => ownershipTransfers++);
        sequence.Accept(Status("preparing"));
        Assert.False(ready.Task.IsCompleted);
        Assert.Equal(0, ownershipTransfers);

        sequence.Accept(Status("ready"));
        Assert.True(ready.Task.IsCompletedSuccessfully);
        Assert.Equal(1, ownershipTransfers);
        sequence.Accept(Status("waiting_for_game"));
        sequence.Accept(Status("exported", itemCount: 2, outputFile: "Honkai Star Rail/20260717T120000Z-abc123.json"));
        Assert.True(sequence.IsTerminal);
    }

    [Fact]
    public void Wrong_job_or_proof_cannot_authenticate_ipc()
    {
        var invocation = Invocation(Path.GetTempPath());
        var wrongJob = JsonSerializer.Serialize(Status("preparing") with { JobId = new string('a', 32) });
        var wrongProof = JsonSerializer.Serialize(Status("preparing") with { Proof = new string('f', 64) });

        Assert.Equal(
            "helper-authentication-failed",
            Assert.Throws<ExportProviderException>(() =>
                ProcessAchievementHelperRunner.ParseAuthenticatedStatus(wrongJob, invocation, Proof)).Code);
        Assert.Equal(
            "helper-authentication-failed",
            Assert.Throws<ExportProviderException>(() =>
                ProcessAchievementHelperRunner.ParseAuthenticatedStatus(wrongProof, invocation, Proof)).Code);
    }

    [Theory]
    [InlineData("capture_timeout_no_frames")]
    [InlineData("capture_timeout_unrecognized_frames")]
    [InlineData("capture_timeout_no_commands")]
    public void Privacy_safe_capture_stage_failures_are_authenticated_and_allowlisted(string code)
    {
        var invocation = Invocation(Path.GetTempPath());
        var line = JsonSerializer.Serialize(Status("failed", errorCode: code));

        var status = ProcessAchievementHelperRunner.ParseAuthenticatedStatus(
            line,
            invocation,
            Proof);

        Assert.Equal("failed", status.State);
        Assert.Equal(code, status.ErrorCode);
    }

    [Fact]
    public void Nonzero_exit_cannot_promote_an_unrelated_json_to_success()
    {
        using var temp = new TemporaryDirectory();
        var gameRoot = temp.Combine("Honkai Star Rail");
        Directory.CreateDirectory(gameRoot);
        File.WriteAllText(Path.Combine(gameRoot, "unrelated.json"), Contract("hsr", 1));

        var error = Assert.Throws<ExportProviderException>(() =>
            ProcessAchievementHelperRunner.ValidateCompletedOutput(Invocation(temp.Path), 1, null));

        Assert.Equal("provider-failed", error.Code);
    }

    [Theory]
    [InlineData("{\"hsr_achievements\":[1,2]}")]
    [InlineData("{\"kind\":\"pengo-achievements\",\"version\":1,\"game\":\"hsr\",\"catalogVersion\":\"hsr-fixture\",\"exportedAt\":\"2026-07-26T12:00:00Z\",\"achievements\":[{\"id\":2,\"status\":\"complete\"},{\"id\":1,\"status\":\"complete\"}]}")]
    [InlineData("{\"kind\":\"pengo-achievements\",\"version\":1,\"game\":\"hsr\",\"catalogVersion\":\"hsr-fixture\",\"exportedAt\":\"2026-07-26T12:00:00Z\",\"achievements\":[{\"id\":1,\"status\":\"partial\"}]}")]
    [InlineData("{\"kind\":\"pengo-achievements\",\"version\":1,\"game\":\"gi\",\"catalogVersion\":\"hsr-fixture\",\"exportedAt\":\"2026-07-26T12:00:00Z\",\"achievements\":[{\"id\":1,\"status\":\"complete\"}]}")]
    [InlineData("{\"kind\":\"pengo-achievements\",\"version\":1,\"game\":\"hsr\",\"catalogVersion\":\"hsr-fixture\",\"exportedAt\":\"not-a-date\",\"achievements\":[{\"id\":1,\"status\":\"complete\"}]}")]
    [InlineData("{\"kind\":\"pengo-achievements\",\"version\":1,\"game\":\"hsr\",\"catalogVersion\":\"hsr-fixture\",\"exportedAt\":\"2026-07-26T12:00:00Z\",\"achievements\":[{\"id\":1,\"status\":\"complete\"}],\"extra\":[]}")]
    [InlineData("not-json")]
    public void Malformed_or_wrong_shape_export_fails_closed(string json)
    {
        using var temp = new TemporaryDirectory();
        var gameRoot = temp.Combine("Honkai Star Rail");
        Directory.CreateDirectory(gameRoot);
        const string fileName = "20260717T120000Z-abc123.json";
        File.WriteAllText(
            Path.Combine(gameRoot, fileName),
            json.Replace(
                "hsr-fixture",
                AchievementCatalogVersions.StarRail,
                StringComparison.Ordinal));
        var final = Status("exported", itemCount: 2, outputFile: "Honkai Star Rail/" + fileName);

        var error = Assert.Throws<ExportProviderException>(() =>
            ProcessAchievementHelperRunner.ValidateCompletedOutput(Invocation(temp.Path), 0, final));

        Assert.Equal("output-invalid", error.Code);
    }

    [Theory]
    [InlineData("Honkai Star Rail/pengo-achievements-20260717T120000Z-abc123.json")]
    [InlineData("Honkai Star Rail/20260717T120000Z.json")]
    [InlineData("Honkai Star Rail/20269999T999999Z-abc123.json")]
    [InlineData("hsr/20260717T120000Z-abc123.json")]
    [InlineData("Genshin Impact/20260717T120000Z-abc123.json")]
    public void Noncontract_name_or_wrong_game_folder_fails_before_file_access(string outputFile)
    {
        using var temp = new TemporaryDirectory();

        var error = Assert.Throws<ExportProviderException>(() =>
            ProcessAchievementHelperRunner.ValidateCompletedOutput(
                Invocation(temp.Path),
                0,
                Status("exported", itemCount: 1, outputFile: outputFile)));

        Assert.Equal("output-unsafe", error.Code);
    }

    [Theory]
    [InlineData("gi", "Genshin Impact")]
    [InlineData("hsr", "Honkai Star Rail")]
    public void Exact_authenticated_result_name_and_import_shape_are_required_for_success(
        string gameId,
        string gameDirectory)
    {
        using var temp = new TemporaryDirectory();
        var gameRoot = temp.Combine(gameDirectory);
        Directory.CreateDirectory(gameRoot);
        const string fileName = "20260717T120000Z-abc123.json";
        File.WriteAllText(Path.Combine(gameRoot, fileName), Contract(gameId, 10, 20));

        var artifact = ProcessAchievementHelperRunner.ValidateCompletedOutput(
            Invocation(temp.Path, gameId),
            0,
            Status(
                "exported",
                itemCount: 2,
                outputFile: gameDirectory + "/" + fileName,
                gameId: gameId));

        Assert.Equal(2, artifact.ItemCount);
        Assert.Equal(Path.Combine(gameRoot, fileName), artifact.OutputPath);
        Assert.Equal("pengo-achievements-v1", artifact.Format);
        Assert.Equal(DateTimeOffset.Parse("2026-07-26T12:00:00Z"), artifact.CreatedAt);
    }

    [Fact]
    public void Cancelled_terminal_state_never_claims_readiness()
    {
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sequence = new ProcessAchievementHelperRunner.StatusSequence(ready);
        sequence.Accept(Status("preparing"));
        sequence.Accept(Status("cancelled"));

        Assert.True(sequence.IsTerminal);
        Assert.False(ready.Task.IsCompleted);
    }

    [Fact]
    public async Task Helper_exit_without_terminal_status_is_bounded_and_reported()
    {
        using var statusCancellation = new CancellationTokenSource();
        var statusRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = statusCancellation.Token.Register(
            () => statusRead.TrySetCanceled(statusCancellation.Token));

        var error = await Assert.ThrowsAsync<ExportProviderException>(() =>
            ProcessAchievementHelperRunner.WaitForStatusDrainAfterExitAsync(
                statusRead.Task,
                statusCancellation,
                TimeSpan.FromMilliseconds(25),
                CancellationToken.None));

        Assert.Equal("helper-protocol-incomplete", error.Code);
        Assert.True(statusCancellation.IsCancellationRequested);
    }

    [Theory]
    [InlineData("gi")]
    [InlineData("hsr")]
    public void Verified_launch_blocks_ancestor_and_leaf_substitution_for_normal_user_capture(
        string gameId)
    {
        using var temp = new TemporaryDirectory();
        var package = temp.Combine("Package");
        var helperPath = Path.Combine(
            package,
            VerifiedAchievementHelperBoundary.ExpectedHelperFileName);
        var attackerPackage = temp.Combine("AttackerPackage");
        var attackerHelper = Path.Combine(
            attackerPackage,
            VerifiedAchievementHelperBoundary.ExpectedHelperFileName);
        Directory.CreateDirectory(package);
        Directory.CreateDirectory(attackerPackage);
        File.WriteAllText(helperPath, "reviewed-helper");
        File.WriteAllText(attackerHelper, "attacker-helper");
        var expectedHash = SHA256.HashData(File.ReadAllBytes(helperPath));
        using var binding = VerifiedAchievementHelperLaunchBinding.OpenAndVerify(
            helperPath,
            expectedHash);
        var invocation = Invocation(temp.Path, helperPath, gameId);
        var dispatcher = new SwappingDispatcher(package, helperPath, attackerPackage, attackerHelper);

        var error = Assert.Throws<ExportProviderException>(() =>
            ProcessAchievementHelperRunner.StartBoundHelper(invocation, binding, dispatcher));

        Assert.Equal("helper-start-failed", error.Code);
        Assert.False(dispatcher.StartInfo!.UseShellExecute);
        Assert.Equal(string.Empty, dispatcher.StartInfo.Verb);
        Assert.Equal("reviewed-helper", File.ReadAllText(helperPath));
        ProcessAchievementHelperRunner.EnsureBoundHelper(invocation, binding);
    }

    private static AchievementHelperInvocation Invocation(string outputRoot) => new(
        Path.Combine(outputRoot, VerifiedAchievementHelperBoundary.ExpectedHelperFileName),
        [],
        "hsr",
        JobId,
        outputRoot);

    private static AchievementHelperInvocation Invocation(string outputRoot, string gameId) => new(
        Path.Combine(outputRoot, VerifiedAchievementHelperBoundary.ExpectedHelperFileName),
        [],
        gameId,
        JobId,
        outputRoot);

    private static AchievementHelperInvocation Invocation(
        string outputRoot,
        string helperPath,
        string gameId) => new(
        helperPath,
        [],
        gameId,
        JobId,
        outputRoot);

    private static ProcessAchievementHelperRunner.HelperStatus Status(
        string state,
        long? itemCount = null,
        string? outputFile = null,
        string? errorCode = null,
        string gameId = "hsr") => new(
            1,
            JobId,
            gameId,
            "achievements",
            state,
            Proof,
            itemCount,
            outputFile,
            errorCode);

    private static string Contract(string gameId, params uint[] ids)
    {
        var catalogVersion = AchievementCatalogVersions.Get(gameId);
        var rows = string.Join(",", ids.Select(id => $"{{\"id\":{id},\"status\":\"complete\"}}"));
        return $"{{\"kind\":\"pengo-achievements\",\"version\":1,\"game\":\"{gameId}\",\"catalogVersion\":\"{catalogVersion}\",\"exportedAt\":\"2026-07-26T12:00:00Z\",\"achievements\":[{rows}]}}";
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "nyx-achievement-runner-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }
        public string Combine(string value) => System.IO.Path.Combine(Path, value);

        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }

    private sealed class SwappingDispatcher(
        string package,
        string helperPath,
        string attackerPackage,
        string attackerHelper) : IAchievementHelperProcessDispatcher
    {
        public ProcessStartInfo? StartInfo { get; private set; }

        public bool Start(Process process)
        {
            StartInfo = process.StartInfo;
            AssertDenied(() => Directory.Move(package, package + ".moved"));
            AssertDenied(() => File.Move(helperPath, helperPath + ".moved"));
            AssertDenied(() => File.Move(attackerHelper, helperPath, overwrite: true));
            AssertDenied(() => Directory.Move(attackerPackage, package));
            return false;
        }

        private static void AssertDenied(Action swap)
        {
            var exception = Record.Exception(swap);
            Assert.True(
                exception is IOException or UnauthorizedAccessException,
                $"Expected the bound path swap to be denied, but got {exception?.GetType().Name ?? "no exception"}.");
        }
    }
}
