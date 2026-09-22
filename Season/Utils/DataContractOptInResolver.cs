// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

using System.Runtime.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Season.Utils;

/// <summary>
/// 复制 Newtonsoft.Json 对 [DataContract] 类型的成员选择语义：
/// 类型自身或任一基类声明了 [DataContract] 时，仅序列化/反序列化带 [DataMember] 的成员；
/// 其余成员（public 字段、只读属性等）默认契约的选择由调用方的选项集决定。
/// 供兼容 Newtonsoft.Json 生成的历史存档数据使用。
/// </summary>
public sealed class DataContractOptInResolver : IJsonTypeInfoResolver
{
    private readonly DefaultJsonTypeInfoResolver _inner = new DefaultJsonTypeInfoResolver();

    /// <inheritdoc/>
    public JsonTypeInfo? GetTypeInfo(Type type, JsonSerializerOptions options)
    {
        JsonTypeInfo? typeInfo = _inner.GetTypeInfo(type, options);
        if (typeInfo == null)
        {
            return null;
        }

        if (HasDataContract(type))
        {
            IList<JsonPropertyInfo> properties = typeInfo.Properties;
            for (int i = properties.Count - 1; i >= 0; i--)
            {
                var provider = properties[i].AttributeProvider;
                if (provider != null && !provider.IsDefined(typeof(DataMemberAttribute), inherit: false))
                {
                    properties.RemoveAt(i);
                }
            }
        }

        return typeInfo;
    }

    /// <summary>
    /// Newtonsoft.Json 判断 [DataContract] 时沿 BaseType 链向上查找
    /// （DataContractAttribute 的 Inherited 为 false，不能依赖 IsDefined(inherit: true)）。
    /// </summary>
    private static bool HasDataContract(Type type)
    {
        for (Type? current = type; current != null && current != typeof(object); current = current.BaseType)
        {
            if (current.IsDefined(typeof(DataContractAttribute), inherit: false))
            {
                return true;
            }
        }
        return false;
    }
}
