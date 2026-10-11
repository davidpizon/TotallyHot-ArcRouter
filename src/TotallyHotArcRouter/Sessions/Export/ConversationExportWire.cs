using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Contract = TotallyHot.ArcRouter.Telemetry.Contract;

namespace TotallyHot.ArcRouter.Sessions.Export;

/// <summary>
/// Converts between the wire messages that describe an export's filter and destination and
/// <see cref="ConversationExportFilter"/>. Shared by the export RPC and the approval-request RPCs so both build
/// the filter, and so the digest an approval is bound to, from the same fields the same way. Does no I/O.
/// </summary>
internal static class ConversationExportWire
{
    /// <summary>Builds the export filter from the wire fields, rejecting an out-of-range timestamp.</summary>
    /// <param name="from">The lower time bound, or <see langword="null"/> when the client omitted it.</param>
    /// <param name="to">The upper time bound, or <see langword="null"/> when the client omitted it.</param>
    /// <param name="sessionId">An archive or client session id, or <see langword="null"/> for any session.</param>
    /// <param name="harness">The harness a turn must have, or <see langword="null"/> for any.</param>
    /// <param name="provider">The provider a turn must have been routed to, or <see langword="null"/> for any.</param>
    /// <param name="model">A model a turn must carry, or <see langword="null"/> for any.</param>
    /// <returns>The filter the fields describe.</returns>
    /// <exception cref="RpcException">With <see cref="StatusCode.InvalidArgument"/> when a timestamp is out of range.</exception>
    public static ConversationExportFilter ToFilter(
        Timestamp? from, Timestamp? to, string? sessionId, string? harness, string? provider, string? model)
    {
        return new ConversationExportFilter(
            From: ParseTimestamp(from, "from_utc"),
            To: ParseTimestamp(to, "to_utc"),
            SessionId: sessionId,
            Harness: harness,
            Provider: provider,
            Model: model);
    }

    /// <summary>Builds the export filter an approval request describes.</summary>
    /// <param name="details">The request's filter and destination.</param>
    /// <returns>The filter.</returns>
    /// <exception cref="RpcException">With <see cref="StatusCode.InvalidArgument"/> when a timestamp is out of range.</exception>
    public static ConversationExportFilter ToFilter(Contract.ExportApprovalDetails details)
    {
        ArgumentNullException.ThrowIfNull(details);
        return ToFilter(
            details.FromUtc,
            details.ToUtc,
            details.HasSessionId ? details.SessionId : null,
            details.HasHarness ? details.Harness : null,
            details.HasProvider ? details.Provider : null,
            details.HasModel ? details.Model : null);
    }

    /// <summary>Describes a filter and destination for the dashboard's approval view.</summary>
    /// <param name="filter">Which turns the export includes.</param>
    /// <param name="destinationPath">The zip's destination, exactly as the caller sent it.</param>
    /// <returns>The wire message.</returns>
    public static Contract.ExportApprovalDetails ToApprovalDetails(ConversationExportFilter filter, string destinationPath)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(destinationPath);

        var details = new Contract.ExportApprovalDetails { DestinationPath = destinationPath };
        if (filter.From is { } from) details.FromUtc = Timestamp.FromDateTimeOffset(from);
        if (filter.To is { } to) details.ToUtc = Timestamp.FromDateTimeOffset(to);
        if (filter.SessionId is not null) details.SessionId = filter.SessionId;
        if (filter.Harness is not null) details.Harness = filter.Harness;
        if (filter.Provider is not null) details.Provider = filter.Provider;
        if (filter.Model is not null) details.Model = filter.Model;
        return details;
    }

    /// <summary>Converts an optional wire timestamp to a <see cref="DateTimeOffset"/>, or <see langword="null"/> when absent.</summary>
    /// <param name="timestamp">The wire timestamp, or <see langword="null"/> when the client omitted it.</param>
    /// <param name="fieldName">The request field's name, for the rejection message.</param>
    /// <returns>The instant, or <see langword="null"/> for no bound.</returns>
    /// <exception cref="RpcException">With <see cref="StatusCode.InvalidArgument"/> when the timestamp is out of range.</exception>
    private static DateTimeOffset? ParseTimestamp(Timestamp? timestamp, string fieldName)
    {
        if (timestamp is null) return null;

        try
        {
            return timestamp.ToDateTimeOffset();
        }
        catch (InvalidOperationException)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, $"'{fieldName}' is not a valid timestamp."));
        }
    }
}
