namespace GameSaveSync.Storage;

/// <summary>
/// 存储适配层接口（方案 9.1）。key 为仓库逻辑路径（见 RepoPaths），
/// 由具体适配器（如 FolderRepositoryStore，对应百度网盘双向同步目录）映射为物理存储。
/// </summary>
public interface IRepositoryStore
{
    Task<bool> ExistsAsync(string key, CancellationToken cancellationToken);

    Task<Stream> OpenReadAsync(string key, CancellationToken cancellationToken);

    /// <summary>
    /// 以不可变方式写入内容。若目标 key 已存在，保持已存在内容不被覆盖（create-if-absent）。
    /// </summary>
    Task PutImmutableAsync(string key, Stream content, string sha256, CancellationToken cancellationToken);

    /// <summary>列举指定前缀下的所有 key。适配器应忽略非规范命名的文件（网盘冲突副本容忍）。</summary>
    Task<IReadOnlyList<string>> ListAsync(string prefix, CancellationToken cancellationToken);

    /// <summary>
    /// 读取对象大小（若存在）。用于快速校验，避免完整哈希。
    /// </summary>
    Task<long?> GetSizeAsync(string key, CancellationToken cancellationToken);

    /// <summary>
    /// 删除指定 key（仅用于本地临时/清理场景；云端对象 V1 不做垃圾回收）。
    /// 不存在时返回 false。
    /// </summary>
    Task<bool> TryDeleteAsync(string key, CancellationToken cancellationToken);
}
