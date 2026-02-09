using EndpointSignalAgent.FeatureExtraction.Contracts;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace EndpointSignalAgent.FeatureExtraction.Storage;

public interface IFeatureStore
{
    /// <summary>
    /// Store a feature row to persistent storage
    /// </summary>
    Task<long> StoreAsync(FeatureRow featureRow, CancellationToken ct = default);

    /// <summary>
    /// Get unsent feature rows (sent_flag = 0) up to a limit
    /// </summary>
    Task<List<FeatureRow>> GetUnsentAsync(int limit = 100, CancellationToken ct = default);

    /// <summary>
    /// Mark feature rows as sent
    /// </summary>
    Task MarkAsSentAsync(IEnumerable<long> ids, CancellationToken ct = default);

    /// <summary>
    /// Get feature rows by ID
    /// </summary>
    Task<List<FeatureRow>> GetByIdsAsync(IEnumerable<long> ids, CancellationToken ct = default);

    /// <summary>
    /// Get feature rows within a time range
    /// </summary>
    Task<List<FeatureRow>> GetRangeAsync(DateTimeOffset start, DateTimeOffset end, CancellationToken ct = default);

    /// <summary>
    /// Get the latest N feature rows
    /// </summary>
    Task<List<FeatureRow>> GetLatestAsync(int count, CancellationToken ct = default);

    /// <summary>
    /// Delete feature rows older than the specified date
    /// </summary>
    Task DeleteOlderThanAsync(DateTimeOffset cutoff, CancellationToken ct = default);

    /// <summary>
    /// Get all feature rows (both sent and unsent)
    /// </summary>
    Task<List<FeatureRow>> GetAllAsync(int limit = 10000, CancellationToken ct = default);

    /// <summary>
    /// Clear all feature rows from the database
    /// </summary>
    Task<int> ClearAllAsync(CancellationToken ct = default);
}

/// <summary>
/// SQLite-based storage for feature rows.
/// Provides persistent storage with upload tracking.
/// </summary>
public sealed class FeatureStore : IFeatureStore, IDisposable
{
    private readonly string _dbPath;
    private readonly ILogger<FeatureStore> _logger;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private bool _initialized;

    private const string CurrentFeatureVersion = "1.0";

    public FeatureStore(ILogger<FeatureStore> logger)
    {
        _logger = logger;
        
        // Store in spool directory
        var spoolDir = Path.Combine(Directory.GetCurrentDirectory(), "spool");
        Directory.CreateDirectory(spoolDir);
        
        _dbPath = Path.Combine(spoolDir, "features.db");
        
        _logger.LogInformation("FeatureStore initialized with database: {DbPath}", _dbPath);
    }

    private async Task EnsureInitializedAsync(CancellationToken ct = default)
    {
        if (_initialized) return;

        await _initLock.WaitAsync(ct);
        try
        {
            if (_initialized) return;

            using var connection = new SqliteConnection($"Data Source={_dbPath}");
            await connection.OpenAsync(ct);

            var createTableSql = @"
                CREATE TABLE IF NOT EXISTS feature_rows (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    device_id TEXT NOT NULL,
                    window_sec INTEGER NOT NULL,
                    window_start_ts TEXT NOT NULL,
                    feature_version TEXT NOT NULL,
                    features_json TEXT NOT NULL,
                    sent_flag INTEGER NOT NULL DEFAULT 0,
                    sent_at TEXT NULL,
                    created_at TEXT NOT NULL DEFAULT (datetime('now'))
                );

                CREATE INDEX IF NOT EXISTS idx_sent_flag ON feature_rows(sent_flag);
                CREATE INDEX IF NOT EXISTS idx_window_start ON feature_rows(window_start_ts);
                CREATE INDEX IF NOT EXISTS idx_device_id ON feature_rows(device_id);
            ";

            using var command = connection.CreateCommand();
            command.CommandText = createTableSql;
            await command.ExecuteNonQueryAsync(ct);

            _initialized = true;
            _logger.LogInformation("FeatureStore database schema initialized");
        }
        finally
        {
            _initLock.Release();
        }
    }

    public async Task<long> StoreAsync(FeatureRow featureRow, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);

        try
        {
            using var connection = new SqliteConnection($"Data Source={_dbPath}");
            await connection.OpenAsync(ct);

            var sql = @"
                INSERT INTO feature_rows (device_id, window_sec, window_start_ts, feature_version, features_json, sent_flag, sent_at)
                VALUES (@device_id, @window_sec, @window_start_ts, @feature_version, @features_json, @sent_flag, @sent_at);
                SELECT last_insert_rowid();
            ";

            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.Parameters.AddWithValue("@device_id", featureRow.DeviceId);
            command.Parameters.AddWithValue("@window_sec", featureRow.WindowSec);
            command.Parameters.AddWithValue("@window_start_ts", featureRow.WindowStartTs.ToString("O")); // ISO 8601
            command.Parameters.AddWithValue("@feature_version", featureRow.FeatureVersion);
            command.Parameters.AddWithValue("@features_json", JsonSerializer.Serialize(featureRow.Features));
            command.Parameters.AddWithValue("@sent_flag", featureRow.SentFlag ? 1 : 0);
            command.Parameters.AddWithValue("@sent_at", featureRow.SentAt?.ToString("O") ?? (object)DBNull.Value);

            var id = (long)(await command.ExecuteScalarAsync(ct) ?? 0L);

            _logger.LogDebug("Stored feature row {Id} for device {DeviceId}, window {WindowStart}",
                id, featureRow.DeviceId, featureRow.WindowStartTs);

            return id;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to store feature row");
            throw;
        }
    }

    public async Task<List<FeatureRow>> GetUnsentAsync(int limit = 100, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);

        try
        {
            using var connection = new SqliteConnection($"Data Source={_dbPath}");
            await connection.OpenAsync(ct);

            var sql = @"
                SELECT id, device_id, window_sec, window_start_ts, feature_version, features_json, sent_flag, sent_at
                FROM feature_rows
                WHERE sent_flag = 0
                ORDER BY window_start_ts ASC
                LIMIT @limit
            ";

            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.Parameters.AddWithValue("@limit", limit);

            var rows = new List<FeatureRow>();
            using var reader = await command.ExecuteReaderAsync(ct);

            while (await reader.ReadAsync(ct))
            {
                rows.Add(ReadFeatureRow(reader));
            }

            _logger.LogDebug("Retrieved {Count} unsent feature rows", rows.Count);
            return rows;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to retrieve unsent feature rows");
            return new List<FeatureRow>();
        }
    }

    public async Task MarkAsSentAsync(IEnumerable<long> ids, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);

        var idList = ids.ToList();
        if (idList.Count == 0) return;

        try
        {
            using var connection = new SqliteConnection($"Data Source={_dbPath}");
            await connection.OpenAsync(ct);

            var placeholders = string.Join(",", idList.Select((_, i) => $"@id{i}"));
            var sql = $@"
                UPDATE feature_rows
                SET sent_flag = 1, sent_at = @sent_at
                WHERE id IN ({placeholders})
            ";

            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.Parameters.AddWithValue("@sent_at", DateTimeOffset.UtcNow.ToString("O"));

            for (int i = 0; i < idList.Count; i++)
            {
                command.Parameters.AddWithValue($"@id{i}", idList[i]);
            }

            var affected = await command.ExecuteNonQueryAsync(ct);
            _logger.LogInformation("Marked {Count} feature rows as sent", affected);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to mark feature rows as sent");
            throw;
        }
    }

    public async Task<List<FeatureRow>> GetByIdsAsync(IEnumerable<long> ids, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);

        var idList = ids.ToList();
        if (idList.Count == 0) return new List<FeatureRow>();

        try
        {
            using var connection = new SqliteConnection($"Data Source={_dbPath}");
            await connection.OpenAsync(ct);

            var placeholders = string.Join(",", idList.Select((_, i) => $"@id{i}"));
            var sql = $@"
                SELECT id, device_id, window_sec, window_start_ts, feature_version, features_json, sent_flag, sent_at
                FROM feature_rows
                WHERE id IN ({placeholders})
            ";

            using var command = connection.CreateCommand();
            command.CommandText = sql;

            for (int i = 0; i < idList.Count; i++)
            {
                command.Parameters.AddWithValue($"@id{i}", idList[i]);
            }

            var rows = new List<FeatureRow>();
            using var reader = await command.ExecuteReaderAsync(ct);

            while (await reader.ReadAsync(ct))
            {
                rows.Add(ReadFeatureRow(reader));
            }

            return rows;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to retrieve feature rows by IDs");
            return new List<FeatureRow>();
        }
    }

    public async Task<List<FeatureRow>> GetRangeAsync(DateTimeOffset start, DateTimeOffset end, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);

        try
        {
            using var connection = new SqliteConnection($"Data Source={_dbPath}");
            await connection.OpenAsync(ct);

            var sql = @"
                SELECT id, device_id, window_sec, window_start_ts, feature_version, features_json, sent_flag, sent_at
                FROM feature_rows
                WHERE window_start_ts >= @start AND window_start_ts <= @end
                ORDER BY window_start_ts ASC
            ";

            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.Parameters.AddWithValue("@start", start.ToString("O"));
            command.Parameters.AddWithValue("@end", end.ToString("O"));

            var rows = new List<FeatureRow>();
            using var reader = await command.ExecuteReaderAsync(ct);

            while (await reader.ReadAsync(ct))
            {
                rows.Add(ReadFeatureRow(reader));
            }

            return rows;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to retrieve feature range");
            return new List<FeatureRow>();
        }
    }

    public async Task<List<FeatureRow>> GetLatestAsync(int count, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);

        try
        {
            using var connection = new SqliteConnection($"Data Source={_dbPath}");
            await connection.OpenAsync(ct);

            var sql = @"
                SELECT id, device_id, window_sec, window_start_ts, feature_version, features_json, sent_flag, sent_at
                FROM feature_rows
                ORDER BY window_start_ts DESC
                LIMIT @count
            ";

            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.Parameters.AddWithValue("@count", count);

            var rows = new List<FeatureRow>();
            using var reader = await command.ExecuteReaderAsync(ct);

            while (await reader.ReadAsync(ct))
            {
                rows.Add(ReadFeatureRow(reader));
            }

            rows.Reverse(); // Return in chronological order
            return rows;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to retrieve latest features");
            return new List<FeatureRow>();
        }
    }

    public async Task DeleteOlderThanAsync(DateTimeOffset cutoff, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);

        try
        {
            using var connection = new SqliteConnection($"Data Source={_dbPath}");
            await connection.OpenAsync(ct);

            var sql = @"
                DELETE FROM feature_rows
                WHERE window_start_ts < @cutoff AND sent_flag = 1
            ";

            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.Parameters.AddWithValue("@cutoff", cutoff.ToString("O"));

            var deleted = await command.ExecuteNonQueryAsync(ct);
            
            if (deleted > 0)
            {
                _logger.LogInformation("Deleted {Count} old feature rows (cutoff: {Cutoff})", deleted, cutoff);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete old feature rows");
        }
    }

    public async Task<List<FeatureRow>> GetAllAsync(int limit = 10000, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);

        try
        {
            using var connection = new SqliteConnection($"Data Source={_dbPath}");
            await connection.OpenAsync(ct);

            var sql = @"
                SELECT id, device_id, window_sec, window_start_ts, feature_version, features_json, sent_flag, sent_at
                FROM feature_rows
                ORDER BY window_start_ts ASC
                LIMIT @limit
            ";

            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.Parameters.AddWithValue("@limit", limit);

            var rows = new List<FeatureRow>();
            using var reader = await command.ExecuteReaderAsync(ct);

            while (await reader.ReadAsync(ct))
            {
                rows.Add(ReadFeatureRow(reader));
            }

            _logger.LogDebug("Retrieved {Count} total feature rows", rows.Count);
            return rows;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to retrieve all feature rows");
            return new List<FeatureRow>();
        }
    }

    public async Task<int> ClearAllAsync(CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);

        try
        {
            using var connection = new SqliteConnection($"Data Source={_dbPath}");
            await connection.OpenAsync(ct);

            var sql = "DELETE FROM feature_rows";

            using var command = connection.CreateCommand();
            command.CommandText = sql;

            var deleted = await command.ExecuteNonQueryAsync(ct);
            
            _logger.LogWarning("Cleared all feature rows from database (deleted {Count} rows)", deleted);
            return deleted;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to clear all feature rows");
            throw;
        }
    }

    private static FeatureRow ReadFeatureRow(SqliteDataReader reader)
    {
        var id = reader.GetInt64(0);
        var deviceId = reader.GetString(1);
        var windowSec = reader.GetInt32(2);
        var windowStartTs = DateTimeOffset.Parse(reader.GetString(3));
        var featureVersion = reader.GetString(4);
        var featuresJson = reader.GetString(5);
        var sentFlag = reader.GetInt32(6) == 1;
        var sentAt = reader.IsDBNull(7) ? (DateTimeOffset?)null : DateTimeOffset.Parse(reader.GetString(7));

        var features = JsonSerializer.Deserialize<Dictionary<string, object>>(featuresJson) 
            ?? new Dictionary<string, object>();

        return new FeatureRow(id, deviceId, windowSec, windowStartTs, featureVersion, features, sentFlag, sentAt);
    }

    public void Dispose()
    {
        _initLock?.Dispose();
    }
}

