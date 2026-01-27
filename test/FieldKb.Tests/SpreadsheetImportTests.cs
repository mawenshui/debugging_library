using FieldKb.Application.Abstractions;
using FieldKb.Application.ImportExport;
using FieldKb.Domain.Models;
using FieldKb.Infrastructure.BulkExport;
using FieldKb.Infrastructure.SpreadsheetImport;
using FieldKb.Infrastructure.Sqlite;
using FieldKb.Infrastructure.Storage;
using Microsoft.Data.Sqlite;

namespace FieldKb.Tests;

public sealed class SpreadsheetImportTests
{
    [Fact]
    public async Task SpreadsheetImport_Bundle_RoundTripsTagsAndAttachments()
    {
        var baseDir = CreateTempDir();
        try
        {
            var sourceDbPath = Path.Combine(baseDir, "source.sqlite");
            var sourceFactory = new SqliteConnectionFactory(new SqliteOptions { DatabasePath = sourceDbPath });
            var sourceStore = new SqliteKbStore(sourceFactory);
            await sourceStore.InitializeAsync(CancellationToken.None);

            var now = DateTimeOffset.UtcNow;
            var problem = new Problem(
                Id: Guid.NewGuid().ToString("D"),
                Title: "Roundtrip",
                Symptom: "s",
                RootCause: "r",
                Solution: "sol",
                EnvironmentJson: "{\"__professionid\":\"software\"}",
                Severity: 1,
                Status: 2,
                CreatedAtUtc: now,
                CreatedBy: "tester",
                UpdatedAtUtc: now,
                UpdatedByInstanceId: "source",
                IsDeleted: false,
                DeletedAtUtc: null,
                SourceKind: SourceKind.Personal);
            await sourceStore.UpsertProblemAsync(problem, CancellationToken.None);

            var tag = await sourceStore.CreateTagAsync("A", now, "source", CancellationToken.None);
            await sourceStore.SetTagsForProblemAsync(problem.Id, new[] { tag.Id }, now, "source", CancellationToken.None);

            var attachmentBytes = System.Text.Encoding.UTF8.GetBytes(Guid.NewGuid().ToString("D"));
            var tempAttachmentPath = Path.Combine(baseDir, "a.bin");
            await File.WriteAllBytesAsync(tempAttachmentPath, attachmentBytes, CancellationToken.None);
            var attachment = await sourceStore.AddAttachmentAsync(problem.Id, tempAttachmentPath, now, "source", CancellationToken.None);

            var exportPath = Path.Combine(baseDir, "export.zip");
            var exporter = new SqliteBulkExportService(sourceFactory);
            await exporter.BulkExportAsync(
                new BulkExportRequest(
                    exportPath,
                    BulkExportFormat.XlsxBundle,
                    new ProblemHardDeleteFilter(Array.Empty<string>(), "all", null, null, false)),
                CancellationToken.None);

            var localAttachmentPath = Path.Combine(AppDataPaths.GetAttachmentsDirectory(), attachment.ContentHash);
            if (File.Exists(localAttachmentPath))
            {
                File.Delete(localAttachmentPath);
            }

            var targetDbPath = Path.Combine(baseDir, "target.sqlite");
            var targetFactory = new SqliteConnectionFactory(new SqliteOptions { DatabasePath = targetDbPath });
            var targetStore = new SqliteKbStore(targetFactory);
            await targetStore.InitializeAsync(CancellationToken.None);

            var identityDir = Path.Combine(baseDir, "identity");
            var importer = new XlsxSpreadsheetImportService(
                targetFactory,
                new InstanceIdentityProvider(identityDir),
                new LocalInstanceContext(InstanceKind.Personal));

            var importReport = await importer.ImportAsync(
                new SpreadsheetImportRequest(exportPath, SpreadsheetImportConflictPolicy.Overwrite, SpreadsheetImportTagMergeMode.Replace),
                CancellationToken.None);

            Assert.Equal(0, importReport.MissingAttachmentFiles);

            var importedProblem = await targetStore.GetProblemByIdAsync(problem.Id, CancellationToken.None);
            Assert.NotNull(importedProblem);

            var importedTags = await targetStore.GetTagsForProblemAsync(problem.Id, CancellationToken.None);
            Assert.Contains(importedTags, t => t.Id == tag.Id);

            var importedAttachments = await targetStore.GetAttachmentsForProblemAsync(problem.Id, CancellationToken.None);
            Assert.Contains(importedAttachments, a => a.Id == attachment.Id);

            Assert.True(File.Exists(localAttachmentPath));
            var importedBytes = await File.ReadAllBytesAsync(localAttachmentPath, CancellationToken.None);
            Assert.Equal(attachmentBytes, importedBytes);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(baseDir, recursive: true);
        }
    }

    [Fact]
    public async Task SpreadsheetImport_XlsxOnly_ImportsProblemsAndTagsButSkipsAttachments()
    {
        var baseDir = CreateTempDir();
        try
        {
            var dbPath = Path.Combine(baseDir, "kb.sqlite");
            var factory = new SqliteConnectionFactory(new SqliteOptions { DatabasePath = dbPath });
            var store = new SqliteKbStore(factory);
            await store.InitializeAsync(CancellationToken.None);

            var now = DateTimeOffset.UtcNow;
            var problem = new Problem(
                Id: Guid.NewGuid().ToString("D"),
                Title: "XlsxOnly",
                Symptom: "s",
                RootCause: "r",
                Solution: "sol",
                EnvironmentJson: "{\"__professionid\":\"software\"}",
                Severity: 0,
                Status: 0,
                CreatedAtUtc: now,
                CreatedBy: "tester",
                UpdatedAtUtc: now,
                UpdatedByInstanceId: "source",
                IsDeleted: false,
                DeletedAtUtc: null,
                SourceKind: SourceKind.Personal);
            await store.UpsertProblemAsync(problem, CancellationToken.None);

            var tag = await store.CreateTagAsync("T", now, "source", CancellationToken.None);
            await store.SetTagsForProblemAsync(problem.Id, new[] { tag.Id }, now, "source", CancellationToken.None);

            var attachmentBytes = System.Text.Encoding.UTF8.GetBytes(Guid.NewGuid().ToString("D"));
            var tempAttachmentPath = Path.Combine(baseDir, "a.bin");
            await File.WriteAllBytesAsync(tempAttachmentPath, attachmentBytes, CancellationToken.None);
            await store.AddAttachmentAsync(problem.Id, tempAttachmentPath, now, "source", CancellationToken.None);

            var outXlsx = Path.Combine(baseDir, "export.xlsx");
            var exporter = new SqliteBulkExportService(factory);
            await exporter.BulkExportAsync(
                new BulkExportRequest(outXlsx, BulkExportFormat.Xlsx, new ProblemHardDeleteFilter(Array.Empty<string>(), "all", null, null, false)),
                CancellationToken.None);

            var targetDbPath = Path.Combine(baseDir, "target.sqlite");
            var targetFactory = new SqliteConnectionFactory(new SqliteOptions { DatabasePath = targetDbPath });
            var targetStore = new SqliteKbStore(targetFactory);
            await targetStore.InitializeAsync(CancellationToken.None);

            var identityDir = Path.Combine(baseDir, "identity");
            var importer = new XlsxSpreadsheetImportService(
                targetFactory,
                new InstanceIdentityProvider(identityDir),
                new LocalInstanceContext(InstanceKind.Personal));

            var report = await importer.ImportAsync(
                new SpreadsheetImportRequest(outXlsx, SpreadsheetImportConflictPolicy.Overwrite, SpreadsheetImportTagMergeMode.Replace),
                CancellationToken.None);

            Assert.True(report.AttachmentsInFile > 0);
            Assert.True(report.MissingAttachmentFiles > 0);

            var importedProblem = await targetStore.GetProblemByIdAsync(problem.Id, CancellationToken.None);
            Assert.NotNull(importedProblem);

            var importedTags = await targetStore.GetTagsForProblemAsync(problem.Id, CancellationToken.None);
            Assert.Contains(importedTags, t => t.Id == tag.Id);

            var importedAttachments = await targetStore.GetAttachmentsForProblemAsync(problem.Id, CancellationToken.None);
            Assert.Empty(importedAttachments);
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
