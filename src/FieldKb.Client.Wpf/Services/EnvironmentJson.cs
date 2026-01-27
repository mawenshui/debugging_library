using System.Text.Json;
using System.Text.Json.Nodes;

namespace FieldKb.Client.Wpf;

public static class EnvironmentJson
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    public static class KnownKeys
    {
        public const string ProfessionId = "__professionid";
        public const string DeviceModel = "deviceModel";
        public const string DeviceVersion = "deviceVersion";
        public const string Workstation = "workstation";
        public const string Customer = "customer";
        public const string IpAddress = "ip";
        public const string Port = "port";
    }

    public static readonly IReadOnlyList<string> CommonKeys = new[]
    {
        "站点",
        "项目",
        "产线",
        "设备编号",
        "PLC 型号",
        "PLC 固件",
        "HMI 型号",
        "HMI 版本",
        "操作系统",
        "应用版本",
        "网络",
        "备注"
    };

    public static string FromEntries(IEnumerable<EnvironmentEntry> entries)
    {
        var obj = new JsonObject();

        foreach (var entry in entries)
        {
            var key = (entry.Key ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            if (IsMetaKey(key))
            {
                continue;
            }

            obj[key] = (entry.Value ?? string.Empty).Trim();
        }

        return obj.ToJsonString(JsonOptions);
    }

    public static string FromStructuredAndCustom(StructuredEnvironment structured, IEnumerable<EnvironmentEntry> custom)
    {
        var obj = new JsonObject();

        void SetIfNotEmpty(string key, string? value)
        {
            var v = (value ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(v))
            {
                return;
            }

            obj[key] = v;
        }

        SetIfNotEmpty(KnownKeys.DeviceModel, structured.DeviceModel);
        SetIfNotEmpty(KnownKeys.DeviceVersion, structured.DeviceVersion);
        SetIfNotEmpty(KnownKeys.Workstation, structured.Workstation);
        SetIfNotEmpty(KnownKeys.Customer, structured.Customer);
        SetIfNotEmpty(KnownKeys.IpAddress, structured.IpAddress);
        SetIfNotEmpty(KnownKeys.Port, structured.Port);

        foreach (var entry in custom)
        {
            var key = (entry.Key ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            if (IsReservedKey(key))
            {
                continue;
            }

            obj[key] = (entry.Value ?? string.Empty).Trim();
        }

        return obj.ToJsonString(JsonOptions);
    }

    public static IReadOnlyList<EnvironmentEntry> TryParseToEntries(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Array.Empty<EnvironmentEntry>();
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return Array.Empty<EnvironmentEntry>();
            }

            var list = new List<EnvironmentEntry>();
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                var value = prop.Value.ValueKind switch
                {
                    JsonValueKind.String => prop.Value.GetString() ?? string.Empty,
                    JsonValueKind.Number => prop.Value.GetRawText(),
                    JsonValueKind.True => "true",
                    JsonValueKind.False => "false",
                    JsonValueKind.Null => string.Empty,
                    _ => prop.Value.GetRawText()
                };

                list.Add(new EnvironmentEntry(prop.Name, value));
            }

            return list;
        }
        catch
        {
            return Array.Empty<EnvironmentEntry>();
        }
    }

    public static StructuredEnvironment TryParseStructured(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new StructuredEnvironment();
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return new StructuredEnvironment();
            }

            string? ReadString(string key)
            {
                if (!doc.RootElement.TryGetProperty(key, out var el))
                {
                    return null;
                }

                return el.ValueKind switch
                {
                    JsonValueKind.String => el.GetString(),
                    JsonValueKind.Number => el.GetRawText(),
                    JsonValueKind.True => "true",
                    JsonValueKind.False => "false",
                    _ => el.GetRawText()
                };
            }

            return new StructuredEnvironment
            {
                DeviceModel = ReadString(KnownKeys.DeviceModel),
                DeviceVersion = ReadString(KnownKeys.DeviceVersion),
                Workstation = ReadString(KnownKeys.Workstation),
                Customer = ReadString(KnownKeys.Customer),
                IpAddress = ReadString(KnownKeys.IpAddress),
                Port = ReadString(KnownKeys.Port)
            };
        }
        catch
        {
            return new StructuredEnvironment();
        }
    }

    public static IReadOnlyList<EnvironmentEntry> TryParseCustom(string? json)
    {
        var entries = TryParseToEntries(json);
        if (entries.Count == 0)
        {
            return entries;
        }

        return entries.Where(e => !IsReservedKey(e.Key)).ToArray();
    }

    public static bool IsReservedKey(string? key)
    {
        return string.Equals(key, KnownKeys.DeviceModel, StringComparison.OrdinalIgnoreCase)
            || string.Equals(key, KnownKeys.DeviceVersion, StringComparison.OrdinalIgnoreCase)
            || string.Equals(key, KnownKeys.Workstation, StringComparison.OrdinalIgnoreCase)
            || string.Equals(key, KnownKeys.Customer, StringComparison.OrdinalIgnoreCase)
            || string.Equals(key, KnownKeys.IpAddress, StringComparison.OrdinalIgnoreCase)
            || string.Equals(key, KnownKeys.Port, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsMetaKey(string? key)
    {
        return string.Equals(key, KnownKeys.ProfessionId, StringComparison.OrdinalIgnoreCase);
    }

    public static string SetOrReplaceMeta(string? json, string metaKey, string metaValue)
    {
        var key = (metaKey ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(key))
        {
            return json ?? "{}";
        }

        try
        {
            JsonNode root;
            if (string.IsNullOrWhiteSpace(json))
            {
                root = new JsonObject();
            }
            else
            {
                root = JsonNode.Parse(json) ?? new JsonObject();
            }

            if (root is not JsonObject obj)
            {
                obj = new JsonObject();
                root = obj;
            }

            obj[key] = (metaValue ?? string.Empty).Trim();
            return root.ToJsonString(JsonOptions);
        }
        catch
        {
            return "{}";
        }
    }

    public static string? TryGetValue(string? json, string key)
    {
        if (string.IsNullOrWhiteSpace(json) || string.IsNullOrWhiteSpace(key))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (!doc.RootElement.TryGetProperty(key, out var el))
            {
                return null;
            }

            return el.ValueKind switch
            {
                JsonValueKind.String => el.GetString(),
                JsonValueKind.Number => el.GetRawText(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                JsonValueKind.Null => string.Empty,
                _ => el.GetRawText()
            };
        }
        catch
        {
            return null;
        }
    }

    public static string ToPrettyText(string? json)
    {
        var entries = TryParseToEntries(json);
        if (entries.Count == 0)
        {
            return string.Empty;
        }

        return string.Join(Environment.NewLine, entries.Where(e => !IsMetaKey(e.Key)).Select(e => $"{e.Key}: {e.Value}"));
    }
}
