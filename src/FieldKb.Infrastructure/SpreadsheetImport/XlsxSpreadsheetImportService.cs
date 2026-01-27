using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using FieldKb.Application.ImportExport;
using FieldKb.Domain.Models;
using FieldKb.Infrastructure.Sqlite;
using FieldKb.Infrastructure.Storage;
using Microsoft.Data.Sqlite;

namespace FieldKb.Infrastructure.SpreadsheetImport;

public sealed class XlsxSpreadsheetImportService : ISpreadsheetImportService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly SqliteConnectionFactory _connectionFactory;
    private readonly InstanceIdentityProvider _identityProvider;
    private readonly LocalInstanceContext _localContext;

    public XlsxSpreadsheetImportService(
        SqliteConnectionFactory connectionFactory,
        InstanceIdentityProvider identityProvider,
        LocalInstanceContext localContext)
    {
        _connectionFactory = connectionFactory;
        _identityProvider = identityProvider;
        _localContext = localContext;
    }

    public Task<SpreadsheetImportReport> PreviewAsync(SpreadsheetImportRequest request, CancellationToken cancellationToken)
    {
        return RunAsync(request, dryRun: true, cancellationToken);
    }

    public Task<SpreadsheetImportReport> ImportAsync(SpreadsheetImportRequest request, CancellationToken cancellationToken)
    {
        return RunAsync(request, dryRun: false, cancellationToken);
    }

    private async Task<SpreadsheetImportReport> RunAsync(SpreadsheetImportRequest request, bool dryRun, CancellationToken cancellationToken)
    {
        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        if (string.IsNullOrWhiteSpace(request.InputPath) || !File.Exists(request.InputPath))
        {
            throw new FileNotFoundException("Import file not found.", request.InputPath);
        }

        var startedAtUtc = DateTimeOffset.UtcNow;
        var errors = new List<string>();

        var tempDir = Path.Combine(Path.GetTempPath(), "FieldKb", "spreadsheet-import", Guid.NewGuid().ToString("D"));
        Directory.CreateDirectory(tempDir);

        var extractedXlsxPath = request.InputPath;
        string? extractedAttachmentsDir = null;

        try
        {
            if (Path.GetExtension(request.InputPath).Equals(".zip", StringComparison.OrdinalIgnoreCase))
            {
                ZipFile.ExtractToDirectory(request.InputPath, tempDir);
                extractedXlsxPath = FindXlsxInBundle(tempDir) ?? throw new InvalidOperationException("xlsx not found in bundle.");
                extractedAttachmentsDir = Path.Combine(tempDir, "attachments");
            }

            var sheets = SimpleXlsxReader.ReadSheets(extractedXlsxPath);

            if (!sheets.TryGetValue("Problems", out var problemsSheet))
            {
                errors.Add("缺少工作表：Problems");
                return BuildReport(startedAtUtc, DateTimeOffset.UtcNow, errors);
            }

            var problems = ParseProblems(problemsSheet, errors);
            var links = sheets.TryGetValue("ProblemTags", out var linksSheet)
                ? ParseProblemTagLinks(linksSheet, errors)
                : new List<ProblemTagLinkRow>();

            var tags = sheets.TryGetValue("Tags", out var tagsSheet)
                ? ParseTags(tagsSheet, errors)
                : DeriveTagsFromLinks(links);

            var attachments = sheets.TryGetValue("Attachments", out var attachmentsSheet)
                ? ParseAttachments(attachmentsSheet, errors)
                : new List<Attachment>();

            var tagsInFileCount = tags.Count;
            var problemTagLinksInFileCount = links.Count;
            var attachmentsInFileCount = attachments.Count;

            var missingAttachmentFiles = 0;
            if (attachments.Count > 0)
            {
                if (extractedAttachmentsDir is null || !Directory.Exists(extractedAttachmentsDir))
                {
                    missingAttachmentFiles = attachments.Count(a => !a.IsDeleted);
                    errors.Add("导入文件未包含附件文件包（attachments/），附件内容无法恢复。");
                }
                else
                {
                    foreach (var a in attachments)
                    {
                        if (a.IsDeleted)
                        {
                            continue;
                        }

                        if (!File.Exists(Path.Combine(extractedAttachmentsDir, a.ContentHash)))
                        {
                            missingAttachmentFiles++;
                        }
                    }
                }
            }

            if (dryRun)
            {
                var finishedAtUtc = DateTimeOffset.UtcNow;
                return new SpreadsheetImportReport(
                    StartedAtUtc: startedAtUtc,
                    FinishedAtUtc: finishedAtUtc,
                    ProblemsInFile: problems.Count,
                    TagsInFile: tagsInFileCount,
                    ProblemTagLinksInFile: problemTagLinksInFileCount,
                    AttachmentsInFile: attachmentsInFileCount,
                    MissingAttachmentFiles: missingAttachmentFiles,
                    ImportedCount: 0,
                    SkippedCount: 0,
                    ConflictCount: 0,
                    Errors: errors);
            }

            var identity = await _identityProvider.GetOrCreateAsync(_localContext.Kind, cancellationToken);
            var nowUtc = DateTimeOffset.UtcNow;

            await using var connection = await _connectionFactory.OpenAsync(cancellationToken);
            await SqliteMigrations.ApplyAsync(connection, cancellationToken);
            await using var tx = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

            (tags, links) = await RemapImportedTagIdsAsync(connection, tx, tags, links, cancellationToken);

            var imported = 0;
            var skipped = 0;
            var conflicts = 0;
            var problemsImported = 0;
            var tagsImported = 0;
            var attachmentsImported = 0;

            foreach (var p in problems)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var result = await ApplyProblemAsync(connection, tx, p, request.ConflictPolicy, cancellationToken);
                imported += result.Imported ? 1 : 0;
                skipped += result.Skipped ? 1 : 0;
                conflicts += result.ConflictRecorded ? 1 : 0;
                problemsImported += result.Imported ? 1 : 0;
            }

            foreach (var t in tags)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var result = await ApplyTagAsync(connection, tx, t, request.ConflictPolicy, cancellationToken);
                imported += result.Imported ? 1 : 0;
                skipped += result.Skipped ? 1 : 0;
                conflicts += result.ConflictRecorded ? 1 : 0;
                tagsImported += result.Imported ? 1 : 0;
            }

            var desiredTagsByProblem = links
                .Where(l => !l.ProblemTagIsDeleted && !l.TagIsDeleted)
                .GroupBy(l => l.ProblemId, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.Select(x => x.TagId).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.Ordinal).ToArray(), StringComparer.Ordinal);

            var problemTagsApplied = 0;
            foreach (var kv in desiredTagsByProblem)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await ApplyProblemTagsAsync(connection, tx, kv.Key, kv.Value, request.TagMergeMode, nowUtc, identity.InstanceId, cancellationToken);
                problemTagsApplied++;
            }

            if (extractedAttachmentsDir is not null && Directory.Exists(extractedAttachmentsDir))
            {
                foreach (var a in attachments)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (a.IsDeleted)
                    {
                        continue;
                    }

                    var filePath = Path.Combine(extractedAttachmentsDir, a.ContentHash);
                    if (!File.Exists(filePath))
                    {
                        errors.Add($"附件文件缺失：{a.ContentHash}（AttachmentId={a.Id}）");
                        continue;
                    }

                    var result = await ApplyAttachmentAsync(connection, tx, a, request.ConflictPolicy, cancellationToken);
                    imported += result.Imported ? 1 : 0;
                    skipped += result.Skipped ? 1 : 0;
                    conflicts += result.ConflictRecorded ? 1 : 0;
                    attachmentsImported += result.Imported ? 1 : 0;
                }
            }

            await tx.CommitAsync(cancellationToken);

            if (extractedAttachmentsDir is not null && Directory.Exists(extractedAttachmentsDir))
            {
                await CopyImportedAttachmentsAsync(extractedAttachmentsDir, cancellationToken);
            }

            var finishedAtUtc2 = DateTimeOffset.UtcNow;
            return new SpreadsheetImportReport(
                StartedAtUtc: startedAtUtc,
                FinishedAtUtc: finishedAtUtc2,
                ProblemsInFile: problems.Count,
                TagsInFile: tagsInFileCount,
                ProblemTagLinksInFile: problemTagLinksInFileCount,
                AttachmentsInFile: attachmentsInFileCount,
                MissingAttachmentFiles: missingAttachmentFiles,
                ImportedCount: imported,
                SkippedCount: skipped,
                ConflictCount: conflicts,
                Errors: errors)
            {
                ProblemsImportedCount = problemsImported,
                TagsImportedCount = tagsImported,
                AttachmentsImportedCount = attachmentsImported,
                ProblemTagsAppliedCount = problemTagsApplied
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

    private static SpreadsheetImportReport BuildReport(DateTimeOffset startedAtUtc, DateTimeOffset finishedAtUtc, IReadOnlyList<string> errors)
    {
        return new SpreadsheetImportReport(
            StartedAtUtc: startedAtUtc,
            FinishedAtUtc: finishedAtUtc,
            ProblemsInFile: 0,
            TagsInFile: 0,
            ProblemTagLinksInFile: 0,
            AttachmentsInFile: 0,
            MissingAttachmentFiles: 0,
            ImportedCount: 0,
            SkippedCount: 0,
            ConflictCount: 0,
            Errors: errors);
    }

    private static string? FindXlsxInBundle(string extractedRoot)
    {
        var candidates = Directory.EnumerateFiles(extractedRoot, "*.xlsx", SearchOption.AllDirectories).ToArray();
        if (candidates.Length == 0)
        {
            return null;
        }

        var data = candidates.FirstOrDefault(p => Path.GetFileName(p).Equals("data.xlsx", StringComparison.OrdinalIgnoreCase));
        return data ?? candidates[0];
    }

    private sealed record ProblemTagLinkRow(string ProblemId, string TagId, string TagName, bool ProblemTagIsDeleted, bool TagIsDeleted);

    private static List<Problem> ParseProblems(SimpleXlsxReader.Sheet sheet, List<string> errors)
    {
        var rows = sheet.Rows;
        if (rows.Count <= 1)
        {
            return new List<Problem>();
        }

        var map = BuildHeaderMap(rows[0]);
        var required = new[] { "ProblemId", "Title", "Symptom", "RootCause", "Solution", "EnvironmentJson", "CreatedAtUtc", "CreatedBy", "UpdatedAtUtc", "UpdatedByInstanceId", "IsDeleted", "DeletedAtUtc", "SourceKind" };
        foreach (var key in required)
        {
            if (!map.ContainsKey(key))
            {
                errors.Add($"Problems 表缺少列：{key}");
            }
        }

        var problems = new List<Problem>();
        for (var i = 1; i < rows.Count; i++)
        {
            var row = rows[i];
            var id = GetCell(map, row, "ProblemId");
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            if (!TryParseUtc(GetCell(map, row, "CreatedAtUtc"), out var createdAtUtc))
            {
                errors.Add($"Problems 第{i + 1}行：CreatedAtUtc 无效");
                continue;
            }

            if (!TryParseUtc(GetCell(map, row, "UpdatedAtUtc"), out var updatedAtUtc))
            {
                errors.Add($"Problems 第{i + 1}行：UpdatedAtUtc 无效");
                continue;
            }

            var isDeleted = GetCell(map, row, "IsDeleted") == "1";
            DateTimeOffset? deletedAtUtc = null;
            var deletedText = GetCell(map, row, "DeletedAtUtc");
            if (!string.IsNullOrWhiteSpace(deletedText) && TryParseUtc(deletedText, out var parsedDeleted))
            {
                deletedAtUtc = parsedDeleted;
            }

            var sourceKind = (SourceKind)TryParseInt(GetCell(map, row, "SourceKind"), defaultValue: 0);
            var severity = map.ContainsKey("Severity") ? TryParseInt(GetCell(map, row, "Severity"), defaultValue: 0) : 0;
            var status = map.ContainsKey("Status") ? TryParseInt(GetCell(map, row, "Status"), defaultValue: 0) : 0;

            var env = GetCell(map, row, "EnvironmentJson");
            if (string.IsNullOrWhiteSpace(env))
            {
                env = "{}";
            }

            problems.Add(new Problem(
                Id: id,
                Title: GetCell(map, row, "Title"),
                Symptom: GetCell(map, row, "Symptom"),
                RootCause: GetCell(map, row, "RootCause"),
                Solution: GetCell(map, row, "Solution"),
                EnvironmentJson: env,
                Severity: severity,
                Status: status,
                CreatedAtUtc: createdAtUtc,
                CreatedBy: GetCell(map, row, "CreatedBy"),
                UpdatedAtUtc: updatedAtUtc,
                UpdatedByInstanceId: GetCell(map, row, "UpdatedByInstanceId"),
                IsDeleted: isDeleted,
                DeletedAtUtc: deletedAtUtc,
                SourceKind: sourceKind));
        }

        return problems;
    }

    private static List<Tag> ParseTags(SimpleXlsxReader.Sheet sheet, List<string> errors)
    {
        var rows = sheet.Rows;
        if (rows.Count <= 1)
        {
            return new List<Tag>();
        }

        var map = BuildHeaderMap(rows[0]);
        var required = new[] { "TagId", "Name", "CreatedAtUtc", "UpdatedAtUtc", "UpdatedByInstanceId", "IsDeleted" };
        foreach (var key in required)
        {
            if (!map.ContainsKey(key))
            {
                errors.Add($"Tags 表缺少列：{key}");
            }
        }

        var tags = new List<Tag>();
        for (var i = 1; i < rows.Count; i++)
        {
            var row = rows[i];
            var id = GetCell(map, row, "TagId");
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            if (!TryParseUtc(GetCell(map, row, "CreatedAtUtc"), out var createdAtUtc))
            {
                errors.Add($"Tags 第{i + 1}行：CreatedAtUtc 无效");
                continue;
            }

            if (!TryParseUtc(GetCell(map, row, "UpdatedAtUtc"), out var updatedAtUtc))
            {
                errors.Add($"Tags 第{i + 1}行：UpdatedAtUtc 无效");
                continue;
            }

            var isDeleted = GetCell(map, row, "IsDeleted") == "1";
            tags.Add(new Tag(
                Id: id,
                Name: GetCell(map, row, "Name"),
                CreatedAtUtc: createdAtUtc,
                UpdatedAtUtc: updatedAtUtc,
                UpdatedByInstanceId: GetCell(map, row, "UpdatedByInstanceId"),
                IsDeleted: isDeleted));
        }

        return tags;
    }

    private static List<ProblemTagLinkRow> ParseProblemTagLinks(SimpleXlsxReader.Sheet sheet, List<string> errors)
    {
        var rows = sheet.Rows;
        if (rows.Count <= 1)
        {
            return new List<ProblemTagLinkRow>();
        }

        var map = BuildHeaderMap(rows[0]);
        var required = new[] { "ProblemId", "TagId", "TagName", "ProblemTagIsDeleted", "TagIsDeleted" };
        foreach (var key in required)
        {
            if (!map.ContainsKey(key))
            {
                errors.Add($"ProblemTags 表缺少列：{key}");
            }
        }

        var links = new List<ProblemTagLinkRow>();
        for (var i = 1; i < rows.Count; i++)
        {
            var row = rows[i];
            var problemId = GetCell(map, row, "ProblemId");
            var tagId = GetCell(map, row, "TagId");
            if (string.IsNullOrWhiteSpace(problemId) || string.IsNullOrWhiteSpace(tagId))
            {
                continue;
            }

            links.Add(new ProblemTagLinkRow(
                ProblemId: problemId,
                TagId: tagId,
                TagName: GetCell(map, row, "TagName"),
                ProblemTagIsDeleted: GetCell(map, row, "ProblemTagIsDeleted") == "1",
                TagIsDeleted: GetCell(map, row, "TagIsDeleted") == "1"));
        }

        return links;
    }

    private static List<Tag> DeriveTagsFromLinks(IReadOnlyList<ProblemTagLinkRow> links)
    {
        var nowUtc = DateTimeOffset.UtcNow;
        return links
            .GroupBy(l => l.TagId, StringComparer.Ordinal)
            .Select(g =>
            {
                var any = g.First();
                return new Tag(
                    Id: any.TagId,
                    Name: any.TagName,
                    CreatedAtUtc: nowUtc,
                    UpdatedAtUtc: nowUtc,
                    UpdatedByInstanceId: "import",
                    IsDeleted: any.TagIsDeleted);
            })
            .ToList();
    }

    private static async Task<(List<Tag> Tags, List<ProblemTagLinkRow> Links)> RemapImportedTagIdsAsync(
        SqliteConnection connection,
        SqliteTransaction tx,
        List<Tag> tags,
        List<ProblemTagLinkRow> links,
        CancellationToken cancellationToken)
    {
        var existingIdByNormalizedName = new Dictionary<string, string>(StringComparer.Ordinal);
        await using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                SELECT id, name
                FROM tag
                WHERE isDeleted = 0 AND trim(name) <> '';
                """;
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var id = reader.GetString(0);
                var name = reader.GetString(1);
                var normalized = NormalizeTagName(name);
                if (normalized is null)
                {
                    continue;
                }

                existingIdByNormalizedName.TryAdd(normalized, id);
            }
        }

        var canonicalImportIdByNormalizedName = new Dictionary<string, string>(StringComparer.Ordinal);
        var idRemap = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var t in tags)
        {
            var normalized = NormalizeTagName(t.Name);
            if (normalized is null)
            {
                continue;
            }

            if (existingIdByNormalizedName.TryGetValue(normalized, out var existingId))
            {
                if (!string.Equals(t.Id, existingId, StringComparison.Ordinal))
                {
                    idRemap[t.Id] = existingId;
                }
                continue;
            }

            if (canonicalImportIdByNormalizedName.TryGetValue(normalized, out var canonicalImportId))
            {
                if (!string.Equals(t.Id, canonicalImportId, StringComparison.Ordinal))
                {
                    idRemap[t.Id] = canonicalImportId;
                }
                continue;
            }

            canonicalImportIdByNormalizedName[normalized] = t.Id;
        }

        foreach (var l in links)
        {
            var normalized = NormalizeTagName(l.TagName);
            if (normalized is null)
            {
                continue;
            }

            if (existingIdByNormalizedName.TryGetValue(normalized, out var existingId))
            {
                if (!string.Equals(l.TagId, existingId, StringComparison.Ordinal))
                {
                    idRemap[l.TagId] = existingId;
                }
                continue;
            }

            if (canonicalImportIdByNormalizedName.TryGetValue(normalized, out var canonicalImportId))
            {
                if (!string.Equals(l.TagId, canonicalImportId, StringComparison.Ordinal))
                {
                    idRemap[l.TagId] = canonicalImportId;
                }
            }
        }

        var remappedLinks = links
            .Select(l => idRemap.TryGetValue(l.TagId, out var newId) ? l with { TagId = newId } : l)
            .ToList();

        var dedupedTagsById = new Dictionary<string, Tag>(StringComparer.Ordinal);
        foreach (var t in tags)
        {
            if (idRemap.TryGetValue(t.Id, out var newId) && !string.Equals(newId, t.Id, StringComparison.Ordinal))
            {
                continue;
            }

            var trimmedName = (t.Name ?? string.Empty).Trim();
            var cleaned = t with { Name = trimmedName };
            dedupedTagsById.TryAdd(cleaned.Id, cleaned);
        }

        return (dedupedTagsById.Values.ToList(), remappedLinks);
    }

    private static string? NormalizeTagName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var trimmed = name.Trim();
        return trimmed.Length == 0 ? null : trimmed.ToLowerInvariant();
    }

    private static List<Attachment> ParseAttachments(SimpleXlsxReader.Sheet sheet, List<string> errors)
    {
        var rows = sheet.Rows;
        if (rows.Count <= 1)
        {
            return new List<Attachment>();
        }

        var map = BuildHeaderMap(rows[0]);
        var required = new[] { "AttachmentId", "ProblemId", "OriginalFileName", "ContentHash", "SizeBytes", "MimeType", "CreatedAtUtc", "UpdatedAtUtc", "UpdatedByInstanceId", "IsDeleted" };
        foreach (var key in required)
        {
            if (!map.ContainsKey(key))
            {
                errors.Add($"Attachments 表缺少列：{key}");
            }
        }

        var list = new List<Attachment>();
        for (var i = 1; i < rows.Count; i++)
        {
            var row = rows[i];
            var id = GetCell(map, row, "AttachmentId");
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            if (!TryParseUtc(GetCell(map, row, "CreatedAtUtc"), out var createdAtUtc))
            {
                errors.Add($"Attachments 第{i + 1}行：CreatedAtUtc 无效");
                continue;
            }

            if (!TryParseUtc(GetCell(map, row, "UpdatedAtUtc"), out var updatedAtUtc))
            {
                errors.Add($"Attachments 第{i + 1}行：UpdatedAtUtc 无效");
                continue;
            }

            var sizeBytes = long.TryParse(GetCell(map, row, "SizeBytes"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedSize) ? parsedSize : 0;
            var isDeleted = GetCell(map, row, "IsDeleted") == "1";

            list.Add(new Attachment(
                Id: id,
                ProblemId: GetCell(map, row, "ProblemId"),
                OriginalFileName: GetCell(map, row, "OriginalFileName"),
                ContentHash: GetCell(map, row, "ContentHash"),
                SizeBytes: sizeBytes,
                MimeType: GetCell(map, row, "MimeType"),
                CreatedAtUtc: createdAtUtc,
                UpdatedAtUtc: updatedAtUtc,
                UpdatedByInstanceId: GetCell(map, row, "UpdatedByInstanceId"),
                IsDeleted: isDeleted));
        }

        return list;
    }

    private static Dictionary<string, int> BuildHeaderMap(IReadOnlyList<string> headerRow)
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < headerRow.Count; i++)
        {
            var key = (headerRow[i] ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            map[key] = i;
        }

        return map;
    }

    private static string GetCell(Dictionary<string, int> map, IReadOnlyList<string> row, string key)
    {
        if (!map.TryGetValue(key, out var idx))
        {
            return string.Empty;
        }

        return idx >= 0 && idx < row.Count ? (row[idx] ?? string.Empty) : string.Empty;
    }

    private static bool TryParseUtc(string value, out DateTimeOffset parsed)
    {
        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out parsed);
    }

    private static int TryParseInt(string value, int defaultValue)
    {
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : defaultValue;
    }

    private sealed record ApplyResult(bool Imported, bool Skipped, bool ConflictRecorded);

    private static async Task<ApplyResult> ApplyProblemAsync(SqliteConnection connection, SqliteTransaction tx, Problem problem, SpreadsheetImportConflictPolicy policy, CancellationToken cancellationToken)
    {
        if (policy == SpreadsheetImportConflictPolicy.SkipIfLocalNewer)
        {
            var local = await GetLocalMetaAsync(connection, tx, "problem", problem.Id, cancellationToken);
            if (local is not null)
            {
                var compare = CompareRemoteVsLocal(problem.UpdatedAtUtc, problem.UpdatedByInstanceId, local.Value.UpdatedAtUtc, local.Value.UpdatedByInstanceId);
                if (compare < 0)
                {
                    var conflictId = Guid.NewGuid().ToString("D");
                    await InsertConflictAsync(connection, tx, conflictId, "Problem", problem.Id, problem.UpdatedAtUtc, local.Value.UpdatedAtUtc, local.Value.LocalJson, JsonSerializer.Serialize(problem, JsonOptions), cancellationToken);
                    return new ApplyResult(false, true, true);
                }
            }
        }

        await UpsertProblemRowAsync(connection, tx, problem, cancellationToken);
        await UpsertProblemFtsAsync(connection, tx, problem, cancellationToken);
        return new ApplyResult(true, false, false);
    }

    private static async Task<ApplyResult> ApplyTagAsync(SqliteConnection connection, SqliteTransaction tx, Tag tag, SpreadsheetImportConflictPolicy policy, CancellationToken cancellationToken)
    {
        if (policy == SpreadsheetImportConflictPolicy.SkipIfLocalNewer)
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

    private static async Task ApplyProblemTagsAsync(
        SqliteConnection connection,
        SqliteTransaction tx,
        string problemId,
        IReadOnlyList<string> tagIds,
        SpreadsheetImportTagMergeMode mergeMode,
        DateTimeOffset nowUtc,
        string localInstanceId,
        CancellationToken cancellationToken)
    {
        tagIds ??= Array.Empty<string>();
        var normalized = tagIds.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.Ordinal).ToArray();
        if (mergeMode == SpreadsheetImportTagMergeMode.Merge)
        {
            var local = await GetLocalTagIdsForProblemAsync(connection, tx, problemId, cancellationToken);
            normalized = local
                .Concat(normalized)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
        }

        var keep = new HashSet<string>(normalized, StringComparer.Ordinal);

        await using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                UPDATE problemTag
                SET isDeleted = 1,
                    updatedAtUtc = $updatedAtUtc,
                    updatedByInstanceId = $updatedByInstanceId
                WHERE problemId = $problemId AND isDeleted = 0;
                """;
            cmd.Parameters.AddWithValue("$problemId", problemId);
            cmd.Parameters.AddWithValue("$updatedAtUtc", ToUtcText(nowUtc));
            cmd.Parameters.AddWithValue("$updatedByInstanceId", localInstanceId);
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var tagId in keep)
        {
            int changed;
            await using (var updateCmd = connection.CreateCommand())
            {
                updateCmd.Transaction = tx;
                updateCmd.CommandText = """
                    UPDATE problemTag
                    SET isDeleted = 0,
                        updatedAtUtc = $updatedAtUtc,
                        updatedByInstanceId = $updatedByInstanceId
                    WHERE problemId = $problemId AND tagId = $tagId;
                    """;
                updateCmd.Parameters.AddWithValue("$problemId", problemId);
                updateCmd.Parameters.AddWithValue("$tagId", tagId);
                updateCmd.Parameters.AddWithValue("$updatedAtUtc", ToUtcText(nowUtc));
                updateCmd.Parameters.AddWithValue("$updatedByInstanceId", localInstanceId);
                changed = await updateCmd.ExecuteNonQueryAsync(cancellationToken);
            }

            if (changed > 0)
            {
                continue;
            }

            await using var insertCmd = connection.CreateCommand();
            insertCmd.Transaction = tx;
            insertCmd.CommandText = """
                INSERT INTO problemTag (id, problemId, tagId, createdAtUtc, updatedAtUtc, updatedByInstanceId, isDeleted)
                VALUES ($id, $problemId, $tagId, $createdAtUtc, $updatedAtUtc, $updatedByInstanceId, 0);
                """;
            insertCmd.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("D"));
            insertCmd.Parameters.AddWithValue("$problemId", problemId);
            insertCmd.Parameters.AddWithValue("$tagId", tagId);
            insertCmd.Parameters.AddWithValue("$createdAtUtc", ToUtcText(nowUtc));
            insertCmd.Parameters.AddWithValue("$updatedAtUtc", ToUtcText(nowUtc));
            insertCmd.Parameters.AddWithValue("$updatedByInstanceId", localInstanceId);
            await insertCmd.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task<IReadOnlyList<string>> GetLocalTagIdsForProblemAsync(SqliteConnection connection, SqliteTransaction tx, string problemId, CancellationToken cancellationToken)
    {
        await using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            SELECT tagId
            FROM problemTag
            WHERE problemId = $problemId AND isDeleted = 0;
            """;
        cmd.Parameters.AddWithValue("$problemId", problemId);

        var list = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            list.Add(reader.GetString(0));
        }

        return list;
    }

    private static async Task<ApplyResult> ApplyAttachmentAsync(SqliteConnection connection, SqliteTransaction tx, Attachment attachment, SpreadsheetImportConflictPolicy policy, CancellationToken cancellationToken)
    {
        if (policy == SpreadsheetImportConflictPolicy.SkipIfLocalNewer)
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

    private static async Task CopyImportedAttachmentsAsync(string extractedAttachmentsDir, CancellationToken cancellationToken)
    {
        var destDir = AppDataPaths.GetAttachmentsDirectory();
        Directory.CreateDirectory(destDir);

        foreach (var file in Directory.EnumerateFiles(extractedAttachmentsDir))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = Path.GetFileName(file);
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

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

        return string.CompareOrdinal(remoteBy ?? string.Empty, localBy ?? string.Empty);
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

    private static string ToUtcText(DateTimeOffset value)
    {
        return value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    }

    private static DateTimeOffset ParseUtcText(string value)
    {
        return DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
    }
}
