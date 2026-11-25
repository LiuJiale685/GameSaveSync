// GameSaveSync | By Jiale | Copyright (c) 2026 Jiale
using System.Text;
using GameSaveSync.Core.Models;
using GameSaveSync.Core.Services;
using Xunit;

namespace GameSaveSync.UnitTests;

public class RepoJsonTests
{
    private static Manifest CreateSampleManifest()
    {
        return new Manifest
        {
            GameId = "0b6f3e2a-1111-4c2e-9f01-000000000001",
            Files =
            [
                new ManifestFileEntry
                {
                    Path = "saves/config.cfg",
                    Sha256 = new string('a', 64),
                    Size = 1234,
                    LastWriteUtc = "2026-01-02T03:04:05Z",
                },
                new ManifestFileEntry
                {
                    Path = "saves/slot1.sav",
                    Sha256 = new string('b', 64),
                    Size = 56,
                    LastWriteUtc = null,
                },
            ],
        };
    }

    [Fact]
    public void SerializeUtf8_两次序列化相等内容_字节完全一致()
    {
        // 确定性：内容相同的 Manifest（不同对象实例）必须生成相同字节。
        var bytes1 = RepoJson.SerializeUtf8(CreateSampleManifest());
        var bytes2 = RepoJson.SerializeUtf8(CreateSampleManifest());

        Assert.Equal(bytes1, bytes2);
    }

    [Fact]
    public void SerializeUtf8_无BOM_且以大括号开头()
    {
        var bytes = RepoJson.SerializeUtf8(CreateSampleManifest());

        // UTF-8 BOM 为 EF BB BF，这里必须不存在。
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF,
            "序列化输出不应包含 UTF-8 BOM。");
        Assert.Equal((byte)'{', bytes[0]);
    }

    [Fact]
    public void Serialize_使用camelCase与2空格缩进()
    {
        var json = RepoJson.Serialize(CreateSampleManifest());
        // 换行符与平台相关，统一后断言缩进结构。
        var normalized = json.Replace("\r\n", "\n");

        Assert.Contains("\"schemaVersion\": 1", normalized);
        Assert.Contains("\"gameId\":", normalized);
        Assert.Contains("\"sha256\":", normalized);
        Assert.Contains("\"lastWriteUtc\":", normalized);
        Assert.Contains("\n  \"files\": [", normalized); // 2 空格缩进
        Assert.Contains("\n      \"path\":", normalized); // 数组元素对象的属性为 6 空格
    }

    [Fact]
    public void Deserialize_Roundtrip_保留全部条目()
    {
        var manifest = CreateSampleManifest();
        var json = RepoJson.Serialize(manifest);

        var restored = RepoJson.Deserialize<Manifest>(json);

        Assert.NotNull(restored);
        Assert.Equal(manifest.GameId, restored!.GameId);
        Assert.Equal(RepoProtocol.SchemaVersion, restored.SchemaVersion);
        Assert.Equal(manifest.Files.Count, restored.Files.Count);
        for (var i = 0; i < manifest.Files.Count; i++)
        {
            Assert.Equal(manifest.Files[i].Path, restored.Files[i].Path);
            Assert.Equal(manifest.Files[i].Sha256, restored.Files[i].Sha256);
            Assert.Equal(manifest.Files[i].Size, restored.Files[i].Size);
            Assert.Equal(manifest.Files[i].LastWriteUtc, restored.Files[i].LastWriteUtc);
        }
    }

    [Fact]
    public void Deserialize_Roundtrip_CommitInfo全部字段()
    {
        var commit = new CommitInfo
        {
            CommitId = "0b6f3e2a-2222-4c2e-9f01-000000000002",
            GameId = "0b6f3e2a-1111-4c2e-9f01-000000000001",
            DeviceId = "0b6f3e2a-3333-4c2e-9f01-000000000003",
            ParentIds = ["p1", "p2"],
            ManifestHash = new string('c', 64),
            CreatedAtUtc = "2026-01-02T03:04:05Z",
            Message = "手动备份",
        };

        var restored = RepoJson.Deserialize<CommitInfo>(RepoJson.Serialize(commit));

        Assert.NotNull(restored);
        Assert.Equal(commit.CommitId, restored!.CommitId);
        Assert.Equal(commit.DeviceId, restored.DeviceId);
        Assert.Equal(commit.ParentIds, restored.ParentIds);
        Assert.Equal(commit.ManifestHash, restored.ManifestHash);
        Assert.Equal(commit.CreatedAtUtc, restored.CreatedAtUtc);
        Assert.Equal(commit.Message, restored.Message);
    }

    [Fact]
    public void SerializeUtf8_与字符串序列化UTF8解码结果一致()
    {
        var manifest = CreateSampleManifest();

        var bytes = RepoJson.SerializeUtf8(manifest);
        var text = RepoJson.Serialize(manifest);

        Assert.Equal(text, Encoding.UTF8.GetString(bytes));
    }
}
