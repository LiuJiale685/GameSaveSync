// GameSaveSync | By Jiale | Copyright (c) 2026 Jiale
using GameSaveSync.Core.Models;
using GameSaveSync.IntegrationTests.Infrastructure;
using Xunit;

namespace GameSaveSync.IntegrationTests;

/// <summary>
/// 共享游戏设置同步：进程名跨设备共享，且统一规范化为 name.exe 格式。
/// </summary>
public class SharedGameSettingsTests : MultiDeviceTestBase
{
    [Fact]
    public async Task 进程名规范化并同步到另一台设备()
    {
        var appA = await CreateDeviceAppAsync(DbPathA);
        var appB = await CreateDeviceAppAsync(DbPathB);

        var config = await appA.CreateNewGameAsync(
            "游戏Z", SaveDirA, new[] { "game1", "Game2.EXE", " game1.exe " });

        // 创建时即规范化：补全 .exe、去重、去空
        Assert.Equal(new[] { "game1.exe", "Game2.exe" }, config.ProcessNames);
        Assert.Equal(config.ProcessNames, await appA.GetSharedProcessNamesAsync(config.GameId));

        // 另一台设备绑定后自动继承共享进程名
        var bound = await appB.BindExistingGameAsync(config.GameId, "游戏Z", SaveDirB);
        Assert.Equal(new[] { "game1.exe", "Game2.exe" }, bound.ProcessNames);

        // A 修改进程名 → B 重新读取列表时拿到最新共享值
        var updated = new LocalGameConfig
        {
            GameId = config.GameId,
            Name = config.Name,
            SavePath = SaveDirA,
            ProcessNames = new() { "newgame" },
        };
        await appA.UpdateGameAsync(updated);

        var gamesOnB = await appB.ListGamesWithSavePathAsync();
        var gameOnB = Assert.Single(gamesOnB);
        Assert.Equal(new[] { "newgame.exe" }, gameOnB.ProcessNames);
        Assert.Equal(SaveDirB, gameOnB.SavePath);
    }
}
