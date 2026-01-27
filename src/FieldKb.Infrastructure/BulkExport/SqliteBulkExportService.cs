using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using FieldKb.Application.Abstractions;
using FieldKb.Domain.Models;
using FieldKb.Infrastructure.Sqlite;
using FieldKb.Infrastructure.Storage;
using Microsoft.Data.Sqlite;

namespace FieldKb.Infrastructure.BulkExport;

public sealed class SqliteBulkExportService : IBulkExportService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly SqliteConnectionFactory _connectionFactory;

    public SqliteBulkExportService(SqliteConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async Task BulkExportAsync(BulkExportRequest request, CancellationToken cancellationToken)
    {
        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        if (string.IsNullOrWhiteSpace(request.OutputPath))
        {
            throw new ArgumentException("OutputPath is required.", nameof(request));
        }

        var filter = request.Filter ?? new ProblemHardDeleteFilter(Array.Empty<string>(), "all", null, null, false);

        var outputDir = Path.GetDirectoryName(request.OutputPath);
        if (!string.IsNullOrWhiteSpace(outputDir))
        {
            Directory.CreateDirectory(outputDir);
        }

        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);
        await SqliteMigrations.ApplyAsync(connection, cancellationToken);
        await using var tx = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

        await CreateTargetsAsync(connection, tx, filter, cancellationToken);

        var problems = await ReadProblemsAsync(connection, tx, filter, cancellationToken);
        var links = await ReadProblemTagLinksAsync(connection, tx, filter, cancellationToken);
        var tags = await ReadTagsForTargetsAsync(connection, tx, cancellationToken);
        var attachments = await ReadAttachmentsAsync(connection, tx, filter, cancellationToken);

        await tx.CommitAsync(cancellationToken);

        switch (request.Format)
        {
            case BulkExportFormat.Csv:
                await WriteCsvAsync(request.OutputPath, problems, links, attachments, cancellationToken);
                break;
            case BulkExportFormat.Jsonl:
                await WriteJsonlAsync(request.OutputPath, problems, links, attachments, cancellationToken);
                break;
            case BulkExportFormat.Xlsx:
                await WriteXlsxAsync(request.OutputPath, problems, tags, links, attachments, cancellationToken);
                break;
            case BulkExportFormat.XlsxBundle:
                await WriteXlsxBundleAsync(request.OutputPath, problems, tags, links, attachments, cancellationToken);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(request.Format), request.Format, "Unsupported export format.");
        }
    }

    private static async Task CreateTargetsAsync(SqliteConnection connection, SqliteTransaction tx, ProblemHardDeleteFilter filter, CancellationToken cancellationToken)
    {
        filter ??= new ProblemHardDeleteFilter(Array.Empty<string>(), "all", null, null, false);
        var tagIds = filter.TagIds ?? Array.Empty<string>();

        await using (var create = connection.CreateCommand())
        {
            create.Transaction = tx;
            create.CommandText = "CREATE TEMP TABLE IF NOT EXISTS __targets (id TEXT PRIMARY KEY); DELETE FROM __targets;";
            await create.ExecuteNonQueryAsync(cancellationToken);
        }

        var professionWhere = BuildProfessionWhere(filter.ProfessionFilterId, out var professionParamValue);
        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = tx;
            if (professionWhere != "1=1")
            {
                insert.Parameters.AddWithValue("$professionPattern", professionParamValue!);
            }

            var deletedWhere = filter.IncludeSoftDeleted ? "1=1" : "p.isDeleted = 0";
            var timeWhere = BuildUpdatedAtWhere(filter.UpdatedFromUtc, filter.UpdatedToUtc, insert);
            var whereAll = $"{deletedWhere} AND ({professionWhere}) AND ({timeWhere})";

            if (tagIds.Count == 0)
            {
                insert.CommandText = $"""
                    INSERT OR IGNORE INTO __targets (id)
                    SELECT p.id
                    FROM problem p
                    WHERE {whereAll};
                    """;
            }
            else
            {
                var inParams = new List<string>();
                for (var i = 0; i < tagIds.Count; i++)
                {
                    var p = $"$t{i}";
                    inParams.Add(p);
                    insert.Parameters.AddWithValue(p, tagIds[i]);
                }

                insert.Parameters.AddWithValue("$tagCount", tagIds.Count);

                insert.CommandText = $"""
                    INSERT OR IGNORE INTO __targets (id)
                    SELECT p.id
                    FROM problem p
                    JOIN problemTag pt ON pt.problemId = p.id AND pt.isDeleted = 0
                    JOIN tag t ON t.id = pt.tagId AND t.isDeleted = 0
                    WHERE {whereAll} AND pt.tagId IN ({string.Join(", ", inParams)})
                    GROUP BY p.id
                    HAVING COUNT(DISTINCT pt.tagId) = $tagCount;
                    """;
            }

            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task<IReadOnlyList<Problem>> ReadProblemsAsync(SqliteConnection connection, SqliteTransaction tx, ProblemHardDeleteFilter filter, CancellationToken cancellationToken)
    {
        var deletedWhere = filter.IncludeSoftDeleted ? "1=1" : "p.isDeleted = 0";
        await using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"""
            SELECT
                p.id, p.title, p.symptom, p.rootCause, p.solution, p.environmentJson,
                p.severity, p.status, p.createdAtUtc, p.createdBy, p.updatedAtUtc, p.updatedByInstanceId,
                p.isDeleted, p.deletedAtUtc, p.sourceKind
            FROM problem p
            WHERE {deletedWhere} AND p.id IN (SELECT id FROM __targets)
            ORDER BY p.updatedAtUtc DESC;
            """;

        var list = new List<Problem>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            list.Add(MapProblem(reader));
        }

        return list;
    }

    private static async Task<IReadOnlyList<ProblemTagLink>> ReadProblemTagLinksAsync(SqliteConnection connection, SqliteTransaction tx, ProblemHardDeleteFilter filter, CancellationToken cancellationToken)
    {
        var includeSoftDeleted = filter.IncludeSoftDeleted;
        var ptDeletedWhere = includeSoftDeleted ? "1=1" : "pt.isDeleted = 0";
        var tagDeletedWhere = includeSoftDeleted ? "1=1" : "t.isDeleted = 0";

        await using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"""
            SELECT pt.problemId, pt.tagId, t.name, pt.isDeleted, t.isDeleted
            FROM problemTag pt
            JOIN tag t ON t.id = pt.tagId
            WHERE pt.problemId IN (SELECT id FROM __targets)
              AND ({ptDeletedWhere})
              AND ({tagDeletedWhere})
            ORDER BY t.name COLLATE NOCASE;
            """;

        var list = new List<ProblemTagLink>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var problemId = reader.GetString(0);
            var tagId = reader.GetString(1);
            var tagName = reader.GetString(2);
            var ptIsDeleted = reader.GetInt32(3) != 0;
            var tIsDeleted = reader.GetInt32(4) != 0;
            list.Add(new ProblemTagLink(problemId, tagId, tagName, ptIsDeleted, tIsDeleted));
        }

        return list;
    }

    private static async Task<IReadOnlyList<Tag>> ReadTagsForTargetsAsync(SqliteConnection connection, SqliteTransaction tx, CancellationToken cancellationToken)
    {
        await using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            SELECT DISTINCT t.id, t.name, t.createdAtUtc, t.updatedAtUtc, t.updatedByInstanceId, t.isDeleted
            FROM tag t
            JOIN problemTag pt ON pt.tagId = t.id
            WHERE pt.problemId IN (SELECT id FROM __targets)
            ORDER BY t.name COLLATE NOCASE;
            """;

        var list = new List<Tag>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            list.Add(MapTag(reader));
        }

        return list;
    }

    private static async Task<IReadOnlyList<Attachment>> ReadAttachmentsAsync(SqliteConnection connection, SqliteTransaction tx, ProblemHardDeleteFilter filter, CancellationToken cancellationToken)
    {
        var deletedWhere = filter.IncludeSoftDeleted ? "1=1" : "a.isDeleted = 0";
        await using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"""
            SELECT a.id, a.problemId, a.originalFileName, a.contentHash, a.sizeBytes, a.mimeType,
                   a.createdAtUtc, a.updatedAtUtc, a.updatedByInstanceId, a.isDeleted
            FROM attachment a
            WHERE {deletedWhere} AND a.problemId IN (SELECT id FROM __targets)
            ORDER BY a.updatedAtUtc DESC;
            """;

        var list = new List<Attachment>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            list.Add(MapAttachment(reader));
        }

        return list;
    }

    private static async Task WriteJsonlAsync(
        string outputPath,
        IReadOnlyList<Problem> problems,
        IReadOnlyList<ProblemTagLink> links,
        IReadOnlyList<Attachment> attachments,
        CancellationToken cancellationToken)
    {
        var linkMap = links
            .Where(l => !l.ProblemTagIsDeleted && !l.TagIsDeleted)
            .GroupBy(l => l.ProblemId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(x => new { x.TagId, x.TagName }).ToArray(), StringComparer.Ordinal);

        var attMap = attachments
            .Where(a => !a.IsDeleted)
            .GroupBy(a => a.ProblemId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);

        await using var fs = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.Read);
        await using var writer = new StreamWriter(fs, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        foreach (var p in problems)
        {
            cancellationToken.ThrowIfCancellationRequested();
            linkMap.TryGetValue(p.Id, out var tags);
            attMap.TryGetValue(p.Id, out var atts);

            var line = JsonSerializer.Serialize(new
            {
                problem = p,
                tags = tags ?? Array.Empty<object>(),
                attachments = (atts ?? Array.Empty<Attachment>()).Select(a => new
                {
                    a.Id,
                    a.OriginalFileName,
                    a.ContentHash,
                    a.SizeBytes,
                    a.MimeType
                })
            }, JsonOptions);

            await writer.WriteLineAsync(line);
        }
    }

    private static async Task WriteCsvAsync(
        string outputPath,
        IReadOnlyList<Problem> problems,
        IReadOnlyList<ProblemTagLink> links,
        IReadOnlyList<Attachment> attachments,
        CancellationToken cancellationToken)
    {
        var linkMap = links
            .Where(l => !l.ProblemTagIsDeleted && !l.TagIsDeleted)
            .GroupBy(l => l.ProblemId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);

        var attMap = attachments
            .Where(a => !a.IsDeleted)
            .GroupBy(a => a.ProblemId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);

        await using var fs = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.Read);
        await using var writer = new StreamWriter(fs, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        var header = new[]
        {
            "ProblemId","Title","Symptom","RootCause","Solution","EnvironmentJson","Severity","Status",
            "CreatedAtUtc","CreatedBy","UpdatedAtUtc","UpdatedByInstanceId","IsDeleted","DeletedAtUtc","SourceKind",
            "TagNames","TagIds","AttachmentCount","AttachmentTotalBytes","Attachments"
        };
        await writer.WriteLineAsync(ToCsvLine(header));

        foreach (var p in problems)
        {
            cancellationToken.ThrowIfCancellationRequested();
            linkMap.TryGetValue(p.Id, out var tagLinks);
            attMap.TryGetValue(p.Id, out var atts);

            var tagNames = tagLinks is null ? string.Empty : string.Join(";", tagLinks.Select(t => t.TagName));
            var tagIds = tagLinks is null ? string.Empty : string.Join(";", tagLinks.Select(t => t.TagId));

            var attachmentCount = atts?.Length ?? 0;
            var attachmentBytes = atts?.Sum(a => a.SizeBytes) ?? 0;
            var attachmentText = atts is null
                ? string.Empty
                : string.Join(";", atts.Select(a => $"{a.OriginalFileName}|{a.ContentHash}|{a.SizeBytes}"));

            var row = new[]
            {
                p.Id,
                p.Title,
                p.Symptom,
                p.RootCause,
                p.Solution,
                p.EnvironmentJson,
                p.Severity.ToString(CultureInfo.InvariantCulture),
                p.Status.ToString(CultureInfo.InvariantCulture),
                p.CreatedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
                p.CreatedBy,
                p.UpdatedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
                p.UpdatedByInstanceId,
                p.IsDeleted ? "1" : "0",
                p.DeletedAtUtc?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) ?? string.Empty,
                ((int)p.SourceKind).ToString(CultureInfo.InvariantCulture),
                tagNames,
                tagIds,
                attachmentCount.ToString(CultureInfo.InvariantCulture),
                attachmentBytes.ToString(CultureInfo.InvariantCulture),
                attachmentText
            };

            await writer.WriteLineAsync(ToCsvLine(row));
        }
    }

    private static Task WriteXlsxAsync(
        string outputPath,
        IReadOnlyList<Problem> problems,
        IReadOnlyList<Tag> tags,
        IReadOnlyList<ProblemTagLink> links,
        IReadOnlyList<Attachment> attachments,
        CancellationToken cancellationToken)
    {
        var problemsSheet = new List<IReadOnlyList<string>>
        {
            new[]
            {
                "ProblemId","Title","Symptom","RootCause","Solution","EnvironmentJson",
                "Severity","Status",
                "CreatedAtUtc","CreatedBy","UpdatedAtUtc","UpdatedByInstanceId","IsDeleted","DeletedAtUtc","SourceKind"
            }
        };
        foreach (var p in problems)
        {
            cancellationToken.ThrowIfCancellationRequested();
            problemsSheet.Add(new[]
            {
                p.Id,
                p.Title,
                p.Symptom,
                p.RootCause,
                p.Solution,
                p.EnvironmentJson,
                p.Severity.ToString(CultureInfo.InvariantCulture),
                p.Status.ToString(CultureInfo.InvariantCulture),
                p.CreatedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
                p.CreatedBy,
                p.UpdatedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
                p.UpdatedByInstanceId,
                p.IsDeleted ? "1" : "0",
                p.DeletedAtUtc?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) ?? string.Empty,
                ((int)p.SourceKind).ToString(CultureInfo.InvariantCulture)
            });
        }

        var tagsSheet = new List<IReadOnlyList<string>>
        {
            new[] { "TagId", "Name", "CreatedAtUtc", "UpdatedAtUtc", "UpdatedByInstanceId", "IsDeleted" }
        };
        foreach (var t in tags)
        {
            cancellationToken.ThrowIfCancellationRequested();
            tagsSheet.Add(new[]
            {
                t.Id,
                t.Name,
                t.CreatedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
                t.UpdatedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
                t.UpdatedByInstanceId,
                t.IsDeleted ? "1" : "0"
            });
        }

        var linksSheet = new List<IReadOnlyList<string>>
        {
            new[] { "ProblemId", "TagId", "TagName", "ProblemTagIsDeleted", "TagIsDeleted" }
        };
        foreach (var l in links)
        {
            cancellationToken.ThrowIfCancellationRequested();
            linksSheet.Add(new[]
            {
                l.ProblemId,
                l.TagId,
                l.TagName,
                l.ProblemTagIsDeleted ? "1" : "0",
                l.TagIsDeleted ? "1" : "0"
            });
        }

        var attachmentsSheet = new List<IReadOnlyList<string>>
        {
            new[] { "AttachmentId", "ProblemId", "OriginalFileName", "ContentHash", "SizeBytes", "MimeType", "CreatedAtUtc", "UpdatedAtUtc", "UpdatedByInstanceId", "IsDeleted" }
        };
        foreach (var a in attachments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            attachmentsSheet.Add(new[]
            {
                a.Id,
                a.ProblemId,
                a.OriginalFileName,
                a.ContentHash,
                a.SizeBytes.ToString(CultureInfo.InvariantCulture),
                a.MimeType,
                a.CreatedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
                a.UpdatedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
                a.UpdatedByInstanceId,
                a.IsDeleted ? "1" : "0"
            });
        }

        var workbook = new SimpleXlsxWriter.Workbook(new[]
        {
            new SimpleXlsxWriter.Sheet("Problems", problemsSheet),
            new SimpleXlsxWriter.Sheet("Tags", tagsSheet),
            new SimpleXlsxWriter.Sheet("ProblemTags", linksSheet),
            new SimpleXlsxWriter.Sheet("Attachments", attachmentsSheet)
        });

        SimpleXlsxWriter.Write(outputPath, workbook);
        return Task.CompletedTask;
    }

    private static async Task WriteXlsxBundleAsync(
        string outputPath,
        IReadOnlyList<Problem> problems,
        IReadOnlyList<Tag> tags,
        IReadOnlyList<ProblemTagLink> links,
        IReadOnlyList<Attachment> attachments,
        CancellationToken cancellationToken)
    {
        var exportId = Guid.NewGuid().ToString("D");
        var tempDir = Path.Combine(Path.GetTempPath(), "FieldKb", "bulkexport", exportId);
        if (Directory.Exists(tempDir))
        {
            Directory.Delete(tempDir, recursive: true);
        }

        Directory.CreateDirectory(tempDir);

        try
        {
            var xlsxPath = Path.Combine(tempDir, "data.xlsx");
            await WriteXlsxAsync(xlsxPath, problems, tags, links, attachments, cancellationToken);

            var bundleAttachmentsDir = Path.Combine(tempDir, "attachments");
            Directory.CreateDirectory(bundleAttachmentsDir);

            var srcDir = AppDataPaths.GetAttachmentsDirectory();
            if (Directory.Exists(srcDir))
            {
                foreach (var a in attachments)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (a.IsDeleted)
                    {
                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(a.ContentHash))
                    {
                        continue;
                    }

                    var src = Path.Combine(srcDir, a.ContentHash);
                    if (!File.Exists(src))
                    {
                        continue;
                    }

                    var dest = Path.Combine(bundleAttachmentsDir, a.ContentHash);
                    if (File.Exists(dest))
                    {
                        continue;
                    }

                    await using var from = File.OpenRead(src);
                    await using var to = File.Create(dest);
                    await from.CopyToAsync(to, cancellationToken);
                }
            }

            if (File.Exists(outputPath))
            {
                File.Delete(outputPath);
            }

            ZipFile.CreateFromDirectory(tempDir, outputPath, CompressionLevel.Optimal, includeBaseDirectory: false);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    private static string ToCsvLine(IReadOnlyList<string> fields)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < fields.Count; i++)
        {
            if (i > 0)
            {
                sb.Append(',');
            }

            sb.Append(EscapeCsv(fields[i] ?? string.Empty));
        }
        return sb.ToString();
    }

    private static string EscapeCsv(string value)
    {
        var mustQuote = value.Contains(',') || value.Contains('"') || value.Contains('\r') || value.Contains('\n');
        if (!mustQuote)
        {
            return value;
        }

        return "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }

    private static string BuildUpdatedAtWhere(DateTimeOffset? fromUtc, DateTimeOffset? toUtc, SqliteCommand cmd)
    {
        var clauses = new List<string>();
        if (fromUtc is not null)
        {
            cmd.Parameters.AddWithValue("$updatedFromUtc", ToUtcText(fromUtc.Value));
            clauses.Add("p.updatedAtUtc >= $updatedFromUtc");
        }

        if (toUtc is not null)
        {
            cmd.Parameters.AddWithValue("$updatedToUtc", ToUtcText(toUtc.Value));
            clauses.Add("p.updatedAtUtc <= $updatedToUtc");
        }

        return clauses.Count == 0 ? "1=1" : string.Join(" AND ", clauses);
    }

    private static string BuildProfessionWhere(string? professionFilterId, out string? professionParamValue)
    {
        professionParamValue = null;
        var normalized = (professionFilterId ?? string.Empty).Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(normalized) || normalized == "all")
        {
            return "1=1";
        }

        if (normalized == "unassigned")
        {
            professionParamValue = "\"__professionid\":";
            return "instr(p.environmentJson, $professionPattern) = 0";
        }

        professionParamValue = $"\"__professionid\":\"{normalized}\"";
        return "instr(p.environmentJson, $professionPattern) > 0";
    }

    private static string ToUtcText(DateTimeOffset value)
    {
        return value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    }

    private static DateTimeOffset ParseUtcText(string value)
    {
        return DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
    }

    private static Problem MapProblem(SqliteDataReader reader)
    {
        var id = reader.GetString(0);
        var title = reader.GetString(1);
        var symptom = reader.GetString(2);
        var rootCause = reader.GetString(3);
        var solution = reader.GetString(4);
        var environmentJson = reader.GetString(5);
        var severity = reader.GetInt32(6);
        var status = reader.GetInt32(7);
        var createdAtUtc = ParseUtcText(reader.GetString(8));
        var createdBy = reader.GetString(9);
        var updatedAtUtc = ParseUtcText(reader.GetString(10));
        var updatedByInstanceId = reader.GetString(11);
        var isDeleted = reader.GetInt32(12) != 0;
        DateTimeOffset? deletedAtUtc = reader.IsDBNull(13) ? null : ParseUtcText(reader.GetString(13));
        var sourceKind = (SourceKind)reader.GetInt32(14);

        return new Problem(
            id,
            title,
            symptom,
            rootCause,
            solution,
            environmentJson,
            severity,
            status,
            createdAtUtc,
            createdBy,
            updatedAtUtc,
            updatedByInstanceId,
            isDeleted,
            deletedAtUtc,
            sourceKind);
    }

    private static Tag MapTag(SqliteDataReader reader)
    {
        return new Tag(
            Id: reader.GetString(0),
            Name: reader.GetString(1),
            CreatedAtUtc: ParseUtcText(reader.GetString(2)),
            UpdatedAtUtc: ParseUtcText(reader.GetString(3)),
            UpdatedByInstanceId: reader.GetString(4),
            IsDeleted: reader.GetInt32(5) != 0);
    }

    private static Attachment MapAttachment(SqliteDataReader reader)
    {
        return new Attachment(
            Id: reader.GetString(0),
            ProblemId: reader.GetString(1),
            OriginalFileName: reader.GetString(2),
            ContentHash: reader.GetString(3),
            SizeBytes: reader.GetInt64(4),
            MimeType: reader.GetString(5),
            CreatedAtUtc: ParseUtcText(reader.GetString(6)),
            UpdatedAtUtc: ParseUtcText(reader.GetString(7)),
            UpdatedByInstanceId: reader.GetString(8),
            IsDeleted: reader.GetInt32(9) != 0);
    }

    private sealed record ProblemTagLink(
        string ProblemId,
        string TagId,
        string TagName,
        bool ProblemTagIsDeleted,
        bool TagIsDeleted);

    private static class SimpleXlsxWriter
    {
        private static readonly XNamespace NsMain = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        private static readonly XNamespace NsRel = "http://schemas.openxmlformats.org/package/2006/relationships";
        private static readonly XNamespace NsDocRel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

        public sealed record Sheet(string Name, IReadOnlyList<IReadOnlyList<string>> Rows);

        public sealed record Workbook(IReadOnlyList<Sheet> Sheets);

        public static void Write(string outputPath, Workbook workbook)
        {
            if (File.Exists(outputPath))
            {
                File.Delete(outputPath);
            }

            using var zip = ZipFile.Open(outputPath, ZipArchiveMode.Create);

            WriteEntry(zip, "[Content_Types].xml", BuildContentTypes(workbook));
            WriteEntry(zip, "_rels/.rels", BuildRootRels());
            WriteEntry(zip, "xl/workbook.xml", BuildWorkbook(workbook));
            WriteEntry(zip, "xl/_rels/workbook.xml.rels", BuildWorkbookRels(workbook));

            for (var i = 0; i < workbook.Sheets.Count; i++)
            {
                var sheetIndex = i + 1;
                WriteEntry(zip, $"xl/worksheets/sheet{sheetIndex}.xml", BuildWorksheet(workbook.Sheets[i]));
            }
        }

        private static void WriteEntry(ZipArchive zip, string path, XDocument doc)
        {
            var entry = zip.CreateEntry(path, CompressionLevel.Optimal);
            using var stream = entry.Open();
            using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            doc.Save(writer, SaveOptions.DisableFormatting);
        }

        private static XDocument BuildContentTypes(Workbook workbook)
        {
            XNamespace ns = "http://schemas.openxmlformats.org/package/2006/content-types";
            var types = new XElement(ns + "Types",
                new XElement(ns + "Default", new XAttribute("Extension", "rels"), new XAttribute("ContentType", "application/vnd.openxmlformats-package.relationships+xml")),
                new XElement(ns + "Default", new XAttribute("Extension", "xml"), new XAttribute("ContentType", "application/xml")),
                new XElement(ns + "Override", new XAttribute("PartName", "/xl/workbook.xml"), new XAttribute("ContentType", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"))
            );

            for (var i = 0; i < workbook.Sheets.Count; i++)
            {
                var sheetIndex = i + 1;
                types.Add(new XElement(ns + "Override",
                    new XAttribute("PartName", $"/xl/worksheets/sheet{sheetIndex}.xml"),
                    new XAttribute("ContentType", "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml")));
            }

            return new XDocument(new XDeclaration("1.0", "UTF-8", "yes"), types);
        }

        private static XDocument BuildRootRels()
        {
            var rels = new XElement(NsRel + "Relationships",
                new XElement(NsRel + "Relationship",
                    new XAttribute("Id", "rId1"),
                    new XAttribute("Type", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument"),
                    new XAttribute("Target", "xl/workbook.xml")));
            return new XDocument(new XDeclaration("1.0", "UTF-8", "yes"), rels);
        }

        private static XDocument BuildWorkbook(Workbook workbook)
        {
            var sheets = new XElement(NsMain + "sheets");
            for (var i = 0; i < workbook.Sheets.Count; i++)
            {
                var sheetIndex = i + 1;
                sheets.Add(new XElement(NsMain + "sheet",
                    new XAttribute("name", workbook.Sheets[i].Name),
                    new XAttribute("sheetId", sheetIndex),
                    new XAttribute(NsDocRel + "id", $"rId{sheetIndex}")));
            }

            var wb = new XElement(NsMain + "workbook",
                new XAttribute(XNamespace.Xmlns + "r", NsDocRel),
                sheets);
            return new XDocument(new XDeclaration("1.0", "UTF-8", "yes"), wb);
        }

        private static XDocument BuildWorkbookRels(Workbook workbook)
        {
            var rels = new XElement(NsRel + "Relationships");
            for (var i = 0; i < workbook.Sheets.Count; i++)
            {
                var sheetIndex = i + 1;
                rels.Add(new XElement(NsRel + "Relationship",
                    new XAttribute("Id", $"rId{sheetIndex}"),
                    new XAttribute("Type", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet"),
                    new XAttribute("Target", $"worksheets/sheet{sheetIndex}.xml")));
            }

            return new XDocument(new XDeclaration("1.0", "UTF-8", "yes"), rels);
        }

        private static XDocument BuildWorksheet(Sheet sheet)
        {
            var sheetData = new XElement(NsMain + "sheetData");
            for (var r = 0; r < sheet.Rows.Count; r++)
            {
                var rowIndex = r + 1;
                var rowEl = new XElement(NsMain + "row", new XAttribute("r", rowIndex));
                var row = sheet.Rows[r];
                for (var c = 0; c < row.Count; c++)
                {
                    var colIndex = c + 1;
                    var cellRef = $"{ToColumnName(colIndex)}{rowIndex}";
                    var value = row[c] ?? string.Empty;
                    var cell = new XElement(NsMain + "c",
                        new XAttribute("r", cellRef),
                        new XAttribute("t", "inlineStr"),
                        new XElement(NsMain + "is",
                            new XElement(NsMain + "t", value)));
                    rowEl.Add(cell);
                }

                sheetData.Add(rowEl);
            }

            var ws = new XElement(NsMain + "worksheet",
                sheetData);
            return new XDocument(new XDeclaration("1.0", "UTF-8", "yes"), ws);
        }

        private static string ToColumnName(int columnIndex)
        {
            var dividend = columnIndex;
            var colName = string.Empty;
            while (dividend > 0)
            {
                var modulo = (dividend - 1) % 26;
                colName = Convert.ToChar('A' + modulo) + colName;
                dividend = (dividend - 1) / 26;
            }
            return colName;
        }
    }
}
