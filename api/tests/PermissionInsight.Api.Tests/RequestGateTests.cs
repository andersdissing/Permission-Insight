using System.Net;
using System.Security.Claims;
using PermissionInsight.Api.Security;
using Xunit;

namespace PermissionInsight.Api.Tests;

/// <summary>
/// The app role check is the access control model: with application
/// permissions behind it, passing this gate is equivalent to read access over
/// every SharePoint site in the tenant. It gets tests before anything else
/// does, and a change to authorisation needs a test that fails without it.
/// </summary>
public sealed class RequestGateTests
{
    private const string ValidToken = "Bearer header.payload.signature";

    [Theory]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    [InlineData("MERGE")]
    public async Task Refuses_a_method_the_api_does_not_perform_even_with_a_perfectly_good_token(string method)
    {
        var gate = GateFor(Authorised());

        var decision = await gate.EvaluateAsync(method, ValidToken, CancellationToken.None);

        var denied = Assert.IsType<GateDecision.Denied>(decision);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, denied.Status);
        Assert.Equal("method_not_allowed", denied.Error);
    }

    [Fact]
    public async Task Refuses_the_method_before_it_looks_at_the_token()
    {
        // A refused method is refused whatever it carries. If the gate
        // validated first, a future change could make one reachable by a token
        // that happened to pass.
        var validator = new RecordingValidator(Authorised());
        var gate = new RequestGate(validator);

        await gate.EvaluateAsync("DELETE", ValidToken, CancellationToken.None);

        Assert.False(validator.WasCalled);
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("HEAD")]
    [InlineData("get")]
    // POST is accepted here and reaches a handler in this process. It carries
    // no permission to send POST to SharePoint; that is a separate allow-list.
    [InlineData("POST")]
    public async Task Allows_the_methods_the_api_performs(string method)
    {
        var gate = GateFor(Authorised());

        var decision = await gate.EvaluateAsync(method, ValidToken, CancellationToken.None);

        Assert.IsType<GateDecision.Allowed>(decision);
    }

    [Fact]
    public async Task A_post_is_gated_by_the_role_exactly_like_a_read()
    {
        var gate = GateFor(Principal(roles: []));

        var decision = await gate.EvaluateAsync("POST", ValidToken, CancellationToken.None);

        var denied = Assert.IsType<GateDecision.Denied>(decision);
        Assert.Equal(HttpStatusCode.Forbidden, denied.Status);
    }

    [Fact]
    public async Task Missing_authorization_header_is_401()
    {
        var gate = GateFor(Authorised());

        var decision = await gate.EvaluateAsync("GET", null, CancellationToken.None);

        var denied = Assert.IsType<GateDecision.Denied>(decision);
        Assert.Equal(HttpStatusCode.Unauthorized, denied.Status);
        Assert.Equal("missing_token", denied.Error);
    }

    [Theory]
    [InlineData("header.payload.signature")]
    [InlineData("Basic dXNlcjpwYXNz")]
    [InlineData("Bearer ")]
    public async Task A_header_that_is_not_a_bearer_token_is_401(string header)
    {
        var gate = GateFor(Authorised());

        var decision = await gate.EvaluateAsync("GET", header, CancellationToken.None);

        var denied = Assert.IsType<GateDecision.Denied>(decision);
        Assert.Equal(HttpStatusCode.Unauthorized, denied.Status);
    }

    [Fact]
    public async Task A_token_that_fails_validation_is_401()
    {
        var gate = new RequestGate(new StubValidator(TokenValidationOutcome.Invalid("SecurityTokenExpiredException")));

        var decision = await gate.EvaluateAsync("GET", ValidToken, CancellationToken.None);

        var denied = Assert.IsType<GateDecision.Denied>(decision);
        Assert.Equal(HttpStatusCode.Unauthorized, denied.Status);
        Assert.Equal("invalid_token", denied.Error);
    }

    [Fact]
    public async Task A_valid_token_without_the_role_is_403()
    {
        // The single most important refusal in the application. Entra's
        // appRoleAssignmentRequired should have stopped this sign-in, but that
        // is the outer layer; this is the one that has to hold.
        var gate = GateFor(Principal(roles: []));

        var decision = await gate.EvaluateAsync("GET", ValidToken, CancellationToken.None);

        var denied = Assert.IsType<GateDecision.Denied>(decision);
        Assert.Equal(HttpStatusCode.Forbidden, denied.Status);
        Assert.Equal("missing_role", denied.Error);
    }

    [Fact]
    public async Task The_403_names_the_missing_role_so_it_can_be_diagnosed_without_logs()
    {
        var gate = GateFor(Principal(roles: ["SomeOther.Role"]));

        var decision = await gate.EvaluateAsync("GET", ValidToken, CancellationToken.None);

        var denied = Assert.IsType<GateDecision.Denied>(decision);
        Assert.Contains("PermissionInsight.Use", denied.Message, StringComparison.Ordinal);
        Assert.NotNull(denied.Details);
        Assert.Equal("PermissionInsight.Use", denied.Details!["requiredRole"]);
        Assert.Equal(new[] { "SomeOther.Role" }, denied.Details["rolesPresent"]);
    }

    [Fact]
    public async Task The_403_carries_the_object_id_so_the_audit_log_can_name_who_was_refused()
    {
        // A valid token without the role is either a misconfiguration or
        // somebody probing. Both are worth seeing, and neither is useful
        // recorded as "anonymous" — by this point the signature and issuer have
        // been checked, so the claim is as trustworthy as it is for a caller
        // who passes.
        var gate = GateFor(Principal(roles: [], objectId: "22222222-2222-2222-2222-222222222222"));

        var decision = await gate.EvaluateAsync("GET", ValidToken, CancellationToken.None);

        var denied = Assert.IsType<GateDecision.Denied>(decision);
        Assert.Equal("22222222-2222-2222-2222-222222222222", denied.ObjectId);
    }

    [Fact]
    public async Task A_refusal_before_the_identity_is_known_names_nobody()
    {
        var gate = GateFor(Authorised());

        var decision = await gate.EvaluateAsync("GET", authorizationHeader: null, CancellationToken.None);

        var denied = Assert.IsType<GateDecision.Denied>(decision);
        Assert.Null(denied.ObjectId);
    }

    [Theory]
    [InlineData("permissioninsight.use")]
    [InlineData("PermissionInsight.Use ")]
    [InlineData("PermissionInsight.UseX")]
    public async Task A_role_that_only_looks_like_the_required_one_is_refused(string role)
    {
        var gate = GateFor(Principal(roles: [role]));

        var decision = await gate.EvaluateAsync("GET", ValidToken, CancellationToken.None);

        Assert.IsType<GateDecision.Denied>(decision);
    }

    [Fact]
    public async Task The_role_is_found_among_several()
    {
        var gate = GateFor(Principal(roles: ["Other.Role", "PermissionInsight.Use"]));

        var decision = await gate.EvaluateAsync("GET", ValidToken, CancellationToken.None);

        Assert.IsType<GateDecision.Allowed>(decision);
    }

    [Fact]
    public async Task An_app_only_token_is_refused_even_when_it_carries_the_role()
    {
        // No scp claim means no signed-in user. An application arriving with
        // tenant-wide read is exactly what this gate exists to refuse, and it
        // would also leave the audit log with nobody to name.
        var gate = GateFor(Principal(scope: null));

        var decision = await gate.EvaluateAsync("GET", ValidToken, CancellationToken.None);

        var denied = Assert.IsType<GateDecision.Denied>(decision);
        Assert.Equal("invalid_scope", denied.Error);
    }

    [Fact]
    public async Task A_v1_token_is_refused()
    {
        var gate = GateFor(Principal(version: "1.0"));

        var decision = await gate.EvaluateAsync("GET", ValidToken, CancellationToken.None);

        var denied = Assert.IsType<GateDecision.Denied>(decision);
        Assert.Equal("unsupported_token_version", denied.Error);
    }

    [Fact]
    public async Task A_token_with_no_object_id_is_refused_because_it_cannot_be_audited()
    {
        var gate = GateFor(Principal(objectId: null));

        var decision = await gate.EvaluateAsync("GET", ValidToken, CancellationToken.None);

        var denied = Assert.IsType<GateDecision.Denied>(decision);
        Assert.Equal(HttpStatusCode.Unauthorized, denied.Status);
    }

    [Fact]
    public async Task An_admitted_caller_carries_the_identity_the_audit_log_needs()
    {
        var gate = GateFor(Authorised());

        var decision = await gate.EvaluateAsync("GET", ValidToken, CancellationToken.None);

        var allowed = Assert.IsType<GateDecision.Allowed>(decision);
        Assert.Equal("11111111-1111-1111-1111-111111111111", allowed.Caller.ObjectId);
        Assert.Equal("Test Person", allowed.Caller.DisplayName);
        Assert.Equal("test.person@example.invalid", allowed.Caller.Mail);
    }

    [Fact]
    public async Task A_caller_with_no_name_claim_falls_back_to_the_object_id()
    {
        // The display name is cosmetic. Losing it must not cost the audit
        // entry, so it degrades rather than refusing the request.
        var gate = GateFor(Principal(name: null));

        var decision = await gate.EvaluateAsync("GET", ValidToken, CancellationToken.None);

        var allowed = Assert.IsType<GateDecision.Allowed>(decision);
        Assert.Equal(allowed.Caller.ObjectId, allowed.Caller.DisplayName);
    }

    private static RequestGate GateFor(ClaimsPrincipal principal) =>
        new(new StubValidator(TokenValidationOutcome.Valid(principal)));

    private static ClaimsPrincipal Authorised() => Principal();

    private static ClaimsPrincipal Principal(
        string? objectId = "11111111-1111-1111-1111-111111111111",
        string? name = "Test Person",
        string? version = "2.0",
        string? scope = "access_as_user",
        string[]? roles = null)
    {
        var claims = new List<Claim>();

        if (objectId is not null)
        {
            claims.Add(new Claim("oid", objectId));
        }

        if (name is not null)
        {
            claims.Add(new Claim("name", name));
        }

        if (version is not null)
        {
            claims.Add(new Claim("ver", version));
        }

        if (scope is not null)
        {
            claims.Add(new Claim("scp", scope));
        }

        claims.Add(new Claim("tid", "22222222-2222-2222-2222-222222222222"));
        claims.Add(new Claim("preferred_username", "test.person@example.invalid"));

        foreach (var role in roles ?? ["PermissionInsight.Use"])
        {
            claims.Add(new Claim("roles", role));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
    }

    private sealed class StubValidator(TokenValidationOutcome outcome) : IAccessTokenValidator
    {
        public Task<TokenValidationOutcome> ValidateAsync(string token, CancellationToken cancellationToken) =>
            Task.FromResult(outcome);
    }

    private sealed class RecordingValidator(ClaimsPrincipal principal) : IAccessTokenValidator
    {
        public bool WasCalled { get; private set; }

        public Task<TokenValidationOutcome> ValidateAsync(string token, CancellationToken cancellationToken)
        {
            WasCalled = true;
            return Task.FromResult(TokenValidationOutcome.Valid(principal));
        }
    }
}
