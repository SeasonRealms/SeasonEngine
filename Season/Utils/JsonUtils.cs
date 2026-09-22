// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using System.Text.Json.Serialization;

namespace Season.Utils;

public static class JsonUtils
{

    public static JsonSerializerOptions JsonSerializerOptions = new JsonSerializerOptions()
    {
        WriteIndented = true,
        IgnoreReadOnlyFields = true,
        IgnoreReadOnlyProperties = true,
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)
    };

    public static string Serialize<T>(T t)
    {
        var json = JsonSerializer.Serialize<T>(t, JsonSerializerOptions);

        return json;
    }

    public static T Deserialize<T>(string json)
    {
        JsonSerializerOptions jsonSerializerOptions = null;

        return JsonSerializer.Deserialize<T>(json, jsonSerializerOptions);
    }

    /// <summary>
    /// 兼容 Newtonsoft.Json 默认序列化语义的选项集（本地存档等历史数据专用）。
    /// 相对默认选项的差异：包含 public 字段与只读成员、允许 NaN/Infinity 字面量、
    /// 容忍注释与尾逗号、属性名匹配不区分大小写，
    /// 并对 [DataContract] 类型仅保留 [DataMember] 成员（见 DataContractOptInResolver）。
    /// </summary>
    public static readonly JsonSerializerOptions NewtonsoftCompatJsonSerializerOptions = CreateNewtonsoftCompatJsonSerializerOptions(false);

    /// <summary>
    /// 同 NewtonsoftCompatJsonSerializerOptions，但输出带缩进（对应 Newtonsoft 的 Formatting.Indented）。
    /// </summary>
    public static readonly JsonSerializerOptions NewtonsoftCompatIndentedJsonSerializerOptions = CreateNewtonsoftCompatJsonSerializerOptions(true);

    private static JsonSerializerOptions CreateNewtonsoftCompatJsonSerializerOptions(bool indented)
    {
        return new JsonSerializerOptions()
        {
            WriteIndented = indented,
            IncludeFields = true,
            IgnoreReadOnlyFields = false,
            IgnoreReadOnlyProperties = false,
            NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
            AllowTrailingCommas = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            PropertyNameCaseInsensitive = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            TypeInfoResolver = new DataContractOptInResolver()
        };
    }

    /// <summary>
    /// 使用指定选项集序列化。
    /// </summary>
    public static string Serialize<T>(T t, JsonSerializerOptions options)
    {
        return JsonSerializer.Serialize<T>(t, options);
    }

    /// <summary>
    /// 使用指定选项集反序列化。
    /// </summary>
    public static T Deserialize<T>(string json, JsonSerializerOptions options)
    {
        return JsonSerializer.Deserialize<T>(json, options);
    }
}