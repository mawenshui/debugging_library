using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using FieldKb.Domain.Models;

namespace FieldKb.Client.Wpf;

public static class ConflictTextFormatter
{
    private static readonly JsonSerializerOptions ReadOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly JsonSerializerOptions PrettyOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static string Format(string entityType, string json)
    {
        if (entityType.Equals("Problem", StringComparison.OrdinalIgnoreCase))
        {
            var p = TryDeserialize<Problem>(json);
            return p is null ? PrettyJson(json) : FormatProblem(p);
        }

        if (entityType.Equals("Tag", StringComparison.OrdinalIgnoreCase))
        {
            var t = TryDeserialize<Tag>(json);
            return t is null ? PrettyJson(json) : FormatTag(t);
        }

        if (entityType.Equals("ProblemTag", StringComparison.OrdinalIgnoreCase))
        {
            var pt = TryDeserialize<ProblemTag>(json);
            return pt is null ? PrettyJson(json) : FormatProblemTag(pt);
        }

        if (entityType.Equals("Attachment", StringComparison.OrdinalIgnoreCase))
        {
            var a = TryDeserialize<Attachment>(json);
            return a is null ? PrettyJson(json) : FormatAttachment(a);
        }

        return PrettyJson(json);
    }

    public static string BuildDiff(string entityType, string localJson, string importedJson, DateTimeOffset localUpdatedAtUtc, DateTimeOffset importedUpdatedAtUtc)
    {
        var parts = new List<string>();

        if (entityType.Equals("Problem", StringComparison.OrdinalIgnoreCase))
        {
            var local = TryDeserialize<Problem>(localJson);
            var imported = TryDeserialize<Problem>(importedJson);
            if (local is not null && imported is not null)
            {
                AddIfChanged(parts, "标题", local.Title, imported.Title);
                AddIfChanged(parts, "现象", local.Symptom, imported.Symptom);
                AddIfChanged(parts, "原因", local.RootCause, imported.RootCause);
                AddIfChanged(parts, "解决方案", local.Solution, imported.Solution);

                var localEnv = NormalizeEnv(local.EnvironmentJson);
                var importedEnv = NormalizeEnv(imported.EnvironmentJson);
                if (!string.Equals(localEnv, importedEnv, StringComparison.Ordinal))
                {
                    parts.Add("环境信息");
                }
            }
        }
        else if (entityType.Equals("Tag", StringComparison.OrdinalIgnoreCase))
        {
            var local = TryDeserialize<Tag>(localJson);
            var imported = TryDeserialize<Tag>(importedJson);
            if (local is not null && imported is not null)
            {
                AddIfChanged(parts, "名称", local.Name, imported.Name);
                AddIfChanged(parts, "是否删除", local.IsDeleted.ToString(), imported.IsDeleted.ToString());
            }
        }
        else if (entityType.Equals("Attachment", StringComparison.OrdinalIgnoreCase))
        {
            var local = TryDeserialize<Attachment>(localJson);
            var imported = TryDeserialize<Attachment>(importedJson);
            if (local is not null && imported is not null)
            {
                AddIfChanged(parts, "文件名", local.OriginalFileName, imported.OriginalFileName);
                AddIfChanged(parts, "大小", local.SizeBytes.ToString(), imported.SizeBytes.ToString());
                AddIfChanged(parts, "类型", local.MimeType, imported.MimeType);
                AddIfChanged(parts, "是否删除", local.IsDeleted.ToString(), imported.IsDeleted.ToString());
            }
        }
        else if (entityType.Equals("ProblemTag", StringComparison.OrdinalIgnoreCase))
        {
            var local = TryDeserialize<ProblemTag>(localJson);
            var imported = TryDeserialize<ProblemTag>(importedJson);
            if (local is not null && imported is not null)
            {
                AddIfChanged(parts, "问题ID", local.ProblemId, imported.ProblemId);
                AddIfChanged(parts, "标签ID", local.TagId, imported.TagId);
                AddIfChanged(parts, "是否删除", local.IsDeleted.ToString(), imported.IsDeleted.ToString());
            }
        }

        var header = $"差异字段：{(parts.Count == 0 ? "（未识别到关键字段差异）" : string.Join("、", parts.Distinct()))}";
        var timeLine = $"时间对比：本地 {localUpdatedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss} / 导入 {importedUpdatedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}";
        return $"{header}{Environment.NewLine}{timeLine}";
    }

    private static void AddIfChanged(List<string> parts, string label, string? local, string? imported)
    {
        if (!string.Equals((local ?? string.Empty).Trim(), (imported ?? string.Empty).Trim(), StringComparison.Ordinal))
        {
            parts.Add(label);
        }
    }

    private static string FormatProblem(Problem p)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"标题：{p.Title}");
        sb.AppendLine();
        sb.AppendLine("现象：");
        sb.AppendLine(p.Symptom ?? string.Empty);
        sb.AppendLine();
        sb.AppendLine("原因：");
        sb.AppendLine(p.RootCause ?? string.Empty);
        sb.AppendLine();
        sb.AppendLine("解决方案：");
        sb.AppendLine(p.Solution ?? string.Empty);
        sb.AppendLine();
        sb.AppendLine("环境信息：");
        var env = NormalizeEnv(p.EnvironmentJson);
        sb.AppendLine(string.IsNullOrWhiteSpace(env) ? "（空）" : env);
        sb.AppendLine();
        sb.AppendLine($"更新时间：{p.UpdatedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"更新人：{p.UpdatedByInstanceId}");
        sb.AppendLine($"创建时间：{p.CreatedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"创建人：{p.CreatedBy}");
        sb.AppendLine($"来源：{p.SourceKind}");
        if (p.IsDeleted)
        {
            sb.AppendLine("状态：已删除");
        }
        return sb.ToString().TrimEnd();
    }

    private static string FormatTag(Tag t)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"名称：{t.Name}");
        sb.AppendLine($"更新时间：{t.UpdatedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"更新人：{t.UpdatedByInstanceId}");
        sb.AppendLine($"创建时间：{t.CreatedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"状态：{(t.IsDeleted ? "已删除" : "正常")}");
        return sb.ToString().TrimEnd();
    }

    private static string FormatProblemTag(ProblemTag pt)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"问题ID：{pt.ProblemId}");
        sb.AppendLine($"标签ID：{pt.TagId}");
        sb.AppendLine($"更新时间：{pt.UpdatedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"更新人：{pt.UpdatedByInstanceId}");
        sb.AppendLine($"创建时间：{pt.CreatedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"状态：{(pt.IsDeleted ? "已删除" : "正常")}");
        return sb.ToString().TrimEnd();
    }

    private static string FormatAttachment(Attachment a)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"文件名：{a.OriginalFileName}");
        sb.AppendLine($"大小：{a.SizeBytes} bytes");
        sb.AppendLine($"类型：{a.MimeType}");
        sb.AppendLine($"内容哈希：{a.ContentHash}");
        sb.AppendLine($"所属问题ID：{a.ProblemId}");
        sb.AppendLine($"更新时间：{a.UpdatedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"更新人：{a.UpdatedByInstanceId}");
        sb.AppendLine($"创建时间：{a.CreatedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"状态：{(a.IsDeleted ? "已删除" : "正常")}");
        return sb.ToString().TrimEnd();
    }

    private static T? TryDeserialize<T>(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return default;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(json, ReadOptions);
        }
        catch
        {
            return default;
        }
    }

    private static string NormalizeEnv(string? envJson)
    {
        var lines = EnvironmentJson.ToPrettyText(envJson);
        return lines ?? string.Empty;
    }

    private static string PrettyJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return string.Empty;
        }

        try
        {
            var node = JsonNode.Parse(json);
            return node is null ? json : node.ToJsonString(PrettyOptions);
        }
        catch
        {
            return json;
        }
    }
}

