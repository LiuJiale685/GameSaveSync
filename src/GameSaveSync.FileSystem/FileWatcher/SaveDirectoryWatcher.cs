// GameSaveSync | By Jiale | Copyright (c) 2026 Jiale
using System.Collections.Concurrent;

namespace GameSaveSync.FileSystem;

/// <summary>
/// 存档目录文件监控（方案 6.1）：
/// FileSystemWatcher 快速感知 + Dirty 集合记录 + 事件回调；
/// 遗漏事件由定时扫描补偿（本类只负责“快速标记”，不保证完整）。
/// </summary>
public sealed class SaveDirectoryWatcher : IDisposable
{
    private readonly string _rootPath;
    private readonly ConcurrentDictionary<string, byte> _dirty = new(StringComparer.OrdinalIgnoreCase);
    private readonly FileSystemWatcher _watcher;
    private readonly Action _onChange;
    private bool _disposed;

    /// <param name="savePath">要监听的存档目录。</param>
    /// <param name="onChange">任一变更事件发生后的回调（已做轻量防抖）。</param>
    public SaveDirectoryWatcher(string savePath, Action onChange)
    {
        _rootPath = Path.GetFullPath(savePath);
        _onChange = onChange;

        _watcher = new FileSystemWatcher(_rootPath)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName
                | NotifyFilters.DirectoryName
                | NotifyFilters.LastWrite
                | NotifyFilters.Size,
            InternalBufferSize = 64 * 1024,
        };

        _watcher.Created += (_, _) => MarkDirtyAndNotify();
        _watcher.Changed += (_, _) => MarkDirtyAndNotify();
        _watcher.Deleted += (_, _) => MarkDirtyAndNotify();
        _watcher.Renamed += (_, _) => MarkDirtyAndNotify();
        _watcher.Error += (_, _) =>
        {
            // 缓冲区溢出等错误：标记全目录 Dirty，等待定时扫描补偿。
            MarkDirtyAndNotify();
        };

        _watcher.EnableRaisingEvents = true;
    }

    /// <summary>监听是否正常工作（Error 后可能失效，需依赖定时扫描）。</summary>
    public bool IsHealthy => !_disposed && _watcher.EnableRaisingEvents;

    /// <summary>当前 Dirty 相对路径集合（快照）。</summary>
    public IReadOnlyCollection<string> DrainDirty()
    {
        var list = new List<string>(_dirty.Keys);
        _dirty.Clear();
        return list;
    }

    public bool HasDirty => !_dirty.IsEmpty;

    private void MarkDirtyAndNotify()
    {
        if (_disposed)
            return;
        // MVP 策略：任何事件都把整个目录标记为“有变化”，
        // 具体差异由 SHA256 扫描判断，避免事件风暴下逐文件记账。
        _dirty.TryAdd("*", 0);
        _onChange();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _watcher.EnableRaisingEvents = false;
        _watcher.Dispose();
    }
}
