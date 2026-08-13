using System.Diagnostics;
using System.Text;

namespace Nyx.Desktop.Tests.Packaging;

public sealed class DesktopStartReadinessTests
{
    private static readonly string DesktopRoot = FindDesktopRoot();
    private static readonly string StartScript = Path.Combine(DesktopRoot, "scripts", "start-nyx.ps1");
    private static readonly string GateScript = Path.Combine(DesktopRoot, "scripts", "test-package-readiness.ps1");

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
        Assert.Contains("if ($isAdministrator)", script);
        Assert.True(
            script.IndexOf("if ($isAdministrator)", StringComparison.Ordinal) <
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
        Assert.DoesNotContain("https://", script, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Real_check_only_preflight_succeeds_without_start_or_restore()
    {
        var result = RunPowerShell(StartScript, "-CheckOnly");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Nyx developer start is ready", result.Output);
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
        fixture.WriteMinimumProject("10.0.100", "Microsoft.WindowsAppSDK.WinUI", "2.2.1");
        fixture.WriteRunAssets("Microsoft.WindowsAppSDK.WinUI", "2.2.1");

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

    [Fact]
    public void Real_package_gate_fails_closed_with_sanitized_categories()
    {
        var result = RunPowerShell(GateScript);

        Assert.Equal(3, result.ExitCode);
        Assert.Contains("NYX_PACKAGE_CONFIGURATION=NOT_READY", result.Output);
        Assert.Contains("BLOCKER=PublisherPlaceholder", result.Output);
        Assert.Contains("BLOCKER=SigningIdentityMissing", result.Output);
        Assert.Contains("BLOCKER=InstallablePackageProfileMissing", result.Output);
        Assert.Contains("BLOCKER=DistributionChannelUnresolved", result.Output);
        Assert.DoesNotContain(DesktopRoot, result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Environment.UserName, result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Synthetic_complete_package_fixture_is_ready()
    {
        using var fixture = PackageFixture.Create();
        fixture.WriteProject(signing: true, channel: "website");
        fixture.WriteManifest("CN=PENGO Software");
        fixture.WriteProfile("MSIX.pubxml", generatePackage: true, protocol: "FileSystem");

        var result = RunPowerShell(GateScript, "-DesktopRoot", fixture.Root);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("NYX_PACKAGE_CONFIGURATION=READY", result.Output.Trim());
    }

    [Theory]
    [InlineData(40)]
    [InlineData(64)]
    public void Package_gate_accepts_only_supported_thumbprint_lengths(int length)
    {
        using var fixture = PackageFixture.Create();
        fixture.WriteRawProject(
            $"<PackageCertificateThumbprint>{new string('A', length)}</PackageCertificateThumbprint>" +
            PackageFixture.ValidChannel);
        fixture.WriteManifest("CN=PENGO Test");
        fixture.WriteProfile("fixture.pubxml", generatePackage: true, protocol: "FileSystem");

        var result = RunPowerShell(GateScript, "-DesktopRoot", fixture.Root);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("NYX_PACKAGE_CONFIGURATION=READY", result.Output.Trim());
    }

    [Theory]
    [InlineData(39)]
    [InlineData(41)]
    [InlineData(63)]
    [InlineData(65)]
    public void Package_gate_rejects_unsupported_thumbprint_lengths(int length)
    {
        using var fixture = PackageFixture.Create();
        fixture.WriteRawProject(
            $"<PackageCertificateThumbprint>{new string('A', length)}</PackageCertificateThumbprint>" +
            PackageFixture.ValidChannel);
        fixture.WriteManifest("CN=PENGO Test");
        fixture.WriteProfile("fixture.pubxml", generatePackage: true, protocol: "FileSystem");

        var result = RunPowerShell(GateScript, "-DesktopRoot", fixture.Root);

        Assert.Equal(3, result.ExitCode);
        Assert.Contains("BLOCKER=SigningIdentityInvalid", result.Output);
        Assert.DoesNotContain(fixture.Root, result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("relative")]
    [InlineData("unc")]
    [InlineData("device")]
    [InlineData("missing")]
    [InlineData("malformed")]
    [InlineData("oversized")]
    public void Package_gate_rejects_unsafe_roots_without_path_disclosure(string caseName)
    {
        var root = caseName switch
        {
            "relative" => "relative\\fixture",
            "unc" => "\\\\server.invalid\\private",
            "device" => "\\\\?\\C:\\private",
            "missing" => Path.Combine(Path.GetTempPath(), "NyxMissingRoot", Guid.NewGuid().ToString("N")),
            "malformed" => "C:\\bad|root",
            "oversized" => "C:\\" + new string('a', 600),
            _ => throw new ArgumentOutOfRangeException(nameof(caseName)),
        };

        var result = RunPowerShell(GateScript, "-DesktopRoot", root);

        AssertRootInvalid(result, root);
    }

    [Fact]
    public void Package_gate_rejects_reparse_root_without_reading_target()
    {
        using var fixture = PackageFixture.Create();
        fixture.WriteReadyInputs();
        var link = fixture.Root + "-link";
        Directory.CreateSymbolicLink(link, fixture.Root);
        try
        {
            var result = RunPowerShell(GateScript, "-DesktopRoot", link);
            AssertRootInvalid(result, link);
        }
        finally
        {
            Directory.Delete(link);
        }
    }

    [Fact]
    public void Package_gate_stops_at_reparse_parent_before_touching_child_root()
    {
        using var fixture = PackageFixture.Create();
        fixture.WriteReadyInputs();
        var linkContainer = Path.Combine(Path.GetTempPath(), "NyxReparseParent", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(linkContainer);
        var parentLink = Path.Combine(linkContainer, "linked-parent");
        var fixtureParent = Directory.GetParent(fixture.Root)!.FullName;
        Directory.CreateSymbolicLink(parentLink, fixtureParent);
        var rootThroughLink = Path.Combine(parentLink, Path.GetFileName(fixture.Root));
        try
        {
            var result = RunPowerShell(GateScript, "-DesktopRoot", rootThroughLink);
            AssertRootInvalid(result, rootThroughLink);
        }
        finally
        {
            Directory.Delete(parentLink);
            Directory.Delete(linkContainer);
        }
    }

    [Fact]
    public void Package_gate_stops_at_reparse_app_root_before_touching_target()
    {
        using var fixture = PackageFixture.Create();
        fixture.WriteReadyInputs();
        fixture.ReplaceAppRootWithSymbolicLink();

        var result = RunPowerShell(GateScript, "-DesktopRoot", fixture.Root);

        AssertRootInvalid(result, fixture.Root);
    }

    [Fact]
    public void Package_gate_rejects_reparse_project_before_xml_read()
    {
        using var fixture = PackageFixture.Create();
        fixture.WriteReadyInputs();
        fixture.ReplaceProjectWithSymbolicLink();

        var result = RunPowerShell(GateScript, "-DesktopRoot", fixture.Root);

        AssertRootInvalid(result, fixture.Root);
    }

    [Theory]
    [InlineData("duplicate-channel")]
    [InlineData("conditional-channel")]
    [InlineData("duplicate-signing")]
    [InlineData("conditional-signing")]
    [InlineData("missing-key-file")]
    [InlineData("empty-key-file")]
    [InlineData("oversized-key-file")]
    [InlineData("package-type-none")]
    public void Package_gate_rejects_ambiguous_or_unsafe_project_properties(string caseName)
    {
        using var fixture = PackageFixture.Create();
        fixture.WriteManifest("CN=PENGO Test");
        fixture.WriteProfile("fixture.pubxml", generatePackage: true, protocol: "FileSystem");
        if (caseName == "empty-key-file")
        {
            fixture.WriteSigningKey("empty.pfx", 0);
        }
        else if (caseName == "oversized-key-file")
        {
            fixture.WriteSigningKey("oversized.pfx", 1_048_577);
        }

        fixture.WriteRawProject(caseName switch
        {
            "duplicate-channel" => PackageFixture.ValidSigning +
                "<NyxDistributionChannel>store</NyxDistributionChannel><NyxDistributionChannel>website</NyxDistributionChannel>",
            "conditional-channel" => PackageFixture.ValidSigning +
                "<NyxDistributionChannel Condition=\"'$(Configuration)'=='Release'\">store</NyxDistributionChannel>",
            "duplicate-signing" => PackageFixture.ValidSigning +
                "<PackageCertificateThumbprint>1111111111111111111111111111111111111111</PackageCertificateThumbprint>" +
                PackageFixture.ValidChannel,
            "conditional-signing" =>
                "<PackageCertificateThumbprint Condition=\"'$(Configuration)'=='Release'\">0123456789ABCDEF0123456789ABCDEF01234567</PackageCertificateThumbprint>" +
                PackageFixture.ValidChannel,
            "missing-key-file" => "<PackageCertificateKeyFile>missing.pfx</PackageCertificateKeyFile>" + PackageFixture.ValidChannel,
            "empty-key-file" => "<PackageCertificateKeyFile>empty.pfx</PackageCertificateKeyFile>" + PackageFixture.ValidChannel,
            "oversized-key-file" => "<PackageCertificateKeyFile>oversized.pfx</PackageCertificateKeyFile>" + PackageFixture.ValidChannel,
            "package-type-none" => PackageFixture.ValidSigning + PackageFixture.ValidChannel +
                "<WindowsPackageType>None</WindowsPackageType>",
            _ => throw new ArgumentOutOfRangeException(nameof(caseName)),
        });

        var result = RunPowerShell(GateScript, "-DesktopRoot", fixture.Root);

        Assert.Equal(3, result.ExitCode);
        Assert.Contains("NYX_PACKAGE_CONFIGURATION=NOT_READY", result.Output);
        Assert.DoesNotContain(fixture.Root, result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("true-then-false")]
    [InlineData("conditional-generate")]
    [InlineData("duplicate-signing")]
    [InlineData("conditional-signing")]
    [InlineData("package-type-none")]
    [InlineData("wrong-runtime")]
    public void Package_gate_rejects_ambiguous_or_non_msix_profiles(string caseName)
    {
        using var fixture = PackageFixture.Create();
        fixture.WriteProject(signing: true, channel: "private-sideload");
        fixture.WriteManifest("CN=PENGO Test");
        fixture.WriteRawProfile("fixture.pubxml", caseName switch
        {
            "true-then-false" => PackageFixture.ValidX64ProfileProperties +
                "<GenerateAppxPackageOnBuild>false</GenerateAppxPackageOnBuild>",
            "conditional-generate" => PackageFixture.ValidX64BaseProperties +
                "<GenerateAppxPackageOnBuild Condition=\"'$(Configuration)'=='Release'\">true</GenerateAppxPackageOnBuild>" +
                PackageFixture.ValidProfileSigning,
            "duplicate-signing" => PackageFixture.ValidX64ProfileProperties +
                "<AppxPackageSigningEnabled>false</AppxPackageSigningEnabled>",
            "conditional-signing" => PackageFixture.ValidX64BaseProperties +
                "<GenerateAppxPackageOnBuild>true</GenerateAppxPackageOnBuild>" +
                "<AppxPackageSigningEnabled Condition=\"'$(Configuration)'=='Release'\">true</AppxPackageSigningEnabled>",
            "package-type-none" => PackageFixture.ValidX64ProfileProperties +
                "<WindowsPackageType>None</WindowsPackageType>",
            "wrong-runtime" => PackageFixture.ValidX64ProfileProperties.Replace("win-x64", "win-x86", StringComparison.Ordinal),
            _ => throw new ArgumentOutOfRangeException(nameof(caseName)),
        });

        var result = RunPowerShell(GateScript, "-DesktopRoot", fixture.Root);

        Assert.Equal(3, result.ExitCode);
        Assert.Contains("BLOCKER=InstallablePackageProfileMissing", result.Output);
        Assert.DoesNotContain(fixture.Root, result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("placeholder")]
    [InlineData("signing")]
    [InlineData("profile")]
    [InlineData("channel")]
    [InlineData("malformed-manifest")]
    [InlineData("missing-project")]
    public void Package_gate_rejects_each_incomplete_fixture(string caseName)
    {
        using var fixture = PackageFixture.Create();
        if (caseName != "missing-project")
        {
            fixture.WriteProject(signing: caseName != "signing", channel: caseName == "channel" ? null : "private-sideload");
        }

        if (caseName == "malformed-manifest")
        {
            fixture.WriteRawManifest("<Package><Identity");
        }
        else
        {
            fixture.WriteManifest(caseName == "placeholder" ? "CN=AppPublisher" : "CN=PENGO Test");
        }

        fixture.WriteProfile(
            "fixture.pubxml",
            generatePackage: caseName != "profile",
            protocol: caseName == "profile" ? "WebDeploy" : "FileSystem");

        var result = RunPowerShell(GateScript, "-DesktopRoot", fixture.Root);

        Assert.Equal(3, result.ExitCode);
        Assert.Contains("NYX_PACKAGE_CONFIGURATION=NOT_READY", result.Output);
        Assert.DoesNotContain(fixture.Root, result.Output, StringComparison.OrdinalIgnoreCase);
        if (caseName == "profile")
        {
            Assert.Contains("BLOCKER=InstallablePackageProfileMissing", result.Output);
        }
    }

    [Fact]
    public void Package_gate_rejects_invalid_publisher_and_missing_profiles()
    {
        using var fixture = PackageFixture.Create();
        fixture.WriteProject(signing: true, channel: "store");
        fixture.WriteManifest("not-a-distinguished-name");

        var result = RunPowerShell(GateScript, "-DesktopRoot", fixture.Root);

        Assert.Equal(3, result.ExitCode);
        Assert.Contains("BLOCKER=PublisherInvalid", result.Output);
        Assert.Contains("BLOCKER=PublishProfileMissing", result.Output);
        Assert.Contains("BLOCKER=InstallablePackageProfileMissing", result.Output);
    }

    private static void AssertRootInvalid(CommandResult result, string untrustedPath)
    {
        Assert.Equal(3, result.ExitCode);
        var lines = result.Output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(["NYX_PACKAGE_CONFIGURATION=NOT_READY", "BLOCKER=RootInvalid"], lines);
        Assert.DoesNotContain(untrustedPath, result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Environment.UserName, result.Output, StringComparison.OrdinalIgnoreCase);
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
            var scripts = Path.Combine(root, "scripts");
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
            var appRoot = Path.Combine(Root, "src", "Nyx.Desktop.App");
            Directory.CreateDirectory(appRoot);
            File.WriteAllText(Path.Combine(appRoot, "Nyx.Desktop.App.csproj"),
                $"<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0-windows10.0.22621.0</TargetFramework><OutputType>WinExe</OutputType><WindowsPackageType>None</WindowsPackageType><WindowsAppSDKSelfContained>true</WindowsAppSDKSelfContained><PublishTrimmed>false</PublishTrimmed></PropertyGroup><ItemGroup><PackageReference Include=\"{packageName}\" Version=\"{packageVersion}\" /></ItemGroup></Project>");
            File.WriteAllText(Path.Combine(appRoot, "Package.appxmanifest"), "<Package />");
        }

        public void WriteRunAssets(
            string packageName = "Microsoft.WindowsAppSDK",
            string packageVersion = "2.2.0")
        {
            var objectRoot = Path.Combine(Root, "src", "Nyx.Desktop.App", "obj");
            Directory.CreateDirectory(objectRoot);
            File.WriteAllText(
                Path.Combine(objectRoot, "project.assets.json"),
                $"{{\"libraries\":{{\"{packageName}/{packageVersion}\":{{}}}}}}");
        }

        public void WriteOversizedProject(string sdk)
        {
            WriteMinimumProject(sdk);
            var project = Path.Combine(Root, "src", "Nyx.Desktop.App", "Nyx.Desktop.App.csproj");
            File.WriteAllText(project, "<Project><!--" + new string('x', 1_048_576) + "--></Project>");
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }

    private sealed class PackageFixture : IDisposable
    {
        public const string ValidSigning =
            "<PackageCertificateThumbprint>0123456789ABCDEF0123456789ABCDEF01234567</PackageCertificateThumbprint>";
        public const string ValidChannel = "<NyxDistributionChannel>private-sideload</NyxDistributionChannel>";
        public const string ValidX64BaseProperties =
            "<Platform>x64</Platform><RuntimeIdentifier>win-x64</RuntimeIdentifier>";
        public const string ValidProfileSigning = "<AppxPackageSigningEnabled>true</AppxPackageSigningEnabled>";
        public const string ValidX64ProfileProperties = ValidX64BaseProperties +
            "<GenerateAppxPackageOnBuild>true</GenerateAppxPackageOnBuild>" + ValidProfileSigning;

        private PackageFixture(string root)
        {
            Root = root;
            AppRoot = Path.Combine(root, "src", "Nyx.Desktop.App");
            Directory.CreateDirectory(AppRoot);
        }

        public string Root { get; }
        private string AppRoot { get; }

        public static PackageFixture Create() =>
            new(Path.Combine(Path.GetTempPath(), "NyxPackageGate", Guid.NewGuid().ToString("N")));

        public void WriteProject(bool signing, string? channel)
        {
            var properties = new StringBuilder();
            if (signing)
            {
                properties.Append("<PackageCertificateThumbprint>0123456789ABCDEF0123456789ABCDEF01234567</PackageCertificateThumbprint>");
            }
            if (channel is not null)
            {
                properties.Append($"<NyxDistributionChannel>{channel}</NyxDistributionChannel>");
            }
            File.WriteAllText(Path.Combine(AppRoot, "Nyx.Desktop.App.csproj"), $"<Project><PropertyGroup>{properties}</PropertyGroup></Project>");
        }

        public void WriteRawProject(string properties) =>
            File.WriteAllText(Path.Combine(AppRoot, "Nyx.Desktop.App.csproj"), $"<Project><PropertyGroup>{properties}</PropertyGroup></Project>");

        public void WriteManifest(string publisher) =>
            WriteRawManifest($"<Package xmlns=\"http://schemas.microsoft.com/appx/manifest/foundation/windows10\"><Identity Name=\"Test\" Publisher=\"{publisher}\" Version=\"1.0.0.0\" /></Package>");

        public void WriteRawManifest(string xml) =>
            File.WriteAllText(Path.Combine(AppRoot, "Package.appxmanifest"), xml);

        public void WriteProfile(string name, bool generatePackage, string protocol)
        {
            var properties = $"<PublishProtocol>{protocol}</PublishProtocol>" + ValidX64BaseProperties +
                $"<GenerateAppxPackageOnBuild>{generatePackage.ToString().ToLowerInvariant()}</GenerateAppxPackageOnBuild>" +
                ValidProfileSigning;
            WriteRawProfile(name, properties);
        }

        public void WriteRawProfile(string name, string properties)
        {
            var profiles = Path.Combine(AppRoot, "Properties", "PublishProfiles");
            Directory.CreateDirectory(profiles);
            File.WriteAllText(Path.Combine(profiles, name), $"<Project><PropertyGroup>{properties}</PropertyGroup></Project>");
        }

        public void WriteReadyInputs()
        {
            WriteProject(signing: true, channel: "website");
            WriteManifest("CN=PENGO Test");
            WriteProfile("fixture.pubxml", generatePackage: true, protocol: "FileSystem");
        }

        public void ReplaceProjectWithSymbolicLink()
        {
            var project = Path.Combine(AppRoot, "Nyx.Desktop.App.csproj");
            var target = Path.Combine(AppRoot, "project-target.xml");
            File.Move(project, target);
            File.CreateSymbolicLink(project, target);
        }

        public void ReplaceAppRootWithSymbolicLink()
        {
            var target = Path.Combine(Root, "linked-app-target");
            Directory.Move(AppRoot, target);
            Directory.CreateDirectory(Path.Combine(Root, "src"));
            Directory.CreateSymbolicLink(AppRoot, target);
        }

        public void WriteSigningKey(string name, int size)
        {
            using var stream = new FileStream(Path.Combine(AppRoot, name), FileMode.CreateNew, FileAccess.Write, FileShare.None);
            stream.SetLength(size);
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
