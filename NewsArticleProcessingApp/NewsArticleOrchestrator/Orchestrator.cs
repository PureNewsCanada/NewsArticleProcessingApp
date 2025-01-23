using System.Text;
using Azure.Messaging.ServiceBus;
using Microsoft.Azure.Functions.Worker;
using Newtonsoft.Json;
using Microsoft.Extensions.Configuration;
using Common.Lib;
using Microsoft.Extensions.Logging;

namespace NewsArticleOrchestrator
{
    public class Orchestrator
    {
        private readonly ServiceBusClient _serviceBusClient;
        private readonly ServiceBusSender _serviceBusSender;
        private readonly ScraperStatusRepository _repository;
        private readonly string _databaseName;
        private readonly string _queueName;
        private readonly ILogger _logger;


        public Orchestrator(IConfiguration configuration, ScraperStatusRepository repository, ILogger<Orchestrator> logger)
        {
            // Read settings from configuration
            var serviceBusConnectionString = configuration["ServiceBusConnectionString"];
            _queueName = configuration["QueueName"]!;
            _databaseName = configuration["DatabaseName"]!;
            // Initialize ServiceBusClient and ServiceBusSender
            _serviceBusClient = new ServiceBusClient(serviceBusConnectionString);
            _serviceBusSender = _serviceBusClient.CreateSender(_queueName);
            _repository = repository;
            _logger = logger;
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
                    if (currentState == "Initiate" || currentState == "Completed" || currentState == "Failed")
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
                        _logger.LogInformation($"Enqueued task for {country}");

                    }
                    else
                    {
                        // Log skipping message
                        _logger.LogInformation($"Skipping task for {country}. Current state: {currentState}");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex.Message.ToString(), new Dictionary<string, string>
        {
            { "Country", country },
              { "Operation", "Enqueue Task" }
        });
                }
            }
        }
    }
}
