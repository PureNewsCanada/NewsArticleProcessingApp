using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.ApplicationInsights;
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

// Register the repository
builder.Services.AddSingleton<Common.Lib.ScraperStatusRepository>();

// Configure Application Insights logging
builder.Logging.AddApplicationInsights(
    configureTelemetryConfiguration: (config) =>
    {
        config.ConnectionString = builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"];
        config.DisableTelemetry = true;
    },
    configureApplicationInsightsLoggerOptions: (options) => { }
);

// Add a logging filter to only log error-level logs and exceptions
builder.Logging.AddFilter<ApplicationInsightsLoggerProvider>("", LogLevel.None); // Exclude all by default
builder.Logging.AddFilter<ApplicationInsightsLoggerProvider>("Microsoft", LogLevel.None); // Exclude system logs
builder.Logging.AddFilter<ApplicationInsightsLoggerProvider>("NewsArticleOrchestrator", LogLevel.Error); // Log errors and above

// Build and run the function app
builder.Build().Run();
