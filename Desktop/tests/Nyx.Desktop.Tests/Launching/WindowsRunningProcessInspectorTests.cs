using Nyx.Desktop.Core.Launching;
using Nyx.Desktop.Infrastructure.Launching;

namespace Nyx.Desktop.Tests.Launching;

public sealed class WindowsRunningProcessInspectorTests
{
    private const string Expected = @"C:\Games\Current\StarRail.exe";

    [Fact]
    public void No_same_name_process_is_the_only_not_running_result()
    {
        Assert.Equal(
            RunningProcessStatus.NotRunning,
            WindowsRunningProcessInspector.EvaluateSameNamePaths(
                [],
                Expected,
                differentPathIsUncertain: true));
    }

    [Fact]
    public void Exact_path_is_running_even_when_another_same_name_path_exists()
    {
        Assert.Equal(
            RunningProcessStatus.Running,
            WindowsRunningProcessInspector.EvaluateSameNamePaths(
                [@"C:\Games\Old\StarRail.exe", Expected],
                Expected,
                differentPathIsUncertain: true));
    }

    [Fact]
    public void Same_name_at_different_install_root_is_uncertain_not_absent()
    {
        Assert.Equal(
            RunningProcessStatus.Uncertain,
            WindowsRunningProcessInspector.EvaluateSameNamePaths(
                [@"C:\Games\Old\StarRail.exe"],
                Expected,
                differentPathIsUncertain: true));
    }

    [Fact]
    public void Inaccessible_same_name_process_is_uncertain_not_absent()
    {
        Assert.Equal(
            RunningProcessStatus.Uncertain,
            WindowsRunningProcessInspector.EvaluateSameNamePaths(
                [null],
                Expected,
                differentPathIsUncertain: true));
    }

    [Fact]
    public void Ordinary_launcher_check_ignores_accessible_different_path_but_not_inaccessible_path()
    {
        Assert.Equal(
            RunningProcessStatus.NotRunning,
            WindowsRunningProcessInspector.EvaluateSameNamePaths(
                [@"C:\Unrelated\launcher.exe"],
                @"C:\Program Files\HoYoPlay\launcher.exe",
                differentPathIsUncertain: false));
        Assert.Equal(
            RunningProcessStatus.Uncertain,
            WindowsRunningProcessInspector.EvaluateSameNamePaths(
                [null],
                @"C:\Program Files\HoYoPlay\launcher.exe",
            differentPathIsUncertain: false));
    }

    [Fact]
    public void Limited_information_query_can_prove_an_elevated_exact_path()
    {
        var query = new FakePathQuery([Expected]);
        var inspector = new WindowsRunningProcessInspector(query);

        var result = inspector.CheckStrict("StarRail", Expected);

        Assert.Equal(RunningProcessStatus.Running, result);
        Assert.Equal("StarRail", Assert.Single(query.ProcessNames));
    }

    [Theory]
    [InlineData("access denied")]
    [InlineData("process exited during query")]
    public void Failed_or_racing_limited_information_query_is_uncertain(string _)
    {
        var inspector = new WindowsRunningProcessInspector(new FakePathQuery([null]));

        Assert.Equal(
            RunningProcessStatus.Uncertain,
            inspector.CheckStrict("StarRail", Expected));
    }

    [Fact]
    public void Native_query_requests_only_process_query_limited_information()
    {
        var type = typeof(LimitedInformationWindowsProcessPathQuery);
        var access = type.GetField(
            "ProcessQueryLimitedInformation",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        var source = File.ReadAllText(Path.Combine(
            FindDesktopRoot(),
            "src",
            "Nyx.Desktop.Infrastructure",
            "Launching",
            "WindowsLaunchProcessBoundaries.cs"));

        Assert.NotNull(access);
        Assert.Equal(0x1000U, access!.GetRawConstantValue());
        Assert.Contains("QueryFullProcessImageName", source, StringComparison.Ordinal);
        Assert.DoesNotContain(".MainModule", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ProcessQueryInformation = 0x0400", source, StringComparison.Ordinal);
    }

    private static string FindDesktopRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "src")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new DirectoryNotFoundException("Desktop source root was not found.");
    }

    private sealed class FakePathQuery(IReadOnlyList<string?> paths) : IWindowsProcessPathQuery
    {
        public List<string> ProcessNames { get; } = [];

        public IReadOnlyList<string?> QueryExecutablePaths(string processName)
        {
            ProcessNames.Add(processName);
            return paths;
        }
    }
}
