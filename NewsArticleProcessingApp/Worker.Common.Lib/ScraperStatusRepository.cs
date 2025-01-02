using Microsoft.Extensions.Configuration;
using MongoDB.Bson;
using MongoDB.Driver;
using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Common.Lib
{
    public class ScraperStatusRepository
    {
        private readonly IMongoDatabase _database;
        private readonly IMongoCollection<BsonDocument> _collection;
        private readonly ILogger<ScraperStatusRepository> _logger;

        public ScraperStatusRepository(IMongoClient mongoClient, IConfiguration configuration, ILogger<ScraperStatusRepository> logger)
        {
            _logger = logger;

            var databaseName = configuration["DatabaseName"]
                               ?? throw new ArgumentException("DatabaseName is not configured.");
            _database = mongoClient.GetDatabase(databaseName);
            _collection = _database.GetCollection<BsonDocument>("ScraperStatus");
        }

        public async Task UpsertProcessingStateAsync(string country, string processState, string? proxyCount = null)
        {
            try
            {
                var filter = Builders<BsonDocument>.Filter.Eq("Country", country);

                var update = Builders<BsonDocument>.Update
                    .Set("ProcessState", processState)
                    .Set("LastUpdated", DateTime.UtcNow);

                if (!string.IsNullOrEmpty(proxyCount))
                {
                    update = update.Set("ProxyCallCount", proxyCount);
                }

                await _collection.UpdateOneAsync(filter, update, new UpdateOptions { IsUpsert = true });

                _logger.LogInformation("Successfully upserted processing state for {Country}: {ProcessState}, ProxyCallCount: {ProxyCount}",
                    country, processState, proxyCount ?? "N/A");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error upserting processing state for {Country}", country);
            }
        }

        public async Task<string> GetProcessingStateAsync(string country)
        {
            try
            {
                var filter = Builders<BsonDocument>.Filter.Regex("Country", new BsonRegularExpression(country, "i"));

                var document = await _collection.Find(filter).FirstOrDefaultAsync();

                if (document != null && document.Contains("ProcessState"))
                {
                    return document["ProcessState"].AsString;
                }

                return "unknown";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching processing state for {Country}", country);
                return "error";
            }
        }
    }
}
