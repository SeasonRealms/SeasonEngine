// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

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
}