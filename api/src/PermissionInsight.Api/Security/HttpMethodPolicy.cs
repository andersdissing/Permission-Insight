namespace PermissionInsight.Api.Security;

/// <summary>
/// Two allow-lists, for two different questions. Keeping them apart is the
/// point of this file: they were one list, and conflating them made the
/// important guarantee harder to see rather than stronger.
///
/// <b>Downstream</b> is the rule that matters. The application never writes to
/// SharePoint: only GET and HEAD may carry the application identity, which can
/// read every site in the tenant. If it could also write, a bug would be a
/// tenant-wide data loss event rather than a wrong answer on a screen. This is
/// enforced in <see cref="Downstream.ReadOnlyHttpClient"/>, the only place an
/// outbound request can be built, so adding a write path means deliberately
/// editing the list below.
///
/// <b>Inbound</b> is a different question: what a browser may ask this API to
/// do. A POST here reaches a handler in this process and nothing else — it is
/// an RPC shape for actions that record something, such as an export. It
/// cannot become a write to SharePoint, because every outbound call still goes
/// through the downstream list.
///
/// The one thing that must never happen is an inbound method being taken as
/// permission for the same method downstream. Nothing forwards a method:
/// handlers call typed client methods, not a generic proxy.
/// </summary>
public static class HttpMethodPolicy
{
    private static readonly HashSet<string> Downstream = new(StringComparer.OrdinalIgnoreCase)
    {
        "GET",
        "HEAD",
    };

    private static readonly HashSet<string> Inbound = new(StringComparer.OrdinalIgnoreCase)
    {
        "GET",
        "HEAD",
        // Recording an export or a person lookup is a state change in the
        // audit log, and a GET that has an effect is a lie about itself.
        "POST",
        // A CORS preflight. The middleware answers it with 204 and it never
        // reaches a handler.
        "OPTIONS",
    };

    /// <summary>
    /// May this method be sent to SharePoint or Microsoft Graph as the
    /// application identity? Only reads.
    /// </summary>
    public static bool IsAllowedDownstream(string? method) =>
        method is not null && Downstream.Contains(method);

    /// <summary>
    /// May this method be accepted from a browser? Anything else is refused
    /// with 405 before a handler sees it.
    /// </summary>
    public static bool IsAllowedInbound(string? method) =>
        method is not null && Inbound.Contains(method);

    /// <summary>The downstream allow-list, for error messages and tests.</summary>
    public static IReadOnlyCollection<string> DownstreamMethods => Downstream;

    /// <summary>The inbound allow-list, for tests.</summary>
    public static IReadOnlyCollection<string> InboundMethods => Inbound;
}
