// GameSaveSync | By Jiale | Copyright (c) 2026 Jiale
using GameSaveSync.Core.Services;
using Xunit;

namespace GameSaveSync.UnitTests;

public class ProcessNameNormalizerTests
{
    [Theory]
    [InlineData("game", "game.exe")]
    [InlineData("game.exe", "game.exe")]
    [InlineData("Game.EXE", "Game.exe")]
    [InlineData("  game.exe  ", "game.exe")]
    [InlineData("game.exe.exe", "game.exe")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    [InlineData(".exe", "")]
    public void Normalize_统一补全exe后缀(string input, string expected)
    {
        Assert.Equal(expected, ProcessNameNormalizer.Normalize(input));
    }

    [Fact]
    public void NormalizeAll_去空去重且忽略大小写()
    {
        var result = ProcessNameNormalizer.NormalizeAll(new[] { "game", "Game.exe", "", "launcher", " game " });

        Assert.Equal(new[] { "game.exe", "launcher.exe" }, result);
    }

    [Theory]
    [InlineData("game.exe", "game")]
    [InlineData("game", "game")]
    [InlineData("Game.EXE", "Game")]
    public void ToProcessName_去掉exe后缀(string input, string expected)
    {
        Assert.Equal(expected, ProcessNameNormalizer.ToProcessName(input));
    }
}
