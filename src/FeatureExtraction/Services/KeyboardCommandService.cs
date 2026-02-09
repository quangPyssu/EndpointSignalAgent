using System.Globalization;
using System.Text;
using EndpointSignalAgent.FeatureExtraction.Contracts;
using EndpointSignalAgent.FeatureExtraction.Storage;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace EndpointSignalAgent.FeatureExtraction.Services;

/// <summary>
/// Keyboard Command Service - monitors keyboard input for administrative commands
/// such as exporting data and clearing the database.
/// </summary>
public sealed class KeyboardCommandService : BackgroundService
{
    private readonly ILogger<KeyboardCommandService> _logger;
    private readonly IFeatureStore _featureStore;

    public KeyboardCommandService(
        ILogger<KeyboardCommandService> logger,
        IFeatureStore featureStore)
    {
        _logger = logger;
        _featureStore = featureStore;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Keyboard command service started");

        await MonitorKeyboardAsync(stoppingToken);
    }

    private async Task MonitorKeyboardAsync(CancellationToken ct)
    {
        await Task.Run(async () =>
        {
            try
            {
                _logger.LogInformation("Keyboard monitoring started.");
                Console.WriteLine("\n[Commands]");
                Console.WriteLine("  Ctrl+P       - Export unsent feature data to CSV");
                Console.WriteLine("  Ctrl+O       - Export all feature data to CSV");
                Console.WriteLine("  Ctrl+Shift+X - Clear all feature data from database\n");

                while (!ct.IsCancellationRequested)
                {
                    if (Console.KeyAvailable)
                    {
                        var keyInfo = Console.ReadKey(intercept: true);

                        // Check for Ctrl+P (export unsent)
                        if (keyInfo.Key == ConsoleKey.P && keyInfo.Modifiers.HasFlag(ConsoleModifiers.Control))
                        {
                            _logger.LogInformation("Ctrl+P detected - exporting unsent feature data...");
                            await ExportFeatureDataAsync(false, ct);
                        }
                        // Check for Ctrl+O (export all)
                        else if (keyInfo.Key == ConsoleKey.O && keyInfo.Modifiers.HasFlag(ConsoleModifiers.Control))
                        {
                            _logger.LogInformation("Ctrl+O detected - exporting all feature data...");
                            await ExportFeatureDataAsync(true, ct);
                        }
                        // Check for Ctrl+Shift+X (clear database)
                        else if (keyInfo.Key == ConsoleKey.X && 
                                 keyInfo.Modifiers.HasFlag(ConsoleModifiers.Control) && 
                                 keyInfo.Modifiers.HasFlag(ConsoleModifiers.Shift))
                        {
                            _logger.LogInformation("Ctrl+Shift+X detected - clearing database...");
                            await ClearDatabaseAsync(ct);
                        }
                    }

                    await Task.Delay(100, ct); // Check every 100ms
                }
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Keyboard monitoring stopped");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in keyboard monitoring");
            }
        }, ct);
    }

    private async Task ExportFeatureDataAsync(bool exportAll, CancellationToken ct)
    {
        try
        {
            List<FeatureRow> rows;
            string exportType;

            if (exportAll)
            {
                // Get all feature rows
                rows = await _featureStore.GetAllAsync(limit: 10000, ct);
                exportType = "all";
            }
            else
            {
                // Get only unsent feature rows
                rows = await _featureStore.GetUnsentAsync(limit: 1000, ct);
                exportType = "unsent";
            }

            if (rows.Count == 0)
            {
                _logger.LogInformation("No {Type} feature data to export", exportType);
                Console.WriteLine($"\n[Export] No {exportType} feature data available to export.");
                return;
            }

            // Create export directory if it doesn't exist
            var exportDir = Path.Combine(Directory.GetCurrentDirectory(), "exports");
            Directory.CreateDirectory(exportDir);

            // Generate filename with timestamp
            var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
            var filename = Path.Combine(exportDir, $"features_{exportType}_{timestamp}.csv");

            // Write CSV file
            await WriteCsvAsync(filename, rows, ct);

            _logger.LogInformation("Exported {Count} {Type} feature rows to {Filename}", rows.Count, exportType, filename);
            Console.WriteLine($"\n[Export] Successfully exported {rows.Count} {exportType} feature rows to:\n  {filename}\n");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to export feature data");
            Console.WriteLine($"\n[Export] Error: {ex.Message}\n");
        }
    }

    private async Task WriteCsvAsync(string filename, List<FeatureRow> rows, CancellationToken ct)
    {
        using var writer = new StreamWriter(filename, false, Encoding.UTF8);

        // Get all unique feature keys from all rows
        var allFeatureKeys = rows
            .SelectMany(r => r.Features.Keys)
            .Distinct()
            .OrderBy(k => k)
            .ToList();

        // Write header
        var header = new List<string>
        {
            "Id",
            "DeviceId",
            "WindowSec",
            "WindowStartTs",
            "FeatureVersion",
            "SentFlag",
            "SentAt"
        };
        header.AddRange(allFeatureKeys);
        await writer.WriteLineAsync(string.Join(",", header.Select(EscapeCsv)));

        // Write data rows
        foreach (var row in rows)
        {
            var values = new List<string>
            {
                row.Id.ToString(CultureInfo.InvariantCulture),
                EscapeCsv(row.DeviceId),
                row.WindowSec.ToString(CultureInfo.InvariantCulture),
                row.WindowStartTs.ToString("O", CultureInfo.InvariantCulture),
                EscapeCsv(row.FeatureVersion),
                row.SentFlag.ToString(),
                row.SentAt?.ToString("O", CultureInfo.InvariantCulture) ?? ""
            };

            // Add feature values in the same order as header
            foreach (var key in allFeatureKeys)
            {
                if (row.Features.TryGetValue(key, out var value))
                {
                    values.Add(EscapeCsv(value?.ToString() ?? ""));
                }
                else
                {
                    values.Add("");
                }
            }

            await writer.WriteLineAsync(string.Join(",", values));
        }

        await writer.FlushAsync();
    }

    private static string EscapeCsv(string value)
    {
        if (string.IsNullOrEmpty(value))
            return "";

        // Escape quotes and wrap in quotes if needed
        if (value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r'))
        {
            return $"\"{value.Replace("\"", "\"\"")}\"";
        }

        return value;
    }

    private async Task ClearDatabaseAsync(CancellationToken ct)
    {
        try
        {
            Console.WriteLine("\n[Clear Database] Are you sure you want to delete ALL feature data? (yes/no): ");
            var confirmation = Console.ReadLine()?.Trim().ToLowerInvariant();

            if (confirmation != "yes")
            {
                _logger.LogInformation("Database clear operation cancelled by user");
                Console.WriteLine("[Clear Database] Operation cancelled.\n");
                return;
            }

            var deletedCount = await _featureStore.ClearAllAsync(ct);

            _logger.LogWarning("Database cleared - deleted {Count} feature rows", deletedCount);
            Console.WriteLine($"\n[Clear Database] Successfully deleted {deletedCount} feature rows.\n");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to clear database");
            Console.WriteLine($"\n[Clear Database] Error: {ex.Message}\n");
        }
    }
}
