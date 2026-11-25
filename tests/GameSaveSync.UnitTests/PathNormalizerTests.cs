// GameSaveSync | By Jiale | Copyright (c) 2026 Jiale
using GameSaveSync.Core.Services;
using Xunit;

namespace GameSaveSync.UnitTests;

public class PathNormalizerTests
{
    [Theory]
    [InlineData("C:/x")]                 // 盘符（正斜杠）
    [InlineData("C:\\x")]                // 盘符（反斜杠）
    [InlineData("c:/x")]                 // 盘符小写
    [InlineData("/etc/passwd")]          // 绝对路径
    [InlineData("\\\\server\\share\\f")] // UNC 路径
    [InlineData("..")]                   // 上级目录
    [InlineData("../x")]                  // 穿越
    [InlineData("..\\x")]                // 穿越（反斜杠）
    [InlineData("a/../b")]               // 中间段穿越
    [InlineData("a\\..\\b")]
    [InlineData("saves/../../x")]
    [InlineData("")]                      // 空
    [InlineData("   ")]                   // 空白
    [InlineData("./")]                    // 规范化后为空
    [InlineData(".")]
    public void Normalize_非法路径_抛出ArgumentException(string path)
    {
        Assert.Throws<ArgumentException>(() => PathNormalizer.Normalize(path));
        Assert.Null(PathNormalizer.TryNormalize(path));
    }

    [Theory]
    [InlineData("saves\\slot1.sav", "saves/slot1.sav")]      // 反斜杠转 '/'
    [InlineData("a\\b\\c.bin", "a/b/c.bin")]
    [InlineData("plain.sav", "plain.sav")]
    [InlineData("Saves/FILE.SAV", "Saves/FILE.SAV")]          // 大小写保留
    [InlineData("a/./b.sav", "a/b.sav")]                       // '.' 段被跳过
    [InlineData(" saves/slot1.sav ", "saves/slot1.sav")]       // 首尾空白被去除
    public void Normalize_合法路径_规范化成功(string input, string expected)
    {
        Assert.Equal(expected, PathNormalizer.Normalize(input));
        Assert.Equal(expected, PathNormalizer.TryNormalize(input));
    }

    [Fact]
    public void Normalize_windows保留名带非法字符_抛出()
    {
        // 非法文件名字符（如 '<'、'|'、'?'）必须被拒绝。
        Assert.Throws<ArgumentException>(() => PathNormalizer.Normalize("a<b.sav"));
        Assert.Throws<ArgumentException>(() => PathNormalizer.Normalize("x|y.bin"));
        Assert.Null(PathNormalizer.TryNormalize("x?.dat"));
    }

    [Fact]
    public void PathsEqual_大小写不敏感比较()
    {
        Assert.True(PathNormalizer.PathsEqual("Saves/Slot1.SAV", "saves/slot1.sav"));
        Assert.False(PathNormalizer.PathsEqual("saves/a.sav", "saves/b.sav"));
    }

    [Fact]
    public void IsUnderRoot_子路径为真_越界路径为假()
    {
        var root = Path.Combine(Path.GetTempPath(), "gss-pn-test-root");
        Directory.CreateDirectory(root);
        try
        {
            var inside = Path.Combine(root, "sub", "file.dat");
            var outside = Path.Combine(root, "..", "escape.dat");

            Assert.True(PathNormalizer.IsUnderRoot(inside, root));
            Assert.False(PathNormalizer.IsUnderRoot(Path.GetFullPath(outside), root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
