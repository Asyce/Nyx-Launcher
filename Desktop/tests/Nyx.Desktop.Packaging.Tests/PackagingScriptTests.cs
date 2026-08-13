using System.Diagnostics;
using System.Security.Cryptography;

namespace Nyx.Desktop.Packaging.Tests;

public sealed class PackagingScriptTests
{
    private static readonly string DesktopRoot = FindDesktopRoot();
    private static readonly string PackagingRoot = Path.Combine(DesktopRoot, "packaging");

    [Fact]
    public void Packaging_and_install_scripts_parse_without_errors()
    {
        foreach (var script in new[]
        {
            Path.Combine(PackagingRoot, "build-development-package.ps1"),
            Path.Combine(PackagingRoot, "verify-genshin-provenance.ps1"),
            Path.Combine(PackagingRoot, "scripts", "Install-Nyx.ps1"),
            Path.Combine(PackagingRoot, "scripts", "Uninstall-Nyx.ps1"),
        })
        {
            var escaped = script.Replace("'", "''", StringComparison.Ordinal);
            var result = RunPowerShell(
                "$errors=$null; [void][Management.Automation.Language.Parser]::ParseFile('" + escaped +
                "',[ref]$null,[ref]$errors); if($errors.Count){$errors | ForEach-Object Message; exit 1}");
            Assert.Equal(0, result.ExitCode);
        }
    }

    [Fact]
    public void Scripts_do_not_interpret_commands_or_download_and_uninstall_requires_explicit_data_switch()
    {
        var build = File.ReadAllText(Path.Combine(PackagingRoot, "build-development-package.ps1"));
        var provenance = File.ReadAllText(Path.Combine(PackagingRoot, "verify-genshin-provenance.ps1"));
        var install = File.ReadAllText(Path.Combine(PackagingRoot, "scripts", "Install-Nyx.ps1"));
        var uninstall = File.ReadAllText(Path.Combine(PackagingRoot, "scripts", "Uninstall-Nyx.ps1"));
        var all = build + provenance + install + uninstall;

        Assert.DoesNotContain("Invoke-Expression", all, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Invoke-WebRequest", all, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Start-Process", all, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("cmd.exe", all, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("-Recurse -Force $", all, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[switch] $RemoveUserData", uninstall);
        Assert.Contains("if ($RemoveUserData)", uninstall);
        Assert.Contains("Run this installer without administrator approval", install);
    }

    [Fact]
    public void Solution_gate_includes_the_updater_and_packaging_tests()
    {
        var solution = File.ReadAllText(Path.Combine(DesktopRoot, "Nyx.Desktop.slnx"));
        var appProject = File.ReadAllText(Path.Combine(
            DesktopRoot,
            "src",
            "Nyx.Desktop.App",
            "Nyx.Desktop.App.csproj"));
        var testProject = File.ReadAllText(Path.Combine(
            DesktopRoot,
            "tests",
            "Nyx.Desktop.Tests",
            "Nyx.Desktop.Tests.csproj"));
        var configuration = solution + appProject + testProject;

        Assert.Contains("tests/Nyx.Desktop.Packaging.Tests/Nyx.Desktop.Packaging.Tests.csproj", solution);
        Assert.Contains("tools/Nyx.Desktop.Update/Nyx.Desktop.Update.csproj", solution);
        Assert.Contains("<Platform Project=\"x64\" />", solution, StringComparison.Ordinal);
        Assert.DoesNotContain("<Platform Project=\"x86\" />", solution, StringComparison.Ordinal);
        Assert.Contains("<Platforms>x64</Platforms>", appProject, StringComparison.Ordinal);
        Assert.Contains("<PlatformTarget>x64</PlatformTarget>", appProject, StringComparison.Ordinal);
        Assert.Contains("<RuntimeIdentifier>win-x64</RuntimeIdentifier>", appProject, StringComparison.Ordinal);
        Assert.DoesNotContain("PublishProfile", configuration, StringComparison.Ordinal);
        Assert.DoesNotContain("Microsoft.Windows.SDK.BuildTools.WinApp", configuration, StringComparison.Ordinal);
        Assert.DoesNotContain("coverlet.collector", configuration, StringComparison.Ordinal);
        Assert.DoesNotContain("Nyx.Desktop.ReadOnlyPilot", configuration, StringComparison.Ordinal);
    }

    [Fact]
    public void Development_package_verifies_and_stamps_the_exact_embedded_achievement_helper()
    {
        var build = File.ReadAllText(Path.Combine(PackagingRoot, "build-development-package.ps1"));
        var project = File.ReadAllText(Path.Combine(
            DesktopRoot,
            "src",
            "Nyx.Desktop.App",
            "Nyx.Desktop.App.csproj"));

        Assert.Contains("verify_release.py", build, StringComparison.Ordinal);
        Assert.Contains("Get-FileHash -LiteralPath $builtHelper -Algorithm SHA256", build, StringComparison.Ordinal);
        Assert.Contains("-p:AchievementHelperSource=$builtHelper", build, StringComparison.Ordinal);
        Assert.Contains("-p:AchievementHelperSha256=$helperSha256", build, StringComparison.Ordinal);
        Assert.True(
            build.IndexOf("verify_release.py", StringComparison.Ordinal) <
            build.IndexOf("Get-FileHash -LiteralPath $builtHelper", StringComparison.Ordinal));
        Assert.Contains("PengoAchievementHelperSha256", project, StringComparison.Ordinal);
        Assert.Contains("Assets\\Tools\\pengo-achievements-launcher.exe", project, StringComparison.Ordinal);
        Assert.Contains("$env:CARGO_ENCODED_RUSTFLAGS", build, StringComparison.Ordinal);
        Assert.Contains("$cargoHome=C:\\_toolchain\\cargo", build, StringComparison.Ordinal);
        Assert.Contains("$userProfile=C:\\_home", build, StringComparison.Ordinal);
        Assert.Contains("$workRoot=C:\\_build\\package", build, StringComparison.Ordinal);
        Assert.Contains("Assert-NoPrivateBuildStrings -Root $publishRoot", build, StringComparison.Ordinal);
        Assert.Contains("'.cargo'", build, StringComparison.Ordinal);
    }

    [Fact]
    public void Private_binary_scan_ignores_common_usernames_but_rejects_exact_private_needles()
    {
        var build = File.ReadAllText(Path.Combine(PackagingRoot, "build-development-package.ps1"));
        const string functionName = "function Assert-NoPrivateBuildStrings";
        var functionStart = build.IndexOf(functionName, StringComparison.Ordinal);
        var functionEnd = build.IndexOf("function Remove-GeneratedDirectory", functionStart, StringComparison.Ordinal);
        Assert.True(functionStart >= 0 && functionEnd > functionStart);

        var temporaryRoot = Path.Combine(Path.GetTempPath(), "Nyx.Privacy.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryRoot);
        try
        {
            var scanner = build[functionStart..functionEnd];
            var testScript = Path.Combine(temporaryRoot, "test-private-binary-scan.ps1");
            var escapedRoot = temporaryRoot.Replace("'", "''", StringComparison.Ordinal);
            File.WriteAllText(testScript, scanner + $$"""

                $root = '{{escapedRoot}}'
                $binary = Join-Path $root 'sample.exe'
                $needles = @(
                    'C:\Users\private-builder',
                    'C:\Users\private-builder\.cargo',
                    '.cargo',
                    'C:\Pengo\Nyx',
                    'C:\Pengo\Nyx\Desktop\packaging\.work\0123456789abcdef0123456789abcdef'
                )

                [IO.File]::WriteAllText($binary, 'user admin owner public', [Text.Encoding]::ASCII)
                Assert-NoPrivateBuildStrings -Root $root -Needles $needles

                foreach ($needle in $needles) {
                    [IO.File]::WriteAllText($binary, "prefix $needle suffix", [Text.Encoding]::ASCII)
                    try {
                        Assert-NoPrivateBuildStrings -Root $root -Needles $needles
                        throw "Expected rejection for $needle"
                    }
                    catch {
                        if ($_.Exception.Message -notlike 'A packaged binary contains private build-path text:*') {
                            throw
                        }
                    }
                }
                """);

            var result = RunPowerShellFile(testScript);
            Assert.Equal(0, result.ExitCode);
        }
        finally
        {
            Directory.Delete(temporaryRoot, recursive: true);
        }
    }

    [Fact]
    public void Development_package_builds_and_seals_the_pinned_genshin_120_helper()
    {
        var build = File.ReadAllText(Path.Combine(PackagingRoot, "build-development-package.ps1"));
        var project = File.ReadAllText(Path.Combine(
            DesktopRoot,
            "src",
            "Nyx.Desktop.App",
            "Nyx.Desktop.App.csproj"));
        var solution = File.ReadAllText(Path.Combine(DesktopRoot, "Nyx.Desktop.slnx"));

        Assert.Contains("https://github.com/34736384/genshin-fps-unlock.git", build, StringComparison.Ordinal);
        Assert.Contains("v3.5.0", build, StringComparison.Ordinal);
        Assert.Contains("2b85d61dd06f6e11ad86fdd6bd90339f9abc58eb", build, StringComparison.Ordinal);
        Assert.Contains("$genshin120VerificationRoot = Join-Path $workRoot", build, StringComparison.Ordinal);
        Assert.Contains("git clone --quiet --depth 1 --branch", build, StringComparison.Ordinal);
        Assert.Contains("verify-release.ps1", build, StringComparison.Ordinal);
        Assert.Contains("Get-FileHash -LiteralPath $genshin120Helper -Algorithm SHA256", build, StringComparison.Ordinal);
        Assert.True(
            build.IndexOf("verify-release.ps1", StringComparison.Ordinal) <
            build.IndexOf("Get-FileHash -LiteralPath $genshin120Helper", StringComparison.Ordinal));
        Assert.Contains("-p:Genshin120HelperSource=$genshin120Helper", build, StringComparison.Ordinal);
        Assert.Contains("-p:Genshin120HelperSha256=$genshin120HelperSha256", build, StringComparison.Ordinal);
        Assert.Contains("-p:Genshin120LicenseSource=$genshin120License", build, StringComparison.Ordinal);
        Assert.Contains("-p:Genshin120ProvenanceSource=$genshin120Provenance", build, StringComparison.Ordinal);
        Assert.Contains("PengoGenshin120HelperSha256", project, StringComparison.Ordinal);
        Assert.Contains("Assets\\Tools\\Nyx.Genshin120.Helper.exe", project, StringComparison.Ordinal);
        Assert.Contains("Assets\\ThirdParty\\genshin-fps-unlock\\LICENSE.txt", project, StringComparison.Ordinal);
        Assert.Contains("Assets\\ThirdParty\\genshin-fps-unlock\\PROVENANCE.md", project, StringComparison.Ordinal);
        Assert.Contains("GetFileHash Files=\"$(Genshin120HelperSource)\"", project, StringComparison.Ordinal);
        Assert.DoesNotContain("Nyx.Genshin120.Stub.dll</Link>", project, StringComparison.Ordinal);
        Assert.DoesNotContain("Nyx.Genshin120.NativeHelper", solution, StringComparison.Ordinal);
        Assert.Contains("verify-genshin-provenance.ps1", build, StringComparison.Ordinal);
    }

    [Fact]
    public void Genshin_provenance_verifier_rejects_a_changed_source_hash()
    {
        var temporaryRoot = Path.Combine(Path.GetTempPath(), "Nyx.Provenance.Tests", Guid.NewGuid().ToString("N"));
        var upstream = Path.Combine(temporaryRoot, "upstream");
        var sourceRoot = Path.Combine(upstream, "UnlockerStub");
        Directory.CreateDirectory(sourceRoot);
        try
        {
            var sources = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["UnlockerStub/dllmain.cpp"] = "stub",
                ["UnlockerStub/Utils.cpp"] = "utils",
                ["UnlockerStub/Utils.h"] = "header",
            };
            foreach (var source in sources)
            {
                File.WriteAllText(Path.Combine(upstream, source.Key.Replace('/', Path.DirectorySeparatorChar)), source.Value);
            }
            var lines = sources.Select(source =>
            {
                var path = Path.Combine(upstream, source.Key.Replace('/', Path.DirectorySeparatorChar));
                var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
                return $"- `{source.Key}`: `{hash}`";
            }).ToArray();
            var provenance = Path.Combine(temporaryRoot, "PROVENANCE.md");
            File.WriteAllText(
                provenance,
                "# Provenance\n\nReviewed upstream source hashes (SHA-256):\n\n" +
                string.Join("\n", lines) + "\n\nEnd.\n");
            var verifier = Path.Combine(PackagingRoot, "verify-genshin-provenance.ps1");

            Assert.Equal(0, RunPowerShellFile(verifier, "-UpstreamRoot", upstream, "-ProvenancePath", provenance).ExitCode);
            var validHash = lines[0].Split('`')[3];
            File.WriteAllText(provenance, File.ReadAllText(provenance).Replace(validHash, new string('0', 64), StringComparison.Ordinal));
            var rejected = RunPowerShellFile(verifier, "-UpstreamRoot", upstream, "-ProvenancePath", provenance);
            Assert.NotEqual(0, rejected.ExitCode);
            Assert.Contains("source hash does not match", rejected.Output, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(temporaryRoot, recursive: true);
        }
    }

    [Fact]
    public void App_package_target_rejects_a_mismatched_genshin_120_helper_hash()
    {
        var temporaryRoot = Path.Combine(Path.GetTempPath(), "Nyx.Packaging.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryRoot);
        try
        {
            var helper = Path.Combine(temporaryRoot, "Nyx.Genshin120.Helper.exe");
            var license = Path.Combine(temporaryRoot, "LICENSE.txt");
            var provenance = Path.Combine(temporaryRoot, "PROVENANCE.md");
            File.WriteAllBytes(helper, [1, 2, 3, 4]);
            File.WriteAllText(license, "MIT");
            File.WriteAllText(provenance, "pinned");
            var correctHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(helper))).ToLowerInvariant();

            Assert.Equal(0, RunGenshinPackageValidation(helper, correctHash, license, provenance).ExitCode);
            var rejected = RunGenshinPackageValidation(helper, new string('0', 64), license, provenance);
            Assert.NotEqual(0, rejected.ExitCode);
            Assert.Contains("does not match Genshin120HelperSource", rejected.Output, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(temporaryRoot, recursive: true);
        }
    }

    [Fact]
    public void Development_package_restores_by_default_with_an_explicit_no_restore_opt_out()
    {
        var build = File.ReadAllText(Path.Combine(PackagingRoot, "build-development-package.ps1"));
        var readme = File.ReadAllText(Path.Combine(PackagingRoot, "README.md"));
        var updateDoc = File.ReadAllText(Path.Combine(
            DesktopRoot,
            "..",
            "docs",
            "desktop-packaging-update-2026-07-17.md"));

        Assert.Contains("[switch] $NoRestore", build, StringComparison.Ordinal);
        Assert.Contains("$restoreArgument = if ($NoRestore) { @('--no-restore') } else { @() }", build, StringComparison.Ordinal);
        Assert.DoesNotContain("[switch] $Restore", build, StringComparison.Ordinal);
        Assert.Contains("build-development-package.ps1 -Version 1.0.0.0", readme, StringComparison.Ordinal);
        Assert.Contains("Use `-NoRestore` only", readme, StringComparison.Ordinal);
        Assert.Contains("`-NoRestore` is an explicit opt-out", updateDoc, StringComparison.Ordinal);
    }

    [Fact]
    public void Development_package_keeps_launcher_art_remote_and_excludes_optional_publish_diagnostics()
    {
        var build = File.ReadAllText(Path.Combine(PackagingRoot, "build-development-package.ps1"));
        var project = File.ReadAllText(Path.Combine(
            DesktopRoot,
            "src",
            "Nyx.Desktop.App",
            "Nyx.Desktop.App.csproj"));

        Assert.DoesNotContain("launcher-art", build, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("<ItemGroup Condition=\"'$(Configuration)' == 'Debug'\">", project, StringComparison.Ordinal);
        Assert.Contains("Site\\src\\data\\generated\\launcher-art\\**\\*", project, StringComparison.Ordinal);
        Assert.Contains("Name=\"ExcludeOptionalPublishDiagnostics\"", project, StringComparison.Ordinal);
        Assert.Contains("AfterTargets=\"ComputeFilesToPublish\"", project, StringComparison.Ordinal);
        Assert.Contains("ResolvedFileToPublish Remove=\"@(ResolvedFileToPublish)\"", project, StringComparison.Ordinal);
        Assert.Contains(
            @"(?i)^(createdump\.exe|mscordaccore(?:_.*)?\.dll|mscordbi\.dll|Microsoft\.DiaSymReader\.Native\.amd64\.dll)$",
            project,
            StringComparison.Ordinal);
        Assert.Contains(
            @"(?i)^(?!en-us\\)[a-z]{2,3}(?:-[a-z0-9]{2,8})*\\(?:Microsoft\.ui\.xaml\.dll\.mui|Microsoft\.UI\.Xaml\.Phone\.dll\.mui)$",
            project,
            StringComparison.Ordinal);
    }

    private static (int ExitCode, string Output) RunPowerShell(string command)
    {
        var start = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add("-NoLogo");
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-Command");
        start.ArgumentList.Add(command);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(30_000));
        return (process.ExitCode, output);
    }

    private static (int ExitCode, string Output) RunPowerShellFile(string script, params string[] arguments)
    {
        var start = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add("-NoLogo");
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-File");
        start.ArgumentList.Add(script);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(30_000));
        return (process.ExitCode, output);
    }

    private static (int ExitCode, string Output) RunGenshinPackageValidation(
        string helper,
        string hash,
        string license,
        string provenance)
    {
        var project = Path.Combine(DesktopRoot, "src", "Nyx.Desktop.App", "Nyx.Desktop.App.csproj");
        var start = new ProcessStartInfo
        {
            FileName = "dotnet.exe",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var argument in new[]
        {
            "msbuild",
            project,
            "-nologo",
            "-t:ValidateGenshin120HelperPackageInput",
            $"-p:Genshin120HelperSource={helper}",
            $"-p:Genshin120HelperSha256={hash}",
            $"-p:Genshin120LicenseSource={license}",
            $"-p:Genshin120ProvenanceSource={provenance}",
        })
        {
            start.ArgumentList.Add(argument);
        }
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(30_000));
        return (process.ExitCode, output);
    }

    private static string FindDesktopRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "Nyx.Desktop.slnx")))
            {
                return current.FullName;
            }
            current = current.Parent;
        }
        throw new DirectoryNotFoundException();
    }
}
