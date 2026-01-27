using System.IO.Compression;
using System.Text.Json;
using FieldKb.Application.Abstractions;
using FieldKb.Domain.Models;
using FieldKb.Infrastructure.BulkExport;
using FieldKb.Infrastructure.Sqlite;
using Microsoft.Data.Sqlite;

namespace FieldKb.Tests;

public sealed class BulkExportTests
{
    [Fact]
    public async Task BulkExport_Jsonl_FiltersByProfessionAndTags()
    {
        var baseDir = CreateTempDir();
        try
        {
            var dbPath = Path.Combine(baseDir, "kb.sqlite");
            var factory = new SqliteConnectionFactory(new SqliteOptions { DatabasePath = dbPath });
            var store = new SqliteKbStore(factory);
            await store.InitializeAsync(CancellationToken.None);

            var now = DateTimeOffset.UtcNow;
            var p1 = new Problem(
                Id: Guid.NewGuid().ToString("D"),
                Title: "SW issue",
                Symptom: "s1",
                RootCause: "r1",
                Solution: "sol1",
                EnvironmentJson: "{\"__professionid\":\"software\"}",
                Severity: 0,
                Status: 0,
                CreatedAtUtc: now,
                CreatedBy: "tester",
                UpdatedAtUtc: now,
                UpdatedByInstanceId: "local",
                IsDeleted: false,
                DeletedAtUtc: null,
                SourceKind: SourceKind.Personal);

            var p2 = p1 with
            {
                Id = Guid.NewGuid().ToString("D"),
                Title = "HW issue",
                EnvironmentJson = "{\"__professionid\":\"hardware\"}"
            };

            await store.UpsertProblemAsync(p1, CancellationToken.None);
            await store.UpsertProblemAsync(p2, CancellationToken.None);

            var tagA = await store.CreateTagAsync("A", now, "local", CancellationToken.None);
            var tagB = await store.CreateTagAsync("B", now, "local", CancellationToken.None);
            await store.SetTagsForProblemAsync(p1.Id, new[] { tagA.Id, tagB.Id }, now, "local", CancellationToken.None);
            await store.SetTagsForProblemAsync(p2.Id, new[] { tagA.Id }, now, "local", CancellationToken.None);

            var svc = new SqliteBulkExportService(factory);

            var outPath = Path.Combine(baseDir, "export.jsonl");
            var filter = new ProblemHardDeleteFilter(
                TagIds: new[] { tagA.Id, tagB.Id },
                ProfessionFilterId: "software",
                UpdatedFromUtc: null,
                UpdatedToUtc: null,
                IncludeSoftDeleted: false);

            await svc.BulkExportAsync(new BulkExportRequest(outPath, BulkExportFormat.Jsonl, filter), CancellationToken.None);

            var lines = File.ReadAllLines(outPath);
            Assert.Single(lines);

            using var doc = JsonDocument.Parse(lines[0]);
            var root = doc.RootElement;
            Assert.Equal(p1.Id, root.GetProperty("problem").GetProperty("id").GetString());
            Assert.True(root.GetProperty("tags").GetArrayLength() >= 2);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(baseDir, recursive: true);
        }
    }

    [Fact]
    public async Task BulkExport_Csv_WritesHeaderAndData()
    {
        var baseDir = CreateTempDir();
        try
        {
            var dbPath = Path.Combine(baseDir, "kb.sqlite");
            var factory = new SqliteConnectionFactory(new SqliteOptions { DatabasePath = dbPath });
            var store = new SqliteKbStore(factory);
            await store.InitializeAsync(CancellationToken.None);

            var now = DateTimeOffset.UtcNow;
            var p = new Problem(
                Id: Guid.NewGuid().ToString("D"),
                Title: "PLC 通讯异常",
                Symptom: "s",
                RootCause: "r",
                Solution: "sol",
                EnvironmentJson: "{}",
                Severity: 0,
                Status: 0,
                CreatedAtUtc: now,
                CreatedBy: "tester",
                UpdatedAtUtc: now,
                UpdatedByInstanceId: "local",
                IsDeleted: false,
                DeletedAtUtc: null,
                SourceKind: SourceKind.Personal);

            await store.UpsertProblemAsync(p, CancellationToken.None);

            var svc = new SqliteBulkExportService(factory);
            var outPath = Path.Combine(baseDir, "export.csv");
            await svc.BulkExportAsync(
                new BulkExportRequest(outPath, BulkExportFormat.Csv, new ProblemHardDeleteFilter(Array.Empty<string>(), "all", null, null, false)),
                CancellationToken.None);

            var text = File.ReadAllText(outPath);
            Assert.Contains("ProblemId,Title,Symptom", text, StringComparison.Ordinal);
            Assert.Contains(p.Id, text, StringComparison.Ordinal);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(baseDir, recursive: true);
        }
    }

    [Fact]
    public async Task BulkExport_Xlsx_WritesZipWithSheets()
    {
        var baseDir = CreateTempDir();
        try
        {
            var dbPath = Path.Combine(baseDir, "kb.sqlite");
            var factory = new SqliteConnectionFactory(new SqliteOptions { DatabasePath = dbPath });
            var store = new SqliteKbStore(factory);
            await store.InitializeAsync(CancellationToken.None);

            var now = DateTimeOffset.UtcNow;
            var p = new Problem(
                Id: Guid.NewGuid().ToString("D"),
                Title: "XLSX",
                Symptom: "s",
                RootCause: "r",
                Solution: "sol",
                EnvironmentJson: "{}",
                Severity: 0,
                Status: 0,
                CreatedAtUtc: now,
                CreatedBy: "tester",
                UpdatedAtUtc: now,
                UpdatedByInstanceId: "local",
                IsDeleted: false,
                DeletedAtUtc: null,
                SourceKind: SourceKind.Personal);
            await store.UpsertProblemAsync(p, CancellationToken.None);

            var svc = new SqliteBulkExportService(factory);
            var outPath = Path.Combine(baseDir, "export.xlsx");
            await svc.BulkExportAsync(
                new BulkExportRequest(outPath, BulkExportFormat.Xlsx, new ProblemHardDeleteFilter(Array.Empty<string>(), "all", null, null, false)),
                CancellationToken.None);

            Assert.True(File.Exists(outPath));
            using var zip = ZipFile.OpenRead(outPath);
            Assert.NotNull(zip.GetEntry("xl/workbook.xml"));
            Assert.NotNull(zip.GetEntry("xl/worksheets/sheet1.xml"));
            Assert.NotNull(zip.GetEntry("xl/worksheets/sheet2.xml"));
            Assert.NotNull(zip.GetEntry("xl/worksheets/sheet3.xml"));
            Assert.NotNull(zip.GetEntry("xl/worksheets/sheet4.xml"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(baseDir, recursive: true);
        }
    }

    [Fact]
    public async Task BulkExport_XlsxBundle_WritesDataXlsxAndAttachmentsFolder()
    {
        var baseDir = CreateTempDir();
        try
        {
            var dbPath = Path.Combine(baseDir, "kb.sqlite");
            var factory = new SqliteConnectionFactory(new SqliteOptions { DatabasePath = dbPath });
            var store = new SqliteKbStore(factory);
            await store.InitializeAsync(CancellationToken.None);

            var now = DateTimeOffset.UtcNow;
            var p = new Problem(
                Id: Guid.NewGuid().ToString("D"),
                Title: "BUNDLE",
                Symptom: "s",
                RootCause: "r",
                Solution: "sol",
                EnvironmentJson: "{}",
                Severity: 0,
                Status: 0,
                CreatedAtUtc: now,
                CreatedBy: "tester",
                UpdatedAtUtc: now,
                UpdatedByInstanceId: "local",
                IsDeleted: false,
                DeletedAtUtc: null,
                SourceKind: SourceKind.Personal);
            await store.UpsertProblemAsync(p, CancellationToken.None);

            var bytes = System.Text.Encoding.UTF8.GetBytes(Guid.NewGuid().ToString("D"));
            var tempAttachmentPath = Path.Combine(baseDir, "a.bin");
            await File.WriteAllBytesAsync(tempAttachmentPath, bytes, CancellationToken.None);
            var att = await store.AddAttachmentAsync(p.Id, tempAttachmentPath, now, "local", CancellationToken.None);

            var svc = new SqliteBulkExportService(factory);
            var outPath = Path.Combine(baseDir, "export.zip");
            await svc.BulkExportAsync(
                new BulkExportRequest(outPath, BulkExportFormat.XlsxBundle, new ProblemHardDeleteFilter(Array.Empty<string>(), "all", null, null, false)),
                CancellationToken.None);

            Assert.True(File.Exists(outPath));
            using var zip = ZipFile.OpenRead(outPath);
            Assert.NotNull(zip.GetEntry("data.xlsx"));
            Assert.NotNull(zip.GetEntry($"attachments/{att.ContentHash}"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(baseDir, recursive: true);
        }
    }

    private static string CreateTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "FieldKbTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }
}
