using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GameSaveSync.Core.Services;

/// <summary>
/// 仓库元数据的确定性 JSON 序列化（方案 3.3 / 5.1）：
/// 固定属性顺序（通过 JsonPropertyOrder）、固定缩进、UTF-8 无 BOM、
/// Invariant Culture 数值与时间格式（时间为字符串字段，见 UtcClock）。
/// 保证内容相同的快照在所有设备上生成相同的 manifestHash。
/// </summary>
public static class RepoJson
{
    public static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        IndentSize = 2,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        NumberHandling = JsonNumberHandling.Strict,
    };

    private static readonly JsonSerializerOptions CompactOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        NumberHandling = JsonNumberHandling.Strict,
    };

    public static string Serialize<T>(T value) where T : class
        => JsonSerializer.Serialize(value, SerializerOptions);

    public static byte[] SerializeUtf8<T>(T value) where T : class
        => new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(Serialize(value));

    public static string SerializeCompact<T>(T value) where T : class
        => JsonSerializer.Serialize(value, CompactOptions);

    public static T? Deserialize<T>(string json) where T : class
        => JsonSerializer.Deserialize<T>(json, SerializerOptions);

    public static T DeserializeOrThrow<T>(string json, string context) where T : class
        => Deserialize<T>(json)
           ?? throw new GameSaveSync.Core.Models.RepoSchemaException($"仓库文件 {context} 内容无法解析为 {typeof(T).Name}。");
}
