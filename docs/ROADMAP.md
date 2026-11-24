# 开发进展与后续工作

对应技术方案 §11.1 的优先级划分（P0 → P1 → P2）。更新日期：2026-11-24。

## 一、当前进展：P0 全部完成 ✅

全解决方案 `dotnet build` 0 警告 0 错误；`dotnet test` **70/70 通过**（60 单元 + 10 集成）。

### 阶段一：本地版本管理（已完成）

| 能力 | 实现 |
|---|---|
| 仓库结构（repo.json / games / manifests / commits / objects） | [RepoPaths](../src/GameSaveSync.Core/Services/RepoPaths.cs) · [RepoModels](../src/GameSaveSync.Core/Models/RepoModels.cs) |
| 确定性 JSON 序列化（跨设备一致 manifestHash） | [RepoJson](../src/GameSaveSync.Core/Services/RepoJson.cs) |
| 内容寻址对象存储 + 文件级去重 | [ObjectStore](../src/GameSaveSync.Snapshot/ObjectStore/ObjectStore.cs) |
| 快照构建（稳定性检查 + 有限重试） | [SnapshotBuilder](../src/GameSaveSync.Snapshot/SnapshotBuilder/SnapshotBuilder.cs) |
| 不可变发布（临时文件 + 校验 + 原子改名） | [FolderRepositoryStore](../src/GameSaveSync.Storage/FolderRepositoryStore.cs) |
| 版本历史浏览 / 文件清单查看 | Desktop `VersionsView` + `ManifestWindow` |
| 恢复历史版本（恢复前自动备份 + 完成校验） | [RestoreManager](../src/GameSaveSync.Sync/RestoreManager/RestoreManager.cs) |

### 阶段二：网盘同步目录接入（已完成）

| 能力 | 实现 |
|---|---|
| 存储适配层抽象（未来接网盘 API） | [IRepositoryStore](../src/GameSaveSync.Storage/IRepositoryStore.cs) |
| 文件夹适配器（百度网盘同步目录） | [FolderRepositoryStore](../src/GameSaveSync.Storage/FolderRepositoryStore.cs) |
| create-if-absent 并发写入容忍（同内容幂等、异内容报错） | 同上，已被单测覆盖 |
| 网盘冲突副本（`xxx (1)`）列举容忍 | `ListAsync` 规范命名过滤（T18 已测） |
| schemaVersion 校验（升级预留） | [RepoProtocol](../src/GameSaveSync.Core/Models/RepoProtocol.cs)（T19 已测） |

### 阶段三：多设备冲突处理（已完成）

| 能力 | 实现 |
|---|---|
| Commit DAG 分析（Head 计算 / 后代判定 / 父缺失容忍） | [CommitGraph](../src/GameSaveSync.Core/Services/CommitGraph.cs) |
| 依赖完整性校验（Commit 可见 ≠ 可应用） | [SyncEngine](../src/GameSaveSync.Sync/SyncEngine/SyncEngine.cs)（T04/T05/T11 已测） |
| 同步决策表（快进应用 / 等待文件 / 冲突） | 同上（T01/T06/T07 已测） |
| 冲突三选项 + 多父 Commit 收敛 | [ConflictResolver](../src/GameSaveSync.Sync/ConflictResolver/ConflictResolver.cs) |
| 绑定已有游戏复用共享 gameId | `BindExistingGameAsync`（T17 已测） |
| 克隆环境设备 ID 重置 | `RegenerateDeviceIdAsync` + 设置页（T20） |

### 阶段四部分能力（提前完成）

- 单实例互斥 Mutex（按仓库路径区分，T15）
- WPF 四页签 UI + 托盘最小化 + 开机自启开关
- 游戏进程监控：5 秒轮询 + 退出后延迟 15 秒自动备份（[GameProcessMonitorService](../src/GameSaveSync.Desktop/Services/GameProcessMonitorService.cs)）
- 启动恢复：崩溃残留同步任务重置（T11）、未完成恢复自动回滚（T14 逻辑）
- Serilog 滚动日志 + 全局异常处理

### 测试覆盖

- 单元：确定性序列化、路径规范化、DAG 分析、存储 create-if-absent / 冲突副本 / 越界 key、schema 校验
- 集成（双临时目录模拟 A/B 两设备）：T01/T02/T03/T04/T05/T06/T07/T10/T11/T17
- 详见 [WIKI §9](WIKI.md#9-测试场景映射)

---

## 二、后续工作

### P1 — 完善与加固（下一步）

1. **补充自动化测试**
   - T08：游戏运行期间禁止自动覆盖（注入假 `IGameProcessChecker` 返回 true）
   - T14：恢复中途 kill 进程 → 启动回滚验证（可模拟：直接调用 `RollbackPendingRestoreAsync`）
   - T12：多进程并发写同一仓库的压力测试（当前 create-if-absent 语义已单测，缺多进程场景）
2. **自动同步循环**
   - 当前"扫描远端状态"需手动触发；计划：启动扫描 + 文件监听（`SaveDirectoryWatcher` 已实现待接线）+ 定时器（如 5 分钟）三路触发 `ScanGameAsync`，满足 `AutoApplyCandidate` 时自动应用并通知
3. **通知与状态可视化**
   - 托盘气泡通知（新版本可用 / 冲突 / 等待文件中）
   - 同步状态页增加持续刷新与"同步中"动画
4. **空间监控（T13）**
   - 应用前检查仓库目录所在盘剩余空间；快照前预估增量体积，不足时警告
5. **游戏库辅助**
   - 常见游戏存档路径内置数据库（可参考 PCGamingWiki 存档路径），添加游戏时自动推荐路径

### P2 — 扩展与生态

6. **网盘 API 直连**（阶段四核心）
   - 实现 `IRepositoryStore` 的百度网盘开放平台适配器（离线下载/上传、增量同步），摆脱"必须装客户端 + 同步目录"限制
   - 大文件分片上传、断点续传
7. **对象垃圾回收**
   - V1 仓库只增不删；后续提供"基于全设备扫描的引用计数 GC"（需各设备上报本地仍引用的 Commit，且仅清理超过保留期的对象），配合回收站二次确认
8. **存档加密**：用户密钥加密 Object 内容（网盘服务商不可见明文）
9. **UI 打磨**：版本对比视图（两个版本文件级 diff）、暗色主题、游戏封面
10. **打包分发**：自包含单文件发布（self-contained）、安装器、自动更新
11. **跨平台探索**：核心/存储/快照/同步层均为 `net10.0` 无 Windows 依赖，可评估 Avalonia/Uno 复用业务层

### 技术债（低优先）

- `SaveDirectoryWatcher` 目前仅"整目录标脏"，可细化为逐文件记账减少哈希扫描
- `FileCache` 表已建未接入（可用于跳过未变化文件的哈希计算，提升大存档扫描速度）
- 快照构建当前串行逐文件哈希，大存档可并行化（注意 IO 顺序与稳定性检查节奏）
- `RestoreManager.RecoverPendingRestoresAsync` 中错误提示信息可附上备份目录路径引导用户
