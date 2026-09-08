using System.Net;
using System.Reflection;
using Microsoft.Azure.Functions.Worker;
using PermissionInsight.Api.Downstream;
using PermissionInsight.Api.Security;
using Xunit;

namespace PermissionInsight.Api.Tests;

/// <summary>
/// The application never writes to SharePoint. These tests are the enforcement
/// of that promise, not a description of it.
/// </summary>
public sealed class ReadOnlyEnforcementTests
{
    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    [InlineData("MERGE")]
    public async Task A_write_never_leaves_the_process(string method)
    {
        var transport = new RecordingHandler();
        var client = ClientOver(transport);

        var request = new HttpRequestMessage(
            new HttpMethod(method),
            new Uri("https://contoso.sharepoint.com/_api/web/lists"));

        await Assert.ThrowsAsync<ReadOnlyViolationException>(
            () => client.SendAsync(request, GraphClient.Scope, CancellationToken.None));

        Assert.Equal(0, transport.Sends);
    }

    [Fact]
    public async Task The_refusal_says_what_it_refused()
    {
        var client = ClientOver(new RecordingHandler());
        var request = new HttpRequestMessage(HttpMethod.Delete, new Uri("https://contoso.sharepoint.com/_api/web"));

        var violation = await Assert.ThrowsAsync<ReadOnlyViolationException>(
            () => client.SendAsync(request, GraphClient.Scope, CancellationToken.None));

        Assert.Equal("DELETE", violation.Method);
        Assert.Contains("never writes to SharePoint", violation.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("HEAD")]
    public async Task Reads_are_sent_as_the_application_identity(string method)
    {
        var transport = new RecordingHandler();
        var client = ClientOver(transport);

        var request = new HttpRequestMessage(new HttpMethod(method), new Uri("https://graph.microsoft.com/v1.0/sites"));
        using var response = await client.SendAsync(request, GraphClient.Scope, CancellationToken.None);

        Assert.Equal(1, transport.Sends);
        Assert.Equal("Bearer", transport.LastRequest!.Headers.Authorization!.Scheme);
        Assert.Equal("token-for-https://graph.microsoft.com/.default", transport.LastRequest.Headers.Authorization.Parameter);
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("OPTIONS")]
    public void A_method_admitted_inbound_is_still_refused_downstream(string method)
    {
        // The whole point of two lists. Accepting POST from a browser must
        // never be read as permission to send POST to SharePoint.
        Assert.True(HttpMethodPolicy.IsAllowedInbound(method));
        Assert.False(HttpMethodPolicy.IsAllowedDownstream(method));
    }

    [Fact]
    public void The_downstream_allow_list_holds_only_read_methods()
    {
        // If this fails, read the diff rather than the test. Widening this
        // list is the one change that turns this tool into something that can
        // damage a tenant.
        Assert.Equal(new[] { "GET", "HEAD" }, HttpMethodPolicy.DownstreamMethods.Order().ToArray());
    }

    [Fact]
    public void The_inbound_allow_list_admits_no_method_that_implies_a_write_elsewhere()
    {
        // POST reaches a handler in this process. PUT, PATCH and DELETE have
        // no use here and stay out, so an unexpected one is still a signal.
        Assert.Equal(
            new[] { "GET", "HEAD", "OPTIONS", "POST" },
            HttpMethodPolicy.InboundMethods.Order().ToArray());
    }

    [Fact]
    public void Every_http_function_declares_only_inbound_methods()
    {
        var triggers = HttpTriggers().ToArray();

        Assert.NotEmpty(triggers);

        foreach (var (function, trigger) in triggers)
        {
            // A trigger with no methods listed accepts every method, which is
            // the one thing no function here may do.
            Assert.NotNull(trigger.Methods);

            foreach (var method in trigger.Methods)
            {
                Assert.True(
                    HttpMethodPolicy.IsAllowedInbound(method),
                    $"Function '{function}' declares the method '{method}', which is not accepted inbound.");
            }
        }
    }

    [Fact]
    public void Only_the_audit_route_accepts_a_write_method()
    {
        // Every other endpoint reads. If a second one appears here, it should
        // be because somebody meant it.
        var writers = HttpTriggers()
            .Where(entry => entry.Trigger.Methods?.Any(method => !HttpMethodPolicy.IsAllowedDownstream(method)) == true)
            .Select(entry => entry.Function)
            .Order()
            .ToArray();

        Assert.Equal(new[] { "audit" }, writers);
    }

    [Fact]
    public void No_function_uses_a_function_key()
    {
        // A function key would be a second credential, weaker than the app role
        // check and able to admit a request the role check would refuse. The
        // authorisation middleware is the only gate.
        foreach (var (function, trigger) in HttpTriggers())
        {
            Assert.True(
                trigger.AuthLevel == AuthorizationLevel.Anonymous,
                $"Function '{function}' uses {trigger.AuthLevel} rather than Anonymous.");
        }
    }

    private static IEnumerable<(string Function, HttpTriggerAttribute Trigger)> HttpTriggers()
    {
        var assembly = typeof(RequestGate).Assembly;

        foreach (var type in assembly.GetTypes())
        {
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
            {
                var function = method.GetCustomAttribute<FunctionAttribute>();
                if (function is null)
                {
                    continue;
                }

                foreach (var parameter in method.GetParameters())
                {
                    if (parameter.GetCustomAttribute<HttpTriggerAttribute>() is { } trigger)
                    {
                        yield return (function.Name, trigger);
                    }
                }
            }
        }
    }

    private static ReadOnlyHttpClient ClientOver(HttpMessageHandler transport) =>
        new(new HttpClient(transport), new StubTokenProvider());

    private sealed class StubTokenProvider : IDownstreamTokenProvider
    {
        public ValueTask<string> GetTokenAsync(string scope, CancellationToken cancellationToken) =>
            ValueTask.FromResult($"token-for-{scope}");
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public int Sends { get; private set; }

        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Sends++;
            LastRequest = request;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }
}
