using System.Text.Json;
using System.Text.Json.Nodes;

namespace Shigure;

public sealed class ConfigService
{
    public const string ConfigDirectoryName = "config";
    public const string CommonConfigFileName = "common.json";
    public const string LegacyConfigFileName = "config.json";
    private static readonly string[] FixedStateNames = ["锚点", "职业", "专精"];

    public JsonObject Root { get; }

    public ConfigService(string configPath)
    {
        Root = LoadRoot(configPath);
    }

    public static ConfigService LoadFromBaseDirectory(string baseDirectory)
    {
        return new ConfigService(ResolveConfigPath(baseDirectory));
    }

    public static string ResolveConfigPath(string baseDirectory)
    {
        var splitConfigPath = Path.Combine(baseDirectory, ConfigDirectoryName);
        return Directory.Exists(splitConfigPath)
            ? splitConfigPath
            : Path.Combine(baseDirectory, LegacyConfigFileName);
    }

    private static JsonObject LoadRoot(string configPath)
    {
        if (Directory.Exists(configPath))
        {
            return LoadSplitConfig(configPath);
        }

        if (!File.Exists(configPath))
        {
            throw new FileNotFoundException("找不到 config 配置", configPath);
        }

        var json = File.ReadAllText(configPath);
        return ParseObject(json, configPath);
    }

    private static JsonObject LoadSplitConfig(string configDirectory)
    {
        var commonPath = Path.Combine(configDirectory, CommonConfigFileName);
        if (!File.Exists(commonPath))
        {
            throw new FileNotFoundException("找不到公共 config 配置", commonPath);
        }

        var root = ReadObject(commonPath);
        foreach (var (classId, _) in ClassNames.GetClasses())
        {
            var classPath = Path.Combine(configDirectory, $"{ClassNames.GetConfigFileName(classId)}.json");
            if (!File.Exists(classPath))
            {
                classPath = Path.Combine(configDirectory, $"{classId}.json");
            }

            if (!File.Exists(classPath))
            {
                throw new FileNotFoundException("找不到职业 config 配置", classPath);
            }

            root[classId.ToString()] = ReadObject(classPath);
        }

        return root;
    }

    private static JsonObject ReadObject(string path)
    {
        return ParseObject(File.ReadAllText(path), path);
    }

    private static JsonObject ParseObject(string json, string path)
    {
        return JsonNode.Parse(json, documentOptions: new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        })?.AsObject() ?? throw new InvalidDataException($"{path} 不是 JSON 对象。");
    }

    public JsonObject? GetObject(params string[] path)
    {
        JsonNode? node = Root;
        foreach (var part in path)
        {
            if (node is not JsonObject obj || !obj.TryGetPropertyValue(part, out node))
            {
                return null;
            }
        }

        return node as JsonObject;
    }

    public JsonObject BuildStateConfig(int? classId, int? specId)
    {
        var merged = new JsonObject();
        foreach (var (key, node) in Root)
        {
            if (node is JsonObject obj && obj.ContainsKey("step"))
            {
                merged[key] = obj.DeepClone();
            }
        }

        if (Root.TryGetPropertyValue("state", out var stateNode) && stateNode is JsonObject state)
        {
            CopyInto(merged, state);
        }

        if (classId is not null && specId is not null)
        {
            var spec = GetObject(classId.Value.ToString(), specId.Value.ToString());
            if (spec is not null)
            {
                CopyInto(merged, spec);
            }
        }

        // 固定启动字段始终以 common.json 为准，禁止职业配置覆盖。
        foreach (var name in FixedStateNames)
        {
            if (Root[name] is { } fixedField)
            {
                merged[name] = fixedField.DeepClone();
            }
        }

        return merged;
    }

    public string? GetKeymapName(int? classId)
    {
        if (classId is null)
        {
            return "keymap.json";
        }

        var classObj = GetObject(classId.Value.ToString());
        if (classObj is null || !classObj.TryGetPropertyValue("keymap", out var node))
        {
            return "keymap.json";
        }

        var value = JsonHelpers.GetString(node);
        return string.IsNullOrWhiteSpace(value) ? "keymap.json" : value;
    }

    public IReadOnlyDictionary<int, long> GetFailedSpells(int? classId)
        => GetClassSpellMap(classId, ModuleSpecialActions.OneKeySpell);

    public IReadOnlyDictionary<int, long> GetOneKeySpells(int? classId)
        => GetClassSpellMap(classId, ModuleSpecialActions.OneKeySpell);

    public IReadOnlyDictionary<int, long> GetInsertItems(int? classId)
        => GetClassSpellMap(classId, ModuleSpecialActions.OneKeyItem);

    private IReadOnlyDictionary<int, long> GetClassSpellMap(int? classId, string configKey)
    {
        if (classId is null
            || GetObject(classId.Value.ToString()) is not { } classObj
            || JsonHelpers.Get(classObj, configKey) is not JsonObject spellMap)
        {
            return new Dictionary<int, long>();
        }

        var result = new Dictionary<int, long>();
        foreach (var (idText, node) in spellMap)
        {
            var spellId = JsonHelpers.GetLong(node);
            if (int.TryParse(idText, out var id) && spellId is > 0)
            {
                result[id] = spellId.Value;
            }
        }

        return result;
    }

    private static void CopyInto(JsonObject target, JsonObject source)
    {
        foreach (var (key, value) in source)
        {
            target[key] = value?.DeepClone();
        }
    }
}
