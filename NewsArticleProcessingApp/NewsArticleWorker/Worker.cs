using Azure.Messaging.ServiceBus;
using Microsoft.Azure.Functions.Worker;
using Microsoft.ApplicationInsights.DataContracts;
using MongoDB.Driver;
using MongoDB.Bson;
using HtmlAgilityPack;
using Common.Lib;
using Newtonsoft.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;

public class Worker
{
    private readonly HttpClient client = new HttpClient();
    private readonly string baseUrl = "https://news.google.com";
    private readonly string username = string.Empty;
    private readonly string password = string.Empty;
    private readonly Dictionary<string, string> headers = new Dictionary<string, string>
    {
        { "accept", "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,image/apng,*/*;q=0.8,application/signed-exchange;v=b3;q=0.7" },
        { "accept-language", "en-US,en;q=0.9" },
        { "user-agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/125.0.0.0 Safari/537.36" }
    };

    private readonly IMongoClient _mongoClient;
    private readonly ILogger _logger;

    private string databaseName = Environment.GetEnvironmentVariable("DatabaseName")!;
    private string country = string.Empty;
    static int proxyCallCountForCountry = 0; // Track proxy calls for the current country
    private readonly ScraperStatusRepository _repository;
    private readonly ServiceBusClient _serviceBusClient;
    private string TopicCollectionName = string.Empty;
    private string ArticleCollectionName = string.Empty;

    public Worker(IMongoClient mongoClient, ScraperStatusRepository repository, ServiceBusClient serviceBusClient, IConfiguration configuration, ILogger<Worker> logger)
    {
        _mongoClient = mongoClient;
        _repository = repository;
        _serviceBusClient = serviceBusClient;
        username = configuration["ProxyUsername"]!;
        password = configuration["ProxyPassword"]!;
        TopicCollectionName = configuration["TopicCollectionName"]!;
        ArticleCollectionName = configuration["ArticleCollectionName"]!;
        _logger = logger;

    }

    [Function("Worker")]
    public async Task Run(
    [ServiceBusTrigger("scraping-queue", Connection = "ServiceBusConnectionString")] ServiceBusReceivedMessage message,
    FunctionContext context)
    {
        string country = string.Empty;

        // Use CancellationToken to handle long-running tasks
        var cancellationToken = context.CancellationToken;

        try
        {
            // Deserialize the message body
            var taskData = JsonConvert.DeserializeObject<Dictionary<string, string>>(message.Body.ToString());
            if (taskData == null || !taskData.TryGetValue("Country", out country!) || !taskData.TryGetValue("CountrySlug", out var countrySlug))
            {
                _logger.LogError($"Invalid message format: {message.Body}");
                return; // Exit function gracefully
            }

            _logger.LogInformation($"Processing scraping task for {country}");

            // Start a task to renew the message lock periodically
            var renewLockTask = RenewLockPeriodicallyAsync(message, cancellationToken);

            // Update processing state to "Running"
            await _repository.UpsertProcessingStateAsync(country, "Running", proxyCallCountForCountry.ToString());

            // Perform country-specific scraping logic
            await ScrapeGoogleNewsByCountry(country, countrySlug);

            // Update processing state to "Completed"
            await _repository.UpsertProcessingStateAsync(country, "Completed", proxyCallCountForCountry.ToString());

            _logger.LogInformation($"Successfully processed message for {country}.");

            // Stop lock renewal after processing completes
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"Error processing scraping task for {country}");

            // Update processing state to "Failed" if the country is available
            if (!string.IsNullOrEmpty(country))
            {
                try
                {
                    await _repository.UpsertProcessingStateAsync(country, "Failed", proxyCallCountForCountry.ToString());
                }
                catch (Exception updateEx)
                {
                    _logger.LogError(updateEx, $"Failed to update processing state to 'Failed' for {country}");
                }
            }

            throw; // Re-throw the exception to trigger retry or dead-lettering
        }
    }

    private async Task RenewLockPeriodicallyAsync(ServiceBusReceivedMessage message, CancellationToken cancellationToken)
    {
        var receiver = _serviceBusClient.CreateReceiver("scraping-queue");

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await receiver.RenewMessageLockAsync(message, cancellationToken);
                await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken); // Adjust the interval as needed
            }
        }
        catch (TaskCanceledException)
        {
            // Expected when the operation is canceled
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error renewing message lock: {ex.Message}");
        }
        finally
        {
            await receiver.CloseAsync();
        }
    }



    public async Task ScrapeGoogleNewsByCountry(string country, string countrySlug)
    {
        bool isRunningStateUpdated = false; // Track if the "Running" state has been updated

        try
        {
            if (string.IsNullOrEmpty(countrySlug))
            {
                LogTrace("Invalid country specified.", SeverityLevel.Warning);
                return;
            }

            // Set the process state to "Running" once at the start
            await _repository.UpsertProcessingStateAsync(country, "Running", proxyCallCountForCountry.ToString());
            isRunningStateUpdated = true;
            // Get proxy for the country
            string proxy = Common.Lib.Helper.GetProxyForCountry(countrySlug, username, password);
            var newsTypes = new[] { "Home", countrySlug, "World", "Business", "Technology", "Entertainment", "Sports", "Science", "Health" };
            var proxies = new Dictionary<string, string>
        {
            { "http", proxy },
            { "https", proxy }
        };

            var newsUrl = $"{baseUrl}/home?gl={countrySlug}&hl=en-{countrySlug}&ceid={countrySlug}:en";

            LogTrace($"Fetching news for {country} from URL: {newsUrl}");

            var response = await SendRequest(newsUrl, proxies);
            if (response == null)
            {
                LogTrace($"Failed to retrieve news for {country}.", SeverityLevel.Warning);
                // Update processing state before exiting
                await _repository.UpsertProcessingStateAsync(country, "Failed", proxyCallCountForCountry.ToString());
                return;
            }

            var doc = new HtmlDocument();
            doc.LoadHtml(response);

            var menuBar = doc.DocumentNode.SelectNodes("//*[@role='menubar']/div[contains(@data-url,'./topic')]");

            if (menuBar == null || menuBar.Count == 0)
            {
                LogTrace($"No menu items found for {country}.", SeverityLevel.Warning);
                return;
            }

            foreach (var menu in menuBar)
            {
                try
                {
                    var menuTitle = menu.SelectSingleNode("./a")?.InnerText?.Trim() ?? "";

                    var menuLink = menu.Attributes["data-url"]?.Value.Trim() ?? "";

                    LogTrace($"Menu Title: {menuTitle}, Menu Link: {menuLink}");

                    if (!string.IsNullOrEmpty(menuLink) && menuLink.StartsWith("./"))
                    {
                        string menuUrl = baseUrl + menuLink.Substring(1);
                        LogTrace($"Constructed Menu URL for {menuTitle}: {menuUrl}");

                        var item = new Dictionary<string, string>
                    {
                        { "Category", menuTitle },
                        { "Category_Slug", menuLink },
                        { "Category_URL", menuUrl }
                    };

                        await ParseCategory(item, countrySlug, proxies);
                    }
                    else
                    {
                        LogTrace($"Invalid or empty menu link for category: {menuTitle}", SeverityLevel.Warning);
                    }

                }
                catch (Exception ex)
                {
                    LogException(ex, $"Error processing menu for {country}. Menu HTML: {menu.OuterHtml}");
                }
            }
        }
        catch (Exception ex)
        {
            LogException(ex, $"An error occurred while scraping news for {country}.");

            // Update the process state to "Failed" if an exception occurs
            if (!isRunningStateUpdated)
            {
                proxyCallCountForCountry = 0; // Ensure no stale counts are used if state wasn't updated
            }
            await _repository.UpsertProcessingStateAsync(country, "Failed", proxyCallCountForCountry.ToString());
        }
    }



    public async Task ParseCategory(Dictionary<string, string> catItem, string countrySlug, Dictionary<string, string> proxies)
    {
        try
        {
            LogTrace($"Processing category: {catItem["Category"]} for {countrySlug}");

            var response = await SendRequest(catItem["Category_URL"], proxies);
            if (response == null)
            {
                LogTrace($"Failed to retrieve category page for {catItem["Category"]}.", SeverityLevel.Warning);
                return;
            }

            var doc = new HtmlDocument();
            doc.LoadHtml(response);

            try
            {
                var stories = doc.DocumentNode.SelectNodes("//a[contains(@href, './stories')]");
                if (stories != null)
                {
                    LogTrace($"Found {stories.Count} stories in category: {catItem["Category"]}.");

                    // Create a list of tasks to run in parallel
                    var tasks = stories.Select(async story =>
                    {
                        try
                        {
                            var hrefValue = story.GetAttributeValue("href", string.Empty);
                            if (!string.IsNullOrEmpty(hrefValue))
                            {
                                // Handling relative path by trimming leading '.'
                                var storyUrl = baseUrl + hrefValue.TrimStart('.');
                                string topicID = ObjectId.GenerateNewId().ToString();
                                await SaveTopicData(catItem, storyUrl, countrySlug, proxies, topicID);
                                LogTrace($"Processing story URL: {storyUrl}");
                                // Call ParseStory asynchronously
                                await ParseStory(catItem, storyUrl, countrySlug, proxies, topicID);
                            }
                            else
                            {
                                LogTrace($"Skipping story with empty href in category: {catItem["Category"]}", SeverityLevel.Warning);
                            }
                        }
                        catch (Exception ex)
                        {
                            LogException(ex, $"Error processing story URL in category: {catItem["Category"]}. Story HTML: {story.OuterHtml}");
                        }
                    }).ToList();

                    // Wait for all tasks to complete
                    await Task.WhenAll(tasks);
                }
                else
                {
                    LogTrace($"No stories found with the specified href pattern in category: {catItem["Category"]}.", SeverityLevel.Warning);
                }
            }
            catch (Exception ex)
            {
                LogException(ex, $"An error occurred while processing the stories in category: {catItem["Category"]}.");
            }
        }
        catch (Exception ex)
        {
            LogException(ex, $"An error occurred while parsing category: {catItem["Category"]}.");
        }
    }
    public async Task<Dictionary<string, object>> ParseStoryToGetDataforTopic(Dictionary<string, string> item, string storyUrl, string countrySlug, Dictionary<string, string> proxies, string topicID)
    {
        var itemDict = new Dictionary<string, object>();
        try
        {
            LogTrace($"Fetching story: {storyUrl} for category: {item["Category"]}");

            // Define query parameters for the request
            var paramsDict = new Dictionary<string, string>
        {
            { "hl", $"en-{countrySlug}" },
            { "gl", countrySlug },
            { "ceid", $"{countrySlug}:en" }
        };

            // Send the request and get the response
            var response = await SendRequest(storyUrl, proxies, paramsDict);
            if (response == null)
            {
                LogTrace($"Failed to retrieve story from {storyUrl}.", SeverityLevel.Warning);
                return itemDict;
            }

            var doc = new HtmlDocument();
            doc.LoadHtml(response);

            // Define the news types to search for
            var newsTypes = new[] { "Top News", "All coverage","Top news" };

            foreach (var newsType in newsTypes)
            {
                try
                {
                    LogTrace($"Searching for news type: {newsType} in story: {storyUrl}");
                    var topic = doc.DocumentNode.SelectNodes($"//h2[contains(text(),\"{newsType}\")]/ancestor::div[1]");
                    var article = doc.DocumentNode.SelectNodes($"//h2[contains(text(),\"{newsType}\")]/ancestor::div[1]/parent::div[1]/following-sibling::div/article");
                    if (article == null || !article.Any())
                    {
                        LogTrace($"No articles found for news type: {newsType} in story: {storyUrl}.", SeverityLevel.Warning);
                        continue;
                    }

                    // Process only the first article
                    foreach (var tn in article)
                    {
                        try
                        {
                            // Extract news URL
                            var node = tn.SelectSingleNode("./a/@href");
                            var newsUrl = node?.GetAttributeValue("href", string.Empty);

                            if (string.IsNullOrEmpty(newsUrl))
                            {
                                LogTrace($"Skipping article with empty URL in story: {storyUrl}.", SeverityLevel.Warning);
                                continue;
                            }

                            // Handle relative URLs
                            newsUrl = newsUrl.StartsWith("./") ? baseUrl + newsUrl.TrimStart('.') : newsUrl;

                            // Prepare the story data for insertion or update
                            itemDict = new Dictionary<string, object>
                        {
                            { "topic_id", topicID ?? ObjectId.GenerateNewId().ToString() }, // Generate new ObjectId
                            { "title", tn.SelectSingleNode("./h4/a//text()")?.InnerText.Trim() ?? string.Empty },
                            { "image_url", "https://news.google.com" + tn.SelectSingleNode("./figure/img")?.GetAttributeValue("srcset", string.Empty)?.Split(',')?.FirstOrDefault()?.Split(' ')?.FirstOrDefault()?.Trim() ?? string.Empty },
                        };

                            break; // Exit after processing the first article
                        }
                        catch (Exception innerEx)
                        {
                            LogException(innerEx, $"Error parsing article in story: {storyUrl}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    LogException(ex, $"Error processing news type: {newsType} in story: {storyUrl}");
                }
            }
        }
        catch (Exception ex)
        {
            LogException(ex, $"An error occurred while parsing the story: {storyUrl}");
        }
        return itemDict;
    }

    public async Task ParseStory(Dictionary<string, string> item, string storyUrl, string countrySlug, Dictionary<string, string> proxies, string topicID)
    {
        try
        {
            LogTrace($"Fetching story: {storyUrl} for category: {item["Category"]}");

            // Define query parameters for the request
            var paramsDict = new Dictionary<string, string>
        {
            { "hl", $"en-{countrySlug}" },
            { "gl", countrySlug },
            { "ceid", $"{countrySlug}:en" }
        };

            // Send the request and get the response
            var response = await SendRequest(storyUrl, proxies, paramsDict);
            if (response == null)
            {
                LogTrace($"Failed to retrieve story from {storyUrl}.", SeverityLevel.Warning);
                return;
            }

            var doc = new HtmlDocument();
            doc.LoadHtml(response);

            // Define the news types to search for
            var newsTypes = new[] { "Top news", "All coverage", "Top News"};

            foreach (var newsType in newsTypes)
            {
                try
                {
                    LogTrace($"Searching for news type: {newsType} in story: {storyUrl}");
                    var topic = doc.DocumentNode.SelectNodes($"//h2[contains(text(),\"{newsType}\")]/ancestor::div[1]");
                    var article = doc.DocumentNode.SelectNodes($"//h2[contains(text(),\"{newsType}\")]/ancestor::div[1]/parent::div[1]/following-sibling::div/article");
                    if (article == null || !article.Any())
                    {
                        LogTrace($"No articles found for news type: {newsType} in story: {storyUrl}.", SeverityLevel.Warning);
                        continue;
                    }

                    foreach (var tn in article)
                    {
                        try
                        {
                            // Extract news URL
                            var node = tn.SelectSingleNode("./a/@href");
                            var newsUrl = node?.GetAttributeValue("href", string.Empty);

                            if (string.IsNullOrEmpty(newsUrl))
                            {
                                LogTrace($"Skipping article with empty URL in story: {storyUrl}.", SeverityLevel.Warning);
                                continue;
                            }

                            // Handle relative URLs
                            newsUrl = newsUrl.StartsWith("./") ? baseUrl + newsUrl.TrimStart('.') : newsUrl;

                            // Extract last modified date
                            var lastModifiedDate = tn.SelectSingleNode("./div//time")?.GetAttributeValue("datetime", null);

                            // Check for existing records in the database
                            var existingRecord = await GetExistingRecordByUrl(newsUrl, countrySlug);
                            if (existingRecord != null)
                            {
                                DateTime existingTimestamp = DateTime.Parse(existingRecord["created"].ToString()!);
                                DateTime lastModifiedTimeStamp = DateTime.Parse(lastModifiedDate!);

                                if (existingTimestamp >= lastModifiedTimeStamp)
                                {
                                    LogTrace($"Skipping duplicate or outdated story: {newsUrl}", SeverityLevel.Information);
                                    continue;
                                }
                            }

                            // Prepare the story data for insertion or update
                            var itemDict = new Dictionary<string, object>
                        {
                            { "topic_id",topicID ?? ObjectId.GenerateNewId().ToString() }, // Generate new ObjectId
                            { "title", tn.SelectSingleNode("./h4/a//text()")?.InnerText.Trim() ?? string.Empty },
                            { "category", item["Category"] },
                            { "provider", tn.SelectSingleNode("./div/img/following-sibling::div[1]/a//text()")?.InnerText.Trim() ?? string.Empty },
                            { "provider_logo_url", tn.SelectSingleNode("./div/img")?.GetAttributeValue("srcset", string.Empty)?.Split(',')?.FirstOrDefault()?.Split(' ')?.FirstOrDefault()?.Trim() ?? string.Empty },
                            { "text", tn.SelectSingleNode("./div//time/@datetime")?.InnerText.Trim() ?? string.Empty },
                            { "location", new BsonDocument { { "country", countrySlug }, { "city", countrySlug } } },
                            { "native_url", newsUrl },
                            { "url", newsUrl },
                            { "image_url", "https://news.google.com" + tn.SelectSingleNode("./figure/img")?.GetAttributeValue("srcset", string.Empty)?.Split(',')?.FirstOrDefault()?.Split(' ')?.FirstOrDefault()?.Trim() ?? string.Empty },
                            { "created", DateTime.UtcNow }, // Timestamp for created
                            { "modified", DateTime.Parse(lastModifiedDate!) },
                            { "meta", "" }
                        };

                            // Insert or update the story in the database
                            await InsertOrUpdateRecord(itemDict, ArticleCollectionName);
                            LogTrace($"Parsed and added story: {newsUrl}");
                        }
                        catch (Exception innerEx)
                        {
                            LogException(innerEx, $"Error parsing article in story: {storyUrl}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    LogException(ex, $"Error processing news type: {newsType} in story: {storyUrl}");
                }
            }
        }
        catch (Exception ex)
        {
            LogException(ex, $"An error occurred while parsing the story: {storyUrl}");
        }
    }

    public async Task SaveTopicData(Dictionary<string, string> catItem, string storyUrl, string countrySlug, Dictionary<string, string> proxies, string topicID)
    {
        try
        {
            Dictionary<string, object> articleDict = await ParseStoryToGetDataforTopic(catItem, storyUrl, countrySlug, proxies, topicID);
            string title = string.Empty;
            string imgURL = string.Empty;
            if (articleDict.ContainsKey("title"))
            {
                title = articleDict["title"]?.ToString() ?? string.Empty;
            }
            if (articleDict.ContainsKey("image_url"))
            {
                imgURL = articleDict["image_url"]?.ToString() ?? string.Empty;
            }

            if (title == string.Empty)
            {
                Console.WriteLine("");
            }
            // Prepare the topic data for insertion or update
            var itemDict = new Dictionary<string, object>
                        {
                            { "_id",topicID },
                            { "title", title},
                            { "category", catItem["Category"] },
                            { "location", new BsonDocument { { "country", countrySlug }, { "city", countrySlug } } },
                            { "native_url", storyUrl },
                            { "image_url", imgURL},
                            { "modified", DateTime.UtcNow }, // Timestamp for modified
                            { "created", DateTime.UtcNow }, // Timestamp for created                            ,                           
                        };

            // Insert or update the topic in the database
            await InsertOrUpdateRecord(itemDict, TopicCollectionName);
            LogTrace($"Parsed and added topic: {catItem["Category_URL"]}");
        }
        catch (Exception innerEx)
        {
            LogException(innerEx, $"Error parsing article in topic: {storyUrl}");
        }
    }
    public async Task<Dictionary<string, object>?> GetExistingRecordByUrl(string newsUrl, string collectionName)
    {
        try
        {
            LogTrace($"Fetching existing record for URL: {newsUrl} in collection: {collectionName}");

            // Access the MongoDB collection
            var collection = _mongoClient.GetDatabase(databaseName).GetCollection<BsonDocument>(collectionName);

            // Query the database for a document where the 'url' matches the given newsUrl
            var filter = Builders<BsonDocument>.Filter.Eq("native_url", newsUrl);
            var existingRecord = await collection.Find(filter).FirstOrDefaultAsync();

            if (existingRecord != null)
            {
                LogTrace($"Record found for URL: {newsUrl} in collection: {collectionName}");

                // Convert the BsonDocument to a dictionary for further processing
                return existingRecord.ToDictionary();
            }
            else
            {
                LogTrace($"No record found for URL: {newsUrl} in collection: {collectionName}", SeverityLevel.Warning);
            }

            return null; // Return null if no record is found
        }
        catch (Exception ex)
        {
            LogException(ex, $"Error fetching record for URL: {newsUrl} in collection: {collectionName}");
            return null;
        }
    }

    public async Task InsertOrUpdateRecord(Dictionary<string, object> itemDict, string collectionName)
    {
        try
        {
            // Access the MongoDB collection
            var collection = _mongoClient.GetDatabase(databaseName).GetCollection<BsonDocument>(collectionName);

            // Convert the item dictionary to a BSON document
            var itemBson = ConvertToBsonDocument(itemDict);

            // Extract the news URL for checking existing records
            var newsUrl = itemDict["native_url"].ToString();

            // Query for an existing record by URL
            var filter = Builders<BsonDocument>.Filter.Eq("native_url", newsUrl);

            // Check if a record with this URL already exists
            var existingRecord = await collection.Find(filter).FirstOrDefaultAsync();

            if (existingRecord != null)
            {
                // Parse the timestamps to compare
                DateTime existingTimestamp = DateTime.Parse(existingRecord["created"].ToString()!);
                DateTime newTimestamp = DateTime.Parse(itemDict["modified"].ToString()!);

                if (newTimestamp > existingTimestamp)
                {
                    // Initialize the update builder
                    var updateBuilder = Builders<BsonDocument>.Update;
                    var updates = new List<UpdateDefinition<BsonDocument>>();

                    // Add updates only if the keys exist in the itemDict
                    if (itemDict.ContainsKey("modified"))
                        updates.Add(updateBuilder.Set("modified", itemDict["modified"]));
                    if (itemDict.ContainsKey("title"))
                        updates.Add(updateBuilder.Set("title", itemDict["title"]));
                    if (itemDict.ContainsKey("provider"))
                        updates.Add(updateBuilder.Set("provider", itemDict["provider"]));
                    if (itemDict.ContainsKey("text"))
                        updates.Add(updateBuilder.Set("text", itemDict["text"]));
                    if (itemDict.ContainsKey("image_url"))
                        updates.Add(updateBuilder.Set("image_url", itemDict["image_url"]));
                    if (itemDict.ContainsKey("meta"))
                        updates.Add(updateBuilder.Set("meta", itemDict["meta"]));
                    if (itemDict.ContainsKey("provider_logo_url"))
                        updates.Add(updateBuilder.Set("provider_logo_url", itemDict["provider_logo_url"]));

                    // Combine all updates
                    var update = updateBuilder.Combine(updates);

                    // Perform the update
                    if (updates.Any()) // Ensure there are updates to apply
                    {
                        await collection.UpdateOneAsync(filter, update);
                        LogTrace($"Updated record for URL: {newsUrl} in collection: {collectionName}");
                    }
                    else
                    {
                        LogTrace($"No updates applied for URL: {newsUrl} as no relevant keys were present.");
                    }
                }
                else
                {
                    LogTrace($"No update required for URL: {newsUrl} as the existing record is more recent.");
                }
            }
            else
            {
                // Insert the new record if no existing record is found
                await collection.InsertOneAsync(itemBson);
                LogTrace($"Inserted new record for URL: {newsUrl} in collection: {collectionName}");
            }
        }
        catch (Exception ex)
        {
            LogException(ex, $"Error in InsertOrUpdateRecord for URL: {itemDict["native_url"]}");
        }
    }

    private BsonDocument ConvertToBsonDocument(Dictionary<string, object> itemDict)
    {
        var bsonDoc = new BsonDocument();

        foreach (var kvp in itemDict)
        {
            try
            {
                if (kvp.Value is Dictionary<string, object> nestedDict)
                {
                    bsonDoc.Add(kvp.Key, ConvertToBsonDocument(nestedDict));
                }
                else if (kvp.Value is IEnumerable<object> list)
                {
                    bsonDoc.Add(kvp.Key, new BsonArray(list.Select(BsonValue.Create)));
                }
                else
                {
                    bsonDoc.Add(kvp.Key, BsonValue.Create(kvp.Value));
                }
            }
            catch (Exception ex)
            {
                LogException(ex, $"Error processing field '{kvp.Key}' in ConvertToBsonDocument.");
            }
        }

        return bsonDoc;
    }

    // Log a trace message
    private void LogTrace(string message, SeverityLevel severityLevel = SeverityLevel.Information)
    {
        _logger.LogInformation(message, severityLevel, new Dictionary<string, string> { { "Country", country } });
    }

    // Log an exception
    private void LogException(Exception ex, string message)
    {
        _logger.LogError(ex.Message.ToString(), new Dictionary<string, string>
        {
            { "Country", country },
            { "Message", message }
        });
    }



    // Method definitions for ScrapeGoogleNewsByCountry, ParseCategory, ParseStory, and MongoDB operations remain unchanged

    public async Task<string?> SendRequest(string url, Dictionary<string, string> proxies, Dictionary<string, string>? parameters = null)
    {
        try
        {
            var queryString = parameters != null ? $"?{string.Join("&", parameters.Select(p => $"{p.Key}={p.Value}"))}" : string.Empty;
            var fullUrl = $"{url}{queryString}";
            var requestMessage = new HttpRequestMessage(HttpMethod.Get, fullUrl);

            foreach (var header in headers)
            {
                requestMessage.Headers.Add(header.Key, header.Value);
            }

            var handler = new HttpClientHandler();
            handler.Proxy = new System.Net.WebProxy(proxies["http"]);
            handler.UseProxy = true;

            var response = await client.SendAsync(requestMessage);
            proxyCallCountForCountry++;
            return await response.Content.ReadAsStringAsync();
        }
        catch (Exception ex)
        {
            LogException(ex, "Error in sending request");
            return null;
        }
    }
}
