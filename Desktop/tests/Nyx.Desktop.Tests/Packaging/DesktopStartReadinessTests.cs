using System.Diagnostics;

namespace Nyx.Desktop.Tests.Packaging;

public sealed class DesktopStartReadinessTests
{
    private static readonly string DesktopRoot = FindDesktopRoot();
    private static readonly string StartScript = Path.Combine(DesktopRoot, "scripts", "start-nyx.ps1");

    [Fact]
    public void Start_wrapper_is_fixed_visible_and_does_not_forward_command_text()
    {
        var wrapper = File.ReadAllText(Path.Combine(DesktopRoot, "Start Nyx.cmd"));

        Assert.Contains("powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File \"%~dp0scripts\\start-nyx.ps1\"", wrapper);
        Assert.Contains("exit /b %nyxExitCode%", wrapper);
        Assert.DoesNotContain("%*", wrapper);
        Assert.DoesNotContain("runas", wrapper, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("-WindowStyle", wrapper, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("http", wrapper, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Start_script_has_fail_closed_normal_user_and_unpackaged_boundaries()
    {
        var script = File.ReadAllText(StartScript);

        Assert.Contains("Version.Build -lt 22621", script);
        Assert.Contains("Architecture]::X64", script);
        Assert.Contains("--list-sdks", script);
        Assert.Contains("WindowsPackageType", script);
        Assert.Contains("WindowsAppSDKSelfContained", script);
        Assert.Contains("PublishTrimmed", script);
        Assert.Contains("Test-UnpackagedOutput", script);
        Assert.Contains("Nyx.Desktop.App.pri", script);
        Assert.Contains("pengo-achievements-launcher.exe", script);
        Assert.Matches(@"if \(-not \$CheckOnly\)\s*\{\s*\$requiredOutputPaths \+= \$achievementHelperOutput\s*\}", script);
        Assert.Contains("verify_release.py", script);
        Assert.Contains("-p:AchievementHelperSource=$builtAchievementHelper", script);
        Assert.Contains("-p:AchievementHelperSha256=$achievementHelperSha256", script);
        Assert.Contains("verify-release.ps1", script);
        Assert.Contains("https://github.com/34736384/genshin-fps-unlock.git", script);
        Assert.Contains("& $git.Source -c core.longpaths=true clone --quiet --depth 1 --branch v3.5.0 https://github.com/34736384/genshin-fps-unlock.git", script, StringComparison.Ordinal);
        Assert.Contains("2b85d61dd06f6e11ad86fdd6bd90339f9abc58eb", script);
        Assert.Contains("-p:Genshin120HelperSource=$genshin120Helper", script);
        Assert.Contains("-p:Genshin120HelperSha256=$genshin120HelperSha256", script);
        Assert.Contains("if ($isAdministrator -and -not $CheckOnly)", script);
        Assert.True(
            script.IndexOf("if ($isAdministrator -and -not $CheckOnly)", StringComparison.Ordinal) <
            script.IndexOf("& $dotnet.Source restore", StringComparison.Ordinal),
            "Elevation must be refused before optional restore.");
        Assert.Contains("if ($CheckOnly -and $Restore)", script);
        Assert.Contains("--no-restore", script);
        Assert.Contains("'build'", script);
        Assert.Contains("[System.Diagnostics.Process]::Start", script);
        Assert.DoesNotContain("Start-Process", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Verb RunAs", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("AppX\\Nyx.Desktop.App.exe", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Invoke-WebRequest", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("http://", script, StringComparison.OrdinalIgnoreCase);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(
            script,
            "https://",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase));
        Assert.Contains("https://github.com/34736384/genshin-fps-unlock.git", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Real_check_only_preflight_succeeds_without_start_or_restore()
    {
        var result = RunPowerShell(StartScript, "-CheckOnly");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Nyx app preflight passed", result.Output);
        Assert.Contains("A real start will build and verify its achievement helper before launching", result.Output);
        Assert.DoesNotContain("Starting Nyx", result.Output);
        Assert.DoesNotContain("Restoring", result.Output);
    }

    [Fact]
    public void Check_only_rejects_restore()
    {
        var result = RunPowerShell(StartScript, "-CheckOnly", "-Restore");

        Assert.Equal(14, result.ExitCode);
        Assert.Contains("Check-only never restores", result.Output);
    }

    [Fact]
    public void Fixture_in_quoted_path_reports_missing_project_without_starting()
    {
        using var fixture = StartFixture.Create("quoted fixture");
        fixture.WriteGlobalJson("10.0.100");

        var result = RunPowerShell(fixture.StartScript, "-CheckOnly");

        Assert.Equal(12, result.ExitCode);
        Assert.Contains("Desktop project is incomplete", result.Output);
        Assert.DoesNotContain("Starting Nyx", result.Output);
    }

    [Fact]
    public void Fixture_reports_missing_pinned_sdk()
    {
        using var fixture = StartFixture.Create("sdk fixture");
        fixture.WriteMinimumProject("99.99.999");

        var result = RunPowerShell(fixture.StartScript, "-CheckOnly");

        Assert.Equal(11, result.ExitCode);
        Assert.Contains("Install the pinned .NET SDK 99.99.999", result.Output);
    }

    [Fact]
    public void Fixture_reports_missing_restore_assets_without_restoring()
    {
        using var fixture = StartFixture.Create("restore fixture");
        fixture.WriteMinimumProject("10.0.100");

        var result = RunPowerShell(fixture.StartScript, "-CheckOnly");

        Assert.Equal(14, result.ExitCode);
        Assert.Contains("Restore assets are missing", result.Output);
        Assert.DoesNotContain("Restoring", result.Output);
    }

    [Fact]
    public void Fixture_reports_incomplete_unpackaged_output_without_starting()
    {
        using var fixture = StartFixture.Create("run support fixture");
        fixture.WriteMinimumProject("10.0.100");
        fixture.WriteRunAssets();

        var result = RunPowerShell(fixture.StartScript, "-CheckOnly");

        Assert.Equal(13, result.ExitCode);
        Assert.Contains("unpackaged x64 build output is incomplete", result.Output);
        Assert.DoesNotContain("Starting Nyx", result.Output);
    }

    [Fact]
    public void Fixture_accepts_the_direct_WinUI_component_package()
    {
        using var fixture = StartFixture.Create("winui component fixture");
        fixture.WriteMinimumProject("10.0.100", "Microsoft.WindowsAppSDK.WinUI", "2.3.6");
        fixture.WriteRunAssets("Microsoft.WindowsAppSDK.WinUI", "2.3.6");

        var result = RunPowerShell(fixture.StartScript, "-CheckOnly");

        Assert.Equal(13, result.ExitCode);
        Assert.Contains("unpackaged x64 build output is incomplete", result.Output);
        Assert.DoesNotContain("configuration is missing or ambiguous", result.Output);
    }

    [Fact]
    public void Start_script_rejects_oversized_project_xml_before_parsing()
    {
        using var fixture = StartFixture.Create("oversized project fixture");
        fixture.WriteOversizedProject("10.0.100");

        var result = RunPowerShell(fixture.StartScript, "-CheckOnly");

        Assert.Equal(12, result.ExitCode);
        Assert.Contains("app project XML is invalid", result.Output);
        Assert.DoesNotContain(fixture.Root, result.Output, StringComparison.OrdinalIgnoreCase);
    }

    private static CommandResult RunPowerShell(string script, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("-NoLogo");
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(script);
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("PowerShell did not start.");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(30_000), "PowerShell fixture timed out.");
        return new CommandResult(process.ExitCode, output + error);
    }

    private static string FindDesktopRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (current.Name.Equals("Desktop", StringComparison.OrdinalIgnoreCase) &&
                File.Exists(Path.Combine(current.FullName, "Nyx.Desktop.slnx")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Desktop repository root was not found.");
    }

    private sealed record CommandResult(int ExitCode, string Output);

    private sealed class StartFixture : IDisposable
    {
        private StartFixture(string root)
        {
            Root = root;
            var scripts = Path.Combine(root, "Desktop", "scripts");
            Directory.CreateDirectory(scripts);
            StartScript = Path.Combine(scripts, "start-nyx.ps1");
            File.Copy(DesktopStartReadinessTests.StartScript, StartScript);
        }

        public string Root { get; }
        public string StartScript { get; }

        public static StartFixture Create(string label) =>
            new(Path.Combine(Path.GetTempPath(), "Nyx Desktop Tests", label, Guid.NewGuid().ToString("N")));

        public void WriteGlobalJson(string sdk) =>
            File.WriteAllText(Path.Combine(Root, "global.json"), $"{{\"sdk\":{{\"version\":\"{sdk}\"}}}}");

        public void WriteMinimumProject(
            string sdk,
            string packageName = "Microsoft.WindowsAppSDK",
            string packageVersion = "2.2.0")
        {
            WriteGlobalJson(sdk);
            var appRoot = Path.Combine(Root, "Desktop", "src", "Nyx.Desktop.App");
            Directory.CreateDirectory(appRoot);
            File.WriteAllText(Path.Combine(appRoot, "Nyx.Desktop.App.csproj"),
                $"<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0-windows10.0.22621.0</TargetFramework><OutputType>WinExe</OutputType><WindowsPackageType>None</WindowsPackageType><WindowsAppSDKSelfContained>true</WindowsAppSDKSelfContained><PublishTrimmed>false</PublishTrimmed></PropertyGroup><ItemGroup><PackageReference Include=\"{packageName}\" Version=\"{packageVersion}\" /></ItemGroup></Project>");
        }

        public void WriteRunAssets(
            string packageName = "Microsoft.WindowsAppSDK",
            string packageVersion = "2.2.0")
        {
            var objectRoot = Path.Combine(Root, "Desktop", "src", "Nyx.Desktop.App", "obj");
            Directory.CreateDirectory(objectRoot);
            File.WriteAllText(
                Path.Combine(objectRoot, "project.assets.json"),
                $"{{\"libraries\":{{\"{packageName}/{packageVersion}\":{{}}}}}}");
        }

        public void WriteOversizedProject(string sdk)
        {
            WriteMinimumProject(sdk);
            var project = Path.Combine(Root, "Desktop", "src", "Nyx.Desktop.App", "Nyx.Desktop.App.csproj");
            File.WriteAllText(project, "<Project><!--" + new string('x', 1_048_576) + "--></Project>");
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }

}
