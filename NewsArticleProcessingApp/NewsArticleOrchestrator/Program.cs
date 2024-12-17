using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.Extensibility;
using Microsoft.ApplicationInsights.Channel;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MongoDB.Driver;

var builder = FunctionsApplication.CreateBuilder(args);

builder.ConfigureFunctionsWebApplication();

// Retrieve the Cosmos DB MongoDB API connection string from configuration
builder.Services.AddSingleton<IMongoClient>(serviceProvider =>
{
    var cosmosConnectionString = builder.Configuration["CosmosDBMongoConnectionString"];
    if (string.IsNullOrEmpty(cosmosConnectionString))
    {
        throw new ArgumentException("CosmosDBMongoConnectionString not configured.");
    }
    return new MongoClient(cosmosConnectionString);
});

// Register TelemetryClient with batching configuration
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

    return new TelemetryClient(telemetryConfiguration);
});
builder.Services.AddSingleton<Common.Lib.ScraperStatusRepository>();

builder.Build().Run();
