using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.Channel;
using Microsoft.ApplicationInsights.Extensibility;
using Microsoft.ApplicationInsights.Extensibility.Implementation;
using Microsoft.ApplicationInsights.Extensibility.Implementation.Tracing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.Hosting;
using MongoDB.Driver;
using Common.Lib;
using Azure.Messaging.ServiceBus;
using Microsoft.ApplicationInsights.DataContracts;

var builder = FunctionsApplication.CreateBuilder(args);

// Retrieve the Cosmos DB MongoDB API connection string from configuration
builder.Services.AddSingleton<IMongoClient>(serviceProvider =>
{
    var cosmosConnectionString = builder.Configuration["CosmosDBMongoConnectionString"];
    var mongoClientSettings = MongoClientSettings.FromConnectionString(cosmosConnectionString);
    mongoClientSettings.MaxConnectionPoolSize = 100; // Increase this number as needed
    mongoClientSettings.WaitQueueSize = 100; // Increase wait queue size
    if (string.IsNullOrEmpty(cosmosConnectionString))
    {
        throw new ArgumentException("CosmosDBMongoConnectionString not configured.");
    }
    return new MongoClient(cosmosConnectionString);
});

builder.Services.AddSingleton(serviceProvider =>
{
    var connectionString = builder.Configuration["ServiceBusConnectionString"];
    return new ServiceBusClient(connectionString);
});

// Register TelemetryClient with batching configuration and Error filtering
builder.Services.AddSingleton(serviceProvider =>
{
    var telemetryConfiguration = TelemetryConfiguration.CreateDefault();
    var connectionString = builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"];

    if (string.IsNullOrEmpty(connectionString))
    {
        throw new ArgumentException("APPLICATIONINSIGHTS_CONNECTION_STRING is not configured.");
    }

    telemetryConfiguration.ConnectionString = connectionString;

    // Configure batching for telemetry
    var inMemoryChannel = new InMemoryChannel
    {
        MaxTelemetryBufferCapacity = 1000, // Maximum number of telemetry items to buffer
        SendingInterval = TimeSpan.FromSeconds(15) // Frequency of sending batched telemetry
    };

    telemetryConfiguration.TelemetryChannel = inMemoryChannel;

    // Add a filter to log only error-level telemetry
    var loggingFilter = new FilteringTelemetryProcessor((item) =>
    {
        if (item is TraceTelemetry traceTelemetry)
        {
            return traceTelemetry.SeverityLevel == SeverityLevel.Error || traceTelemetry.SeverityLevel == SeverityLevel.Critical;
        }
        return true;
    });
    telemetryConfiguration.DefaultTelemetrySink.TelemetryProcessorChainBuilder.Use((next) => loggingFilter).Build();

    return new TelemetryClient(telemetryConfiguration);
});

// Register your repository
builder.Services.AddSingleton<Common.Lib.ScraperStatusRepository>();

// Build the function app and run
builder.Build().Run();

// Define FilteringTelemetryProcessor
public class FilteringTelemetryProcessor : ITelemetryProcessor
{
    private readonly Func<ITelemetry, bool> _filter;
    private readonly ITelemetryProcessor _next;

    public FilteringTelemetryProcessor(Func<ITelemetry, bool> filter, ITelemetryProcessor next = null!)
    {
        _filter = filter ?? throw new ArgumentNullException(nameof(filter));
        _next = next;
    }

    public void Process(ITelemetry item)
    {
        if (_filter(item))
        {
            _next.Process(item);
        }
    }
}
