using System.Text.RegularExpressions;

namespace Nyx.Desktop.Tests.Packaging;

public sealed class NativeSmokeScriptTests
{
    private static readonly string Script = Path.Combine(
        FindDesktopRoot(), "scripts", "test-native-smoke.ps1");

    [Fact]
    public void Native_smoke_script_keeps_data_and_side_effect_boundaries_closed()
    {
        var source = File.ReadAllText(Script);

        Assert.Contains("Assert-NoNyxProcesses", source, StringComparison.Ordinal);
        Assert.Contains("BACKUP_ALREADY_EXISTS", source, StringComparison.Ordinal);
        Assert.Contains("BACKUP_VOLUME_INVALID", source, StringComparison.Ordinal);
        Assert.Contains("[IO.Directory]::Move($state.Original, $state.Backup)", source, StringComparison.Ordinal);
        Assert.Contains("finally {", source, StringComparison.Ordinal);
        Assert.Contains("Restore-DataRoots -States $dataStates", source, StringComparison.Ordinal);
        Assert.Contains("Remove-SafeTree -LiteralPath $state.Original", source, StringComparison.Ordinal);
        Assert.Contains("Expand-ManifestPayload", source, StringComparison.Ordinal);
        Assert.Contains("$expected.TryGetValue($relative, [ref] $file)", source, StringComparison.Ordinal);
        Assert.Contains("[long] $declaredTotal = 0", source, StringComparison.Ordinal);
        Assert.Contains("$size -gt (6GB - $declaredTotal)", source, StringComparison.Ordinal);
        Assert.Contains("retryStateCheckedForGames++", source, StringComparison.Ordinal);
        Assert.Contains("retryStates += [ordered]@{ game = $gameName; state = $retryState }", source, StringComparison.Ordinal);
        Assert.Contains("SCREENSHOT_BLACK_OR_FLAT", source, StringComparison.Ordinal);
        Assert.Contains("[NyxNativeSmokeCapture]::PrintWindow", source, StringComparison.Ordinal);
        Assert.Contains("$height = [Math]::Min([int] $rect.Height, 720)", source, StringComparison.Ordinal);
        Assert.Contains("$safeHeight = [Math]::Min($height, 120)", source, StringComparison.Ordinal);
        Assert.Contains("-Surface $gameName", source, StringComparison.Ordinal);
        Assert.Equal(3, Regex.Matches(source, @"Save-SanitizedScreenshot", RegexOptions.CultureInvariant).Count);
        Assert.DoesNotContain("CopyFromScreen", source, StringComparison.Ordinal);
        Assert.Single(Regex.Matches(source, @"\.Invoke\(\)", RegexOptions.CultureInvariant).Cast<Match>());

        foreach (var automationId in new[]
                 {
                     "LaunchButton",
                     "StableOpenUpdaterButton",
                     "StableOpenScreenshotFolderButton",
                 })
        {
            Assert.Contains(
                $"Assert-SideEffectControl -Root $window -AutomationId '{automationId}'",
                source,
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                $"Invoke-SafeElement -Element ${automationId}",
                source,
                StringComparison.OrdinalIgnoreCase);
        }

        var safeBlock = Regex.Match(
            source,
            @"function Invoke-SafeElement \{(?<body>[\s\S]*?)^\}",
            RegexOptions.Multiline | RegexOptions.CultureInvariant);
        Assert.True(safeBlock.Success);
        Assert.DoesNotContain("Launch", safeBlock.Groups["body"].Value, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Official", safeBlock.Groups["body"].Value, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Screenshot", safeBlock.Groups["body"].Value, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Export", safeBlock.Groups["body"].Value, StringComparison.OrdinalIgnoreCase);
        Assert.Matches(
            new Regex(@"function Assert-SideEffectControl \{[\s\S]*?AddSeconds\(20\)[\s\S]*?Find-AutomationIdElement -Root \$Root -AutomationId \$AutomationId[\s\S]*?Start-Sleep -Milliseconds 100[\s\S]*?SIDE_EFFECT_CONTROL_MISSING[\s\S]*?^\}", RegexOptions.Multiline | RegexOptions.CultureInvariant),
            source);
        Assert.Matches(
            new Regex(@"\$settings\.SetFocus\(\)\r?\n\s*Assert-FocusIs -Expected \$settings\r?\n\s*Send-SafeKey -Key Tab", RegexOptions.CultureInvariant),
            source);
        Assert.Matches(
            new Regex(@"\$cancel\.SetFocus\(\)[\s\S]{0,120}\r?\n\s*Send-SafeKey -Key Enter", RegexOptions.CultureInvariant),
            source);
        Assert.Matches(
            new Regex(@"\$cancel\.SetFocus\(\)[\s\S]{0,120}\r?\n\s*Send-SafeKey -Key Escape", RegexOptions.CultureInvariant),
            source);
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
}
