using System.Data;
using System.Text.Json;
using DeterministicProxy.Core.Abstractions;
using DeterministicProxy.Core.Models;
using Microsoft.Data.Sqlite;

namespace DeterministicProxy.Storage.Sqlite;

public sealed class SqliteExecutionStore : IExecutionStore, IAsyncDisposable
{
    private const string SelectFrameColumns = "session_id, branch_id, step_index, step_id, parent_step_hash, frame_hash, http_method, target_uri, request_headers_json, request_body_hash, request_body_canonical, response_status, response_headers_json, chunks_json, ws_frames_json, side_effect_type, duration_ms, ttft_ms, created_at_utc";

    private readonly string _connectionString;
    private readonly SqliteConnection _connection;
    private readonly SqliteConnection _readConnection;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private volatile bool _initialized;

    public SqliteExecutionStore(string dbPath = "execution_traces.db")
    {
        _connectionString = $"Data Source={dbPath};Mode=ReadWriteCreate;Cache=Shared";
        _connection = new SqliteConnection(_connectionString);
        _readConnection = new SqliteConnection(_connectionString);
    }

    public async ValueTask InitializeAsync(CancellationToken ct = default)
    {
        if (_initialized) return;

        await _lock.WaitAsync(ct);
        try
        {
            if (_initialized) return;
            await _connection.OpenAsync(ct);

            // High performance SQLite tuning (WAL mode)
            using var walCmd = _connection.CreateCommand();
            walCmd.CommandText = "PRAGMA journal_mode = WAL; PRAGMA synchronous = NORMAL; PRAGMA temp_store = MEMORY; PRAGMA cache_size = -64000; PRAGMA mmap_size = 268435456; PRAGMA busy_timeout = 5000;";
            await walCmd.ExecuteNonQueryAsync(ct);

            using var cmd = _connection.CreateCommand();
            cmd.CommandText = @"
                CREATE TABLE IF NOT EXISTS sessions (
                    session_id TEXT PRIMARY KEY,
                    root_branch_id TEXT NOT NULL,
                    active_branch_id TEXT NOT NULL,
                    created_at_utc TEXT NOT NULL,
                    metadata_json TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS execution_frames (
                    frame_hash TEXT PRIMARY KEY,
                    session_id TEXT NOT NULL,
                    branch_id TEXT NOT NULL,
                    step_index INTEGER NOT NULL,
                    step_id TEXT NOT NULL,
                    parent_step_hash TEXT,
                    http_method TEXT NOT NULL,
                    target_uri TEXT NOT NULL,
                    request_headers_json TEXT NOT NULL,
                    request_body_hash TEXT NOT NULL,
                    request_body_canonical BLOB,
                    response_status INTEGER NOT NULL,
                    response_headers_json TEXT NOT NULL,
                    chunks_json TEXT NOT NULL,
                    ws_frames_json TEXT,
                    side_effect_type INTEGER NOT NULL,
                    duration_ms INTEGER NOT NULL DEFAULT 0,
                    ttft_ms INTEGER,
                    created_at_utc TEXT NOT NULL,
                    UNIQUE(session_id, branch_id, step_index)
                );

                CREATE INDEX IF NOT EXISTS idx_frames_session_branch ON execution_frames (session_id, branch_id, step_index);
                CREATE INDEX IF NOT EXISTS idx_frames_hash ON execution_frames (frame_hash);
            ";
            await cmd.ExecuteNonQueryAsync(ct);

            await _readConnection.OpenAsync(ct);
            using var readWalCmd = _readConnection.CreateCommand();
            readWalCmd.CommandText = "PRAGMA synchronous = NORMAL; PRAGMA temp_store = MEMORY; PRAGMA cache_size = -64000; PRAGMA mmap_size = 268435456; PRAGMA busy_timeout = 5000;";
            await readWalCmd.ExecuteNonQueryAsync(ct);

            _initialized = true;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async ValueTask SaveFrameAsync(ExecutionFrame frame, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        await _lock.WaitAsync(ct);
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = GetUpsertFrameSql();
            var parameters = CreateFrameParameters(cmd);
            BindFrameParameters(parameters, frame);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async ValueTask SaveFramesBatchAsync(IReadOnlyList<ExecutionFrame> frames, CancellationToken ct = default)
    {
        if (frames.Count == 0) return;

        await InitializeAsync(ct);
        await _lock.WaitAsync(ct);
        try
        {
            using var transaction = _connection.BeginTransaction();
            using var cmd = _connection.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = GetUpsertFrameSql();
            var parameters = CreateFrameParameters(cmd);

            for (int i = 0; i < frames.Count; i++)
            {
                BindFrameParameters(parameters, frames[i]);
                await cmd.ExecuteNonQueryAsync(ct);
            }

            transaction.Commit();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async ValueTask<ExecutionFrame?> GetFrameByStepIndexAsync(string sessionId, string branchId, int stepIndex, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        using var cmd = _readConnection.CreateCommand();
        cmd.CommandText = $"SELECT {SelectFrameColumns} FROM execution_frames WHERE session_id = @session_id AND branch_id = @branch_id AND step_index = @step_index LIMIT 1;";
        cmd.Parameters.AddWithValue("@session_id", sessionId);
        cmd.Parameters.AddWithValue("@branch_id", branchId);
        cmd.Parameters.AddWithValue("@step_index", stepIndex);

        using var reader = await cmd.ExecuteReaderAsync(ct);
        if (await reader.ReadAsync(ct))
        {
            return MapFrame(reader);
        }
        return null;
    }

    public async ValueTask<ExecutionFrame?> GetFrameByHashAsync(string frameHash, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        using var cmd = _readConnection.CreateCommand();
        cmd.CommandText = $"SELECT {SelectFrameColumns} FROM execution_frames WHERE frame_hash = @frame_hash LIMIT 1;";
        cmd.Parameters.AddWithValue("@frame_hash", frameHash);

        using var reader = await cmd.ExecuteReaderAsync(ct);
        if (await reader.ReadAsync(ct))
        {
            return MapFrame(reader);
        }
        return null;
    }

    public async ValueTask<string?> GetLatestStepHashAsync(string sessionId, string branchId, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        using var cmd = _readConnection.CreateCommand();
        cmd.CommandText = "SELECT frame_hash FROM execution_frames WHERE session_id = @session_id AND branch_id = @branch_id ORDER BY step_index DESC LIMIT 1;";
        cmd.Parameters.AddWithValue("@session_id", sessionId);
        cmd.Parameters.AddWithValue("@branch_id", branchId);

        var result = await cmd.ExecuteScalarAsync(ct);
        return result?.ToString();
    }

    public async ValueTask<IReadOnlyList<ExecutionFrame>> GetExecutionHistoryAsync(string sessionId, string branchId, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        using var cmd = _readConnection.CreateCommand();
        cmd.CommandText = $"SELECT {SelectFrameColumns} FROM execution_frames WHERE session_id = @session_id AND branch_id = @branch_id ORDER BY step_index ASC;";
        cmd.Parameters.AddWithValue("@session_id", sessionId);
        cmd.Parameters.AddWithValue("@branch_id", branchId);

        var frames = new List<ExecutionFrame>();
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            frames.Add(MapFrame(reader));
        }
        return frames;
    }

    public async ValueTask<ExecutionSession?> GetSessionAsync(string sessionId, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        using var cmd = _readConnection.CreateCommand();
        cmd.CommandText = "SELECT session_id, root_branch_id, active_branch_id, created_at_utc, metadata_json FROM sessions WHERE session_id = @session_id LIMIT 1;";
        cmd.Parameters.AddWithValue("@session_id", sessionId);

        using var reader = await cmd.ExecuteReaderAsync(ct);
        if (await reader.ReadAsync(ct))
        {
            return new ExecutionSession(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                DateTimeOffset.Parse(reader.GetString(3)),
                JsonSerializer.Deserialize(reader.GetString(4), DeterministicProxyJsonContext.Default.DictionaryStringString) ?? new()
            );
        }
        return null;
    }

    public async ValueTask<IReadOnlyList<ExecutionSession>> ListSessionsAsync(CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        using var cmd = _readConnection.CreateCommand();
        cmd.CommandText = "SELECT session_id, root_branch_id, active_branch_id, created_at_utc, metadata_json FROM sessions ORDER BY created_at_utc DESC;";

        var sessions = new List<ExecutionSession>();
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            sessions.Add(new ExecutionSession(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                DateTimeOffset.Parse(reader.GetString(3)),
                JsonSerializer.Deserialize(reader.GetString(4), DeterministicProxyJsonContext.Default.DictionaryStringString) ?? new()
            ));
        }
        return sessions;
    }

    public async ValueTask SaveSessionAsync(ExecutionSession session, CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        await _lock.WaitAsync(ct);
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO sessions (session_id, root_branch_id, active_branch_id, created_at_utc, metadata_json)
                VALUES (@session_id, @root_branch_id, @active_branch_id, @created_at_utc, @metadata_json)
                ON CONFLICT(session_id) DO UPDATE SET
                    root_branch_id = excluded.root_branch_id,
                    active_branch_id = excluded.active_branch_id,
                    metadata_json = excluded.metadata_json;
            ";
            cmd.Parameters.AddWithValue("@session_id", session.SessionId);
            cmd.Parameters.AddWithValue("@root_branch_id", session.RootBranchId);
            cmd.Parameters.AddWithValue("@active_branch_id", session.ActiveBranchId);
            cmd.Parameters.AddWithValue("@created_at_utc", session.CreatedAtUtc.ToString("O"));
            cmd.Parameters.AddWithValue("@metadata_json", JsonSerializer.Serialize(session.Metadata, DeterministicProxyJsonContext.Default.DictionaryStringString));

            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async ValueTask<BranchDiff> CompareBranchesAsync(string sessionId, string baseBranchId, string targetBranchId, CancellationToken ct = default)
    {
        var baseFrames = await GetExecutionHistoryAsync(sessionId, baseBranchId, ct);
        var targetFrames = await GetExecutionHistoryAsync(sessionId, targetBranchId, ct);
        return BranchDiffer.Compute(sessionId, baseBranchId, targetBranchId, baseFrames, targetFrames);
    }

    public async ValueTask<bool> VerifyDagIntegrityAsync(string sessionId, string branchId, CancellationToken ct = default)
    {
        var frames = await GetExecutionHistoryAsync(sessionId, branchId, ct);
        return DagVerifier.Verify(frames);
    }

    private static string GetUpsertFrameSql() => @"
        INSERT INTO execution_frames (
            frame_hash, session_id, branch_id, step_index, step_id, parent_step_hash,
            http_method, target_uri, request_headers_json, request_body_hash, request_body_canonical,
            response_status, response_headers_json, chunks_json, ws_frames_json, side_effect_type,
            duration_ms, ttft_ms, created_at_utc
        ) VALUES (
            @frame_hash, @session_id, @branch_id, @step_index, @step_id, @parent_step_hash,
            @http_method, @target_uri, @request_headers_json, @request_body_hash, @request_body_canonical,
            @response_status, @response_headers_json, @chunks_json, @ws_frames_json, @side_effect_type,
            @duration_ms, @ttft_ms, @created_at_utc
        )
        ON CONFLICT(session_id, branch_id, step_index) DO UPDATE SET
            frame_hash = excluded.frame_hash,
            step_id = excluded.step_id,
            parent_step_hash = excluded.parent_step_hash,
            http_method = excluded.http_method,
            target_uri = excluded.target_uri,
            request_headers_json = excluded.request_headers_json,
            request_body_hash = excluded.request_body_hash,
            request_body_canonical = excluded.request_body_canonical,
            response_status = excluded.response_status,
            response_headers_json = excluded.response_headers_json,
            chunks_json = excluded.chunks_json,
            ws_frames_json = excluded.ws_frames_json,
            side_effect_type = excluded.side_effect_type,
            duration_ms = excluded.duration_ms,
            ttft_ms = excluded.ttft_ms,
            created_at_utc = excluded.created_at_utc;
    ";

    private static SqliteParameter[] CreateFrameParameters(SqliteCommand cmd)
    {
        return new[]
        {
            cmd.Parameters.Add("@frame_hash", SqliteType.Text),
            cmd.Parameters.Add("@session_id", SqliteType.Text),
            cmd.Parameters.Add("@branch_id", SqliteType.Text),
            cmd.Parameters.Add("@step_index", SqliteType.Integer),
            cmd.Parameters.Add("@step_id", SqliteType.Text),
            cmd.Parameters.Add("@parent_step_hash", SqliteType.Text),
            cmd.Parameters.Add("@http_method", SqliteType.Text),
            cmd.Parameters.Add("@target_uri", SqliteType.Text),
            cmd.Parameters.Add("@request_headers_json", SqliteType.Text),
            cmd.Parameters.Add("@request_body_hash", SqliteType.Text),
            cmd.Parameters.Add("@request_body_canonical", SqliteType.Blob),
            cmd.Parameters.Add("@response_status", SqliteType.Integer),
            cmd.Parameters.Add("@response_headers_json", SqliteType.Text),
            cmd.Parameters.Add("@chunks_json", SqliteType.Text),
            cmd.Parameters.Add("@ws_frames_json", SqliteType.Text),
            cmd.Parameters.Add("@side_effect_type", SqliteType.Integer),
            cmd.Parameters.Add("@duration_ms", SqliteType.Integer),
            cmd.Parameters.Add("@ttft_ms", SqliteType.Integer),
            cmd.Parameters.Add("@created_at_utc", SqliteType.Text)
        };
    }

    private static void BindFrameParameters(SqliteParameter[] parameters, ExecutionFrame frame)
    {
        parameters[0].Value = frame.FrameHash;
        parameters[1].Value = frame.SessionId;
        parameters[2].Value = frame.BranchId;
        parameters[3].Value = frame.StepIndex;
        parameters[4].Value = frame.StepId;
        parameters[5].Value = (object?)frame.ParentStepHash ?? DBNull.Value;
        parameters[6].Value = frame.HttpMethod;
        parameters[7].Value = frame.TargetUri;
        parameters[8].Value = JsonSerializer.Serialize(frame.RequestHeaders, DeterministicProxyJsonContext.Default.DictionaryStringString);
        parameters[9].Value = frame.RequestBodyHash;
        parameters[10].Value = (object?)frame.RequestBodyCanonical ?? DBNull.Value;
        parameters[11].Value = frame.ResponseStatusCode;
        parameters[12].Value = JsonSerializer.Serialize(frame.ResponseHeaders, DeterministicProxyJsonContext.Default.DictionaryStringString);
        parameters[13].Value = JsonSerializer.Serialize(frame.Chunks, DeterministicProxyJsonContext.Default.ListStreamChunk);
        parameters[14].Value = frame.WebSocketFrames is null ? (object)DBNull.Value : JsonSerializer.Serialize(frame.WebSocketFrames, DeterministicProxyJsonContext.Default.ListWebSocketCapturedFrame);
        parameters[15].Value = (int)frame.SideEffectType;
        parameters[16].Value = frame.DurationMs;
        parameters[17].Value = (object?)frame.TimeToFirstTokenMs ?? DBNull.Value;
        parameters[18].Value = frame.CreatedAtUtc.ToString("O");
    }

    private static ExecutionFrame MapFrame(SqliteDataReader reader)
    {
        return new ExecutionFrame(
            SessionId: reader.GetString(0),
            BranchId: reader.GetString(1),
            StepIndex: reader.GetInt32(2),
            StepId: reader.GetString(3),
            ParentStepHash: reader.IsDBNull(4) ? null : reader.GetString(4),
            FrameHash: reader.GetString(5),
            HttpMethod: reader.GetString(6),
            TargetUri: reader.GetString(7),
            RequestHeaders: JsonSerializer.Deserialize(reader.GetString(8), DeterministicProxyJsonContext.Default.DictionaryStringString) ?? new(),
            RequestBodyHash: reader.GetString(9),
            RequestBodyCanonical: reader.IsDBNull(10) ? null : (byte[])reader[10],
            ResponseStatusCode: reader.GetInt32(11),
            ResponseHeaders: JsonSerializer.Deserialize(reader.GetString(12), DeterministicProxyJsonContext.Default.DictionaryStringString) ?? new(),
            Chunks: JsonSerializer.Deserialize(reader.GetString(13), DeterministicProxyJsonContext.Default.ListStreamChunk) ?? new(),
            WebSocketFrames: reader.IsDBNull(14) ? null : JsonSerializer.Deserialize(reader.GetString(14), DeterministicProxyJsonContext.Default.ListWebSocketCapturedFrame),
            SideEffectType: (SideEffectType)reader.GetInt32(15),
            DurationMs: reader.GetInt64(16),
            TimeToFirstTokenMs: reader.IsDBNull(17) ? null : reader.GetInt64(17),
            CreatedAtUtc: DateTimeOffset.Parse(reader.GetString(18))
        );
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection != null)
        {
            await _connection.DisposeAsync();
        }
        if (_readConnection != null)
        {
            await _readConnection.DisposeAsync();
        }
        _lock.Dispose();
    }
}
