using System.Text.Json;
using Azure.Core.Serialization;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PermissionInsight.Api.Auditing;
using PermissionInsight.Api.Configuration;
using PermissionInsight.Api.Downstream;
using PermissionInsight.Api.Security;

var host = new HostBuilder()
    .ConfigureFunctionsWorkerDefaults(worker =>
    {
        // The access control model. Registered once, in the worker pipeline, so
        // it runs before every function invocation and no handler can opt out.
        // Do not move this into a base class or an attribute.
        worker.UseMiddleware<AuthorizationMiddleware>();
    })
    .ConfigureServices((context, services) =>
    {
        // Throws if anything is missing, so a half-configured deployment fails
        // at startup rather than on somebody's first search.
        var options = ApiOptions.FromConfiguration(context.Configuration);
        services.AddSingleton(options);

        services.Configure<WorkerOptions>(worker =>
            worker.Serializer = new JsonObjectSerializer(new JsonSerializerOptions(JsonSerializerDefaults.Web)));

        services.AddApplicationInsightsTelemetryWorkerService();
        services.ConfigureFunctionsApplicationInsights();

        services.AddSingleton<AuditLog>();
        services.AddSingleton<IDownstreamTokenProvider, DownstreamTokenProvider>();

        // One long-lived client for Entra's discovery document. The validator
        // caches the signing keys for the life of the process, so a per-request
        // client would throw that cache away and re-fetch on every call.
        services.AddSingleton<IAccessTokenValidator>(_ => new EntraAccessTokenValidator(
            options,
            new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(15) })));

        services.AddSingleton<RequestGate>();

        services.AddHttpClient<ReadOnlyHttpClient>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(100);
            client.DefaultRequestHeaders.Add("User-Agent", "PermissionInsight");
        });

        services.AddTransient<GraphClient>();
        services.AddTransient<SharePointClient>();

        // Singleton so the per-site cache survives the hundreds of requests a
        // sharing link scan makes.
        services.AddSingleton<SiteContextProvider>(provider => new SiteContextProvider(
            provider.GetRequiredService<GraphClient>()));
    })
    .ConfigureLogging(logging =>
    {
        // Application Insights installs a rule that drops anything below
        // Warning. The audit log is written at Information and is the only
        // record of who accessed what, so leaving this in place would lose the
        // one thing the backend is required to keep.
        logging.Services.Configure<LoggerFilterOptions>(filterOptions =>
        {
            var applicationInsightsRule = filterOptions.Rules.FirstOrDefault(rule =>
                rule.ProviderName == "Microsoft.Extensions.Logging.ApplicationInsights.ApplicationInsightsLoggerProvider");

            if (applicationInsightsRule is not null)
            {
                filterOptions.Rules.Remove(applicationInsightsRule);
            }
        });
    })
    .Build();

host.Run();
