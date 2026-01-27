using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FieldKb.Application.Abstractions;
using FieldKb.Application.ImportExport;
using FieldKb.Domain.Models;
using FieldKb.Infrastructure.Sqlite;
using FieldKb.Infrastructure.Storage;
using Microsoft.Data.Sqlite;

namespace FieldKb.Infrastructure.ImportExport;

public sealed class SqlitePackageTransferService : IPackageTransferService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly SqliteConnectionFactory _connectionFactory;
    private readonly InstanceIdentityProvider _identityProvider;
    private readonly LocalInstanceContext _localContext;

    public SqlitePackageTransferService(
        SqliteConnectionFactory connectionFactory,
        InstanceIdentityProvider identityProvider,
        LocalInstanceContext localContext)
    {
        _connectionFactory = connectionFactory;
        _identityProvider = identityProvider;
        _localContext = localContext;
    }

    public async Task<ExportResult> ExportAsync(ExportRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.OutputDirectory))
        {
            throw new ArgumentException("OutputDirectory is required.", nameof(request));
        }

        if (string.IsNullOrWhiteSpace(request.RemoteInstanceId))
        {
            throw new ArgumentException("RemoteInstanceId is required.", nameof(request));
        }

        Directory.CreateDirectory(request.OutputDirectory);

        var identity = await _identityProvider.GetOrCreateAsync(_localContext.Kind, cancellationToken);

        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);
        await SqliteMigrations.ApplyAsync(connection, cancellationToken);

        var baseWatermarkUtc = request.Mode == ExportMode.Incremental
            ? request.UpdatedAfterUtc ?? await GetLastExportedUpdatedAtUtcAsync(connection, identity.InstanceId, request.RemoteInstanceId, cancellationToken)
            : null;

        var createdAtUtc = DateTimeOffset.UtcNow;
        var packageId = Guid.NewGuid().ToString("D");

        var tempDir = Path.Combine(Path.GetTempPath(), "FieldKb", "export", packageId);
        if (Directory.Exists(tempDir))
        {
            Directory.Delete(tempDir, recursive: true);
        }

        Directory.CreateDirectory(tempDir);
        var dataDir = Path.Combine(tempDir, "data");
        var attachmentsDir = Path.Combine(tempDir, "attachments");
        Directory.CreateDirectory(dataDir);
        Directory.CreateDirectory(attachmentsDir);

        var recordCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var maxUpdatedAtUtc = baseWatermarkUtc ?? DateTimeOffset.MinValue;

        var problemsPath = Path.Combine(dataDir, "problems.jsonl");
        var (problemsCount, problemsMaxUpdatedAtUtc) = await ExportJsonlAsync(
            problemsPath,
            await ReadProblemsAsync(connection, baseWatermarkUtc, request.Limit, cancellationToken),
            line => new ChangeLine<Problem>("Upsert", line),
            p => p.UpdatedAtUtc);
        recordCounts["data/problems.jsonl"] = problemsCount;
        maxUpdatedAtUtc = Max(maxUpdatedAtUtc, problemsMaxUpdatedAtUtc);

        var tagsPath = Path.Combine(dataDir, "tags.jsonl");
        var (tagsCount, tagsMaxUpdatedAtUtc) = await ExportJsonlAsync(
            tagsPath,
            await ReadTagsAsync(connection, baseWatermarkUtc, request.Limit, cancellationToken),
            line => new ChangeLine<Tag>("Upsert", line),
            t => t.UpdatedAtUtc);
        recordCounts["data/tags.jsonl"] = tagsCount;
        maxUpdatedAtUtc = Max(maxUpdatedAtUtc, tagsMaxUpdatedAtUtc);

        var problemTagsPath = Path.Combine(dataDir, "problemTags.jsonl");
        var (problemTagsCount, problemTagsMaxUpdatedAtUtc) = await ExportJsonlAsync(
            problemTagsPath,
            await ReadProblemTagsAsync(connection, baseWatermarkUtc, request.Limit, cancellationToken),
            line => new ChangeLine<ProblemTag>("Upsert", line),
            pt => pt.UpdatedAtUtc);
        recordCounts["data/problemTags.jsonl"] = problemTagsCount;
        maxUpdatedAtUtc = Max(maxUpdatedAtUtc, problemTagsMaxUpdatedAtUtc);

        var attachmentsPath = Path.Combine(dataDir, "attachments.jsonl");
        var attachments = await ReadAttachmentsAsync(connection, baseWatermarkUtc, request.Limit, cancellationToken);
        var (attachmentsCount, attachmentsMaxUpdatedAtUtc) = await ExportJsonlAsync(
            attachmentsPath,
            attachments,
            line => new ChangeLine<Attachment>("Upsert", line),
            a => a.UpdatedAtUtc);
        recordCounts["data/attachments.jsonl"] = attachmentsCount;
        maxUpdatedAtUtc = Max(maxUpdatedAtUtc, attachmentsMaxUpdatedAtUtc);

        var copiedAttachmentCount = await CopyAttachmentFilesAsync(attachmentsDir, attachments, cancellationToken);
        recordCounts["attachments/*"] = copiedAttachmentCount;

        var checksums = await ComputeChecksumsAsync(tempDir, cancellationToken);
        var manifest = new PackageManifest(
            PackageId: packageId,
            SchemaVersion: 0,
            CreatedAtUtc: createdAtUtc,
            ExporterInstanceId: identity.InstanceId,
            ExporterKind: identity.Kind.ToString(),
            Mode: request.Mode.ToString(),
            BaseWatermarkUtc: baseWatermarkUtc,
            MaxUpdatedAtUtc: maxUpdatedAtUtc == DateTimeOffset.MinValue ? createdAtUtc : maxUpdatedAtUtc,
            RecordCounts: recordCounts,
            Checksums: checksums);

        var manifestPath = Path.Combine(tempDir, "manifest.json");
        await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(manifest, JsonOptions), Encoding.UTF8, cancellationToken);

        checksums = await ComputeChecksumsAsync(tempDir, cancellationToken);
        manifest = manifest with { Checksums = checksums };
        await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(manifest, JsonOptions), Encoding.UTF8, cancellationToken);

        var fileName = $"kbpkg_{identity.InstanceId}_{createdAtUtc:yyyyMMddTHHmmssZ}_{request.Mode}.zip";
        var packagePath = Path.Combine(request.OutputDirectory, fileName);
        if (File.Exists(packagePath))
        {
            File.Delete(packagePath);
        }

        ZipFile.CreateFromDirectory(tempDir, packagePath, CompressionLevel.Optimal, includeBaseDirectory: false);

        await UpdateExportStateAsync(connection, identity.InstanceId, request.RemoteInstanceId, manifest.MaxUpdatedAtUtc, manifest.PackageId, cancellationToken);

        return new ExportResult(
            PackageId: manifest.PackageId,
            PackagePath: packagePath,
            CreatedAtUtc: createdAtUtc,
            BaseWatermarkUtc: baseWatermarkUtc,
            MaxUpdatedAtUtc: manifest.MaxUpdatedAtUtc);
    }

    public async Task<ImportReport> ImportAsync(string packagePath, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(packagePath))
        {
            throw new ArgumentException("packagePath is required.", nameof(packagePath));
        }

        var startedAtUtc = DateTimeOffset.UtcNow;
        var errors = new List<string>();

        var identity = await _identityProvider.GetOrCreateAsync(_localContext.Kind, cancellationToken);

        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);
        await SqliteMigrations.ApplyAsync(connection, cancellationToken);

        var tempDir = Path.Combine(Path.GetTempPath(), "FieldKb", "import", Guid.NewGuid().ToString("D"));
        Directory.CreateDirectory(tempDir);

        try
        {
            ZipFile.ExtractToDirectory(packagePath, tempDir);

            var manifestFile = Path.Combine(tempDir, "manifest.json");
            if (!File.Exists(manifestFile))
            {
                throw new InvalidOperationException("manifest.json not found in package.");
            }

            var manifestJson = await File.ReadAllTextAsync(manifestFile, Encoding.UTF8, cancellationToken);
            var manifest = JsonSerializer.Deserialize<PackageManifest>(manifestJson, JsonOptions)
                ?? throw new InvalidOperationException("Invalid manifest.json.");

            await ValidateChecksumsAsync(tempDir, manifest, cancellationToken);

            var imported = 0;
            var skipped = 0;
            var conflicts = 0;

            var problemsImported = 0;
            var problemsSkipped = 0;
            var problemsConflicts = 0;
            var tagsImported = 0;
            var problemTagsImported = 0;
            var attachmentsImported = 0;

            await using var tx = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

            var dataDir = Path.Combine(tempDir, "data");
            if (Directory.Exists(dataDir))
            {
                var problemsFile = Path.Combine(dataDir, "problems.jsonl");
                if (File.Exists(problemsFile))
                {
                    var result = await ImportJsonlAsync<Problem>(
                        connection,
                        tx,
                        problemsFile,
                        "Problem",
                        ApplyProblemAsync,
                        cancellationToken);
                    imported += result.Imported;
                    skipped += result.Skipped;
                    conflicts += result.Conflicts;
                    problemsImported += result.Imported;
                    problemsSkipped += result.Skipped;
                    problemsConflicts += result.Conflicts;
                    errors.AddRange(result.Errors);
                }

                var tagsFile = Path.Combine(dataDir, "tags.jsonl");
                if (File.Exists(tagsFile))
                {
                    var result = await ImportJsonlAsync<Tag>(
                        connection,
                        tx,
                        tagsFile,
                        "Tag",
                        ApplyTagAsync,
                        cancellationToken);
                    imported += result.Imported;
                    skipped += result.Skipped;
                    conflicts += result.Conflicts;
                    tagsImported += result.Imported;
                    errors.AddRange(result.Errors);
                }

                var problemTagsFile = Path.Combine(dataDir, "problemTags.jsonl");
                if (File.Exists(problemTagsFile))
                {
                    var result = await ImportJsonlAsync<ProblemTag>(
                        connection,
                        tx,
                        problemTagsFile,
                        "ProblemTag",
                        ApplyProblemTagAsync,
                        cancellationToken);
                    imported += result.Imported;
                    skipped += result.Skipped;
                    conflicts += result.Conflicts;
                    problemTagsImported += result.Imported;
                    errors.AddRange(result.Errors);
                }

                var attachmentsFile = Path.Combine(dataDir, "attachments.jsonl");
                if (File.Exists(attachmentsFile))
                {
                    var result = await ImportJsonlAsync<Attachment>(
                        connection,
                        tx,
                        attachmentsFile,
                        "Attachment",
                        ApplyAttachmentAsync,
                        cancellationToken);
                    imported += result.Imported;
                    skipped += result.Skipped;
                    conflicts += result.Conflicts;
                    attachmentsImported += result.Imported;
                    errors.AddRange(result.Errors);
                }
            }

            await tx.CommitAsync(cancellationToken);

            await CopyImportedAttachmentsAsync(tempDir, cancellationToken);
            await UpdateSyncStateAsync(connection, identity.InstanceId, manifest.ExporterInstanceId, manifest.MaxUpdatedAtUtc, manifest.PackageId, cancellationToken);

            var finishedAtUtc = DateTimeOffset.UtcNow;
            return new ImportReport(
                PackageId: manifest.PackageId,
                ExporterInstanceId: manifest.ExporterInstanceId,
                StartedAtUtc: startedAtUtc,
                FinishedAtUtc: finishedAtUtc,
                ImportedCount: imported,
                SkippedCount: skipped,
                ConflictCount: conflicts,
                Errors: errors)
            {
                ProblemsImportedCount = problemsImported,
                ProblemsSkippedCount = problemsSkipped,
                ProblemsConflictCount = problemsConflicts,
                TagsImportedCount = tagsImported,
                ProblemTagsImportedCount = problemTagsImported,
                AttachmentsImportedCount = attachmentsImported
            };
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    private static async Task ValidateChecksumsAsync(string extractedDir, PackageManifest manifest, CancellationToken cancellationToken)
    {
        foreach (var kv in manifest.Checksums)
        {
            var relative = kv.Key.Replace('/', Path.DirectorySeparatorChar);
            var path = Path.Combine(extractedDir, relative);
            if (!File.Exists(path))
            {
                throw new InvalidOperationException($"Checksum target missing: {kv.Key}");
            }

            var sha = await ComputeSha256Async(path, cancellationToken);
            if (!sha.Equals(kv.Value, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Checksum mismatch: {kv.Key}");
            }
        }
    }

    private static async Task<Dictionary<string, string>> ComputeChecksumsAsync(string baseDir, CancellationToken cancellationToken)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in Directory.EnumerateFiles(baseDir, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(baseDir, file).Replace(Path.DirectorySeparatorChar, '/');
            if (relative.Equals("manifest.json", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            map[relative] = await ComputeSha256Async(file, cancellationToken);
        }

        return map;
    }

    private static async Task<string> ComputeSha256Async(string filePath, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(filePath);
        using var sha = SHA256.Create();
        var hash = await sha.ComputeHashAsync(stream, cancellationToken);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static async Task<int> CopyAttachmentFilesAsync(string exportAttachmentsDir, IReadOnlyList<Attachment> attachments, CancellationToken cancellationToken)
    {
        var srcDir = AppDataPaths.GetAttachmentsDirectory();
        if (!Directory.Exists(srcDir))
        {
            return 0;
        }

        var copied = 0;
        foreach (var attachment in attachments)
        {
            if (attachment.IsDeleted)
            {
                continue;
            }

            var src = Path.Combine(srcDir, attachment.ContentHash);
            if (!File.Exists(src))
            {
                continue;
            }

            var dest = Path.Combine(exportAttachmentsDir, attachment.ContentHash);
            await using var from = File.OpenRead(src);
            await using var to = File.Create(dest);
            await from.CopyToAsync(to, cancellationToken);
            copied++;
        }

        return copied;
    }

    private static async Task CopyImportedAttachmentsAsync(string tempDir, CancellationToken cancellationToken)
    {
        var srcDir = Path.Combine(tempDir, "attachments");
        if (!Directory.Exists(srcDir))
        {
            return;
        }

        var destDir = AppDataPaths.GetAttachmentsDirectory();
        Directory.CreateDirectory(destDir);

        foreach (var file in Directory.EnumerateFiles(srcDir))
        {
            var name = Path.GetFileName(file);
            var dest = Path.Combine(destDir, name);
            if (File.Exists(dest))
            {
                continue;
            }

            await using var from = File.OpenRead(file);
            await using var to = File.Create(dest);
            await from.CopyToAsync(to, cancellationToken);
        }
    }

    private static async Task<(int Count, DateTimeOffset MaxUpdatedAtUtc)> ExportJsonlAsync<T>(
        string filePath,
        IReadOnlyList<T> entities,
        Func<T, object> wrap,
        Func<T, DateTimeOffset> getUpdatedAtUtc)
    {
        if (entities.Count == 0)
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }

            return (0, DateTimeOffset.MinValue);
        }

        await using var stream = File.Create(filePath);
        await using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        var count = 0;
        var max = DateTimeOffset.MinValue;
        foreach (var entity in entities)
        {
            var updatedAt = getUpdatedAtUtc(entity);
            if (updatedAt > max)
            {
                max = updatedAt;
            }

            var json = JsonSerializer.Serialize(wrap(entity), JsonOptions);
            await writer.WriteLineAsync(json);
            count++;
        }

        await writer.FlushAsync();
        return (count, max);
    }

    private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;

    private static async Task<IReadOnlyList<Problem>> ReadProblemsAsync(
        SqliteConnection connection,
        DateTimeOffset? updatedAfterUtc,
        int? limit,
        CancellationToken cancellationToken)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT
                id, title, symptom, rootCause, solution, environmentJson,
                severity, status,
                createdAtUtc, createdBy,
                updatedAtUtc, updatedByInstanceId,
                isDeleted, deletedAtUtc,
                sourceKind
            FROM problem
            WHERE ($updatedAfterUtc IS NULL OR updatedAtUtc > $updatedAfterUtc)
            ORDER BY updatedAtUtc
            LIMIT COALESCE($limit, 2147483647);
            """;
        cmd.Parameters.AddWithValue("$updatedAfterUtc", updatedAfterUtc is null ? (object)DBNull.Value : ToUtcText(updatedAfterUtc.Value));
        cmd.Parameters.AddWithValue("$limit", limit is null ? (object)DBNull.Value : limit.Value);

        var list = new List<Problem>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            list.Add(new Problem(
                Id: reader.GetString(0),
                Title: reader.GetString(1),
                Symptom: reader.GetString(2),
                RootCause: reader.GetString(3),
                Solution: reader.GetString(4),
                EnvironmentJson: reader.GetString(5),
                Severity: reader.GetInt32(6),
                Status: reader.GetInt32(7),
                CreatedAtUtc: ParseUtcText(reader.GetString(8)),
                CreatedBy: reader.GetString(9),
                UpdatedAtUtc: ParseUtcText(reader.GetString(10)),
                UpdatedByInstanceId: reader.GetString(11),
                IsDeleted: reader.GetInt32(12) != 0,
                DeletedAtUtc: reader.IsDBNull(13) ? null : ParseUtcText(reader.GetString(13)),
                SourceKind: (SourceKind)reader.GetInt32(14)));
        }

        return list;
    }

    private static async Task<IReadOnlyList<Tag>> ReadTagsAsync(
        SqliteConnection connection,
        DateTimeOffset? updatedAfterUtc,
        int? limit,
        CancellationToken cancellationToken)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT id, name, createdAtUtc, updatedAtUtc, updatedByInstanceId, isDeleted
            FROM tag
            WHERE ($updatedAfterUtc IS NULL OR updatedAtUtc > $updatedAfterUtc)
            ORDER BY updatedAtUtc
            LIMIT COALESCE($limit, 2147483647);
            """;
        cmd.Parameters.AddWithValue("$updatedAfterUtc", updatedAfterUtc is null ? (object)DBNull.Value : ToUtcText(updatedAfterUtc.Value));
        cmd.Parameters.AddWithValue("$limit", limit is null ? (object)DBNull.Value : limit.Value);

        var list = new List<Tag>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            list.Add(new Tag(
                Id: reader.GetString(0),
                Name: reader.GetString(1),
                CreatedAtUtc: ParseUtcText(reader.GetString(2)),
                UpdatedAtUtc: ParseUtcText(reader.GetString(3)),
                UpdatedByInstanceId: reader.GetString(4),
                IsDeleted: reader.GetInt32(5) != 0));
        }

        return list;
    }

    private static async Task<IReadOnlyList<ProblemTag>> ReadProblemTagsAsync(
        SqliteConnection connection,
        DateTimeOffset? updatedAfterUtc,
        int? limit,
        CancellationToken cancellationToken)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT id, problemId, tagId, createdAtUtc, updatedAtUtc, updatedByInstanceId, isDeleted
            FROM problemTag
            WHERE ($updatedAfterUtc IS NULL OR updatedAtUtc > $updatedAfterUtc)
            ORDER BY updatedAtUtc
            LIMIT COALESCE($limit, 2147483647);
            """;
        cmd.Parameters.AddWithValue("$updatedAfterUtc", updatedAfterUtc is null ? (object)DBNull.Value : ToUtcText(updatedAfterUtc.Value));
        cmd.Parameters.AddWithValue("$limit", limit is null ? (object)DBNull.Value : limit.Value);

        var list = new List<ProblemTag>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            list.Add(new ProblemTag(
                Id: reader.GetString(0),
                ProblemId: reader.GetString(1),
                TagId: reader.GetString(2),
                CreatedAtUtc: ParseUtcText(reader.GetString(3)),
                UpdatedAtUtc: ParseUtcText(reader.GetString(4)),
                UpdatedByInstanceId: reader.GetString(5),
                IsDeleted: reader.GetInt32(6) != 0));
        }

        return list;
    }

    private static async Task<IReadOnlyList<Attachment>> ReadAttachmentsAsync(
        SqliteConnection connection,
        DateTimeOffset? updatedAfterUtc,
        int? limit,
        CancellationToken cancellationToken)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT id, problemId, originalFileName, contentHash, sizeBytes, mimeType, createdAtUtc, updatedAtUtc, updatedByInstanceId, isDeleted
            FROM attachment
            WHERE ($updatedAfterUtc IS NULL OR updatedAtUtc > $updatedAfterUtc)
            ORDER BY updatedAtUtc
            LIMIT COALESCE($limit, 2147483647);
            """;
        cmd.Parameters.AddWithValue("$updatedAfterUtc", updatedAfterUtc is null ? (object)DBNull.Value : ToUtcText(updatedAfterUtc.Value));
        cmd.Parameters.AddWithValue("$limit", limit is null ? (object)DBNull.Value : limit.Value);

        var list = new List<Attachment>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            list.Add(new Attachment(
                Id: reader.GetString(0),
                ProblemId: reader.GetString(1),
                OriginalFileName: reader.GetString(2),
                ContentHash: reader.GetString(3),
                SizeBytes: reader.GetInt64(4),
                MimeType: reader.GetString(5),
                CreatedAtUtc: ParseUtcText(reader.GetString(6)),
                UpdatedAtUtc: ParseUtcText(reader.GetString(7)),
                UpdatedByInstanceId: reader.GetString(8),
                IsDeleted: reader.GetInt32(9) != 0));
        }

        return list;
    }

    private sealed record ImportCounters(int Imported, int Skipped, int Conflicts, IReadOnlyList<string> Errors);

    private static async Task<ImportCounters> ImportJsonlAsync<T>(
        SqliteConnection connection,
        SqliteTransaction tx,
        string jsonlPath,
        string entityType,
        Func<SqliteConnection, SqliteTransaction, T, CancellationToken, Task<ApplyResult>> apply,
        CancellationToken cancellationToken)
    {
        var imported = 0;
        var skipped = 0;
        var conflicts = 0;
        var errors = new List<string>();

        await using var stream = File.OpenRead(jsonlPath);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

        while (!reader.EndOfStream)
        {
            var line = await reader.ReadLineAsync();
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            ChangeLine<T>? change;
            try
            {
                change = JsonSerializer.Deserialize<ChangeLine<T>>(line, JsonOptions);
            }
            catch (Exception ex)
            {
                errors.Add($"{entityType}: invalid jsonl line: {ex.Message}");
                continue;
            }

            if (change is null)
            {
                errors.Add($"{entityType}: invalid jsonl line.");
                continue;
            }

            try
            {
                var result = await apply(connection, tx, change.Entity, cancellationToken);
                imported += result.Imported ? 1 : 0;
                skipped += result.Skipped ? 1 : 0;
                conflicts += result.ConflictRecorded ? 1 : 0;
            }
            catch (Exception ex)
            {
                errors.Add($"{entityType}: apply failed: {ex.Message}");
            }
        }

        return new ImportCounters(imported, skipped, conflicts, errors);
    }

    private sealed record ApplyResult(bool Imported, bool Skipped, bool ConflictRecorded);

    private static async Task<ApplyResult> ApplyProblemAsync(SqliteConnection connection, SqliteTransaction tx, Problem problem, CancellationToken cancellationToken)
    {
        var local = await GetLocalMetaAsync(connection, tx, "problem", problem.Id, cancellationToken);
        var importedUpdatedAt = problem.UpdatedAtUtc;
        var importedBy = problem.UpdatedByInstanceId;

        if (local is not null)
        {
            var compare = CompareRemoteVsLocal(importedUpdatedAt, importedBy, local.Value.UpdatedAtUtc, local.Value.UpdatedByInstanceId);
            if (compare < 0)
            {
                var conflictId = Guid.NewGuid().ToString("D");
                await InsertConflictAsync(connection, tx, conflictId, "Problem", problem.Id, importedUpdatedAt, local.Value.UpdatedAtUtc, local.Value.LocalJson, JsonSerializer.Serialize(problem, JsonOptions), cancellationToken);
                return new ApplyResult(Imported: false, Skipped: true, ConflictRecorded: true);
            }
        }

        await UpsertProblemRowAsync(connection, tx, problem, cancellationToken);
        await UpsertProblemFtsAsync(connection, tx, problem, cancellationToken);
        return new ApplyResult(Imported: true, Skipped: false, ConflictRecorded: false);
    }

    private static async Task<ApplyResult> ApplyTagAsync(SqliteConnection connection, SqliteTransaction tx, Tag tag, CancellationToken cancellationToken)
    {
        var local = await GetLocalMetaAsync(connection, tx, "tag", tag.Id, cancellationToken);
        if (local is not null)
        {
            var compare = CompareRemoteVsLocal(tag.UpdatedAtUtc, tag.UpdatedByInstanceId, local.Value.UpdatedAtUtc, local.Value.UpdatedByInstanceId);
            if (compare < 0)
            {
                var conflictId = Guid.NewGuid().ToString("D");
                await InsertConflictAsync(connection, tx, conflictId, "Tag", tag.Id, tag.UpdatedAtUtc, local.Value.UpdatedAtUtc, local.Value.LocalJson, JsonSerializer.Serialize(tag, JsonOptions), cancellationToken);
                return new ApplyResult(false, true, true);
            }
        }

        await using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO tag (id, name, createdAtUtc, updatedAtUtc, updatedByInstanceId, isDeleted)
            VALUES ($id, $name, $createdAtUtc, $updatedAtUtc, $updatedByInstanceId, $isDeleted)
            ON CONFLICT(id) DO UPDATE SET
                name = excluded.name,
                createdAtUtc = excluded.createdAtUtc,
                updatedAtUtc = excluded.updatedAtUtc,
                updatedByInstanceId = excluded.updatedByInstanceId,
                isDeleted = excluded.isDeleted;
            """;
        cmd.Parameters.AddWithValue("$id", tag.Id);
        cmd.Parameters.AddWithValue("$name", tag.Name);
        cmd.Parameters.AddWithValue("$createdAtUtc", ToUtcText(tag.CreatedAtUtc));
        cmd.Parameters.AddWithValue("$updatedAtUtc", ToUtcText(tag.UpdatedAtUtc));
        cmd.Parameters.AddWithValue("$updatedByInstanceId", tag.UpdatedByInstanceId);
        cmd.Parameters.AddWithValue("$isDeleted", tag.IsDeleted ? 1 : 0);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
        return new ApplyResult(true, false, false);
    }

    private static async Task<ApplyResult> ApplyProblemTagAsync(SqliteConnection connection, SqliteTransaction tx, ProblemTag problemTag, CancellationToken cancellationToken)
    {
        var local = await GetLocalMetaAsync(connection, tx, "problemTag", problemTag.Id, cancellationToken);
        if (local is not null)
        {
            var compare = CompareRemoteVsLocal(problemTag.UpdatedAtUtc, problemTag.UpdatedByInstanceId, local.Value.UpdatedAtUtc, local.Value.UpdatedByInstanceId);
            if (compare < 0)
            {
                var conflictId = Guid.NewGuid().ToString("D");
                await InsertConflictAsync(connection, tx, conflictId, "ProblemTag", problemTag.Id, problemTag.UpdatedAtUtc, local.Value.UpdatedAtUtc, local.Value.LocalJson, JsonSerializer.Serialize(problemTag, JsonOptions), cancellationToken);
                return new ApplyResult(false, true, true);
            }
        }

        await using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO problemTag (id, problemId, tagId, createdAtUtc, updatedAtUtc, updatedByInstanceId, isDeleted)
            VALUES ($id, $problemId, $tagId, $createdAtUtc, $updatedAtUtc, $updatedByInstanceId, $isDeleted)
            ON CONFLICT(id) DO UPDATE SET
                problemId = excluded.problemId,
                tagId = excluded.tagId,
                createdAtUtc = excluded.createdAtUtc,
                updatedAtUtc = excluded.updatedAtUtc,
                updatedByInstanceId = excluded.updatedByInstanceId,
                isDeleted = excluded.isDeleted;
            """;
        cmd.Parameters.AddWithValue("$id", problemTag.Id);
        cmd.Parameters.AddWithValue("$problemId", problemTag.ProblemId);
        cmd.Parameters.AddWithValue("$tagId", problemTag.TagId);
        cmd.Parameters.AddWithValue("$createdAtUtc", ToUtcText(problemTag.CreatedAtUtc));
        cmd.Parameters.AddWithValue("$updatedAtUtc", ToUtcText(problemTag.UpdatedAtUtc));
        cmd.Parameters.AddWithValue("$updatedByInstanceId", problemTag.UpdatedByInstanceId);
        cmd.Parameters.AddWithValue("$isDeleted", problemTag.IsDeleted ? 1 : 0);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
        return new ApplyResult(true, false, false);
    }

    private static async Task<ApplyResult> ApplyAttachmentAsync(SqliteConnection connection, SqliteTransaction tx, Attachment attachment, CancellationToken cancellationToken)
    {
        var local = await GetLocalMetaAsync(connection, tx, "attachment", attachment.Id, cancellationToken);
        if (local is not null)
        {
            var compare = CompareRemoteVsLocal(attachment.UpdatedAtUtc, attachment.UpdatedByInstanceId, local.Value.UpdatedAtUtc, local.Value.UpdatedByInstanceId);
            if (compare < 0)
            {
                var conflictId = Guid.NewGuid().ToString("D");
                await InsertConflictAsync(connection, tx, conflictId, "Attachment", attachment.Id, attachment.UpdatedAtUtc, local.Value.UpdatedAtUtc, local.Value.LocalJson, JsonSerializer.Serialize(attachment, JsonOptions), cancellationToken);
                return new ApplyResult(false, true, true);
            }
        }

        await using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO attachment (
                id, problemId, originalFileName, contentHash, sizeBytes, mimeType,
                createdAtUtc, updatedAtUtc, updatedByInstanceId, isDeleted
            )
            VALUES (
                $id, $problemId, $originalFileName, $contentHash, $sizeBytes, $mimeType,
                $createdAtUtc, $updatedAtUtc, $updatedByInstanceId, $isDeleted
            )
            ON CONFLICT(id) DO UPDATE SET
                problemId = excluded.problemId,
                originalFileName = excluded.originalFileName,
                contentHash = excluded.contentHash,
                sizeBytes = excluded.sizeBytes,
                mimeType = excluded.mimeType,
                createdAtUtc = excluded.createdAtUtc,
                updatedAtUtc = excluded.updatedAtUtc,
                updatedByInstanceId = excluded.updatedByInstanceId,
                isDeleted = excluded.isDeleted;
            """;
        cmd.Parameters.AddWithValue("$id", attachment.Id);
        cmd.Parameters.AddWithValue("$problemId", attachment.ProblemId);
        cmd.Parameters.AddWithValue("$originalFileName", attachment.OriginalFileName);
        cmd.Parameters.AddWithValue("$contentHash", attachment.ContentHash);
        cmd.Parameters.AddWithValue("$sizeBytes", attachment.SizeBytes);
        cmd.Parameters.AddWithValue("$mimeType", attachment.MimeType);
        cmd.Parameters.AddWithValue("$createdAtUtc", ToUtcText(attachment.CreatedAtUtc));
        cmd.Parameters.AddWithValue("$updatedAtUtc", ToUtcText(attachment.UpdatedAtUtc));
        cmd.Parameters.AddWithValue("$updatedByInstanceId", attachment.UpdatedByInstanceId);
        cmd.Parameters.AddWithValue("$isDeleted", attachment.IsDeleted ? 1 : 0);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
        return new ApplyResult(true, false, false);
    }

    private readonly record struct LocalMeta(DateTimeOffset UpdatedAtUtc, string UpdatedByInstanceId, string LocalJson);

    private static async Task<LocalMeta?> GetLocalMetaAsync(SqliteConnection connection, SqliteTransaction tx, string table, string id, CancellationToken cancellationToken)
    {
        await using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"SELECT updatedAtUtc, updatedByInstanceId FROM {table} WHERE id = $id LIMIT 1;";
        cmd.Parameters.AddWithValue("$id", id);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var updatedAtUtc = ParseUtcText(reader.GetString(0));
        var updatedBy = reader.GetString(1);

        var localJson = JsonSerializer.Serialize(
            new { id, updatedAtUtc, updatedByInstanceId = updatedBy },
            JsonOptions);

        return new LocalMeta(updatedAtUtc, updatedBy, localJson);
    }

    private static int CompareRemoteVsLocal(DateTimeOffset remoteUpdatedAt, string remoteBy, DateTimeOffset localUpdatedAt, string localBy)
    {
        var timeCompare = remoteUpdatedAt.CompareTo(localUpdatedAt);
        if (timeCompare != 0)
        {
            return timeCompare;
        }

        return string.CompareOrdinal(remoteBy, localBy);
    }

    private static async Task InsertConflictAsync(
        SqliteConnection connection,
        SqliteTransaction tx,
        string conflictId,
        string entityType,
        string entityId,
        DateTimeOffset importedUpdatedAtUtc,
        DateTimeOffset localUpdatedAtUtc,
        string localJson,
        string importedJson,
        CancellationToken cancellationToken)
    {
        await using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO conflictRecord (
                id, entityType, entityId,
                importedUpdatedAtUtc, localUpdatedAtUtc,
                localJson, importedJson, createdAtUtc
            )
            VALUES (
                $id, $entityType, $entityId,
                $importedUpdatedAtUtc, $localUpdatedAtUtc,
                $localJson, $importedJson, $createdAtUtc
            );
            """;
        cmd.Parameters.AddWithValue("$id", conflictId);
        cmd.Parameters.AddWithValue("$entityType", entityType);
        cmd.Parameters.AddWithValue("$entityId", entityId);
        cmd.Parameters.AddWithValue("$importedUpdatedAtUtc", ToUtcText(importedUpdatedAtUtc));
        cmd.Parameters.AddWithValue("$localUpdatedAtUtc", ToUtcText(localUpdatedAtUtc));
        cmd.Parameters.AddWithValue("$localJson", localJson);
        cmd.Parameters.AddWithValue("$importedJson", importedJson);
        cmd.Parameters.AddWithValue("$createdAtUtc", ToUtcText(DateTimeOffset.UtcNow));
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task UpsertProblemRowAsync(SqliteConnection connection, SqliteTransaction tx, Problem problem, CancellationToken cancellationToken)
    {
        await using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO problem (
                id, title, symptom, rootCause, solution, environmentJson,
                severity, status,
                createdAtUtc, createdBy,
                updatedAtUtc, updatedByInstanceId,
                isDeleted, deletedAtUtc,
                sourceKind
            )
            VALUES (
                $id, $title, $symptom, $rootCause, $solution, $environmentJson,
                $severity, $status,
                $createdAtUtc, $createdBy,
                $updatedAtUtc, $updatedByInstanceId,
                $isDeleted, $deletedAtUtc,
                $sourceKind
            )
            ON CONFLICT(id) DO UPDATE SET
                title = excluded.title,
                symptom = excluded.symptom,
                rootCause = excluded.rootCause,
                solution = excluded.solution,
                environmentJson = excluded.environmentJson,
                severity = excluded.severity,
                status = excluded.status,
                createdAtUtc = excluded.createdAtUtc,
                createdBy = excluded.createdBy,
                updatedAtUtc = excluded.updatedAtUtc,
                updatedByInstanceId = excluded.updatedByInstanceId,
                isDeleted = excluded.isDeleted,
                deletedAtUtc = excluded.deletedAtUtc,
                sourceKind = excluded.sourceKind;
            """;

        cmd.Parameters.AddWithValue("$id", problem.Id);
        cmd.Parameters.AddWithValue("$title", problem.Title);
        cmd.Parameters.AddWithValue("$symptom", problem.Symptom);
        cmd.Parameters.AddWithValue("$rootCause", problem.RootCause);
        cmd.Parameters.AddWithValue("$solution", problem.Solution);
        cmd.Parameters.AddWithValue("$environmentJson", problem.EnvironmentJson);
        cmd.Parameters.AddWithValue("$severity", problem.Severity);
        cmd.Parameters.AddWithValue("$status", problem.Status);
        cmd.Parameters.AddWithValue("$createdAtUtc", ToUtcText(problem.CreatedAtUtc));
        cmd.Parameters.AddWithValue("$createdBy", problem.CreatedBy);
        cmd.Parameters.AddWithValue("$updatedAtUtc", ToUtcText(problem.UpdatedAtUtc));
        cmd.Parameters.AddWithValue("$updatedByInstanceId", problem.UpdatedByInstanceId);
        cmd.Parameters.AddWithValue("$isDeleted", problem.IsDeleted ? 1 : 0);
        cmd.Parameters.AddWithValue("$deletedAtUtc", problem.DeletedAtUtc is null ? (object)DBNull.Value : ToUtcText(problem.DeletedAtUtc.Value));
        cmd.Parameters.AddWithValue("$sourceKind", (int)problem.SourceKind);

        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task UpsertProblemFtsAsync(SqliteConnection connection, SqliteTransaction tx, Problem problem, CancellationToken cancellationToken)
    {
        await using (var deleteCmd = connection.CreateCommand())
        {
            deleteCmd.Transaction = tx;
            deleteCmd.CommandText = "DELETE FROM problem_fts WHERE problemId = $id;";
            deleteCmd.Parameters.AddWithValue("$id", problem.Id);
            await deleteCmd.ExecuteNonQueryAsync(cancellationToken);
        }

        if (problem.IsDeleted)
        {
            return;
        }

        await using var insertCmd = connection.CreateCommand();
        insertCmd.Transaction = tx;
        insertCmd.CommandText = """
            INSERT INTO problem_fts (problemId, title, symptom, rootCause, solution, environmentJson)
            VALUES ($id, $title, $symptom, $rootCause, $solution, $environmentJson);
            """;
        insertCmd.Parameters.AddWithValue("$id", problem.Id);
        insertCmd.Parameters.AddWithValue("$title", problem.Title);
        insertCmd.Parameters.AddWithValue("$symptom", problem.Symptom);
        insertCmd.Parameters.AddWithValue("$rootCause", problem.RootCause);
        insertCmd.Parameters.AddWithValue("$solution", problem.Solution);
        insertCmd.Parameters.AddWithValue("$environmentJson", problem.EnvironmentJson);
        await insertCmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<DateTimeOffset?> GetLastExportedUpdatedAtUtcAsync(SqliteConnection connection, string localInstanceId, string remoteInstanceId, CancellationToken cancellationToken)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT lastExportedUpdatedAtUtc
            FROM exportState
            WHERE localInstanceId = $local AND remoteInstanceId = $remote
            LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$local", localInstanceId);
        cmd.Parameters.AddWithValue("$remote", remoteInstanceId);
        var result = await cmd.ExecuteScalarAsync(cancellationToken);
        if (result is null)
        {
            return null;
        }

        return ParseUtcText(result.ToString()!);
    }

    private static async Task UpdateExportStateAsync(SqliteConnection connection, string localInstanceId, string remoteInstanceId, DateTimeOffset lastExportedUpdatedAtUtc, string packageId, CancellationToken cancellationToken)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO exportState (localInstanceId, remoteInstanceId, lastExportedUpdatedAtUtc, lastExportedPackageId)
            VALUES ($local, $remote, $lastExportedUpdatedAtUtc, $packageId)
            ON CONFLICT(localInstanceId, remoteInstanceId) DO UPDATE SET
                lastExportedUpdatedAtUtc = excluded.lastExportedUpdatedAtUtc,
                lastExportedPackageId = excluded.lastExportedPackageId;
            """;
        cmd.Parameters.AddWithValue("$local", localInstanceId);
        cmd.Parameters.AddWithValue("$remote", remoteInstanceId);
        cmd.Parameters.AddWithValue("$lastExportedUpdatedAtUtc", ToUtcText(lastExportedUpdatedAtUtc));
        cmd.Parameters.AddWithValue("$packageId", packageId);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task UpdateSyncStateAsync(SqliteConnection connection, string localInstanceId, string remoteInstanceId, DateTimeOffset lastImportedUpdatedAtUtc, string packageId, CancellationToken cancellationToken)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO syncState (localInstanceId, remoteInstanceId, lastImportedUpdatedAtUtc, lastImportedPackageId)
            VALUES ($local, $remote, $lastImportedUpdatedAtUtc, $packageId)
            ON CONFLICT(localInstanceId, remoteInstanceId) DO UPDATE SET
                lastImportedUpdatedAtUtc = excluded.lastImportedUpdatedAtUtc,
                lastImportedPackageId = excluded.lastImportedPackageId;
            """;
        cmd.Parameters.AddWithValue("$local", localInstanceId);
        cmd.Parameters.AddWithValue("$remote", remoteInstanceId);
        cmd.Parameters.AddWithValue("$lastImportedUpdatedAtUtc", ToUtcText(lastImportedUpdatedAtUtc));
        cmd.Parameters.AddWithValue("$packageId", packageId);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string ToUtcText(DateTimeOffset value)
    {
        return value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    }

    private static DateTimeOffset ParseUtcText(string value)
    {
        return DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
    }
}
