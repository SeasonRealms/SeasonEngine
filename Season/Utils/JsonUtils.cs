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
    /// Compatible with Newtonsoft. Johnson's default serialization semantics option set (specific to historical data such as local archives).
    /// Differences from default options: including public fields and read-only members, allowing NaN/Infinity literals
    /// Tolerant comments that match with trailing commas and attribute names are not case sensitive,
    /// And only retain the [DataMember] member for the [DataContract] type (see DataMractOptAInResolver).
    /// </summary>
    public static readonly JsonSerializerOptions NewtonsoftCompatJsonSerializerOptions = CreateNewtonsoftCompatJsonSerializerOptions(false);

    /// <summary>
    /// Compatible with NewtonsoftCompatJsonSerializerOptions, but outputs with indentation (corresponding to Newtonsoft's Formatting.Indented).
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
    /// Use the specified option set for serialization.
    /// </summary>
    public static string Serialize<T>(T t, JsonSerializerOptions options)
    {
        return JsonSerializer.Serialize<T>(t, options);
    }

    /// <summary>
    /// Use the specified option set for deserialization.
    /// </summary>
    public static T Deserialize<T>(string json, JsonSerializerOptions options)
    {
        return JsonSerializer.Deserialize<T>(json, options);
    }
}