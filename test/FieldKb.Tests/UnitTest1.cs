using FieldKb.Application.ImportExport;
using FieldKb.Domain.Models;
using FieldKb.Infrastructure.ImportExport;
using FieldKb.Infrastructure.Sqlite;
using FieldKb.Infrastructure.Storage;
using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace FieldKb.Tests;

public sealed class StorageAndPackageTests
{
    [Fact]
    public async Task UpsertAndSearch_ReturnsHit()
    {
        var baseDir = CreateTempDir();
        try
        {
            var dbPath = Path.Combine(baseDir, "kb.sqlite");
            var store = CreateStore(dbPath);
            await store.InitializeAsync(CancellationToken.None);

            var now = DateTimeOffset.UtcNow;
            var problem = new Problem(
                Id: Guid.NewGuid().ToString("D"),
                Title: "PLC 通讯异常",
                Symptom: "上位机无法读取数据",
                RootCause: "现场网络抖动",
                Solution: "更换网线并检查交换机",
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

            await store.UpsertProblemAsync(problem, CancellationToken.None);

            var hits = await store.SearchProblemsAsync("PLC", Array.Empty<string>(), professionFilterId: null, limit: 10, offset: 0, CancellationToken.None);
            Assert.Contains(hits, h => h.ProblemId == problem.Id);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(baseDir, recursive: true);
        }
    }

    [Fact]
    public async Task ExportAndImport_Full_ImportsProblem_AndReimportCreatesConflict()
    {
        var exportWorkDir = CreateTempDir();
        var exportIdentityDir = CreateTempDir();
        var importWorkDir = CreateTempDir();
        var importIdentityDir = CreateTempDir();
        try
        {
            var exporterDbPath = Path.Combine(exportWorkDir, "exporter.sqlite");
            var exporterFactory = new SqliteConnectionFactory(new SqliteOptions { DatabasePath = exporterDbPath });
            var exporterStore = new SqliteKbStore(exporterFactory);
            await exporterStore.InitializeAsync(CancellationToken.None);

            var now = DateTimeOffset.UtcNow;
            var envJson = JsonSerializer.Serialize(new Dictionary<string, string>
            {
                ["appName"] = "FieldKb",
                ["appVersion"] = "1.2.3",
                ["os"] = "Windows 11",
                ["firmwareVersion"] = "FW-0.9.1",
                ["deviceModel"] = "FK-2000",
                ["ip"] = "192.168.1.10",
                ["port"] = "502",
                ["UI 入口路径"] = "设置/主题"
            });
            var problem = new Problem(
                Id: Guid.NewGuid().ToString("D"),
                Title: "设备无法启动",
                Symptom: "开机无反应",
                RootCause: "电源模块损坏",
                Solution: "更换电源模块",
                EnvironmentJson: envJson,
                Severity: 0,
                Status: 0,
                CreatedAtUtc: now,
                CreatedBy: "tester",
                UpdatedAtUtc: now,
                UpdatedByInstanceId: "exporter",
                IsDeleted: false,
                DeletedAtUtc: null,
                SourceKind: SourceKind.Personal);

            await exporterStore.UpsertProblemAsync(problem, CancellationToken.None);

            var tagA = await exporterStore.CreateTagAsync("A", now, "exporter", CancellationToken.None);
            var tagB = await exporterStore.CreateTagAsync("B", now, "exporter", CancellationToken.None);
            var tagC = await exporterStore.CreateTagAsync("C", now, "exporter", CancellationToken.None);
            await exporterStore.SetTagsForProblemAsync(problem.Id, new[] { tagA.Id, tagB.Id, tagC.Id }, now, "exporter", CancellationToken.None);

            var exporterIdentity = new InstanceIdentityProvider(exportIdentityDir);
            var exporterService = new SqlitePackageTransferService(
                exporterFactory,
                exporterIdentity,
                new LocalInstanceContext(InstanceKind.Personal));

            var exportResult = await exporterService.ExportAsync(
                new ExportRequest(
                    OutputDirectory: exportWorkDir,
                    RemoteInstanceId: "remote",
                    Mode: ExportMode.Full,
                    UpdatedAfterUtc: null,
                    Limit: null),
                CancellationToken.None);

            Assert.True(File.Exists(exportResult.PackagePath));

            var importerDbPath = Path.Combine(importWorkDir, "importer.sqlite");
            var importerFactory = new SqliteConnectionFactory(new SqliteOptions { DatabasePath = importerDbPath });
            var importerStore = new SqliteKbStore(importerFactory);
            await importerStore.InitializeAsync(CancellationToken.None);

            var importerIdentity = new InstanceIdentityProvider(importIdentityDir);
            var importerService = new SqlitePackageTransferService(
                importerFactory,
                importerIdentity,
                new LocalInstanceContext(InstanceKind.Corporate));

            var report = await importerService.ImportAsync(exportResult.PackagePath, CancellationToken.None);
            Assert.True(report.ImportedCount > 0);
            Assert.Equal(1, report.ProblemsImportedCount);
            Assert.True(report.ImportedCount > report.ProblemsImportedCount);

            var imported = await importerStore.GetProblemByIdAsync(problem.Id, CancellationToken.None);
            Assert.NotNull(imported);
            AssertJsonContains(imported!.EnvironmentJson, envJson);

            var newer = imported! with
            {
                Solution = "更换电源模块并复测",
                UpdatedAtUtc = DateTimeOffset.UtcNow.AddMinutes(1),
                UpdatedByInstanceId = "importer"
            };
            await importerStore.UpsertProblemAsync(newer, CancellationToken.None);

            var report2 = await importerService.ImportAsync(exportResult.PackagePath, CancellationToken.None);
            Assert.True(report2.ConflictCount >= 1);
            Assert.True(report2.SkippedCount >= 1);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(exportWorkDir, recursive: true);
            Directory.Delete(exportIdentityDir, recursive: true);
            Directory.Delete(importWorkDir, recursive: true);
            Directory.Delete(importIdentityDir, recursive: true);
        }
    }

    [Fact]
    public async Task Search_WithProfessionFilter_ReturnsOnlyMatching()
    {
        var baseDir = CreateTempDir();
        try
        {
            var dbPath = Path.Combine(baseDir, "kb.sqlite");
            var store = CreateStore(dbPath);
            await store.InitializeAsync(CancellationToken.None);

            var now = DateTimeOffset.UtcNow;
            var softwareProblem = new Problem(
                Id: Guid.NewGuid().ToString("D"),
                Title: "SW issue",
                Symptom: "startup error",
                RootCause: "missing config",
                Solution: "add config",
                EnvironmentJson: "{\"__professionid\":\"software\",\"appVersion\":\"1.0.0\"}",
                Severity: 0,
                Status: 0,
                CreatedAtUtc: now,
                CreatedBy: "tester",
                UpdatedAtUtc: now,
                UpdatedByInstanceId: "local",
                IsDeleted: false,
                DeletedAtUtc: null,
                SourceKind: SourceKind.Personal);

            var hardwareProblem = softwareProblem with
            {
                Id = Guid.NewGuid().ToString("D"),
                Title = "HW issue",
                EnvironmentJson = "{\"__professionid\":\"hardware\",\"deviceModel\":\"FK-2000\"}"
            };

            var unassignedProblem = softwareProblem with
            {
                Id = Guid.NewGuid().ToString("D"),
                Title = "Unassigned issue",
                EnvironmentJson = "{\"deviceModel\":\"FK-700\"}"
            };

            await store.UpsertProblemAsync(softwareProblem, CancellationToken.None);
            await store.UpsertProblemAsync(hardwareProblem, CancellationToken.None);
            await store.UpsertProblemAsync(unassignedProblem, CancellationToken.None);

            var allHits = await store.SearchProblemsAsync(string.Empty, Array.Empty<string>(), "all", 50, 0, CancellationToken.None);
            Assert.Contains(allHits, h => h.ProblemId == softwareProblem.Id);
            var allCount = await store.CountProblemsAsync(string.Empty, Array.Empty<string>(), "all", CancellationToken.None);
            Assert.True(allCount >= 3);

            var softwareHits = await store.SearchProblemsAsync(string.Empty, Array.Empty<string>(), "software", 50, 0, CancellationToken.None);
            Assert.Contains(softwareHits, h => h.ProblemId == softwareProblem.Id);
            Assert.DoesNotContain(softwareHits, h => h.ProblemId == hardwareProblem.Id);
            Assert.DoesNotContain(softwareHits, h => h.ProblemId == unassignedProblem.Id);
            var softwareCount = await store.CountProblemsAsync(string.Empty, Array.Empty<string>(), "software", CancellationToken.None);
            Assert.Equal(1, softwareCount);

            var unassignedHits = await store.SearchProblemsAsync(string.Empty, Array.Empty<string>(), "unassigned", 50, 0, CancellationToken.None);
            Assert.Contains(unassignedHits, h => h.ProblemId == unassignedProblem.Id);
            Assert.DoesNotContain(unassignedHits, h => h.ProblemId == softwareProblem.Id);
            Assert.DoesNotContain(unassignedHits, h => h.ProblemId == hardwareProblem.Id);
            var unassignedCount = await store.CountProblemsAsync(string.Empty, Array.Empty<string>(), "unassigned", CancellationToken.None);
            Assert.Equal(1, unassignedCount);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(baseDir, recursive: true);
        }
    }

    [Fact]
    public async Task HardDeleteProblems_ByProfessionTagAndTime_RemovesRowsPhysically()
    {
        var baseDir = CreateTempDir();
        try
        {
            var dbPath = Path.Combine(baseDir, "kb.sqlite");
            var store = CreateStore(dbPath);
            await store.InitializeAsync(CancellationToken.None);

            var now = DateTimeOffset.UtcNow;
            var p1 = new Problem(
                Id: Guid.NewGuid().ToString("D"),
                Title: "P1",
                Symptom: "S",
                RootCause: "R",
                Solution: "Sol",
                EnvironmentJson: "{\"__professionid\":\"software\"}",
                Severity: 0,
                Status: 0,
                CreatedAtUtc: now.AddDays(-10),
                CreatedBy: "tester",
                UpdatedAtUtc: now.AddDays(-3),
                UpdatedByInstanceId: "local",
                IsDeleted: false,
                DeletedAtUtc: null,
                SourceKind: SourceKind.Personal);

            var p2 = p1 with
            {
                Id = Guid.NewGuid().ToString("D"),
                Title = "P2",
                UpdatedAtUtc = now
            };

            var p3 = p1 with
            {
                Id = Guid.NewGuid().ToString("D"),
                Title = "P3",
                EnvironmentJson = "{\"__professionid\":\"hardware\"}",
                UpdatedAtUtc = now
            };

            await store.UpsertProblemAsync(p1, CancellationToken.None);
            await store.UpsertProblemAsync(p2, CancellationToken.None);
            await store.UpsertProblemAsync(p3, CancellationToken.None);

            var tagA = await store.CreateTagAsync("A", now, "local", CancellationToken.None);
            var tagB = await store.CreateTagAsync("B", now, "local", CancellationToken.None);
            await store.SetTagsForProblemAsync(p1.Id, new[] { tagA.Id }, now, "local", CancellationToken.None);
            await store.SetTagsForProblemAsync(p2.Id, new[] { tagA.Id, tagB.Id }, now, "local", CancellationToken.None);
            await store.SetTagsForProblemAsync(p3.Id, new[] { tagA.Id }, now, "local", CancellationToken.None);

            var attachmentFile = Path.Combine(baseDir, "a.txt");
            await File.WriteAllTextAsync(attachmentFile, "hello");
            await store.AddAttachmentAsync(p2.Id, attachmentFile, now, "local", CancellationToken.None);

            var filter = new FieldKb.Application.Abstractions.ProblemHardDeleteFilter(
                TagIds: new[] { tagA.Id },
                ProfessionFilterId: "software",
                UpdatedFromUtc: now.AddDays(-1),
                UpdatedToUtc: now.AddDays(1),
                IncludeSoftDeleted: false);

            var preview = await store.CountProblemsForHardDeleteAsync(filter, CancellationToken.None);
            Assert.Equal(1, preview);

            var deleted = await store.HardDeleteProblemsAsync(filter, CancellationToken.None);
            Assert.Equal(1, deleted);

            Assert.Null(await store.GetProblemByIdAsync(p2.Id, CancellationToken.None));
            Assert.NotNull(await store.GetProblemByIdAsync(p1.Id, CancellationToken.None));
            Assert.NotNull(await store.GetProblemByIdAsync(p3.Id, CancellationToken.None));

            await using var conn = new SqliteConnection($"Data Source={dbPath}");
            await conn.OpenAsync();
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*) FROM attachment WHERE problemId = $id;";
                cmd.Parameters.AddWithValue("$id", p2.Id);
                var count = (long)(await cmd.ExecuteScalarAsync() ?? 0L);
                Assert.Equal(0L, count);
            }
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*) FROM problemTag WHERE problemId = $id;";
                cmd.Parameters.AddWithValue("$id", p2.Id);
                var count = (long)(await cmd.ExecuteScalarAsync() ?? 0L);
                Assert.Equal(0L, count);
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(baseDir, recursive: true);
        }
    }

    private static SqliteKbStore CreateStore(string databasePath)
    {
        return new SqliteKbStore(new SqliteConnectionFactory(new SqliteOptions { DatabasePath = databasePath }));
    }

    private static string CreateTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "FieldKb.Tests", Guid.NewGuid().ToString("D"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void AssertJsonContains(string actualJson, string expectedJson)
    {
        var actual = JsonSerializer.Deserialize<Dictionary<string, string>>(actualJson) ?? new Dictionary<string, string>();
        var expected = JsonSerializer.Deserialize<Dictionary<string, string>>(expectedJson) ?? new Dictionary<string, string>();

        foreach (var kv in expected)
        {
            Assert.True(actual.TryGetValue(kv.Key, out var v), $"Missing key: {kv.Key}");
            Assert.Equal(kv.Value, v);
        }
    }

    [Fact]
    public async Task AppSettingsStore_ProfessionFixedFields_Roundtrip()
    {
        var baseDir = CreateTempDir();
        try
        {
            var path = Path.Combine(baseDir, "appsettings.json");
            var store = new FieldKb.Client.Wpf.JsonAppSettingsStore(path);

            await store.WriteProfessionFixedFieldsAsync("software", new[]
            {
                new FieldKb.Client.Wpf.ProfessionFixedFieldSetting("appName", "应用名称"),
                new FieldKb.Client.Wpf.ProfessionFixedFieldSetting("自定义字段", "自定义字段")
            }, CancellationToken.None);

            var map = await store.ReadProfessionFixedFieldsAsync(CancellationToken.None);
            Assert.True(map.TryGetValue("software", out var list));
            Assert.Equal(2, list.Count);
            Assert.Contains(list, x => x.Key == "appName");
            Assert.Contains(list, x => x.Key == "自定义字段");
        }
        finally
        {
            Directory.Delete(baseDir, recursive: true);
        }
    }

    [Fact]
    public async Task OperationPasswordService_SetAndVerify_Works()
    {
        var baseDir = CreateTempDir();
        try
        {
            var path = Path.Combine(baseDir, "appsettings.json");
            var store = new FieldKb.Client.Wpf.JsonAppSettingsStore(path);
            var svc = new FieldKb.Client.Wpf.OperationPasswordService(store);

            Assert.False(svc.IsConfigured);
            await svc.SetAsync("123456", CancellationToken.None);
            Assert.True(svc.IsConfigured);
            Assert.True(svc.Verify("123456"));
            Assert.False(svc.Verify("wrong"));
        }
        finally
        {
            Directory.Delete(baseDir, recursive: true);
        }
    }
}
