using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using FieldKb.Application.Abstractions;
using FieldKb.Domain.Models;
using FieldKb.Infrastructure.Storage;
using Microsoft.Data.Sqlite;

namespace FieldKb.Infrastructure.Sqlite;

public sealed class SqliteKbStore : IKbStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly SqliteConnectionFactory _connectionFactory;

    public SqliteKbStore(SqliteConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);
        await SqliteMigrations.ApplyAsync(connection, cancellationToken);
    }

    public async Task UpsertProblemAsync(Problem problem, CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);
        await SqliteMigrations.ApplyAsync(connection, cancellationToken);

        await using var tx = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

        await UpsertProblemRowAsync(connection, tx, problem, cancellationToken);
        await UpsertProblemFtsAsync(connection, tx, problem, cancellationToken);

        await tx.CommitAsync(cancellationToken);
    }

    public async Task SoftDeleteProblemAsync(string problemId, DateTimeOffset deletedAtUtc, string updatedByInstanceId, CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);
        await SqliteMigrations.ApplyAsync(connection, cancellationToken);

        await using var tx = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

        await using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                UPDATE problem
                SET isDeleted = 1,
                    deletedAtUtc = $deletedAtUtc,
                    updatedAtUtc = $updatedAtUtc,
                    updatedByInstanceId = $updatedByInstanceId
                WHERE id = $id;
                """;
            cmd.Parameters.AddWithValue("$id", problemId);
            cmd.Parameters.AddWithValue("$deletedAtUtc", ToUtcText(deletedAtUtc));
            cmd.Parameters.AddWithValue("$updatedAtUtc", ToUtcText(deletedAtUtc));
            cmd.Parameters.AddWithValue("$updatedByInstanceId", updatedByInstanceId);
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var deleteCmd = connection.CreateCommand())
        {
            deleteCmd.Transaction = tx;
            deleteCmd.CommandText = "DELETE FROM problem_fts WHERE problemId = $id;";
            deleteCmd.Parameters.AddWithValue("$id", problemId);
            await deleteCmd.ExecuteNonQueryAsync(cancellationToken);
        }

        await tx.CommitAsync(cancellationToken);
    }

    public async Task<Problem?> GetProblemByIdAsync(string problemId, CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);
        await SqliteMigrations.ApplyAsync(connection, cancellationToken);

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT
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
                sourceKind
            FROM problem
            WHERE id = $id
            LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$id", problemId);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return MapProblem(reader);
    }

    public async Task<IReadOnlyList<ProblemSearchHit>> SearchProblemsAsync(string query, IReadOnlyList<string> tagIds, string? professionFilterId, int limit, int offset, CancellationToken cancellationToken)
    {
        tagIds ??= Array.Empty<string>();

        if (limit <= 0)
        {
            return Array.Empty<ProblemSearchHit>();
        }

        if (offset < 0)
        {
            offset = 0;
        }

        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);
        await SqliteMigrations.ApplyAsync(connection, cancellationToken);

        var terms = SplitQueryTerms(query);
        var hasQuery = terms.Length > 0;
        var professionWhere = BuildProfessionWhere(professionFilterId, out var professionParamValue);

        await using var cmd = connection.CreateCommand();
        cmd.Parameters.AddWithValue("$limit", limit);
        cmd.Parameters.AddWithValue("$offset", offset);
        if (professionWhere != "1=1")
        {
            cmd.Parameters.AddWithValue("$professionPattern", professionParamValue!);
        }

        var termClauses = new List<string>();
        for (var i = 0; i < terms.Length; i++)
        {
            var p = $"$q{i}";
            cmd.Parameters.AddWithValue(p, terms[i]);
            termClauses.Add($"""
                (
                    instr(p.title, {p}) > 0 OR instr(lower(p.title), lower({p})) > 0 OR
                    instr(p.symptom, {p}) > 0 OR instr(lower(p.symptom), lower({p})) > 0 OR
                    instr(p.rootCause, {p}) > 0 OR instr(lower(p.rootCause), lower({p})) > 0 OR
                    instr(p.solution, {p}) > 0 OR instr(lower(p.solution), lower({p})) > 0 OR
                    instr(p.environmentJson, {p}) > 0 OR instr(lower(p.environmentJson), lower({p})) > 0
                )
                """);
        }

        var whereQuery = hasQuery ? string.Join(" AND ", termClauses) : "1=1";
        var whereAll = $"({professionWhere}) AND ({whereQuery})";

        var scoreExpr = "0.0";
        if (hasQuery)
        {
            var scoreParts = new List<string>();
            for (var i = 0; i < terms.Length; i++)
            {
                var p = $"$q{i}";
                scoreParts.Add($"""
                    (
                        CASE WHEN (instr(p.title, {p}) > 0 OR instr(lower(p.title), lower({p})) > 0) THEN 120 ELSE 0 END +
                        CASE WHEN (instr(p.symptom, {p}) > 0 OR instr(lower(p.symptom), lower({p})) > 0) THEN 45 ELSE 0 END +
                        CASE WHEN (instr(p.rootCause, {p}) > 0 OR instr(lower(p.rootCause), lower({p})) > 0) THEN 40 ELSE 0 END +
                        CASE WHEN (instr(p.solution, {p}) > 0 OR instr(lower(p.solution), lower({p})) > 0) THEN 35 ELSE 0 END +
                        CASE WHEN (instr(p.environmentJson, {p}) > 0 OR instr(lower(p.environmentJson), lower({p})) > 0) THEN 15 ELSE 0 END
                    )
                    """);
            }

            scoreExpr = string.Join(" + ", scoreParts);
        }

        string snippetExpr;
        if (hasQuery)
        {
            var first = "$q0";
            snippetExpr = $"""
                CASE
                    WHEN (instr(p.symptom, {first}) > 0 OR instr(lower(p.symptom), lower({first})) > 0) THEN substr(p.symptom, CASE WHEN instr(p.symptom, {first}) > 10 THEN instr(p.symptom, {first}) - 10 ELSE 1 END, 100)
                    WHEN (instr(p.rootCause, {first}) > 0 OR instr(lower(p.rootCause), lower({first})) > 0) THEN substr(p.rootCause, CASE WHEN instr(p.rootCause, {first}) > 10 THEN instr(p.rootCause, {first}) - 10 ELSE 1 END, 100)
                    WHEN (instr(p.solution, {first}) > 0 OR instr(lower(p.solution), lower({first})) > 0) THEN substr(p.solution, CASE WHEN instr(p.solution, {first}) > 10 THEN instr(p.solution, {first}) - 10 ELSE 1 END, 100)
                    WHEN (instr(p.environmentJson, {first}) > 0 OR instr(lower(p.environmentJson), lower({first})) > 0) THEN substr(p.environmentJson, CASE WHEN instr(p.environmentJson, {first}) > 10 THEN instr(p.environmentJson, {first}) - 10 ELSE 1 END, 100)
                    ELSE NULL
                END
                """;
        }
        else
        {
            snippetExpr = "NULL";
        }

        if (tagIds.Count == 0)
        {
            cmd.CommandText = $"""
                SELECT
                    p.id AS problemId,
                    p.title AS title,
                    p.updatedAtUtc AS updatedAtUtc,
                    {scoreExpr} AS score,
                    {snippetExpr} AS snippet
                FROM problem p
                WHERE p.isDeleted = 0 AND {whereAll}
                ORDER BY score DESC, p.updatedAtUtc DESC
                LIMIT $limit OFFSET $offset;
                """;
        }
        else
        {
            var inParams = new List<string>();
            for (var i = 0; i < tagIds.Count; i++)
            {
                var p = $"$t{i}";
                inParams.Add(p);
                cmd.Parameters.AddWithValue(p, tagIds[i]);
            }

            cmd.Parameters.AddWithValue("$tagCount", tagIds.Count);

            cmd.CommandText = $"""
                SELECT
                    p.id AS problemId,
                    p.title AS title,
                    p.updatedAtUtc AS updatedAtUtc,
                    {scoreExpr} AS score,
                    {snippetExpr} AS snippet
                FROM problem p
                JOIN problemTag pt ON pt.problemId = p.id AND pt.isDeleted = 0
                JOIN tag t ON t.id = pt.tagId AND t.isDeleted = 0
                WHERE p.isDeleted = 0 AND pt.tagId IN ({string.Join(", ", inParams)}) AND {whereAll}
                GROUP BY p.id, p.title, p.updatedAtUtc, p.symptom, p.rootCause, p.solution, p.environmentJson
                HAVING COUNT(DISTINCT pt.tagId) = $tagCount
                ORDER BY score DESC, p.updatedAtUtc DESC
                LIMIT $limit OFFSET $offset;
                """;
        }

        var hits = new List<ProblemSearchHit>(capacity: Math.Min(limit, 128));
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var id = reader.GetString(0);
            var title = reader.GetString(1);
            var updatedAtUtc = ParseUtcText(reader.GetString(2));
            var rawScore = reader.GetDouble(3);
            var snippet = reader.IsDBNull(4) ? null : reader.GetString(4);

            hits.Add(new ProblemSearchHit(id, title, updatedAtUtc, rawScore, snippet));
        }

        return hits;
    }

    public async Task<int> CountProblemsAsync(string query, IReadOnlyList<string> tagIds, string? professionFilterId, CancellationToken cancellationToken)
    {
        tagIds ??= Array.Empty<string>();

        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);
        await SqliteMigrations.ApplyAsync(connection, cancellationToken);

        var terms = SplitQueryTerms(query);
        var hasQuery = terms.Length > 0;
        var professionWhere = BuildProfessionWhere(professionFilterId, out var professionParamValue);

        await using var cmd = connection.CreateCommand();
        if (professionWhere != "1=1")
        {
            cmd.Parameters.AddWithValue("$professionPattern", professionParamValue!);
        }

        var termClauses = new List<string>();
        for (var i = 0; i < terms.Length; i++)
        {
            var p = $"$q{i}";
            cmd.Parameters.AddWithValue(p, terms[i]);
            termClauses.Add($"""
                (
                    instr(p.title, {p}) > 0 OR instr(lower(p.title), lower({p})) > 0 OR
                    instr(p.symptom, {p}) > 0 OR instr(lower(p.symptom), lower({p})) > 0 OR
                    instr(p.rootCause, {p}) > 0 OR instr(lower(p.rootCause), lower({p})) > 0 OR
                    instr(p.solution, {p}) > 0 OR instr(lower(p.solution), lower({p})) > 0 OR
                    instr(p.environmentJson, {p}) > 0 OR instr(lower(p.environmentJson), lower({p})) > 0
                )
                """);
        }

        var whereQuery = hasQuery ? string.Join(" AND ", termClauses) : "1=1";
        var whereAll = $"({professionWhere}) AND ({whereQuery})";

        if (tagIds.Count == 0)
        {
            cmd.CommandText = $"""
                SELECT COUNT(*)
                FROM problem p
                WHERE p.isDeleted = 0 AND {whereAll};
                """;
        }
        else
        {
            var inParams = new List<string>();
            for (var i = 0; i < tagIds.Count; i++)
            {
                var p = $"$t{i}";
                inParams.Add(p);
                cmd.Parameters.AddWithValue(p, tagIds[i]);
            }

            cmd.Parameters.AddWithValue("$tagCount", tagIds.Count);

            cmd.CommandText = $"""
                SELECT COUNT(*) FROM (
                    SELECT p.id
                    FROM problem p
                    JOIN problemTag pt ON pt.problemId = p.id AND pt.isDeleted = 0
                    JOIN tag t ON t.id = pt.tagId AND t.isDeleted = 0
                    WHERE p.isDeleted = 0 AND pt.tagId IN ({string.Join(", ", inParams)}) AND {whereAll}
                    GROUP BY p.id, p.title, p.updatedAtUtc, p.symptom, p.rootCause, p.solution, p.environmentJson
                    HAVING COUNT(DISTINCT pt.tagId) = $tagCount
                ) x;
                """;
        }

        var result = await cmd.ExecuteScalarAsync(cancellationToken);
        if (result is long l)
        {
            return (int)Math.Min(int.MaxValue, l);
        }

        if (result is int i32)
        {
            return i32;
        }

        return 0;
    }

    public async Task<int> CountProblemsForHardDeleteAsync(ProblemHardDeleteFilter filter, CancellationToken cancellationToken)
    {
        filter ??= new ProblemHardDeleteFilter(Array.Empty<string>(), "all", null, null, false);
        var tagIds = filter.TagIds ?? Array.Empty<string>();

        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);
        await SqliteMigrations.ApplyAsync(connection, cancellationToken);

        var professionWhere = BuildProfessionWhere(filter.ProfessionFilterId, out var professionParamValue);
        await using var cmd = connection.CreateCommand();
        if (professionWhere != "1=1")
        {
            cmd.Parameters.AddWithValue("$professionPattern", professionParamValue!);
        }

        var deletedWhere = filter.IncludeSoftDeleted ? "1=1" : "p.isDeleted = 0";
        var timeWhere = BuildUpdatedAtWhere(filter.UpdatedFromUtc, filter.UpdatedToUtc, cmd);
        var whereAll = $"{deletedWhere} AND ({professionWhere}) AND ({timeWhere})";

        if (tagIds.Count == 0)
        {
            cmd.CommandText = $"""
                SELECT COUNT(*)
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
                cmd.Parameters.AddWithValue(p, tagIds[i]);
            }

            cmd.Parameters.AddWithValue("$tagCount", tagIds.Count);

            cmd.CommandText = $"""
                SELECT COUNT(*) FROM (
                    SELECT p.id
                    FROM problem p
                    JOIN problemTag pt ON pt.problemId = p.id AND pt.isDeleted = 0
                    JOIN tag t ON t.id = pt.tagId AND t.isDeleted = 0
                    WHERE {whereAll} AND pt.tagId IN ({string.Join(", ", inParams)})
                    GROUP BY p.id
                    HAVING COUNT(DISTINCT pt.tagId) = $tagCount
                ) x;
                """;
        }

        var result = await cmd.ExecuteScalarAsync(cancellationToken);
        if (result is long l)
        {
            return (int)Math.Min(int.MaxValue, l);
        }

        if (result is int i32)
        {
            return i32;
        }

        return 0;
    }

    public async Task<int> HardDeleteProblemsAsync(ProblemHardDeleteFilter filter, CancellationToken cancellationToken)
    {
        filter ??= new ProblemHardDeleteFilter(Array.Empty<string>(), "all", null, null, false);
        var tagIds = filter.TagIds ?? Array.Empty<string>();

        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);
        await SqliteMigrations.ApplyAsync(connection, cancellationToken);

        await using var tx = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

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

        int targetCount;
        await using (var count = connection.CreateCommand())
        {
            count.Transaction = tx;
            count.CommandText = "SELECT COUNT(*) FROM __targets;";
            var scalar = await count.ExecuteScalarAsync(cancellationToken);
            targetCount = scalar is long l ? (int)Math.Min(int.MaxValue, l) : scalar is int i32 ? i32 : 0;
        }

        if (targetCount > 0)
        {
            await using (var del = connection.CreateCommand())
            {
                del.Transaction = tx;
                del.CommandText = """
                    DELETE FROM problemTag WHERE problemId IN (SELECT id FROM __targets);
                    DELETE FROM attachment WHERE problemId IN (SELECT id FROM __targets);
                    DELETE FROM problem_fts WHERE problemId IN (SELECT id FROM __targets);
                    DELETE FROM conflictRecord WHERE lower(entityType) = 'problem' AND entityId IN (SELECT id FROM __targets);
                    DELETE FROM problem WHERE id IN (SELECT id FROM __targets);
                    """;
                await del.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        await using (var drop = connection.CreateCommand())
        {
            drop.Transaction = tx;
            drop.CommandText = "DELETE FROM __targets;";
            await drop.ExecuteNonQueryAsync(cancellationToken);
        }

        await tx.CommitAsync(cancellationToken);
        return targetCount;
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

    public async Task<IReadOnlyList<Tag>> GetAllTagsAsync(CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);
        await SqliteMigrations.ApplyAsync(connection, cancellationToken);

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT id, name, createdAtUtc, updatedAtUtc, updatedByInstanceId, isDeleted
            FROM tag
            WHERE isDeleted = 0
            ORDER BY name COLLATE NOCASE;
            """;

        var list = new List<Tag>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            list.Add(MapTag(reader));
        }

        return list;
    }

    public async Task<Tag> CreateTagAsync(string name, DateTimeOffset nowUtc, string updatedByInstanceId, CancellationToken cancellationToken)
    {
        var normalized = (name ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new ArgumentException("Tag name is required.", nameof(name));
        }

        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);
        await SqliteMigrations.ApplyAsync(connection, cancellationToken);

        await using (var findCmd = connection.CreateCommand())
        {
            findCmd.CommandText = """
                SELECT id, name, createdAtUtc, updatedAtUtc, updatedByInstanceId, isDeleted
                FROM tag
                WHERE isDeleted = 0 AND lower(name) = lower($name)
                LIMIT 1;
                """;
            findCmd.Parameters.AddWithValue("$name", normalized);

            await using var reader = await findCmd.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                return MapTag(reader);
            }
        }

        var tag = new Tag(
            Id: Guid.NewGuid().ToString("D"),
            Name: normalized,
            CreatedAtUtc: nowUtc,
            UpdatedAtUtc: nowUtc,
            UpdatedByInstanceId: updatedByInstanceId,
            IsDeleted: false);

        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO tag (id, name, createdAtUtc, updatedAtUtc, updatedByInstanceId, isDeleted)
                VALUES ($id, $name, $createdAtUtc, $updatedAtUtc, $updatedByInstanceId, 0);
                """;
            cmd.Parameters.AddWithValue("$id", tag.Id);
            cmd.Parameters.AddWithValue("$name", tag.Name);
            cmd.Parameters.AddWithValue("$createdAtUtc", ToUtcText(tag.CreatedAtUtc));
            cmd.Parameters.AddWithValue("$updatedAtUtc", ToUtcText(tag.UpdatedAtUtc));
            cmd.Parameters.AddWithValue("$updatedByInstanceId", tag.UpdatedByInstanceId);
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        return tag;
    }

    public async Task SoftDeleteTagAsync(string tagId, DateTimeOffset nowUtc, string updatedByInstanceId, CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);
        await SqliteMigrations.ApplyAsync(connection, cancellationToken);

        await using var tx = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                UPDATE tag
                SET isDeleted = 1,
                    updatedAtUtc = $updatedAtUtc,
                    updatedByInstanceId = $updatedByInstanceId
                WHERE id = $id;
                """;
            cmd.Parameters.AddWithValue("$id", tagId);
            cmd.Parameters.AddWithValue("$updatedAtUtc", ToUtcText(nowUtc));
            cmd.Parameters.AddWithValue("$updatedByInstanceId", updatedByInstanceId);
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                UPDATE problemTag
                SET isDeleted = 1,
                    updatedAtUtc = $updatedAtUtc,
                    updatedByInstanceId = $updatedByInstanceId
                WHERE tagId = $tagId;
                """;
            cmd.Parameters.AddWithValue("$tagId", tagId);
            cmd.Parameters.AddWithValue("$updatedAtUtc", ToUtcText(nowUtc));
            cmd.Parameters.AddWithValue("$updatedByInstanceId", updatedByInstanceId);
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        await tx.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Tag>> GetTagsForProblemAsync(string problemId, CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);
        await SqliteMigrations.ApplyAsync(connection, cancellationToken);

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT t.id, t.name, t.createdAtUtc, t.updatedAtUtc, t.updatedByInstanceId, t.isDeleted
            FROM problemTag pt
            JOIN tag t ON t.id = pt.tagId
            WHERE pt.problemId = $problemId AND pt.isDeleted = 0 AND t.isDeleted = 0
            ORDER BY t.name COLLATE NOCASE;
            """;
        cmd.Parameters.AddWithValue("$problemId", problemId);

        var list = new List<Tag>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            list.Add(MapTag(reader));
        }

        return list;
    }

    public async Task SetTagsForProblemAsync(string problemId, IReadOnlyList<string> tagIds, DateTimeOffset nowUtc, string updatedByInstanceId, CancellationToken cancellationToken)
    {
        tagIds ??= Array.Empty<string>();
        var normalized = tagIds.Where(static x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.Ordinal).ToArray();

        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);
        await SqliteMigrations.ApplyAsync(connection, cancellationToken);
        await using var tx = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

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
            cmd.Parameters.AddWithValue("$updatedByInstanceId", updatedByInstanceId);
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
                updateCmd.Parameters.AddWithValue("$updatedByInstanceId", updatedByInstanceId);
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
            insertCmd.Parameters.AddWithValue("$updatedByInstanceId", updatedByInstanceId);
            await insertCmd.ExecuteNonQueryAsync(cancellationToken);
        }

        await tx.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Attachment>> GetAttachmentsForProblemAsync(string problemId, CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);
        await SqliteMigrations.ApplyAsync(connection, cancellationToken);

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT id, problemId, originalFileName, contentHash, sizeBytes, mimeType, createdAtUtc, updatedAtUtc, updatedByInstanceId, isDeleted
            FROM attachment
            WHERE problemId = $problemId AND isDeleted = 0
            ORDER BY createdAtUtc DESC;
            """;
        cmd.Parameters.AddWithValue("$problemId", problemId);

        var list = new List<Attachment>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            list.Add(MapAttachment(reader));
        }

        return list;
    }

    public async Task<Attachment> AddAttachmentAsync(string problemId, string sourceFilePath, DateTimeOffset nowUtc, string updatedByInstanceId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sourceFilePath) || !File.Exists(sourceFilePath))
        {
            throw new FileNotFoundException("Attachment file not found.", sourceFilePath);
        }

        var fileInfo = new FileInfo(sourceFilePath);
        var contentHash = await ComputeSha256Async(sourceFilePath, cancellationToken);
        var destDir = AppDataPaths.GetAttachmentsDirectory();
        Directory.CreateDirectory(destDir);
        var destPath = Path.Combine(destDir, contentHash);
        if (!File.Exists(destPath))
        {
            File.Copy(sourceFilePath, destPath, overwrite: false);
        }

        var attachment = new Attachment(
            Id: Guid.NewGuid().ToString("D"),
            ProblemId: problemId,
            OriginalFileName: fileInfo.Name,
            ContentHash: contentHash,
            SizeBytes: fileInfo.Length,
            MimeType: "application/octet-stream",
            CreatedAtUtc: nowUtc,
            UpdatedAtUtc: nowUtc,
            UpdatedByInstanceId: updatedByInstanceId,
            IsDeleted: false);

        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);
        await SqliteMigrations.ApplyAsync(connection, cancellationToken);

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO attachment (
                id, problemId, originalFileName, contentHash, sizeBytes, mimeType,
                createdAtUtc, updatedAtUtc, updatedByInstanceId, isDeleted
            )
            VALUES (
                $id, $problemId, $originalFileName, $contentHash, $sizeBytes, $mimeType,
                $createdAtUtc, $updatedAtUtc, $updatedByInstanceId, 0
            );
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
        await cmd.ExecuteNonQueryAsync(cancellationToken);

        return attachment;
    }

    public Task<string> GetAttachmentLocalPathAsync(string contentHash, CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        var path = Path.Combine(AppDataPaths.GetAttachmentsDirectory(), contentHash);
        return Task.FromResult(path);
    }

    public async Task<IReadOnlyList<ConflictRecordSummary>> GetUnresolvedConflictsAsync(int limit, CancellationToken cancellationToken)
    {
        if (limit <= 0)
        {
            return Array.Empty<ConflictRecordSummary>();
        }

        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);
        await SqliteMigrations.ApplyAsync(connection, cancellationToken);

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT id, entityType, entityId, importedUpdatedAtUtc, localUpdatedAtUtc, createdAtUtc
            FROM conflictRecord
            WHERE resolvedAtUtc IS NULL
            ORDER BY createdAtUtc DESC
            LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$limit", limit);

        var list = new List<ConflictRecordSummary>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            list.Add(new ConflictRecordSummary(
                ConflictId: reader.GetString(0),
                EntityType: reader.GetString(1),
                EntityId: reader.GetString(2),
                ImportedUpdatedAtUtc: ParseUtcText(reader.GetString(3)),
                LocalUpdatedAtUtc: ParseUtcText(reader.GetString(4)),
                CreatedAtUtc: ParseUtcText(reader.GetString(5))));
        }

        return list;
    }

    public async Task<ConflictRecordDetail?> GetConflictDetailAsync(string conflictId, CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);
        await SqliteMigrations.ApplyAsync(connection, cancellationToken);

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT id, entityType, entityId, importedUpdatedAtUtc, localUpdatedAtUtc, importedJson
            FROM conflictRecord
            WHERE id = $id
            LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$id", conflictId);

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var entityType = reader.GetString(1);
        var entityId = reader.GetString(2);
        var localJson = await SerializeLocalEntityJsonAsync(connection, entityType, entityId, cancellationToken) ?? "{}";

        return new ConflictRecordDetail(
            ConflictId: reader.GetString(0),
            EntityType: entityType,
            EntityId: entityId,
            ImportedUpdatedAtUtc: ParseUtcText(reader.GetString(3)),
            LocalUpdatedAtUtc: ParseUtcText(reader.GetString(4)),
            ImportedJson: reader.GetString(5),
            LocalJson: localJson);
    }

    public async Task ResolveConflictAsync(string conflictId, ConflictResolution resolution, DateTimeOffset nowUtc, string resolvedByInstanceId, CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);
        await SqliteMigrations.ApplyAsync(connection, cancellationToken);
        await using var tx = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

        string? entityType = null;
        string? importedJson = null;

        await using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "SELECT entityType, importedJson FROM conflictRecord WHERE id = $id LIMIT 1;";
            cmd.Parameters.AddWithValue("$id", conflictId);
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                entityType = reader.GetString(0);
                importedJson = reader.GetString(1);
            }
        }

        if (entityType is null || importedJson is null)
        {
            await tx.CommitAsync(cancellationToken);
            return;
        }

        if (resolution == ConflictResolution.UseImported)
        {
            await ApplyImportedEntityJsonAsync(connection, tx, entityType, importedJson, cancellationToken);
        }

        await using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                UPDATE conflictRecord
                SET resolvedAtUtc = $resolvedAtUtc,
                    resolution = $resolution,
                    resolvedBy = $resolvedBy
                WHERE id = $id;
                """;
            cmd.Parameters.AddWithValue("$id", conflictId);
            cmd.Parameters.AddWithValue("$resolvedAtUtc", ToUtcText(nowUtc));
            cmd.Parameters.AddWithValue("$resolution", resolution.ToString());
            cmd.Parameters.AddWithValue("$resolvedBy", resolvedByInstanceId);
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        await tx.CommitAsync(cancellationToken);
    }

    private static async Task<string?> SerializeLocalEntityJsonAsync(SqliteConnection connection, string entityType, string entityId, CancellationToken cancellationToken)
    {
        if (entityType.Equals("Problem", StringComparison.OrdinalIgnoreCase))
        {
            var problem = await ReadProblemAsync(connection, entityId, cancellationToken);
            return problem is null ? null : JsonSerializer.Serialize(problem, JsonOptions);
        }

        if (entityType.Equals("Tag", StringComparison.OrdinalIgnoreCase))
        {
            var tag = await ReadTagAsync(connection, entityId, cancellationToken);
            return tag is null ? null : JsonSerializer.Serialize(tag, JsonOptions);
        }

        if (entityType.Equals("ProblemTag", StringComparison.OrdinalIgnoreCase))
        {
            var pt = await ReadProblemTagAsync(connection, entityId, cancellationToken);
            return pt is null ? null : JsonSerializer.Serialize(pt, JsonOptions);
        }

        if (entityType.Equals("Attachment", StringComparison.OrdinalIgnoreCase))
        {
            var a = await ReadAttachmentAsync(connection, entityId, cancellationToken);
            return a is null ? null : JsonSerializer.Serialize(a, JsonOptions);
        }

        return null;
    }

    private static async Task ApplyImportedEntityJsonAsync(SqliteConnection connection, SqliteTransaction tx, string entityType, string importedJson, CancellationToken cancellationToken)
    {
        if (entityType.Equals("Problem", StringComparison.OrdinalIgnoreCase))
        {
            var problem = JsonSerializer.Deserialize<Problem>(importedJson, JsonOptions);
            if (problem is null)
            {
                return;
            }

            await UpsertProblemRowAsync(connection, tx, problem, cancellationToken);
            await UpsertProblemFtsAsync(connection, tx, problem, cancellationToken);
            return;
        }

        if (entityType.Equals("Tag", StringComparison.OrdinalIgnoreCase))
        {
            var tag = JsonSerializer.Deserialize<Tag>(importedJson, JsonOptions);
            if (tag is null)
            {
                return;
            }

            await UpsertTagRowAsync(connection, tx, tag, cancellationToken);
            return;
        }

        if (entityType.Equals("ProblemTag", StringComparison.OrdinalIgnoreCase))
        {
            var pt = JsonSerializer.Deserialize<ProblemTag>(importedJson, JsonOptions);
            if (pt is null)
            {
                return;
            }

            await UpsertProblemTagRowAsync(connection, tx, pt, cancellationToken);
            return;
        }

        if (entityType.Equals("Attachment", StringComparison.OrdinalIgnoreCase))
        {
            var a = JsonSerializer.Deserialize<Attachment>(importedJson, JsonOptions);
            if (a is null)
            {
                return;
            }

            await UpsertAttachmentRowAsync(connection, tx, a, cancellationToken);
        }
    }

    private static async Task<Problem?> ReadProblemAsync(SqliteConnection connection, string problemId, CancellationToken cancellationToken)
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
            WHERE id = $id
            LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$id", problemId);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? MapProblem(reader) : null;
    }

    private static async Task<Tag?> ReadTagAsync(SqliteConnection connection, string tagId, CancellationToken cancellationToken)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT id, name, createdAtUtc, updatedAtUtc, updatedByInstanceId, isDeleted
            FROM tag
            WHERE id = $id
            LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$id", tagId);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? MapTag(reader) : null;
    }

    private static async Task<ProblemTag?> ReadProblemTagAsync(SqliteConnection connection, string id, CancellationToken cancellationToken)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT id, problemId, tagId, createdAtUtc, updatedAtUtc, updatedByInstanceId, isDeleted
            FROM problemTag
            WHERE id = $id
            LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$id", id);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? MapProblemTag(reader) : null;
    }

    private static async Task<Attachment?> ReadAttachmentAsync(SqliteConnection connection, string id, CancellationToken cancellationToken)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT id, problemId, originalFileName, contentHash, sizeBytes, mimeType, createdAtUtc, updatedAtUtc, updatedByInstanceId, isDeleted
            FROM attachment
            WHERE id = $id
            LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$id", id);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? MapAttachment(reader) : null;
    }

    private static async Task UpsertTagRowAsync(SqliteConnection connection, SqliteTransaction tx, Tag tag, CancellationToken cancellationToken)
    {
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
    }

    private static async Task UpsertProblemTagRowAsync(SqliteConnection connection, SqliteTransaction tx, ProblemTag problemTag, CancellationToken cancellationToken)
    {
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
    }

    private static async Task UpsertAttachmentRowAsync(SqliteConnection connection, SqliteTransaction tx, Attachment attachment, CancellationToken cancellationToken)
    {
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
    }

    private static async Task<string> ComputeSha256Async(string filePath, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(filePath);
        using var sha = SHA256.Create();
        var hash = await sha.ComputeHashAsync(stream, cancellationToken);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static async Task UpsertProblemRowAsync(
        SqliteConnection connection,
        SqliteTransaction tx,
        Problem problem,
        CancellationToken cancellationToken)
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

    private static async Task UpsertProblemFtsAsync(
        SqliteConnection connection,
        SqliteTransaction tx,
        Problem problem,
        CancellationToken cancellationToken)
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

    private static string[] SplitQueryTerms(string query)
    {
        return (query ?? string.Empty)
            .Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .ToArray();
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

    private static ProblemTag MapProblemTag(SqliteDataReader reader)
    {
        return new ProblemTag(
            Id: reader.GetString(0),
            ProblemId: reader.GetString(1),
            TagId: reader.GetString(2),
            CreatedAtUtc: ParseUtcText(reader.GetString(3)),
            UpdatedAtUtc: ParseUtcText(reader.GetString(4)),
            UpdatedByInstanceId: reader.GetString(5),
            IsDeleted: reader.GetInt32(6) != 0);
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
}
