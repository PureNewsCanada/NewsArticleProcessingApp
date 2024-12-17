using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Specialized;
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Threading;

public class LogHelper : IDisposable
{
    private readonly BlobContainerClient _blobContainerClient;
    private readonly ConcurrentDictionary<string, StringBuilder> _logBuffers;
    private readonly Timer _flushTimer;
    private readonly int _maxBufferSize;

    public LogHelper(BlobContainerClient blobContainerClient, LogHelperOptions options)
    {
        _blobContainerClient = blobContainerClient;
        _logBuffers = new ConcurrentDictionary<string, StringBuilder>();
        _maxBufferSize = options.MaxBufferSize;

        // Set up a timer to flush logs periodically
        _flushTimer = new Timer(_ => FlushLogs(), null, options.FlushInterval, options.FlushInterval);
    }

    public void AppendLog(string country, string message)
    {
        var logKey = $"{country}/{DateTime.UtcNow:yyyy-MM-dd}.log";

        // Append log message to the buffer
        var logMessage = $"{DateTime.UtcNow:o}  {message}{Environment.NewLine}";
        var logBuffer = _logBuffers.GetOrAdd(logKey, _ => new StringBuilder());
        lock (logBuffer)
        {
            logBuffer.Append(logMessage);

            // Flush if the buffer size exceeds the maximum allowed size
            if (logBuffer.Length >= _maxBufferSize)
            {
                FlushLog(logKey, logBuffer);
            }
        }
    }

    private void FlushLogs()
    {
        foreach (var key in _logBuffers.Keys)
        {
            if (_logBuffers.TryRemove(key, out var logBuffer))
            {
                FlushLog(key, logBuffer);
            }
        }
    }

    private void FlushLog(string logKey, StringBuilder logBuffer)
    {
        try
        {
            var appendBlobClient = _blobContainerClient.GetAppendBlobClient(logKey);

            // Ensure the blob exists
            if (!appendBlobClient.Exists())
            {
                appendBlobClient.Create();
            }

            // Write the buffer to the blob
            var logData = Encoding.UTF8.GetBytes(logBuffer.ToString());
            using var stream = new MemoryStream(logData);
            appendBlobClient.AppendBlock(stream);

            Console.WriteLine($"Flushed log for {logKey}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error flushing log for {logKey}: {ex.Message}");
        }
    }

    public void Dispose()
    {
        // Dispose the timer and flush remaining logs
        _flushTimer.Dispose();
        FlushLogs();
    }
}

public class LogHelperOptions
{
    public TimeSpan FlushInterval { get; set; } = TimeSpan.FromMinutes(1); // Default to 1 minute
    public int MaxBufferSize { get; set; } = 1024 * 1024; // Default to 1 MB
}

