// ============================================================================
// File: HistoryStore.cs
// Author: Scott Sheen (with ChatGpt)
// Created: 2026-10-02
// Updated: 2026-10-02
// Purpose: Batched SQLite persistence and historical measurement retrieval.
// ============================================================================

using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace PowerMonitor;

public sealed class HistoryStore : IDisposable
{

    private readonly SqliteConnection _connection;

    private readonly List<Sample> _pending = [];

    public long SamplesWritten { get; private set; }

    public long Transactions { get; private set; }

    public HistoryStore(string path, int retentionDays)
    {
        _connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        _connection.Open();
        using var command = _connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=NORMAL;
            CREATE TABLE IF NOT EXISTS samples(timestamp TEXT PRIMARY KEY, online INTEGER, watts REAL, json TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS alerts(id INTEGER PRIMARY KEY, timestamp TEXT, severity TEXT, watts REAL, details TEXT);
            CREATE TABLE IF NOT EXISTS actions(id INTEGER PRIMARY KEY, timestamp TEXT, json TEXT);
            CREATE INDEX IF NOT EXISTS idx_samples_online ON samples(online,timestamp);
            """;
        command.ExecuteNonQuery();
        command.CommandText = "DELETE FROM samples WHERE timestamp < $cut; DELETE FROM alerts WHERE timestamp < $cut; DELETE FROM actions WHERE timestamp < $cut;";
        command.Parameters.AddWithValue("$cut", DateTimeOffset.UtcNow.AddDays(-retentionDays).ToString("O"));
        command.ExecuteNonQuery();
    }

    public void Add(Sample sample)
    {
        _pending.Add(sample);
        if (_pending.Count >= 3)
            Flush();
    }

    public void Flush()
    {
        if (_pending.Count == 0)
            return;
        using var transaction = _connection.BeginTransaction();
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT OR REPLACE INTO samples VALUES($time,$online,$watts,$json)";
        foreach (var sample in _pending)
        {
            command.Parameters.Clear();
            command.Parameters.AddWithValue("$time", sample.Time.ToString("O"));
            command.Parameters.AddWithValue("$online", sample.Online.HasValue ? sample.Online.Value ? 1 : 0 : DBNull.Value);
            command.Parameters.AddWithValue("$watts", (object? )sample.Watts ?? DBNull.Value);
            command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(sample));
            command.ExecuteNonQuery();
        }

        transaction.Commit();
        SamplesWritten += _pending.Count;
        Transactions++;
        _pending.Clear();
    }

    public List<Sample> Read(DateTimeOffset since, int limit = 5000)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT json FROM samples WHERE timestamp >= $since ORDER BY timestamp DESC LIMIT $limit";
        command.Parameters.AddWithValue("$since", since.ToString("O"));
        command.Parameters.AddWithValue("$limit", limit);
        using var reader = command.ExecuteReader();
        var list = new List<Sample>();
        while (reader.Read())
        {
            var sample = JsonSerializer.Deserialize<Sample>(reader.GetString(0));
            if (sample != null)
                list.Add(sample);
        }

        list.Reverse();
        return list;
    }

    public void AddAlert(AlertEvent alert)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "INSERT INTO alerts(timestamp,severity,watts,details) VALUES($t,$s,$w,$d)";
        command.Parameters.AddWithValue("$t", alert.Time.ToString("O"));
        command.Parameters.AddWithValue("$s", alert.Severity);
        command.Parameters.AddWithValue("$w", alert.Watts);
        command.Parameters.AddWithValue("$d", alert.Details);
        command.ExecuteNonQuery();
    }

    public List<AlertEvent> ReadAlerts()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT timestamp,severity,watts,details FROM alerts ORDER BY id DESC LIMIT 200";
        using var reader = command.ExecuteReader();
        var list = new List<AlertEvent>();
        while (reader.Read())
            list.Add(new(DateTimeOffset.Parse(reader.GetString(0)), reader.GetString(1), reader.GetDouble(2), reader.GetString(3)));
        return list;
    }

    public void Dispose()
    {
        // Release owned native handles and flush pending work when the component shuts down.
        try
        {
            Flush();
        }
        finally
        {
            _connection.Dispose();
        }
    }
}
