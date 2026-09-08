using Microsoft.Azure.Functions.Worker;
using PermissionInsight.Api.Security;
using Xunit;

namespace PermissionInsight.Api.Tests;

public sealed class CallerContextTests
{
    [Fact]
    public void A_handler_that_runs_without_the_middleware_fails_rather_than_serving_data()
    {
        // This is the second half of "enforce by construction". Even if a
        // handler somehow became reachable without the authorisation
        // middleware, it cannot obtain a caller, so it throws instead of
        // returning tenant-wide permission data to an unchecked request.
        var context = new FakeFunctionContext();

        var failure = Assert.Throws<InvalidOperationException>(() => context.GetCaller());

        Assert.Contains("PermissionInsight.Use", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_caller_the_middleware_stored_is_the_caller_the_handler_reads()
    {
        var context = new FakeFunctionContext();
        var caller = new CallerContext("oid", "Name", "mail@example.invalid", "tid");

        context.Items[FunctionContextExtensions.CallerKey] = caller;

        Assert.Same(caller, context.GetCaller());
    }

    private sealed class FakeFunctionContext : FunctionContext
    {
        public override IDictionary<object, object> Items { get; set; } = new Dictionary<object, object>();

        public override IServiceProvider InstanceServices { get; set; } = null!;

        public override string InvocationId => "test-invocation";

        public override string FunctionId => "test-function";

        public override TraceContext TraceContext => throw new NotSupportedException();

        public override BindingContext BindingContext => throw new NotSupportedException();

        public override RetryContext RetryContext => throw new NotSupportedException();

        public override FunctionDefinition FunctionDefinition => throw new NotSupportedException();

        public override IInvocationFeatures Features => throw new NotSupportedException();
    }
}
