using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Data.Sqlite;

namespace FieldKb.Infrastructure.Sqlite;

public static class SqliteMigrations
{
    public static async Task ApplyAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await ExecuteNonQueryAsync(connection, "PRAGMA foreign_keys=ON;", cancellationToken);
        await ExecuteNonQueryAsync(connection, "PRAGMA journal_mode=WAL;", cancellationToken);
        await ExecuteNonQueryAsync(connection, "PRAGMA synchronous=NORMAL;", cancellationToken);

        var currentVersion = await GetUserVersionAsync(connection, cancellationToken);
        if (currentVersion == 0)
        {
            await CreateSchemaV1Async(connection, cancellationToken);
            await SetUserVersionAsync(connection, 1, cancellationToken);
            currentVersion = 1;
        }

        if (currentVersion == 1)
        {
            await CreateSchemaV2Async(connection, cancellationToken);
            await SetUserVersionAsync(connection, 2, cancellationToken);
            currentVersion = 2;
        }

        if (currentVersion == 2)
        {
            await CreateSchemaV3Async(connection, cancellationToken);
            await SetUserVersionAsync(connection, 3, cancellationToken);
            currentVersion = 3;
        }

        if (currentVersion == 3)
        {
            await CreateSchemaV4Async(connection, cancellationToken);
            await SetUserVersionAsync(connection, 4, cancellationToken);
            currentVersion = 4;
        }
    }

    private static async Task<int> GetUserVersionAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA user_version;";
        var result = await cmd.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt32(result);
    }

    private static async Task SetUserVersionAsync(SqliteConnection connection, int version, CancellationToken cancellationToken)
    {
        await ExecuteNonQueryAsync(connection, $"PRAGMA user_version={version};", cancellationToken);
    }

    private static async Task CreateSchemaV1Async(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var sql = """
        CREATE TABLE IF NOT EXISTS problem (
            id TEXT PRIMARY KEY,
            title TEXT NOT NULL,
            symptom TEXT NOT NULL,
            rootCause TEXT NOT NULL,
            solution TEXT NOT NULL,
            environmentJson TEXT NOT NULL,
            severity INTEGER NOT NULL,
            status INTEGER NOT NULL,
            createdAtUtc TEXT NOT NULL,
            createdBy TEXT NOT NULL,
            updatedAtUtc TEXT NOT NULL,
            updatedByInstanceId TEXT NOT NULL,
            isDeleted INTEGER NOT NULL,
            deletedAtUtc TEXT NULL,
            sourceKind INTEGER NOT NULL
        );
        CREATE INDEX IF NOT EXISTS idx_problem_updatedAtUtc ON problem(updatedAtUtc);

        CREATE TABLE IF NOT EXISTS tag (
            id TEXT PRIMARY KEY,
            name TEXT NOT NULL,
            createdAtUtc TEXT NOT NULL,
            updatedAtUtc TEXT NOT NULL,
            updatedByInstanceId TEXT NOT NULL,
            isDeleted INTEGER NOT NULL
        );
        CREATE INDEX IF NOT EXISTS idx_tag_name ON tag(name);

        CREATE TABLE IF NOT EXISTS problemTag (
            id TEXT PRIMARY KEY,
            problemId TEXT NOT NULL,
            tagId TEXT NOT NULL,
            createdAtUtc TEXT NOT NULL,
            updatedAtUtc TEXT NOT NULL,
            updatedByInstanceId TEXT NOT NULL,
            isDeleted INTEGER NOT NULL,
            FOREIGN KEY(problemId) REFERENCES problem(id),
            FOREIGN KEY(tagId) REFERENCES tag(id)
        );
        CREATE INDEX IF NOT EXISTS idx_problemTag_problemId ON problemTag(problemId);
        CREATE INDEX IF NOT EXISTS idx_problemTag_tagId ON problemTag(tagId);

        CREATE TABLE IF NOT EXISTS attachment (
            id TEXT PRIMARY KEY,
            problemId TEXT NOT NULL,
            originalFileName TEXT NOT NULL,
            contentHash TEXT NOT NULL,
            sizeBytes INTEGER NOT NULL,
            mimeType TEXT NOT NULL,
            createdAtUtc TEXT NOT NULL,
            updatedAtUtc TEXT NOT NULL,
            updatedByInstanceId TEXT NOT NULL,
            isDeleted INTEGER NOT NULL,
            FOREIGN KEY(problemId) REFERENCES problem(id)
        );
        CREATE INDEX IF NOT EXISTS idx_attachment_problemId ON attachment(problemId);
        CREATE INDEX IF NOT EXISTS idx_attachment_contentHash ON attachment(contentHash);

        CREATE TABLE IF NOT EXISTS syncState (
            localInstanceId TEXT NOT NULL,
            remoteInstanceId TEXT NOT NULL,
            lastImportedUpdatedAtUtc TEXT NOT NULL,
            lastImportedPackageId TEXT NOT NULL,
            PRIMARY KEY(localInstanceId, remoteInstanceId)
        );

        CREATE TABLE IF NOT EXISTS conflictRecord (
            id TEXT PRIMARY KEY,
            entityType TEXT NOT NULL,
            entityId TEXT NOT NULL,
            importedUpdatedAtUtc TEXT NOT NULL,
            localUpdatedAtUtc TEXT NOT NULL,
            localJson TEXT NOT NULL,
            importedJson TEXT NOT NULL,
            createdAtUtc TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS idx_conflictRecord_entity ON conflictRecord(entityType, entityId);

        CREATE VIRTUAL TABLE IF NOT EXISTS problem_fts USING fts5(
            problemId UNINDEXED,
            title,
            symptom,
            rootCause,
            solution,
            environmentJson,
            tokenize = 'unicode61'
        );
        """;

        await ExecuteNonQueryAsync(connection, sql, cancellationToken);
    }

    private static async Task CreateSchemaV2Async(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var sql = """
        CREATE INDEX IF NOT EXISTS idx_tag_updatedAtUtc ON tag(updatedAtUtc);
        CREATE INDEX IF NOT EXISTS idx_problemTag_updatedAtUtc ON problemTag(updatedAtUtc);
        CREATE INDEX IF NOT EXISTS idx_attachment_updatedAtUtc ON attachment(updatedAtUtc);

        CREATE TABLE IF NOT EXISTS exportState (
            localInstanceId TEXT NOT NULL,
            remoteInstanceId TEXT NOT NULL,
            lastExportedUpdatedAtUtc TEXT NOT NULL,
            lastExportedPackageId TEXT NOT NULL,
            PRIMARY KEY(localInstanceId, remoteInstanceId)
        );
        """;

        await ExecuteNonQueryAsync(connection, sql, cancellationToken);
    }

    private static async Task CreateSchemaV3Async(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var sql = """
        ALTER TABLE conflictRecord ADD COLUMN resolvedAtUtc TEXT NULL;
        ALTER TABLE conflictRecord ADD COLUMN resolution TEXT NULL;
        ALTER TABLE conflictRecord ADD COLUMN resolvedBy TEXT NULL;
        CREATE INDEX IF NOT EXISTS idx_conflictRecord_resolvedAtUtc ON conflictRecord(resolvedAtUtc);
        """;

        await ExecuteNonQueryAsync(connection, sql, cancellationToken);
    }

    private static async Task CreateSchemaV4Async(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var nowUtc = DateTimeOffset.UtcNow;
        var nowText = nowUtc.ToString("O");

        var tags = new List<(string Id, string Name, string CreatedAtUtc)>();
        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = """
                SELECT id, name, createdAtUtc
                FROM tag
                WHERE isDeleted = 0 AND trim(name) <> ''
                ORDER BY lower(trim(name)) ASC, createdAtUtc ASC, id ASC;
                """;
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                tags.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
            }
        }

        var groups = tags.GroupBy(t => t.Name.Trim().ToLowerInvariant(), StringComparer.Ordinal);
        foreach (var g in groups)
        {
            var list = g.ToList();
            if (list.Count <= 1)
            {
                continue;
            }

            var canonicalId = list[0].Id;
            for (var i = 1; i < list.Count; i++)
            {
                var dupId = list[i].Id;

                await using (var updateLinks = connection.CreateCommand())
                {
                    updateLinks.CommandText = """
                        UPDATE problemTag
                        SET tagId = $canonicalId
                        WHERE tagId = $dupId AND isDeleted = 0;
                        """;
                    updateLinks.Parameters.AddWithValue("$canonicalId", canonicalId);
                    updateLinks.Parameters.AddWithValue("$dupId", dupId);
                    await updateLinks.ExecuteNonQueryAsync(cancellationToken);
                }

                await using (var markTag = connection.CreateCommand())
                {
                    markTag.CommandText = """
                        UPDATE tag
                        SET isDeleted = 1,
                            updatedAtUtc = $nowUtc,
                            updatedByInstanceId = $updatedByInstanceId
                        WHERE id = $id;
                        """;
                    markTag.Parameters.AddWithValue("$nowUtc", nowText);
                    markTag.Parameters.AddWithValue("$updatedByInstanceId", "migration");
                    markTag.Parameters.AddWithValue("$id", dupId);
                    await markTag.ExecuteNonQueryAsync(cancellationToken);
                }
            }
        }

        await ExecuteNonQueryAsync(connection, """
            DELETE FROM problemTag
            WHERE isDeleted = 0 AND id IN (
                SELECT pt.id
                FROM problemTag pt
                JOIN (
                    SELECT problemId, tagId, MIN(id) AS keepId
                    FROM problemTag
                    WHERE isDeleted = 0
                    GROUP BY problemId, tagId
                    HAVING COUNT(*) > 1
                ) d ON pt.problemId = d.problemId AND pt.tagId = d.tagId
                WHERE pt.id <> d.keepId
            );
            """, cancellationToken);

        await ExecuteNonQueryAsync(connection, """
            CREATE UNIQUE INDEX IF NOT EXISTS ux_tag_name_active
            ON tag(lower(trim(name)))
            WHERE isDeleted = 0 AND trim(name) <> '';
            """, cancellationToken);
    }

    private static async Task ExecuteNonQueryAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }
}
