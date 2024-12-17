//using MongoDB.Bson;
//using MongoDB.Driver;
//using System;
//using System.Threading.Tasks;

//namespace Common.Lib
//{
//    public class DbHelper
//    {
//        private static string serviceBusConnectionString = Environment.GetEnvironmentVariable("ServiceBusConnectionString")!;
//        private static string queueName = Environment.GetEnvironmentVariable("QueueName")!;
//        private static string databaseName = Environment.GetEnvironmentVariable("DatabaseName")!;
//        private static string dbConn = Environment.GetEnvironmentVariable("CosmosDBMongoConnectionString");
//        public static async Task UpsertProcessingStateAsync(string country, string processState, string proxyCount)
//        {
//            try
//            {
//                var mongoClient = new MongoClient(dbConn);
//                var database = mongoClient.GetDatabase(databaseName);
//                var collection = database.GetCollection<BsonDocument>("ScraperStatus");

//                // Upsert query
//                var filter = Builders<BsonDocument>.Filter.Eq("Country", country);
//                var update = Builders<BsonDocument>.Update
//                    .Set("ProcessState", processState)
//                    .Set("LastUpdated", DateTime.UtcNow);

//                await collection.UpdateOneAsync(filter, update, new UpdateOptions { IsUpsert = true });

//                Console.WriteLine($"Successfully upserted processing state for {country}: {processState}");
//            }
//            catch (Exception ex)
//            {
//                Console.WriteLine($"Error upserting processing state: {ex.Message}");
//            }
//        }

//        public static async Task<string> GetProcessingStateAsync(string country)
//        {
//            try
//            {
//                var mongoClient = new MongoClient(dbConn);
//                var database = mongoClient.GetDatabase(databaseName);
//                var collection = database.GetCollection<BsonDocument>("ScraperStatus");

//                // Field to search in
//                string fieldName = "Country"; // Replace with the actual field name

//                // Define a filter to search for text in the specific field
//                var filter = Builders<BsonDocument>.Filter.Regex(fieldName, new BsonRegularExpression(country, "i"));

//                // Find matching documents
//                var matchedDocuments = await collection.Find(filter).ToListAsync();
//                var document = await collection.Find(filter).FirstOrDefaultAsync();

//                if (document != null && document.Contains("ProcessState"))
//                {
//                    return document["ProcessState"].AsString;
//                }

//                return "unknown"; // Default if not found
//            }
//            catch (Exception ex)
//            {
//                Console.WriteLine($"Error fetching processing state: {ex.Message}");
//                return "error"; // Default for error handling
//            }
//        }
//    }

//}
