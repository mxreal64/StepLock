using System.Data;
using System.Text.Json;
using DeterministicProxy.Core.Abstractions;
using DeterministicProxy.Core.Cryptography;
using DeterministicProxy.Core.Models;
using Microsoft.Data.Sqlite;

namespace DeterministicProxy.Storage.Sqlite;

public sealed class SqliteExecutionStore : IExecutionStore, IAsyncDisposable
{
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
            walCmd.CommandText = "PRAGMA journal_mode = WAL; PRAGMA synchronous = NORMAL; PRAGMA temp_store = MEMORY;";
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
            readWalCmd.CommandText = "PRAGMA synchronous = NORMAL;";
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
            PopulateFrameParameters(cmd, frame);
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

            foreach (var frame in frames)
            {
                cmd.Parameters.Clear();
                PopulateFrameParameters(cmd, frame);
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
        cmd.CommandText = "SELECT * FROM execution_frames WHERE session_id = @session_id AND branch_id = @branch_id AND step_index = @step_index LIMIT 1;";
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
        cmd.CommandText = "SELECT * FROM execution_frames WHERE frame_hash = @frame_hash LIMIT 1;";
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
        cmd.CommandText = "SELECT * FROM execution_frames WHERE session_id = @session_id AND branch_id = @branch_id ORDER BY step_index ASC;";
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
                JsonSerializer.Deserialize<Dictionary<string, string>>(reader.GetString(4)) ?? new()
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
                JsonSerializer.Deserialize<Dictionary<string, string>>(reader.GetString(4)) ?? new()
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
            cmd.Parameters.AddWithValue("@metadata_json", JsonSerializer.Serialize(session.Metadata));

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

        int maxLen = Math.Max(baseFrames.Count, targetFrames.Count);
        int? divergenceStep = null;
        int identicalCount = 0;
        var stepDiffs = new List<StepDiff>();

        for (int i = 0; i < maxLen; i++)
        {
            var baseF = i < baseFrames.Count ? baseFrames[i] : null;
            var targetF = i < targetFrames.Count ? targetFrames[i] : null;

            bool isMatch = baseF != null && targetF != null && baseF.FrameHash == targetF.FrameHash;
            if (isMatch)
            {
                identicalCount++;
            }
            else if (divergenceStep == null)
            {
                divergenceStep = i;
            }

            stepDiffs.Add(new StepDiff(
                StepIndex: i,
                BaseFrameHash: baseF?.FrameHash,
                TargetFrameHash: targetF?.FrameHash,
                IsMatch: isMatch,
                BaseTargetUri: baseF?.TargetUri,
                TargetTargetUri: targetF?.TargetUri,
                StatusCodeChanged: baseF?.ResponseStatusCode != targetF?.ResponseStatusCode
            ));
        }

        return new BranchDiff(sessionId, baseBranchId, targetBranchId, divergenceStep, identicalCount, stepDiffs);
    }

    public async ValueTask<bool> VerifyDagIntegrityAsync(string sessionId, string branchId, CancellationToken ct = default)
    {
        var frames = await GetExecutionHistoryAsync(sessionId, branchId, ct);
        string? expectedParentHash = null;

        for (int i = 0; i < frames.Count; i++)
        {
            var frame = frames[i];
            if (frame.ParentStepHash != expectedParentHash)
                return false;

            var computedHash = FrameHasher.Instance.ComputeFrameHash(
                frame.ParentStepHash,
                frame.HttpMethod,
                frame.TargetUri,
                frame.RequestBodyHash,
                frame.StepIndex);

            if (computedHash != frame.FrameHash)
                return false;

            expectedParentHash = frame.FrameHash;
        }

        return true;
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

    private static void PopulateFrameParameters(SqliteCommand cmd, ExecutionFrame frame)
    {
        cmd.Parameters.AddWithValue("@frame_hash", frame.FrameHash);
        cmd.Parameters.AddWithValue("@session_id", frame.SessionId);
        cmd.Parameters.AddWithValue("@branch_id", frame.BranchId);
        cmd.Parameters.AddWithValue("@step_index", frame.StepIndex);
        cmd.Parameters.AddWithValue("@step_id", frame.StepId);
        cmd.Parameters.AddWithValue("@parent_step_hash", (object?)frame.ParentStepHash ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@http_method", frame.HttpMethod);
        cmd.Parameters.AddWithValue("@target_uri", frame.TargetUri);
        cmd.Parameters.AddWithValue("@request_headers_json", JsonSerializer.Serialize(frame.RequestHeaders));
        cmd.Parameters.AddWithValue("@request_body_hash", frame.RequestBodyHash);
        cmd.Parameters.AddWithValue("@request_body_canonical", (object?)frame.RequestBodyCanonical ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@response_status", frame.ResponseStatusCode);
        cmd.Parameters.AddWithValue("@response_headers_json", JsonSerializer.Serialize(frame.ResponseHeaders));
        cmd.Parameters.AddWithValue("@chunks_json", JsonSerializer.Serialize(frame.Chunks));
        cmd.Parameters.AddWithValue("@ws_frames_json", frame.WebSocketFrames is null ? (object)DBNull.Value : JsonSerializer.Serialize(frame.WebSocketFrames));
        cmd.Parameters.AddWithValue("@side_effect_type", (int)frame.SideEffectType);
        cmd.Parameters.AddWithValue("@duration_ms", frame.DurationMs);
        cmd.Parameters.AddWithValue("@ttft_ms", (object?)frame.TimeToFirstTokenMs ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@created_at_utc", frame.CreatedAtUtc.ToString("O"));
    }

    private static ExecutionFrame MapFrame(SqliteDataReader reader)
    {
        return new ExecutionFrame(
            SessionId: reader.GetString(reader.GetOrdinal("session_id")),
            BranchId: reader.GetString(reader.GetOrdinal("branch_id")),
            StepIndex: reader.GetInt32(reader.GetOrdinal("step_index")),
            StepId: reader.GetString(reader.GetOrdinal("step_id")),
            ParentStepHash: reader.IsDBNull(reader.GetOrdinal("parent_step_hash")) ? null : reader.GetString(reader.GetOrdinal("parent_step_hash")),
            FrameHash: reader.GetString(reader.GetOrdinal("frame_hash")),
            HttpMethod: reader.GetString(reader.GetOrdinal("http_method")),
            TargetUri: reader.GetString(reader.GetOrdinal("target_uri")),
            RequestHeaders: JsonSerializer.Deserialize<Dictionary<string, string>>(reader.GetString(reader.GetOrdinal("request_headers_json"))) ?? new(),
            RequestBodyHash: reader.GetString(reader.GetOrdinal("request_body_hash")),
            RequestBodyCanonical: reader.IsDBNull(reader.GetOrdinal("request_body_canonical")) ? null : (byte[])reader["request_body_canonical"],
            ResponseStatusCode: reader.GetInt32(reader.GetOrdinal("response_status")),
            ResponseHeaders: JsonSerializer.Deserialize<Dictionary<string, string>>(reader.GetString(reader.GetOrdinal("response_headers_json"))) ?? new(),
            Chunks: JsonSerializer.Deserialize<List<StreamChunk>>(reader.GetString(reader.GetOrdinal("chunks_json"))) ?? new(),
            WebSocketFrames: reader.IsDBNull(reader.GetOrdinal("ws_frames_json")) ? null : JsonSerializer.Deserialize<List<WebSocketCapturedFrame>>(reader.GetString(reader.GetOrdinal("ws_frames_json"))),
            SideEffectType: (SideEffectType)reader.GetInt32(reader.GetOrdinal("side_effect_type")),
            DurationMs: reader.GetInt64(reader.GetOrdinal("duration_ms")),
            TimeToFirstTokenMs: reader.IsDBNull(reader.GetOrdinal("ttft_ms")) ? null : reader.GetInt64(reader.GetOrdinal("ttft_ms")),
            CreatedAtUtc: DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("created_at_utc")))
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
