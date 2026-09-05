using System.IO;
using System.Threading.Channels;
using Microsoft.Data.Sqlite;
using QingSnap.App.Models;

namespace QingSnap.App.Services;

public sealed class HistoryMetadataStore : IDisposable
{
    public const int CurrentSchemaVersion = 1;

    private readonly Channel<WriteRequest> _writeQueue = Channel.CreateUnbounded<WriteRequest>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _initialization;
    private readonly Task _writer;
    private bool _disposed;

    public HistoryMetadataStore(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        Directory.CreateDirectory(dataDirectory);
        DatabasePath = Path.Combine(dataDirectory, "history-metadata.db");
        _initialization = InitializeWithRecoveryAsync();
        _writer = Task.Run(ProcessWritesAsync);
    }

    public string DatabasePath { get; }

    public void QueueUpsert(HistoryMetadata metadata) =>
        Enqueue(new WriteRequest(WriteKind.Upsert, metadata.FilePath, metadata));

    public void QueueFavorite(string filePath, bool isFavorite) =>
        Enqueue(new WriteRequest(WriteKind.Favorite, filePath, IsFavorite: isFavorite));

    public void QueueOcrText(string filePath, string text) =>
        Enqueue(new WriteRequest(WriteKind.OcrText, filePath, OcrText: text));

    public void QueueImageHash(string filePath, string imageHash) =>
        Enqueue(new WriteRequest(WriteKind.ImageHash, filePath, ImageHash: imageHash));

    public async Task AddTagsAsync(
        string filePath,
        IEnumerable<string> tags,
        CancellationToken cancellationToken = default)
    {
        var normalizedTags = tags
            .Select(NormalizeTagName)
            .Where(tag => tag.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (normalizedTags.Length == 0)
        {
            return;
        }

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Enqueue(new WriteRequest(WriteKind.AddTags, filePath, Tags: normalizedTags, Completion: completion));
        await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task RemoveTagAsync(
        string filePath,
        string tag,
        CancellationToken cancellationToken = default)
    {
        var normalizedTag = NormalizeTagName(tag);
        if (normalizedTag.Length == 0)
        {
            return;
        }

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Enqueue(new WriteRequest(WriteKind.RemoveTag, filePath, TagName: normalizedTag, Completion: completion));
        await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Enqueue(new WriteRequest(WriteKind.Delete, filePath, Completion: completion));
        await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public void QueueRemoveMissing(IReadOnlySet<string> existingPaths) =>
        Enqueue(new WriteRequest(WriteKind.RemoveMissing, ExistingPaths: existingPaths));

    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Enqueue(new WriteRequest(WriteKind.Flush, Completion: completion));
        await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyDictionary<string, HistoryMetadata>> LoadByPathsAsync(
        IEnumerable<string> filePaths,
        CancellationToken cancellationToken = default)
    {
        var normalizedPaths = filePaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (normalizedPaths.Length == 0)
        {
            return new Dictionary<string, HistoryMetadata>(StringComparer.OrdinalIgnoreCase);
        }

        await FlushAsync(cancellationToken).ConfigureAwait(false);
        await _initialization.WaitAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var result = new Dictionary<string, HistoryMetadata>(StringComparer.OrdinalIgnoreCase);
        const int batchSize = 400;
        for (var offset = 0; offset < normalizedPaths.Length; offset += batchSize)
        {
            var batch = normalizedPaths.Skip(offset).Take(batchSize).ToArray();
            await using var command = connection.CreateCommand();
            var parameters = new string[batch.Length];
            for (var index = 0; index < batch.Length; index++)
            {
                parameters[index] = $"$path{index}";
                command.Parameters.AddWithValue(parameters[index], batch[index]);
            }

            command.CommandText = $"SELECT {SelectColumns} FROM HistoryItems WHERE FilePath IN ({string.Join(',', parameters)});";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var metadata = ReadMetadata(reader);
                result[metadata.FilePath] = metadata;
            }
        }

        return result;
    }

    public async Task<int> GetSchemaVersionAsync(CancellationToken cancellationToken = default)
    {
        await _initialization.WaitAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Version FROM SchemaInfo WHERE Id = 1;";
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
    }

    public async Task<IReadOnlyList<HistoryMetadata>> LoadAllAsync(
        CancellationToken cancellationToken = default)
    {
        await FlushAsync(cancellationToken).ConfigureAwait(false);
        await _initialization.WaitAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {SelectColumns} FROM HistoryItems;";
        var result = new List<HistoryMetadata>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(ReadMetadata(reader));
        }

        return result;
    }

    public async Task<IReadOnlyList<HistoryMigrationEntry>> LoadMigrationIndexAsync(
        CancellationToken cancellationToken = default)
    {
        await FlushAsync(cancellationToken).ConfigureAwait(false);
        await _initialization.WaitAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT FilePath, FileSize, ImageHash, UpdatedAt FROM HistoryItems;";
        var result = new List<HistoryMigrationEntry>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new HistoryMigrationEntry(
                reader.GetString(0),
                reader.GetInt64(1),
                ReadNullableString(reader, 2),
                ParseDatabaseTime(reader.GetString(3))));
        }

        return result;
    }

    public async Task<HistoryQueryPage> QueryHistoryAsync(
        HistoryQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var offset = Math.Max(0, query.Offset);
        var limit = Math.Clamp(query.Limit, 1, 200);
        await FlushAsync(cancellationToken).ConfigureAwait(false);
        await _initialization.WaitAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        var where = new List<string>();
        var parameters = new List<(string Name, object Value)>();
        AddQueryConditions(query, where, parameters);
        var whereSql = where.Count == 0 ? string.Empty : "WHERE " + string.Join(" AND ", where);

        var totalCount = -1;
        long totalBytes = 0;
        if (query.IncludeStatistics)
        {
            await using var stats = connection.CreateCommand();
            stats.CommandText = $"SELECT COUNT(*), COALESCE(SUM(h.FileSize), 0) FROM HistoryItems h {whereSql};";
            AddParameters(stats, parameters);
            await using var reader = await stats.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            totalCount = reader.GetInt32(0);
            totalBytes = reader.GetInt64(1);
        }

        var items = new List<HistorySummary>(limit);
        await using (var command = connection.CreateCommand())
        {
            var order = query.SortOrder == HistorySortOrder.OldestFirst ? "ASC" : "DESC";
            command.CommandText = $"""
                SELECT h.Id, h.FilePath, h.CaptureTime, h.Width, h.Height, h.FileSize, h.Format,
                       h.IsLongCapture, h.IsFavorite, h.OcrIndexStatus, h.SourceProcess,
                       h.SourceWindowTitle, h.ImageHash,
                       COALESCE((
                           SELECT group_concat(tag.Name, char(31))
                           FROM HistoryItemTags hit
                           INNER JOIN Tags tag ON tag.Id = hit.TagId
                           WHERE hit.HistoryItemId = h.Id
                           ORDER BY tag.Name COLLATE NOCASE
                       ), '')
                FROM HistoryItems h
                {whereSql}
                ORDER BY h.CaptureTime {order}, h.Id {order}
                LIMIT $limit OFFSET $offset;
                """;
            AddParameters(command, parameters);
            command.Parameters.AddWithValue("$limit", limit);
            command.Parameters.AddWithValue("$offset", offset);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var tagText = reader.GetString(13);
                items.Add(new HistorySummary(
                    reader.GetInt64(0),
                    reader.GetString(1),
                    ParseDatabaseTime(reader.GetString(2)),
                    reader.GetInt32(3),
                    reader.GetInt32(4),
                    reader.GetInt64(5),
                    reader.GetString(6),
                    reader.GetInt32(7) != 0,
                    reader.GetInt32(8) != 0,
                    (HistoryOcrIndexState)reader.GetInt32(9),
                    ReadNullableString(reader, 10),
                    ReadNullableString(reader, 11),
                    ReadNullableString(reader, 12),
                    tagText.Length == 0 ? [] : tagText.Split((char)31)));
            }
        }

        var hasMore = query.IncludeStatistics
            ? offset + items.Count < totalCount
            : items.Count == limit;
        return new HistoryQueryPage(items, totalCount, totalBytes, hasMore);
    }

    public async Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> LoadTagsByPathsAsync(
        IEnumerable<string> filePaths,
        CancellationToken cancellationToken = default)
    {
        var normalizedPaths = filePaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (normalizedPaths.Length == 0)
        {
            return new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        }

        await FlushAsync(cancellationToken).ConfigureAwait(false);
        await _initialization.WaitAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var mutable = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        const int batchSize = 400;
        for (var offset = 0; offset < normalizedPaths.Length; offset += batchSize)
        {
            var batch = normalizedPaths.Skip(offset).Take(batchSize).ToArray();
            await using var command = connection.CreateCommand();
            var parameters = new string[batch.Length];
            for (var index = 0; index < batch.Length; index++)
            {
                parameters[index] = $"$path{index}";
                command.Parameters.AddWithValue(parameters[index], batch[index]);
            }

            command.CommandText = $"""
                SELECT h.FilePath, t.Name
                FROM HistoryItems h
                INNER JOIN HistoryItemTags ht ON ht.HistoryItemId = h.Id
                INNER JOIN Tags t ON t.Id = ht.TagId
                WHERE h.FilePath IN ({string.Join(',', parameters)})
                ORDER BY t.Name COLLATE NOCASE;
                """;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var path = reader.GetString(0);
                if (!mutable.TryGetValue(path, out var pathTags))
                {
                    pathTags = [];
                    mutable[path] = pathTags;
                }

                pathTags.Add(reader.GetString(1));
            }
        }

        return mutable.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<string>)pair.Value,
            StringComparer.OrdinalIgnoreCase);
    }

    private static void AddQueryConditions(
        HistoryQuery query,
        ICollection<string> conditions,
        ICollection<(string Name, object Value)> parameters)
    {
        switch (query.Filter)
        {
            case HistoryFilterKind.Today:
                conditions.Add("h.CaptureTime >= $dateFrom");
                parameters.Add(("$dateFrom", ToDatabaseTime(new DateTimeOffset(DateTime.Today).ToUniversalTime())));
                break;
            case HistoryFilterKind.LastSevenDays:
                conditions.Add("h.CaptureTime >= $dateFrom");
                parameters.Add(("$dateFrom", ToDatabaseTime(new DateTimeOffset(DateTime.Today.AddDays(-6)).ToUniversalTime())));
                break;
            case HistoryFilterKind.LongCapture:
                conditions.Add("h.IsLongCapture = 1");
                break;
            case HistoryFilterKind.Favorite:
                conditions.Add("h.IsFavorite = 1");
                break;
        }

        var selectedTag = NormalizeTagName(query.Tag);
        if (selectedTag.Length > 0)
        {
            conditions.Add("EXISTS (SELECT 1 FROM HistoryItemTags ft INNER JOIN Tags t ON t.Id = ft.TagId WHERE ft.HistoryItemId = h.Id AND t.Name = $selectedTag COLLATE NOCASE)");
            parameters.Add(("$selectedTag", selectedTag));
        }

        var parsed = HistorySearchQuery.Parse(query.SearchText);
        for (var index = 0; index < parsed.TagTerms.Count; index++)
        {
            var name = $"$tagTerm{index}";
            conditions.Add($"EXISTS (SELECT 1 FROM HistoryItemTags st{index} INNER JOIN Tags tt{index} ON tt{index}.Id = st{index}.TagId WHERE st{index}.HistoryItemId = h.Id AND tt{index}.Name = {name} COLLATE NOCASE)");
            parameters.Add((name, parsed.TagTerms[index]));
        }

        for (var index = 0; index < parsed.Terms.Count; index++)
        {
            var name = $"$term{index}";
            conditions.Add($"""
                (instr(lower(h.FilePath), lower({name})) > 0
                 OR instr(lower(CAST(h.Width AS TEXT) || ' × ' || CAST(h.Height AS TEXT) || ' px'), lower({name})) > 0
                 OR instr(lower(CAST(h.Width AS TEXT) || 'x' || CAST(h.Height AS TEXT)), lower({name})) > 0
                 OR instr(lower(h.OcrText), lower({name})) > 0
                 OR instr(lower(COALESCE(h.SourceProcess, '')), lower({name})) > 0
                 OR instr(lower(COALESCE(h.SourceWindowTitle, '')), lower({name})) > 0
                 OR EXISTS (
                     SELECT 1 FROM HistoryItemTags qt{index}
                     INNER JOIN Tags qtag{index} ON qtag{index}.Id = qt{index}.TagId
                     WHERE qt{index}.HistoryItemId = h.Id
                       AND instr(lower(qtag{index}.Name), lower({name})) > 0))
                """);
            parameters.Add((name, parsed.Terms[index]));
        }
    }

    private static void AddParameters(SqliteCommand command, IEnumerable<(string Name, object Value)> parameters)
    {
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }
    }

    public async Task<IReadOnlyList<string>> LoadAllTagsAsync(CancellationToken cancellationToken = default)
    {
        await FlushAsync(cancellationToken).ConfigureAwait(false);
        await _initialization.WaitAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT t.Name
            FROM Tags t
            WHERE EXISTS (SELECT 1 FROM HistoryItemTags ht WHERE ht.TagId = t.Id)
            ORDER BY t.Name COLLATE NOCASE;
            """;
        var result = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(reader.GetString(0));
        }

        return result;
    }

    private void Enqueue(WriteRequest request)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_writeQueue.Writer.TryWrite(request))
        {
            request.Completion?.TrySetException(new InvalidOperationException("历史 Metadata 写入队列已关闭。"));
        }
    }

    private async Task ProcessWritesAsync()
    {
        try
        {
            await _initialization.ConfigureAwait(false);
            while (await _writeQueue.Reader.WaitToReadAsync(_shutdown.Token).ConfigureAwait(false))
            {
                // Larger transactions materially reduce SQLite commit overhead during a
                // first-run import while keeping individual screenshot writes responsive.
                var batch = new List<WriteRequest>(256);
                while (batch.Count < 256 && _writeQueue.Reader.TryRead(out var request))
                {
                    batch.Add(request);
                }

                if (batch.Count == 0)
                {
                    continue;
                }

                try
                {
                    await ExecuteBatchAsync(batch, _shutdown.Token).ConfigureAwait(false);
                    foreach (var request in batch)
                    {
                        request.Completion?.TrySetResult();
                    }
                }
                catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
                {
                    foreach (var request in batch)
                    {
                        request.Completion?.TrySetCanceled(_shutdown.Token);
                    }

                    break;
                }
                catch (Exception exception)
                {
                    DiagnosticLog.Error("HistoryMetadata", exception, "历史 Metadata 批量写入失败。");
                    foreach (var request in batch)
                    {
                        request.Completion?.TrySetException(exception);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            DiagnosticLog.Error("HistoryMetadata", exception, "历史 Metadata 写入线程异常退出。");
            while (_writeQueue.Reader.TryRead(out var request))
            {
                request.Completion?.TrySetException(exception);
            }
        }
    }

    private async Task ExecuteBatchAsync(IReadOnlyList<WriteRequest> requests, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        foreach (var request in requests)
        {
            switch (request.Kind)
            {
                case WriteKind.Upsert when request.Metadata is not null:
                    await UpsertAsync(connection, transaction, request.Metadata, cancellationToken).ConfigureAwait(false);
                    break;
                case WriteKind.Favorite:
                    await UpdateFavoriteAsync(connection, transaction, request.FilePath!, request.IsFavorite, cancellationToken).ConfigureAwait(false);
                    break;
                case WriteKind.OcrText:
                    await UpdateOcrAsync(connection, transaction, request.FilePath!, request.OcrText ?? string.Empty, cancellationToken).ConfigureAwait(false);
                    break;
                case WriteKind.ImageHash:
                    await UpdateImageHashAsync(connection, transaction, request.FilePath!, request.ImageHash!, cancellationToken).ConfigureAwait(false);
                    break;
                case WriteKind.AddTags when request.Tags is not null:
                    await AddTagsCoreAsync(connection, transaction, request.FilePath!, request.Tags, cancellationToken).ConfigureAwait(false);
                    break;
                case WriteKind.RemoveTag:
                    await RemoveTagCoreAsync(connection, transaction, request.FilePath!, request.TagName!, cancellationToken).ConfigureAwait(false);
                    break;
                case WriteKind.Delete:
                    await DeleteCoreAsync(connection, transaction, request.FilePath!, cancellationToken).ConfigureAwait(false);
                    break;
                case WriteKind.RemoveMissing when request.ExistingPaths is not null:
                    await RemoveMissingCoreAsync(connection, transaction, request.ExistingPaths, cancellationToken).ConfigureAwait(false);
                    break;
                case WriteKind.Flush:
                    break;
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task InitializeWithRecoveryAsync()
    {
        try
        {
            await InitializeDatabaseAsync().ConfigureAwait(false);
        }
        catch (SqliteException exception)
        {
            DiagnosticLog.Error("HistoryMetadata", exception, "历史 Metadata 数据库无法打开，准备重建索引。");
            MoveCorruptDatabaseAside();
            await InitializeDatabaseAsync().ConfigureAwait(false);
            DiagnosticLog.Info("HistoryMetadata", "历史 Metadata 数据库已重建，图片目录将在后台重新导入。");
        }
    }

    private async Task InitializeDatabaseAsync()
    {
        await using var connection = await OpenConnectionAsync(CancellationToken.None).ConfigureAwait(false);
        await using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;";
            await pragma.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        await using var transaction = await connection.BeginTransactionAsync().ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS SchemaInfo (
                Id INTEGER NOT NULL PRIMARY KEY CHECK (Id = 1),
                Version INTEGER NOT NULL,
                UpdatedAt TEXT NOT NULL
            );
            INSERT OR IGNORE INTO SchemaInfo (Id, Version, UpdatedAt) VALUES (1, 0, $now);
            """;
        command.Parameters.AddWithValue("$now", ToDatabaseTime(DateTimeOffset.UtcNow));
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);

        command.Parameters.Clear();
        command.CommandText = "SELECT Version FROM SchemaInfo WHERE Id = 1;";
        var version = Convert.ToInt32(await command.ExecuteScalarAsync().ConfigureAwait(false));
        if (version > CurrentSchemaVersion)
        {
            throw new InvalidOperationException($"历史数据库版本 {version} 高于当前支持版本 {CurrentSchemaVersion}。");
        }

        if (version < 1)
        {
            command.CommandText = """
                CREATE TABLE HistoryItems (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    FilePath TEXT NOT NULL COLLATE NOCASE UNIQUE,
                    CaptureTime TEXT NOT NULL,
                    Width INTEGER NOT NULL,
                    Height INTEGER NOT NULL,
                    FileSize INTEGER NOT NULL,
                    Format TEXT NOT NULL,
                    IsLongCapture INTEGER NOT NULL DEFAULT 0,
                    IsFavorite INTEGER NOT NULL DEFAULT 0,
                    OcrText TEXT NOT NULL DEFAULT '',
                    OcrIndexStatus INTEGER NOT NULL DEFAULT 0,
                    SourceProcess TEXT NULL,
                    SourceWindowTitle TEXT NULL,
                    MonitorId TEXT NULL,
                    MonitorDeviceName TEXT NULL,
                    CaptureX INTEGER NULL,
                    CaptureY INTEGER NULL,
                    CaptureWidth INTEGER NULL,
                    CaptureHeight INTEGER NULL,
                    ImageHash TEXT NULL,
                    CreatedAt TEXT NOT NULL,
                    UpdatedAt TEXT NOT NULL
                );
                CREATE INDEX IX_HistoryItems_CaptureTime ON HistoryItems(CaptureTime DESC);
                CREATE INDEX IX_HistoryItems_IsFavorite_CaptureTime ON HistoryItems(IsFavorite, CaptureTime DESC);
                CREATE INDEX IX_HistoryItems_ImageHash ON HistoryItems(ImageHash);
                CREATE TABLE Tags (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    Name TEXT NOT NULL COLLATE NOCASE UNIQUE,
                    CreatedAt TEXT NOT NULL
                );
                CREATE TABLE HistoryItemTags (
                    HistoryItemId INTEGER NOT NULL,
                    TagId INTEGER NOT NULL,
                    PRIMARY KEY (HistoryItemId, TagId),
                    FOREIGN KEY (HistoryItemId) REFERENCES HistoryItems(Id) ON DELETE CASCADE,
                    FOREIGN KEY (TagId) REFERENCES Tags(Id) ON DELETE CASCADE
                );
                UPDATE SchemaInfo SET Version = 1, UpdatedAt = $now WHERE Id = 1;
                """;
            command.Parameters.AddWithValue("$now", ToDatabaseTime(DateTimeOffset.UtcNow));
            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        await transaction.CommitAsync().ConfigureAwait(false);
    }

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
            DefaultTimeout = 5
        };
        var connection = new SqliteConnection(builder.ToString());
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000; PRAGMA synchronous=NORMAL;";
        await pragma.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private static async Task UpsertAsync(
        SqliteConnection connection,
        System.Data.Common.DbTransaction transaction,
        HistoryMetadata metadata,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = """
            INSERT INTO HistoryItems (
                FilePath, CaptureTime, Width, Height, FileSize, Format, IsLongCapture, IsFavorite,
                OcrText, OcrIndexStatus, SourceProcess, SourceWindowTitle, MonitorId, MonitorDeviceName,
                CaptureX, CaptureY, CaptureWidth, CaptureHeight, ImageHash, CreatedAt, UpdatedAt)
            VALUES (
                $filePath, $captureTime, $width, $height, $fileSize, $format, $isLongCapture, $isFavorite,
                $ocrText, $ocrIndexStatus, $sourceProcess, $sourceWindowTitle, $monitorId, $monitorDeviceName,
                $captureX, $captureY, $captureWidth, $captureHeight, $imageHash, $createdAt, $updatedAt)
            ON CONFLICT(FilePath) DO UPDATE SET
                CaptureTime = excluded.CaptureTime,
                Width = excluded.Width,
                Height = excluded.Height,
                FileSize = excluded.FileSize,
                Format = excluded.Format,
                IsLongCapture = MAX(HistoryItems.IsLongCapture, excluded.IsLongCapture),
                IsFavorite = MAX(HistoryItems.IsFavorite, excluded.IsFavorite),
                OcrText = CASE WHEN excluded.OcrIndexStatus = 0 THEN HistoryItems.OcrText ELSE excluded.OcrText END,
                OcrIndexStatus = MAX(HistoryItems.OcrIndexStatus, excluded.OcrIndexStatus),
                SourceProcess = COALESCE(excluded.SourceProcess, HistoryItems.SourceProcess),
                SourceWindowTitle = COALESCE(excluded.SourceWindowTitle, HistoryItems.SourceWindowTitle),
                MonitorId = COALESCE(excluded.MonitorId, HistoryItems.MonitorId),
                MonitorDeviceName = COALESCE(excluded.MonitorDeviceName, HistoryItems.MonitorDeviceName),
                CaptureX = COALESCE(excluded.CaptureX, HistoryItems.CaptureX),
                CaptureY = COALESCE(excluded.CaptureY, HistoryItems.CaptureY),
                CaptureWidth = COALESCE(excluded.CaptureWidth, HistoryItems.CaptureWidth),
                CaptureHeight = COALESCE(excluded.CaptureHeight, HistoryItems.CaptureHeight),
                ImageHash = COALESCE(excluded.ImageHash, HistoryItems.ImageHash),
                UpdatedAt = excluded.UpdatedAt;
            """;
        AddMetadataParameters(command, metadata);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task UpdateFavoriteAsync(
        SqliteConnection connection,
        System.Data.Common.DbTransaction transaction,
        string filePath,
        bool isFavorite,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = "UPDATE HistoryItems SET IsFavorite = $favorite, UpdatedAt = $updatedAt WHERE FilePath = $filePath;";
        command.Parameters.AddWithValue("$favorite", isFavorite ? 1 : 0);
        command.Parameters.AddWithValue("$updatedAt", ToDatabaseTime(DateTimeOffset.UtcNow));
        command.Parameters.AddWithValue("$filePath", Path.GetFullPath(filePath));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task UpdateOcrAsync(
        SqliteConnection connection,
        System.Data.Common.DbTransaction transaction,
        string filePath,
        string text,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = """
            UPDATE HistoryItems
            SET OcrText = $ocrText, OcrIndexStatus = $status, UpdatedAt = $updatedAt
            WHERE FilePath = $filePath;
            """;
        command.Parameters.AddWithValue("$ocrText", text.Trim());
        command.Parameters.AddWithValue("$status", (int)HistoryOcrIndexState.Indexed);
        command.Parameters.AddWithValue("$updatedAt", ToDatabaseTime(DateTimeOffset.UtcNow));
        command.Parameters.AddWithValue("$filePath", Path.GetFullPath(filePath));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task UpdateImageHashAsync(
        SqliteConnection connection,
        System.Data.Common.DbTransaction transaction,
        string filePath,
        string imageHash,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = "UPDATE HistoryItems SET ImageHash = $imageHash, UpdatedAt = $updatedAt WHERE FilePath = $filePath;";
        command.Parameters.AddWithValue("$imageHash", imageHash);
        command.Parameters.AddWithValue("$updatedAt", ToDatabaseTime(DateTimeOffset.UtcNow));
        command.Parameters.AddWithValue("$filePath", Path.GetFullPath(filePath));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task AddTagsCoreAsync(
        SqliteConnection connection,
        System.Data.Common.DbTransaction transaction,
        string filePath,
        IReadOnlyList<string> tags,
        CancellationToken cancellationToken)
    {
        foreach (var tag in tags)
        {
            await using var insertTag = connection.CreateCommand();
            insertTag.Transaction = (SqliteTransaction)transaction;
            insertTag.CommandText = "INSERT OR IGNORE INTO Tags (Name, CreatedAt) VALUES ($name, $createdAt);";
            insertTag.Parameters.AddWithValue("$name", tag);
            insertTag.Parameters.AddWithValue("$createdAt", ToDatabaseTime(DateTimeOffset.UtcNow));
            await insertTag.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            await using var link = connection.CreateCommand();
            link.Transaction = (SqliteTransaction)transaction;
            link.CommandText = """
                INSERT OR IGNORE INTO HistoryItemTags (HistoryItemId, TagId)
                SELECT h.Id, t.Id
                FROM HistoryItems h, Tags t
                WHERE h.FilePath = $filePath AND t.Name = $name COLLATE NOCASE;
                """;
            link.Parameters.AddWithValue("$filePath", Path.GetFullPath(filePath));
            link.Parameters.AddWithValue("$name", tag);
            await link.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task RemoveTagCoreAsync(
        SqliteConnection connection,
        System.Data.Common.DbTransaction transaction,
        string filePath,
        string tag,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = """
            DELETE FROM HistoryItemTags
            WHERE HistoryItemId = (SELECT Id FROM HistoryItems WHERE FilePath = $filePath)
              AND TagId = (SELECT Id FROM Tags WHERE Name = $name COLLATE NOCASE);
            """;
        command.Parameters.AddWithValue("$filePath", Path.GetFullPath(filePath));
        command.Parameters.AddWithValue("$name", tag);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task DeleteCoreAsync(
        SqliteConnection connection,
        System.Data.Common.DbTransaction transaction,
        string filePath,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = "DELETE FROM HistoryItems WHERE FilePath = $filePath;";
        command.Parameters.AddWithValue("$filePath", Path.GetFullPath(filePath));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task RemoveMissingCoreAsync(
        SqliteConnection connection,
        System.Data.Common.DbTransaction transaction,
        IReadOnlySet<string> existingPaths,
        CancellationToken cancellationToken)
    {
        await using var select = connection.CreateCommand();
        select.Transaction = (SqliteTransaction)transaction;
        select.CommandText = "SELECT FilePath FROM HistoryItems;";
        var missing = new List<string>();
        await using (var reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var filePath = reader.GetString(0);
                if (!existingPaths.Contains(filePath))
                {
                    missing.Add(filePath);
                }
            }
        }

        foreach (var filePath in missing)
        {
            await DeleteCoreAsync(connection, transaction, filePath, cancellationToken).ConfigureAwait(false);
        }
    }

    private static void AddMetadataParameters(SqliteCommand command, HistoryMetadata metadata)
    {
        command.Parameters.AddWithValue("$filePath", Path.GetFullPath(metadata.FilePath));
        command.Parameters.AddWithValue("$captureTime", ToDatabaseTime(metadata.CaptureTime));
        command.Parameters.AddWithValue("$width", metadata.Width);
        command.Parameters.AddWithValue("$height", metadata.Height);
        command.Parameters.AddWithValue("$fileSize", metadata.FileSize);
        command.Parameters.AddWithValue("$format", metadata.Format);
        command.Parameters.AddWithValue("$isLongCapture", metadata.IsLongCapture ? 1 : 0);
        command.Parameters.AddWithValue("$isFavorite", metadata.IsFavorite ? 1 : 0);
        command.Parameters.AddWithValue("$ocrText", metadata.OcrText ?? string.Empty);
        command.Parameters.AddWithValue("$ocrIndexStatus", (int)metadata.OcrIndexState);
        command.Parameters.AddWithValue("$sourceProcess", (object?)metadata.SourceProcess ?? DBNull.Value);
        command.Parameters.AddWithValue("$sourceWindowTitle", (object?)metadata.SourceWindowTitle ?? DBNull.Value);
        command.Parameters.AddWithValue("$monitorId", (object?)metadata.MonitorId ?? DBNull.Value);
        command.Parameters.AddWithValue("$monitorDeviceName", (object?)metadata.MonitorDeviceName ?? DBNull.Value);
        command.Parameters.AddWithValue("$captureX", (object?)metadata.CaptureX ?? DBNull.Value);
        command.Parameters.AddWithValue("$captureY", (object?)metadata.CaptureY ?? DBNull.Value);
        command.Parameters.AddWithValue("$captureWidth", (object?)metadata.CaptureWidth ?? DBNull.Value);
        command.Parameters.AddWithValue("$captureHeight", (object?)metadata.CaptureHeight ?? DBNull.Value);
        command.Parameters.AddWithValue("$imageHash", (object?)metadata.ImageHash ?? DBNull.Value);
        command.Parameters.AddWithValue("$createdAt", ToDatabaseTime(metadata.CreatedAt));
        command.Parameters.AddWithValue("$updatedAt", ToDatabaseTime(metadata.UpdatedAt));
    }

    private static HistoryMetadata ReadMetadata(SqliteDataReader reader) => new(
        reader.GetInt64(0),
        reader.GetString(1),
        ParseDatabaseTime(reader.GetString(2)),
        reader.GetInt32(3),
        reader.GetInt32(4),
        reader.GetInt64(5),
        reader.GetString(6),
        reader.GetInt32(7) != 0,
        reader.GetInt32(8) != 0,
        reader.GetString(9),
        (HistoryOcrIndexState)reader.GetInt32(10),
        ReadNullableString(reader, 11),
        ReadNullableString(reader, 12),
        ReadNullableString(reader, 13),
        ReadNullableString(reader, 14),
        ReadNullableInt32(reader, 15),
        ReadNullableInt32(reader, 16),
        ReadNullableInt32(reader, 17),
        ReadNullableInt32(reader, 18),
        ReadNullableString(reader, 19),
        ParseDatabaseTime(reader.GetString(20)),
        ParseDatabaseTime(reader.GetString(21)));

    private void MoveCorruptDatabaseAside()
    {
        var suffix = $".corrupt-{DateTime.Now:yyyyMMdd-HHmmss}";
        foreach (var path in new[] { DatabasePath, DatabasePath + "-wal", DatabasePath + "-shm" })
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Move(path, path + suffix, true);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                DiagnosticLog.Error("HistoryMetadata", exception, $"无法备份损坏数据库文件 {Path.GetFileName(path)}。");
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }
    }

    private static string? ReadNullableString(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static int? ReadNullableInt32(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);

    private static string ToDatabaseTime(DateTimeOffset value) => value.ToUniversalTime().ToString("O");

    private static DateTimeOffset ParseDatabaseTime(string value) =>
        DateTimeOffset.Parse(value, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind);

    internal static string NormalizeTagName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = new string(value
            .Trim()
            .Where(character => !char.IsControl(character))
            .ToArray());
        return normalized.Length <= 32 ? normalized : normalized[..32];
    }

    private const string SelectColumns = """
        Id, FilePath, CaptureTime, Width, Height, FileSize, Format, IsLongCapture, IsFavorite,
        OcrText, OcrIndexStatus, SourceProcess, SourceWindowTitle, MonitorId, MonitorDeviceName,
        CaptureX, CaptureY, CaptureWidth, CaptureHeight, ImageHash, CreatedAt, UpdatedAt
        """;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _writeQueue.Writer.TryComplete();
        try
        {
            if (!_writer.Wait(TimeSpan.FromSeconds(3)))
            {
                _shutdown.Cancel();
            }
        }
        catch (Exception exception) when (exception is AggregateException or OperationCanceledException)
        {
            DiagnosticLog.Warning("HistoryMetadata", $"关闭 Metadata 写入队列时发生异常：{exception.Message}");
        }

        _shutdown.Cancel();
        _shutdown.Dispose();
    }

    private enum WriteKind
    {
        Upsert,
        Favorite,
        OcrText,
        ImageHash,
        AddTags,
        RemoveTag,
        Delete,
        RemoveMissing,
        Flush
    }

    private sealed record WriteRequest(
        WriteKind Kind,
        string? FilePath = null,
        HistoryMetadata? Metadata = null,
        bool IsFavorite = false,
        string? OcrText = null,
        string? ImageHash = null,
        IReadOnlyList<string>? Tags = null,
        string? TagName = null,
        IReadOnlySet<string>? ExistingPaths = null,
        TaskCompletionSource? Completion = null);
}
