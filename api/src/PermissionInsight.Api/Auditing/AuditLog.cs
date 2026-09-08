using Microsoft.Extensions.Logging;
using PermissionInsight.Api.Security;

namespace PermissionInsight.Api.Auditing;

/// <summary>
/// The audit trail. The backend holds no other record of what was accessed, so
/// this is not telemetry and must not be treated as such: a complete list of
/// the sites somebody searched for is an organisation chart, and knowing who
/// looked is the only compensating control the design has.
///
/// Entries go to Application Insights through the logging pipeline. The
/// property names below become custom dimensions, so keep them stable — a
/// renamed property silently breaks every saved query written against it.
/// </summary>
public sealed class AuditLog
{
    public const string Category = "PermissionInsight.Audit";

    private readonly ILogger _logger;

    public AuditLog(ILoggerFactory loggerFactory) => _logger = loggerFactory.CreateLogger(Category);

    /// <summary>
    /// One entry per authorised request: who, when, what they asked for. The
    /// query string is included deliberately — for a search it is the target —
    /// but only through <see cref="AuditQuery.Redact"/>, which passes
    /// identifiers and search terms and withholds everything else. Redaction
    /// happens here rather than at the call site so there is one path in and no
    /// way to log a raw query by forgetting.
    /// </summary>
    public void RecordRequest(CallerContext caller, string action, string route, string? query)
    {
        _logger.LogInformation(
            "Audit {AuditAction} by {ActorObjectId} on {AuditTarget} at {AuditTimestamp}",
            action,
            caller.ObjectId,
            $"{route}{AuditQuery.Redact(query)}",
            DateTimeOffset.UtcNow.ToString("O"));
    }

    /// <summary>
    /// A named action logged in its own right, for the cases the specification
    /// singles out: a person lookup, which profiles a named individual, and an
    /// export, which is the last moment the data can be recorded before it
    /// leaves the application. Phases 5 and 9.
    /// </summary>
    /// <remarks>
    /// <paramref name="target"/> and <paramref name="subject"/> are caller
    /// supplied free text — a library title, a person's name — and are capped
    /// rather than trusted. An unbounded value here would let any role holder
    /// pad the one record the design depends on, or push arbitrary volume into
    /// Application Insights.
    /// </remarks>
    public void RecordAction(CallerContext caller, string action, string target, string? subject = null)
    {
        _logger.LogInformation(
            "Audit {AuditAction} by {ActorObjectId} on {AuditTarget} about {AuditSubject} at {AuditTimestamp}",
            action,
            caller.ObjectId,
            Cap(target),
            Cap(subject) ?? "-",
            DateTimeOffset.UtcNow.ToString("O"));
    }

    /// <summary>Longer than any real library or display name, short enough to bound the entry.</summary>
    internal const int MaxFreeTextLength = 200;

    private static string? Cap(string? value) =>
        value is { Length: > MaxFreeTextLength } ? value[..MaxFreeTextLength] + "…" : value;

    /// <summary>
    /// A refused request. Warning rather than information, because a token that
    /// is valid but lacks the role is either a misconfiguration or somebody
    /// probing, and both are worth seeing.
    /// </summary>
    public void RecordDenied(string action, string method, string error, string? objectId)
    {
        _logger.LogWarning(
            "Audit denied {AuditAction} {HttpMethod} for {ActorObjectId} because {DenyReason} at {AuditTimestamp}",
            action,
            method,
            objectId ?? "anonymous",
            error,
            DateTimeOffset.UtcNow.ToString("O"));
    }
}
