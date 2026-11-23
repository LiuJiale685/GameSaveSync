using System.Security.Cryptography;
using System.Text;
using GameSaveSync.Core.Interfaces;
using GameSaveSync.Data;
using GameSaveSync.Sync;
using Xunit;

namespace GameSaveSync.IntegrationTests.Infrastructure;

/// <summary>
/// 游戏进程检查的测试假实现：始终报告"游戏未运行"。
/// </summary>
public sealed class NeverRunningProcessChecker : IGameProcessChecker
{
    public Task<bool> IsAnyRunningAsync(
        IReadOnlyList<string> processNames, CancellationToken cancellationToken = default)
        => Task.FromResult(false);
}

/// <summary>
/// 双设备集成测试基类：两个独立本地目录（设备 A/B 存档目录）+ 两个独立本地 SQLite
/// 数据库，共享同一个仓库目录，模拟两台设备通过网盘同步目录协作。
/// 每个测试方法使用独立的临时根目录，结束后清理。
/// </summary>
public abstract class MultiDeviceTestBase : IAsyncLifetime
{
    private readonly List<GameSaveSyncAppService> _apps = new();

    protected string Root { get; private set; } = string.Empty;
    protected string RepoDir => Path.Combine(Root, "repo");
    protected string SaveDirA => Path.Combine(Root, "deviceA", "saves", "TestGame");
    protected string SaveDirB => Path.Combine(Root, "deviceB", "saves", "TestGame");
    protected string DbPathA => Path.Combine(Root, "deviceA", "state.db");
    protected string DbPathB => Path.Combine(Root, "deviceB", "state.db");

    public Task InitializeAsync()
    {
        Root = Path.Combine(Path.GetTempPath(), "gss-integration-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(SaveDirA);
        Directory.CreateDirectory(SaveDirB);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        foreach (var app in _apps)
        {
            try { await app.DisposeAsync(); }
            catch { /* 清理阶段尽力而为 */ }
        }

        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (Directory.Exists(Root))
                    Directory.Delete(Root, recursive: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                await Task.Delay(200); // SQLite 文件锁释放可能需要短暂等待
            }
        }
    }

    /// <summary>创建一台"设备"：独立数据库 + 绑定共享仓库目录。</summary>
    protected async Task<GameSaveSyncAppService> CreateDeviceAppAsync(string dbPath)
    {
        var app = new GameSaveSyncAppService(new SqliteDatabase(dbPath), new NeverRunningProcessChecker());
        await app.InitializeAsync(RepoDir);
        _apps.Add(app);
        return app;
    }

    // ---------- 存档目录操作 ----------

    protected static async Task WriteSaveFileAsync(string saveDir, string relativePath, byte[] content)
    {
        var full = Path.Combine(saveDir, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        await File.WriteAllBytesAsync(full, content);
    }

    protected static Task WriteSaveFileAsync(string saveDir, string relativePath, string text)
        => WriteSaveFileAsync(saveDir, relativePath, Encoding.UTF8.GetBytes(text));

    protected static async Task<byte[]> ReadSaveFileAsync(string saveDir, string relativePath)
    {
        var full = Path.Combine(saveDir, relativePath.Replace('/', Path.DirectorySeparatorChar));
        return await File.ReadAllBytesAsync(full);
    }

    protected static string Sha256Hex(byte[] content)
        => Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

    /// <summary>列出目录全部文件的规范化相对路径（'/' 分隔）。</summary>
    protected static IReadOnlyList<string> ListRelativeFiles(string dir)
    {
        if (!Directory.Exists(dir))
            return Array.Empty<string>();
        return Directory
            .EnumerateFiles(dir, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(dir, f).Replace(Path.DirectorySeparatorChar, '/'))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>断言两个目录内容逐文件一致（字节 + SHA256）。</summary>
    protected static void AssertDirectoriesEqual(string expectedDir, string actualDir, string context)
    {
        var expectedFiles = ListRelativeFiles(expectedDir);
        var actualFiles = ListRelativeFiles(actualDir);
        Assert.True(
            expectedFiles.SequenceEqual(actualFiles, StringComparer.Ordinal),
            $"{context}: 文件列表不一致。期望 [{string.Join(", ", expectedFiles)}]，实际 [{string.Join(", ", actualFiles)}]");

        foreach (var relative in expectedFiles)
        {
            var expected = ReadSaveFileAsync(expectedDir, relative).GetAwaiter().GetResult();
            var actual = ReadSaveFileAsync(actualDir, relative).GetAwaiter().GetResult();
            Assert.True(expected.SequenceEqual(actual),
                $"{context}: 文件 {relative} 字节内容不一致。");
            Assert.Equal(Sha256Hex(expected), Sha256Hex(actual));
        }
    }

    /// <summary>统计仓库 objects 目录下的物理对象文件数。</summary>
    protected static int CountPhysicalRepoObjects(string repoDir)
    {
        var objectsDir = Path.Combine(repoDir, "objects", "sha256");
        return Directory.Exists(objectsDir)
            ? Directory.EnumerateFiles(objectsDir, "*", SearchOption.AllDirectories).Count()
            : 0;
    }

    /// <summary>按 sha256 定位仓库中 object 的物理路径。</summary>
    protected static string PhysicalObjectPath(string repoDir, string sha256)
        => Path.Combine(repoDir, GameSaveSync.Core.Services.RepoPaths.ObjectKey(sha256)
            .Replace('/', Path.DirectorySeparatorChar));
}
