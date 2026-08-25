using Nyx.Desktop.Update;

return UpdaterProgram.Run(args);

internal static class UpdaterProgram
{
    public static int Run(string[] args)
    {
        try
        {
#if NYX_UPDATER_DISPOSABLE_SMOKE
            var layout = DisposableSmokeLayout();
#else
            var layout = UpdateLayout.ForCurrentUser();
#endif
            if (args is ["launch"])
            {
                StableUpdateRunner.Launch(layout);
                return 0;
            }

            if (args is ["confirm-current", "--caller-pid", var callerProcessIdText]
                && int.TryParse(
                    callerProcessIdText,
                    System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var callerProcessId))
            {
                _ = StableUpdateRunner.ConfirmCurrent(layout, callerProcessId);
                return 0;
            }

            UpdateTransaction.Reconcile(layout);
            if (args is [
                "handoff",
                "--manifest", var handoffManifestPath,
                "--package", var handoffPackagePath,
                "--parent-pid", var parentProcessIdText]
                && int.TryParse(
                    parentProcessIdText,
                    System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var parentProcessId))
            {
                StableUpdateRunner.Handoff(
                    layout,
                    handoffManifestPath,
                    handoffPackagePath,
                    parentProcessId,
                    Console.In,
                    Console.Out);
                return 0;
            }

            if (args is ["install", "--bundle", var bundle])
            {
                var manifest = NyxInstaller.Install(layout, bundle);
                Console.WriteLine($"NYX_UPDATE=INSTALLED VERSION={manifest.Version}");
                return 0;
            }

            if (args is ["stage", "--manifest", var manifestPath, "--package", var packagePath])
            {
                var manifest = UpdateManifestFile.Read(manifestPath);
                Directory.CreateDirectory(layout.StagingRoot);
                _ = UpdatePackageStager.Stage(manifest, packagePath, layout.StagingRoot);
                Console.WriteLine($"NYX_UPDATE=STAGED VERSION={manifest.Version}");
                return 0;
            }

            if (args is ["verify", "--manifest", var verifyManifestPath, "--package", var verifyPackagePath])
            {
                var manifest = UpdateManifestFile.Read(verifyManifestPath);
                UpdatePackageStager.VerifyDownload(manifest, verifyPackagePath);
                Console.WriteLine($"NYX_UPDATE=VERIFIED VERSION={manifest.Version}");
                return 0;
            }

            if (args is ["apply", "--manifest", var applyManifestPath])
            {
                var manifest = UpdateManifestFile.Read(applyManifestPath);
                var staged = Path.Combine(layout.StagingRoot, $"ready-{manifest.Version}");
                UpdateTransaction.Apply(layout, manifest, staged);
                Console.WriteLine($"NYX_UPDATE=PENDING VERSION={manifest.Version}");
                return 0;
            }

            if (args is ["confirm", "--manifest", var confirmManifestPath])
            {
                var manifest = UpdateManifestFile.Read(confirmManifestPath);
                UpdateTransaction.Confirm(layout, manifest);
                Console.WriteLine($"NYX_UPDATE=CONFIRMED VERSION={manifest.Version}");
                return 0;
            }

            if (args is ["rollback"])
            {
                var rolledBack = UpdateTransaction.Rollback(layout);
                Console.WriteLine(rolledBack ? "NYX_UPDATE=ROLLED_BACK" : "NYX_UPDATE=NO_PENDING_UPDATE");
                return 0;
            }

            if (args is ["uninstall"] or ["uninstall", "--remove-user-data"])
            {
                var removeData = args.Length == 2;
                NyxUninstaller.Uninstall(layout, removeData);
                Console.WriteLine(removeData ? "NYX_UNINSTALL=REMOVED_WITH_USER_DATA" : "NYX_UNINSTALL=REMOVED_DATA_RETAINED");
                return 0;
            }

            Console.Error.WriteLine("NYX_UPDATE=REJECTED CODE=INVALID_ARGUMENTS");
            return 2;
        }
        catch (UpdateContractException exception)
        {
            Console.Error.WriteLine($"NYX_UPDATE=REJECTED CODE={exception.Code}");
            return 3;
        }
        catch (UnauthorizedAccessException)
        {
            Console.Error.WriteLine("NYX_UPDATE=FAILED CODE=ACCESS_DENIED");
            return 4;
        }
        catch (IOException)
        {
            Console.Error.WriteLine("NYX_UPDATE=FAILED CODE=IO_FAILURE");
            return 4;
        }
        catch (System.Text.Json.JsonException)
        {
            Console.Error.WriteLine("NYX_UPDATE=REJECTED CODE=JSON_INVALID");
            return 3;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            Console.Error.WriteLine("NYX_UPDATE=FAILED CODE=PROCESS_FAILURE");
            return 4;
        }
    }

#if NYX_UPDATER_DISPOSABLE_SMOKE
    private const string DisposableRootVariable = "NYX_UPDATER_DISPOSABLE_ROOT";
    private const string DisposableMarker = ".nyx-updater-disposable-smoke";
    private const string DisposableMarkerContents = "NYX_UPDATER_DISPOSABLE_SMOKE_V1";
    private const string DisposableRootPrefix = "nyx-updater-smoke-";

    private static UpdateLayout DisposableSmokeLayout()
    {
        var root = SafePaths.RequireExistingDirectory(
            Environment.GetEnvironmentVariable(DisposableRootVariable)
            ?? throw new UpdateContractException("DisposableRootMissing"));
        var name = Path.GetFileName(root);
        if (name.Length != DisposableRootPrefix.Length + 32
            || !name.StartsWith(DisposableRootPrefix, StringComparison.Ordinal)
            || !name[DisposableRootPrefix.Length..].All(character =>
                character is >= '0' and <= '9' or >= 'a' and <= 'f'))
        {
            throw new UpdateContractException("DisposableRootInvalid");
        }

        var marker = SafePaths.RequireExistingFile(SafePaths.CombineUnder(root, DisposableMarker));
        if (!string.Equals(File.ReadAllText(marker), DisposableMarkerContents, StringComparison.Ordinal))
        {
            throw new UpdateContractException("DisposableRootInvalid");
        }

        var layout = UpdateLayout.ForUserRoots(
            SafePaths.CombineUnder(root, "local"),
            SafePaths.CombineUnder(root, "roaming"));
        foreach (var path in new[]
                 {
                     layout.InstallRoot,
                     layout.UserDataRoot,
                     layout.LegacyUserDataRoot,
                     layout.StartMenuShortcut,
                 })
        {
            var relative = Path.GetRelativePath(root, Path.GetFullPath(path));
            if (Path.IsPathRooted(relative)
                || relative is ".."
                || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                throw new UpdateContractException("DisposableRootInvalid");
            }
        }

        return layout;
    }
#endif
}
