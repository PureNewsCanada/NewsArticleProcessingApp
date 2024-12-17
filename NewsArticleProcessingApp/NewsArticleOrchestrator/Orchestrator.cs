using System.Text;
using Azure.Messaging.ServiceBus;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.DataContracts;
using Microsoft.Extensions.Configuration;
using Common.Lib;

namespace NewsArticleOrchestrator
{
    public class Orchestrator
    {
        private readonly TelemetryClient _telemetryClient;
        private readonly IConfiguration _configuration;
        private readonly ServiceBusClient _serviceBusClient;
        private readonly ServiceBusSender _serviceBusSender;
        private readonly ScraperStatusRepository _repository;
        private readonly string _databaseName;
        private readonly string _queueName;

        public Orchestrator(TelemetryClient telemetryClient, IConfiguration configuration, ScraperStatusRepository repository)
        {
            _telemetryClient = telemetryClient;
            _configuration = configuration;

            // Read settings from configuration
            var serviceBusConnectionString = configuration["ServiceBusConnectionString"];
            _queueName = configuration["QueueName"]!;
            _databaseName = configuration["DatabaseName"]!;

            // Initialize ServiceBusClient and ServiceBusSender
            _serviceBusClient = new ServiceBusClient(serviceBusConnectionString);
            _serviceBusSender = _serviceBusClient.CreateSender(_queueName);
            _repository = repository;
        }

        [Function("Orchestrator")]
        public async Task RunAsync([TimerTrigger("%TimerSchedule%")] TimerInfo timer)
        {
            // List of countries to scrape
            List<string> countries = new List<string> { "Canada", "USA", "UK" };

            // Enqueue tasks for each country
            foreach (var country in countries)
            {
                try
                {
                    // Check the current processing state from MongoDB
                    var currentState = await _repository.GetProcessingStateAsync(country);
                    if (currentState == "Initiate" || currentState == "Completed")
                    {
                        var taskMessage = new Dictionary<string, string>
                        {
                            { "Country", country },
                            { "CountrySlug", Helper.GetCountrySlug(country) }
                        };

                        var message = new ServiceBusMessage(Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(taskMessage)))
                        {
                            ContentType = "application/json",
                            ApplicationProperties = { { "Label", country } }
                        };

                        // Send message to the queue
                        await _serviceBusSender.SendMessageAsync(message);

                        // Log successful enqueue
                        _telemetryClient.TrackTrace($"Enqueued task for {country}", SeverityLevel.Information);
                        
                    }
                    else
                    {
                        // Log skipping message
                        _telemetryClient.TrackTrace($"Skipping task for {country}. Current state: {currentState}", SeverityLevel.Information);
                    }
                }
                catch (Exception ex)
                {
                    // Log exceptions
                    _telemetryClient.TrackException(ex, new Dictionary<string, string>
                    {
                        { "Country", country },
                        { "Operation", "Enqueue Task" }
                    });
                }
            }
        }
    }
}
