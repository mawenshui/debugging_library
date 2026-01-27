using FieldKb.Domain.Models;

namespace FieldKb.Application.Abstractions;

public interface IKbStore
{
    Task InitializeAsync(CancellationToken cancellationToken);

    Task UpsertProblemAsync(Problem problem, CancellationToken cancellationToken);

    Task SoftDeleteProblemAsync(string problemId, DateTimeOffset deletedAtUtc, string updatedByInstanceId, CancellationToken cancellationToken);

    Task<Problem?> GetProblemByIdAsync(string problemId, CancellationToken cancellationToken);

    Task<IReadOnlyList<ProblemSearchHit>> SearchProblemsAsync(string query, IReadOnlyList<string> tagIds, string? professionFilterId, int limit, int offset, CancellationToken cancellationToken);

    Task<int> CountProblemsAsync(string query, IReadOnlyList<string> tagIds, string? professionFilterId, CancellationToken cancellationToken);

    Task<int> CountProblemsForHardDeleteAsync(ProblemHardDeleteFilter filter, CancellationToken cancellationToken);

    Task<int> HardDeleteProblemsAsync(ProblemHardDeleteFilter filter, CancellationToken cancellationToken);

    Task<IReadOnlyList<Tag>> GetAllTagsAsync(CancellationToken cancellationToken);

    Task<Tag> CreateTagAsync(string name, DateTimeOffset nowUtc, string updatedByInstanceId, CancellationToken cancellationToken);

    Task SoftDeleteTagAsync(string tagId, DateTimeOffset nowUtc, string updatedByInstanceId, CancellationToken cancellationToken);

    Task<IReadOnlyList<Tag>> GetTagsForProblemAsync(string problemId, CancellationToken cancellationToken);

    Task SetTagsForProblemAsync(string problemId, IReadOnlyList<string> tagIds, DateTimeOffset nowUtc, string updatedByInstanceId, CancellationToken cancellationToken);

    Task<IReadOnlyList<Attachment>> GetAttachmentsForProblemAsync(string problemId, CancellationToken cancellationToken);

    Task<Attachment> AddAttachmentAsync(string problemId, string sourceFilePath, DateTimeOffset nowUtc, string updatedByInstanceId, CancellationToken cancellationToken);

    Task<string> GetAttachmentLocalPathAsync(string contentHash, CancellationToken cancellationToken);

    Task<IReadOnlyList<ConflictRecordSummary>> GetUnresolvedConflictsAsync(int limit, CancellationToken cancellationToken);

    Task<ConflictRecordDetail?> GetConflictDetailAsync(string conflictId, CancellationToken cancellationToken);

    Task ResolveConflictAsync(string conflictId, ConflictResolution resolution, DateTimeOffset nowUtc, string resolvedByInstanceId, CancellationToken cancellationToken);
}

public enum ConflictResolution
{
    KeepLocal = 0,
    UseImported = 1
}

public sealed record ConflictRecordSummary(
    string ConflictId,
    string EntityType,
    string EntityId,
    DateTimeOffset ImportedUpdatedAtUtc,
    DateTimeOffset LocalUpdatedAtUtc,
    DateTimeOffset CreatedAtUtc);

public sealed record ConflictRecordDetail(
    string ConflictId,
    string EntityType,
    string EntityId,
    DateTimeOffset ImportedUpdatedAtUtc,
    DateTimeOffset LocalUpdatedAtUtc,
    string ImportedJson,
    string LocalJson);

public sealed record ProblemHardDeleteFilter(
    IReadOnlyList<string> TagIds,
    string? ProfessionFilterId,
    DateTimeOffset? UpdatedFromUtc,
    DateTimeOffset? UpdatedToUtc,
    bool IncludeSoftDeleted = false);
