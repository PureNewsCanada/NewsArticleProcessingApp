using Microsoft.Extensions.DependencyInjection;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.Hosting;
using MongoDB.Driver;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.ApplicationInsights;

var builder = FunctionsApplication.CreateBuilder(args);

// Retrieve the Cosmos DB MongoDB API connection string from configuration
builder.Services.AddSingleton<IMongoClient>(serviceProvider =>
{
    var cosmosConnectionString = builder.Configuration["CosmosDBMongoConnectionString"];
    if (string.IsNullOrEmpty(cosmosConnectionString))
    {
        throw new ArgumentException("CosmosDBMongoConnectionString not configured.");
    }

    var mongoClientSettings = MongoClientSettings.FromConnectionString(cosmosConnectionString);
    mongoClientSettings.MaxConnectionPoolSize = 100; // Increase as needed
    mongoClientSettings.WaitQueueSize = 100; // Increase as needed
    return new MongoClient(mongoClientSettings);
});

// Register Service Bus client
builder.Services.AddSingleton(serviceProvider =>
{
    var connectionString = builder.Configuration["ServiceBusConnectionString"];
    if (string.IsNullOrEmpty(connectionString))
    {
        throw new ArgumentException("ServiceBusConnectionString not configured.");
    }

    return new ServiceBusClient(connectionString);
});

// Configure Application Insights logging
builder.Logging.AddApplicationInsights(
    configureTelemetryConfiguration: (config) =>
    {
        config.ConnectionString = builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"];
    },
    configureApplicationInsightsLoggerOptions: (options) => { }
);

// Add filters to log only error-level logs and exceptions
builder.Logging.AddFilter<ApplicationInsightsLoggerProvider>("", LogLevel.None); // Exclude all by default
builder.Logging.AddFilter<ApplicationInsightsLoggerProvider>("NewsArticleWorker", LogLevel.Error); // Include errors and exceptions only

// Register additional services
builder.Services.AddSingleton<Common.Lib.ScraperStatusRepository>();

// Build and run the function app
builder.Build().Run();
