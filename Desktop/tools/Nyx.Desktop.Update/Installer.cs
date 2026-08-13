namespace Nyx.Desktop.Update;

public static class NyxInstaller
{
    private static readonly string[] ControlFiles =
    [
        "Nyx.Desktop.Update.exe",
        "Uninstall-Nyx.ps1",
        "release.json",
        "release-notes.md",
        "first-run-defaults.json",
    ];

    public static UpdateReleaseManifest Install(UpdateLayout layout, string bundleRoot)
    {
        var safeBundle = SafePaths.RequireExistingDirectory(bundleRoot);
        var manifestPath = SafePaths.CombineUnder(safeBundle, "release.json");
        var manifest = UpdateManifestReader.Read(manifestPath);
        var packagePath = SafePaths.CombineUnder(safeBundle, $"payload/{manifest.PackageFile}");
        SafePaths.RequireExistingFile(packagePath);

        foreach (var name in ControlFiles)
        {
            SafePaths.RequireExistingFile(SafePaths.CombineUnder(safeBundle, name));
        }

        SafePaths.CreateDirectoryTree(layout.InstallRoot);
        SafePaths.CreateDirectoryTree(layout.StagingRoot);
        var staged = UpdatePackageStager.Stage(manifest, packagePath, layout.StagingRoot);
        UpdateTransaction.Apply(layout, manifest, staged);

        var incomingControl = Path.Combine(layout.InstallRoot, $"control-incoming-{Guid.NewGuid():N}");
        try
        {
            SafePaths.CreateDirectoryTree(incomingControl);
            foreach (var name in ControlFiles)
            {
                File.Copy(
                    SafePaths.CombineUnder(safeBundle, name),
                    SafePaths.CombineUnder(incomingControl, name),
                    overwrite: false);
            }

            if (Directory.Exists(layout.ControlRoot))
            {
                throw new UpdateContractException("ControlAlreadyExists");
            }

            Directory.Move(incomingControl, layout.ControlRoot);
            return manifest;
        }
        catch
        {
            if (Directory.Exists(incomingControl))
            {
                SafePaths.DeleteTreeWithoutFollowingLinks(incomingControl);
            }

            try
            {
                UpdateTransaction.Rollback(layout);
            }
            catch (UpdateContractException exception) when (exception.Code == "RollbackUnavailable")
            {
                _ = UpdateTransaction.AbandonUnconfirmedFirstInstall(layout);
            }

            throw;
        }
    }
}

public static class NyxUninstaller
{
    public static void Uninstall(UpdateLayout layout, bool removeUserData)
    {
        var shortcutExists = File.Exists(layout.StartMenuShortcut);
        if (shortcutExists)
        {
            _ = SafePaths.RequireExistingFile(layout.StartMenuShortcut);
        }

        if (Directory.Exists(layout.InstallRoot))
        {
            _ = SafePaths.AuditTreeWithoutLinks(layout.InstallRoot);
        }

        if (removeUserData)
        {
            AuditDataRoot(layout.UserDataRoot);
            AuditDataRoot(layout.LegacyUserDataRoot);
        }

        if (shortcutExists)
        {
            File.Delete(layout.StartMenuShortcut);
        }

        if (Directory.Exists(layout.InstallRoot))
        {
            SafePaths.DeleteTreeWithoutFollowingLinks(layout.InstallRoot);
        }

        if (removeUserData)
        {
            DeleteDataRoot(layout.UserDataRoot);
            DeleteDataRoot(layout.LegacyUserDataRoot);
        }
    }

    private static void AuditDataRoot(string path)
    {
        if (File.Exists(path))
        {
            throw new UpdateContractException("UnsafePath");
        }

        if (Directory.Exists(path))
        {
            _ = SafePaths.AuditTreeWithoutLinks(path);
        }
    }

    private static void DeleteDataRoot(string path)
    {
        if (File.Exists(path))
        {
            throw new UpdateContractException("UnsafePath");
        }

        if (Directory.Exists(path))
        {
            SafePaths.DeleteTreeWithoutFollowingLinks(path);
        }
    }
}
