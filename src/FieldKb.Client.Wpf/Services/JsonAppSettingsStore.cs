using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace FieldKb.Client.Wpf;

public sealed class JsonAppSettingsStore : IAppSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly string _path;

    public JsonAppSettingsStore(string path)
    {
        _path = path;
    }

    public async Task<string?> ReadUserNameAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path))
        {
            return null;
        }

        await using var stream = File.OpenRead(_path);
        var root = await JsonNode.ParseAsync(stream, cancellationToken: cancellationToken);
        return root?["User"]?["Name"]?.GetValue<string>();
    }

    public async Task<string?> ReadProfessionIdAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path))
        {
            return null;
        }

        await using var stream = File.OpenRead(_path);
        var root = await JsonNode.ParseAsync(stream, cancellationToken: cancellationToken);
        return root?["User"]?["Profession"]?.GetValue<string>();
    }

    public async Task WriteUserNameAsync(string userName, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);

        JsonNode root;
        if (File.Exists(_path))
        {
            await using var readStream = File.OpenRead(_path);
            root = (await JsonNode.ParseAsync(readStream, cancellationToken: cancellationToken)) ?? new JsonObject();
        }
        else
        {
            root = new JsonObject();
        }

        if (root is not JsonObject obj)
        {
            obj = new JsonObject();
            root = obj;
        }

        if (obj["User"] is not JsonObject userObj)
        {
            userObj = new JsonObject();
            obj["User"] = userObj;
        }

        userObj["Name"] = UserNameRules.Normalize(userName);

        var json = root.ToJsonString(JsonOptions);
        await File.WriteAllTextAsync(_path, json, cancellationToken);
    }

    public async Task WriteProfessionIdAsync(string professionId, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);

        JsonNode root;
        if (File.Exists(_path))
        {
            await using var readStream = File.OpenRead(_path);
            root = (await JsonNode.ParseAsync(readStream, cancellationToken: cancellationToken)) ?? new JsonObject();
        }
        else
        {
            root = new JsonObject();
        }

        if (root is not JsonObject obj)
        {
            obj = new JsonObject();
            root = obj;
        }

        if (obj["User"] is not JsonObject userObj)
        {
            userObj = new JsonObject();
            obj["User"] = userObj;
        }

        userObj["Profession"] = ProfessionIds.Normalize(professionId);

        var json = root.ToJsonString(JsonOptions);
        await File.WriteAllTextAsync(_path, json, cancellationToken);
    }

    public async Task<IReadOnlyDictionary<string, IReadOnlyList<ProfessionFixedFieldSetting>>> ReadProfessionFixedFieldsAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path))
        {
            return new Dictionary<string, IReadOnlyList<ProfessionFixedFieldSetting>>(StringComparer.OrdinalIgnoreCase);
        }

        await using var stream = File.OpenRead(_path);
        var root = await JsonNode.ParseAsync(stream, cancellationToken: cancellationToken);
        var settingsObj = root?["ProfessionSettings"] as JsonObject;
        if (settingsObj is null)
        {
            return new Dictionary<string, IReadOnlyList<ProfessionFixedFieldSetting>>(StringComparer.OrdinalIgnoreCase);
        }

        var map = new Dictionary<string, IReadOnlyList<ProfessionFixedFieldSetting>>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in settingsObj)
        {
            var professionId = kv.Key;
            if (string.IsNullOrWhiteSpace(professionId))
            {
                continue;
            }

            if (kv.Value is not JsonObject profObj)
            {
                continue;
            }

            if (profObj["FixedFields"] is not JsonArray arr)
            {
                continue;
            }

            var list = new List<ProfessionFixedFieldSetting>();
            foreach (var node in arr)
            {
                if (node is not JsonObject item)
                {
                    continue;
                }

                var key = (item["Key"]?.GetValue<string>() ?? string.Empty).Trim();
                var label = (item["Label"]?.GetValue<string>() ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(label))
                {
                    continue;
                }

                list.Add(new ProfessionFixedFieldSetting(key, label));
            }

            if (list.Count > 0)
            {
                map[ProfessionIds.Normalize(professionId)] = list;
            }
        }

        return map;
    }

    public async Task WriteProfessionFixedFieldsAsync(string professionId, IReadOnlyList<ProfessionFixedFieldSetting> fixedFields, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);

        JsonNode root;
        if (File.Exists(_path))
        {
            await using var readStream = File.OpenRead(_path);
            root = (await JsonNode.ParseAsync(readStream, cancellationToken: cancellationToken)) ?? new JsonObject();
        }
        else
        {
            root = new JsonObject();
        }

        if (root is not JsonObject obj)
        {
            obj = new JsonObject();
            root = obj;
        }

        if (obj["ProfessionSettings"] is not JsonObject settingsObj)
        {
            settingsObj = new JsonObject();
            obj["ProfessionSettings"] = settingsObj;
        }

        var pid = ProfessionIds.Normalize(professionId);
        if (settingsObj[pid] is not JsonObject profObj)
        {
            profObj = new JsonObject();
            settingsObj[pid] = profObj;
        }

        var arr = new JsonArray();
        foreach (var f in fixedFields ?? Array.Empty<ProfessionFixedFieldSetting>())
        {
            var key = (f.Key ?? string.Empty).Trim();
            var label = (f.Label ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(label))
            {
                continue;
            }

            arr.Add(new JsonObject
            {
                ["Key"] = key,
                ["Label"] = label
            });
        }

        profObj["FixedFields"] = arr;

        var json = root.ToJsonString(JsonOptions);
        await File.WriteAllTextAsync(_path, json, cancellationToken);
    }

    public async Task<OperationPasswordConfig?> ReadOperationPasswordAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path))
        {
            return null;
        }

        await using var stream = File.OpenRead(_path);
        var root = await JsonNode.ParseAsync(stream, cancellationToken: cancellationToken);
        var obj = root?["Security"]?["OperationPassword"] as JsonObject;
        if (obj is null)
        {
            return null;
        }

        var salt = obj["SaltBase64"]?.GetValue<string>();
        var hash = obj["HashBase64"]?.GetValue<string>();
        var iter = obj["Iterations"]?.GetValue<int>() ?? 0;
        if (string.IsNullOrWhiteSpace(salt) || string.IsNullOrWhiteSpace(hash) || iter <= 0)
        {
            return null;
        }

        return new OperationPasswordConfig(salt, hash, iter);
    }

    public async Task WriteOperationPasswordAsync(OperationPasswordConfig? passwordConfig, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);

        JsonNode root;
        if (File.Exists(_path))
        {
            await using var readStream = File.OpenRead(_path);
            root = (await JsonNode.ParseAsync(readStream, cancellationToken: cancellationToken)) ?? new JsonObject();
        }
        else
        {
            root = new JsonObject();
        }

        if (root is not JsonObject obj)
        {
            obj = new JsonObject();
            root = obj;
        }

        if (obj["Security"] is not JsonObject securityObj)
        {
            securityObj = new JsonObject();
            obj["Security"] = securityObj;
        }

        if (passwordConfig is null)
        {
            securityObj.Remove("OperationPassword");
        }
        else
        {
            securityObj["OperationPassword"] = new JsonObject
            {
                ["SaltBase64"] = passwordConfig.SaltBase64,
                ["HashBase64"] = passwordConfig.HashBase64,
                ["Iterations"] = passwordConfig.Iterations
            };
        }

        var json = root.ToJsonString(JsonOptions);
        await File.WriteAllTextAsync(_path, json, cancellationToken);
    }

    public async Task<string?> ReadLanExchangeSharedKeyAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path))
        {
            return null;
        }

        await using var stream = File.OpenRead(_path);
        var root = await JsonNode.ParseAsync(stream, cancellationToken: cancellationToken);
        var key = root?["LanExchange"]?["SharedKey"]?.GetValue<string>();
        return string.IsNullOrWhiteSpace(key) ? null : key;
    }

    public async Task WriteLanExchangeSharedKeyAsync(string? sharedKey, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);

        JsonNode root;
        if (File.Exists(_path))
        {
            await using var readStream = File.OpenRead(_path);
            root = (await JsonNode.ParseAsync(readStream, cancellationToken: cancellationToken)) ?? new JsonObject();
        }
        else
        {
            root = new JsonObject();
        }

        if (root is not JsonObject obj)
        {
            obj = new JsonObject();
            root = obj;
        }

        if (obj["LanExchange"] is not JsonObject lanObj)
        {
            lanObj = new JsonObject();
            obj["LanExchange"] = lanObj;
        }

        var normalized = (sharedKey ?? string.Empty).Trim();
        lanObj["SharedKey"] = normalized;

        var json = root.ToJsonString(JsonOptions);
        await File.WriteAllTextAsync(_path, json, cancellationToken);
    }
}
