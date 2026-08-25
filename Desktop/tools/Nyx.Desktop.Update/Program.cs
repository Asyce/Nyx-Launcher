using Nyx.Desktop.Update;

return UpdaterProgram.Run(args);

internal static class UpdaterProgram
{
    public static int Run(string[] args)
    {
        try
        {
            var layout = UpdateLayout.ForCurrentUser();
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
}
