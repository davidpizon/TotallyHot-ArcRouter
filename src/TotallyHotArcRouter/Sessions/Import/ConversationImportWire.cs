using Contract = TotallyHot.ArcRouter.Telemetry.Contract;

namespace TotallyHot.ArcRouter.Sessions.Import;

/// <summary>
/// Converts between the wire messages that describe an import's options and <see cref="ConversationImportOptions"/>.
/// Shared by the import RPC, the approval-request RPCs and the command-line client so all three read and write the
/// options the same way. Does no I/O.
/// </summary>
internal static class ConversationImportWire
{
    /// <summary>Builds the import options from the wire message; an absent message is the defaults.</summary>
    /// <param name="options">The wire options, or <see langword="null"/> when the client omitted them.</param>
    /// <returns>The options.</returns>
    public static ConversationImportOptions ToOptions(Contract.ImportOptions? options)
    {
        if (options is null) return new ConversationImportOptions();

        return new ConversationImportOptions(
            options.Timestamps == Contract.ImportTimestampMode.ImportTime
                ? ImportTimestampMode.ImportTime
                : ImportTimestampMode.Original,
            options.OnConflict switch
            {
                Contract.ImportConflictMode.Overwrite => ImportConflictMode.Overwrite,
                Contract.ImportConflictMode.KeepBoth => ImportConflictMode.KeepBoth,
                _ => ImportConflictMode.Skip,
            },
            KeepLocalCopy: !options.ReplaceLocalOnConflict);
    }

    /// <summary>Describes import options for the wire.</summary>
    /// <param name="options">The options.</param>
    /// <returns>The wire message.</returns>
    public static Contract.ImportOptions ToWire(ConversationImportOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return new Contract.ImportOptions
        {
            Timestamps = options.Timestamps == ImportTimestampMode.ImportTime
                ? Contract.ImportTimestampMode.ImportTime
                : Contract.ImportTimestampMode.Original,
            OnConflict = options.OnConflict switch
            {
                ImportConflictMode.Overwrite => Contract.ImportConflictMode.Overwrite,
                ImportConflictMode.KeepBoth => Contract.ImportConflictMode.KeepBoth,
                _ => Contract.ImportConflictMode.Skip,
            },
            ReplaceLocalOnConflict = !options.KeepLocalCopy,
        };
    }

    /// <summary>Describes an import request for the dashboard's approval view.</summary>
    /// <param name="info">What the request shows the operator.</param>
    /// <returns>The wire message.</returns>
    public static Contract.ImportApprovalDetails ToApprovalDetails(Proxy.Auth.Passkey.ImportApprovalInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);

        return new Contract.ImportApprovalDetails
        {
            StagedId = info.StagedId,
            SourcePath = info.SourcePath,
            SizeBytes = info.SizeBytes,
            Sha256 = info.Sha256Hex,
            Options = ToWire(info.Options),
        };
    }
}
