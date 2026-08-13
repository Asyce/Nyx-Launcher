namespace Nyx.Desktop.Core.Exports;

/// <summary>Stable, non-sensitive failure codes for local pull exports.</summary>
public static class PullExportErrorCodes
{
    public const string UnsupportedGame = "pulls-unsupported";
    public const string HistoryNotFound = "pulls-history-not-found";
    public const string HistoryNotUpdated = "pulls-history-not-updated";
    public const string CacheTooLarge = "pulls-cache-too-large";
    public const string InvalidHistoryLink = "pulls-history-link-invalid";
    public const string UpstreamRejected = "pulls-upstream-rejected";
    public const string UpstreamInvalid = "pulls-upstream-invalid";
    public const string SafetyLimit = "pulls-safety-limit";
    public const string OutputFailed = "pulls-output-failed";
}

/// <summary>
/// An export failure whose message is safe for a launcher status surface. It never
/// carries a file path, URL, authentication query, response body, or inner exception.
/// </summary>
public sealed class PullExportException : Exception
{
    public PullExportException(string errorCode)
        : base(IsKnown(errorCode) ? errorCode : "pulls-failed")
    {
        ErrorCode = IsKnown(errorCode) ? errorCode : "pulls-failed";
    }

    public string ErrorCode { get; }

    private static bool IsKnown(string value) => value is
        PullExportErrorCodes.UnsupportedGame or
        PullExportErrorCodes.HistoryNotFound or
        PullExportErrorCodes.HistoryNotUpdated or
        PullExportErrorCodes.CacheTooLarge or
        PullExportErrorCodes.InvalidHistoryLink or
        PullExportErrorCodes.UpstreamRejected or
        PullExportErrorCodes.UpstreamInvalid or
        PullExportErrorCodes.SafetyLimit or
        PullExportErrorCodes.OutputFailed;
}
