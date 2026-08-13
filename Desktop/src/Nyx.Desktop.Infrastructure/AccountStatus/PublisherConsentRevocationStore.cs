namespace Nyx.Desktop.Infrastructure.AccountStatus;

/// <summary>
/// Persists only the fact that publisher-account cleanup must finish. The marker
/// contains no account, role, cookie, or server identifier.
/// </summary>
public sealed class PublisherConsentRevocationStore
{
    private readonly string publisherRoot;
    private readonly string root;

    public PublisherConsentRevocationStore(string publisherProfilesRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(publisherProfilesRoot);
        publisherRoot = Path.GetFullPath(publisherProfilesRoot);
        root = Path.Combine(
            publisherRoot,
            ".pending-account-revocations");
    }

    public bool IsPending(string provider) =>
        IsCleanupPending(provider)
        || IsMarkerPending(provider, optOut: true);

    public bool IsCleanupPending(string provider) =>
        IsMarkerPending(provider, optOut: false);

    public bool IsOptOutPending(string provider) =>
        IsMarkerPending(provider, optOut: true);

    public bool RecoveryMustDisableAccess(
        string provider,
        bool stateAccountAccess,
        bool stateCleanupPending)
    {
        if (!stateAccountAccess) return true;
        if (IsOptOutPending(provider)) return true;
        // A generic marker without the independently persisted state bit is
        // ambiguous across upgrades. Treat it as a legacy explicit opt-out.
        return IsCleanupPending(provider) && !stateCleanupPending;
    }

    private bool IsMarkerPending(string provider, bool optOut)
    {
        if (!TryMarkerPaths(provider, optOut, out var path, out var fallbackPath)) return true;
        var publisherState = InspectPath(publisherRoot, out var publisherAttributes);
        if (publisherState == MarkerPathState.Unreadable) return true;
        if (publisherState == MarkerPathState.Exists
            && (!publisherAttributes.HasFlag(FileAttributes.Directory)
                || publisherAttributes.HasFlag(FileAttributes.ReparsePoint)))
            return true;

        var rootState = InspectPath(root, out var rootAttributes);
        if (rootState == MarkerPathState.Unreadable) return true;
        if (rootState == MarkerPathState.Exists)
        {
            if (!rootAttributes.HasFlag(FileAttributes.Directory)
                || rootAttributes.HasFlag(FileAttributes.ReparsePoint))
                return true;
            if (InspectPath(path, out _) != MarkerPathState.Missing) return true;
        }

        return InspectPath(fallbackPath, out _) != MarkerPathState.Missing;
    }

    public bool MarkPending(string provider)
    {
        if (!TryMarkerPaths(
            provider,
            optOut: false,
            out var path,
            out var fallbackPath))
            return false;
        if (TryWriteMarker(path, EnsureRoot)) return true;
        return TryWriteMarker(fallbackPath, EnsurePublisherRoot);
    }

    public bool MarkOptOutPending(string provider)
    {
        if (!TryMarkerPaths(
            provider,
            optOut: true,
            out var path,
            out var fallbackPath))
            return false;
        if (TryWriteMarker(path, EnsureRoot)) return true;
        return TryWriteMarker(fallbackPath, EnsurePublisherRoot);
    }

    public bool Clear(string provider) =>
        Clear(provider, includeOptOut: true);

    public bool ClearCleanupPending(string provider) =>
        Clear(provider, includeOptOut: false);

    private bool Clear(string provider, bool includeOptOut)
    {
        if (!TryMarkerPaths(
                provider,
                optOut: false,
                out var path,
                out var fallbackPath))
            return false;
        var optOutPath = string.Empty;
        var optOutFallbackPath = string.Empty;
        if (includeOptOut
            && !TryMarkerPaths(
                provider,
                optOut: true,
                out optOutPath,
                out optOutFallbackPath))
            return false;
        try
        {
            var publisherState = InspectPath(publisherRoot, out var publisherAttributes);
            if (publisherState == MarkerPathState.Unreadable
                || (publisherState == MarkerPathState.Exists
                    && (!publisherAttributes.HasFlag(FileAttributes.Directory)
                        || publisherAttributes.HasFlag(FileAttributes.ReparsePoint))))
                return false;

            var rootState = InspectPath(root, out var rootAttributes);
            if (rootState == MarkerPathState.Unreadable
                || (rootState == MarkerPathState.Exists
                    && (!rootAttributes.HasFlag(FileAttributes.Directory)
                        || rootAttributes.HasFlag(FileAttributes.ReparsePoint))))
                return false;

            return TryDeleteMarker(path)
                && TryDeleteMarker(fallbackPath)
                && (!includeOptOut
                    || (TryDeleteMarker(optOutPath)
                        && TryDeleteMarker(optOutFallbackPath)));
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool TryWriteMarker(string path, Action ensureParent)
    {
        try
        {
            ensureParent();
            var state = InspectPath(path, out var attributes);
            if (state == MarkerPathState.Unreadable) return false;
            if (state == MarkerPathState.Exists)
                return !attributes.HasFlag(FileAttributes.Directory)
                    && !attributes.HasFlag(FileAttributes.ReparsePoint);
            using var stream = new FileStream(
                path,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                1,
                FileOptions.WriteThrough);
            stream.Flush(flushToDisk: true);
            return true;
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool TryDeleteMarker(string path)
    {
        var state = InspectPath(path, out var attributes);
        if (state == MarkerPathState.Missing) return true;
        if (state == MarkerPathState.Unreadable
            || attributes.HasFlag(FileAttributes.Directory)
            || attributes.HasFlag(FileAttributes.ReparsePoint))
            return false;
        File.Delete(path);
        return InspectPath(path, out _) == MarkerPathState.Missing;
    }

    private void EnsureRoot()
    {
        Directory.CreateDirectory(root);
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Publisher revocation marker root cannot be a reparse point.");
    }

    private void EnsurePublisherRoot()
    {
        Directory.CreateDirectory(publisherRoot);
        if ((File.GetAttributes(publisherRoot) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Publisher profile root cannot be a reparse point.");
    }

    private bool TryMarkerPaths(
        string provider,
        bool optOut,
        out string path,
        out string fallbackPath)
    {
        var name = provider switch
        {
            "HoYoLAB" => optOut ? "hoyolab.opt-out.pending" : "hoyolab.pending",
            "SKPORT" => optOut ? "skport.opt-out.pending" : "skport.pending",
            _ => null,
        };
        path = name is null ? string.Empty : Path.Combine(root, name);
        fallbackPath = name is null
            ? string.Empty
            : Path.Combine(publisherRoot, "." + name);
        return name is not null;
    }

    private static MarkerPathState InspectPath(
        string path,
        out FileAttributes attributes)
    {
        try
        {
            attributes = File.GetAttributes(path);
            return MarkerPathState.Exists;
        }
        catch (FileNotFoundException)
        {
            attributes = default;
            return MarkerPathState.Missing;
        }
        catch (DirectoryNotFoundException)
        {
            attributes = default;
            return MarkerPathState.Missing;
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException)
        {
            attributes = default;
            return MarkerPathState.Unreadable;
        }
    }

    private enum MarkerPathState
    {
        Missing,
        Exists,
        Unreadable,
    }
}
