# GameSaveSync 项目 Wiki

技术方案的落地实现知识库。需求背景见 [游戏存档同步.md](../游戏存档同步.md)，进展见 [ROADMAP.md](ROADMAP.md)。

## 目录

1. [总体架构](#1-总体架构)
2. [仓库协议](#2-仓库协议)
3. [关键算法](#3-关键算法)
4. [同步决策](#4-同步决策)
5. [冲突处理](#5-冲突处理)
6. [恢复机制](#6-恢复机制)
7. [本地数据库](#7-本地数据库)
8. [安全设计原则](#8-安全设计原则)
9. [测试场景映射](#9-测试场景映射)
10. [FAQ](#10-faq)

---

## 1. 总体架构

依赖方向严格自上而下，UI 层永不感知存储细节：

```
┌────────────────────────────────────────────────────────┐
│  Desktop (WPF)                                         │
│  四页签 UI · 托盘 · 单实例 Mutex · GameProcessMonitor   │
│  （开机自启 · Serilog 滚动日志 · MVVM）                │
└───────────────────────┬────────────────────────────────┘
                        │ 只调用门面
┌───────────────────────▼────────────────────────────────┐
│  Sync（业务核心，可被任意 UI 复用）                     │
│  GameSaveSyncAppService ─ 门面                          │
│  ├─ CommitManager      发布顺序：快照→对象→清单→Commit │
│  ├─ SyncEngine         扫描仓库 + 同步决策表            │
│  ├─ ConflictResolver   三选项冲突处理 + 多父收敛        │
│  └─ RestoreManager     staging + Journal + 自动回滚    │
└──────┬──────────────────────────────┬──────────────────┘
       │                              │
┌──────▼──────────┐  ┌────────────────▼───────────────┐
│ Snapshot        │  │ Data (SQLite, 仅本机)           │
│ ObjectStore     │  │ 设备/游戏/路径/已应用版本/       │
│ SnapshotBuilder │  │ Commit缓存/冲突/任务/恢复日志    │
└──────┬──────────┘  └────────────────────────────────┘
       │
┌──────▼──────────────────────────────┐
│ FileSystem（扫描/SHA256/Watcher）     │
└──────┬──────────────────────────────┘
       │
┌──────▼──────────────────────────────┐
│ Storage（IRepositoryStore 抽象）     │
│ FolderRepositoryStore → 网盘同步目录 │
└──────┬──────────────────────────────┘
       │
┌──────▼──────────┐
│ Core（模型/协议） │  RepoModels · RepoJson · CommitGraph · PathNormalizer
└─────────────────┘
```

## 2. 仓库协议

共享仓库是一个**只能追加**（append-only）的目录树，直接放在网盘双向同步目录中，所有设备看到同一份：

```
GameSaveVault/
├── repo.json                                  # 仓库身份（repositoryId），schemaVersion 校验
├── devices/<device-uuid>.json                  # 设备注册（仅 ID/名称/时间，非敏感）
├── games/<game-uuid>/
│   ├── game.json                               # 共享游戏元数据（create-if-absent，不可变）
│   ├── manifests/<manifest-sha256>.json        # 版本文件清单（内容寻址）
│   └── commits/<device8>/<commit-uuid>.json    # 提交记录（device8=设备ID前8位，缓解路径长度）
└── objects/sha256/<hash前2位>/<完整hash>       # 存档文件内容（内容寻址，天然去重）
```

**不可变性与 create-if-absent**：`PutImmutableAsync` 的语义是"存在即复用、不存在才写入"。写入流程：临时文件 → SHA256 校验 → 改名发布；若目标已被并发创建，校验内容一致后复用而非覆盖。

**确定性序列化**（`RepoJson`）：固定属性顺序（`JsonPropertyOrder`）、2 空格缩进、UTF-8 无 BOM、Invariant Culture。**保证内容相同的存档在任何设备上算出相同的 manifestHash**——这是 Manifest 内容寻址可行的前提。

**为什么没有 current.json 指针**：共享可变指针在网盘同步下极易产生冲突副本，且"最后写入"在网盘语义下不可靠。改为每台设备发布自己的不可变 Commit，Head 由扫描全部 Commit 计算得出。

**依赖完整性**（网盘乱序到达的关键容忍）：Commit 文件先到齐 ≠ 可应用。`SyncEngine` 校验 Manifest 存在且全部 Object 到位（大小 + 哈希）后，才允许应用；否则标记 `WaitingForFiles` 继续等待。

## 3. 关键算法

### 3.1 稳定快照（SnapshotBuilder）

```
扫描存档目录（跳过 ReparsePoint，防越界）
  → 逐文件：两次采样 (大小, mtime) 间隔 2 秒一致 → 稳定
  → SHA256 → EnsureObject（已存在则复用，不重传）
  → 组装 Manifest（按路径排序）→ manifestHash = SHA256(确定性 JSON)
  → 校验全部引用对象完整存在 → 交给 CommitManager 发布
```

被占用文件最多重试 3 次，仍不稳定则失败（不会产出半成品版本）。

### 3.2 Head 计算（CommitGraph）

```
Head = 依赖齐备 且 未被任何已知 Commit 引用为父版本 的 Commit
```

- **单一 Head** → 顺序版本，可快进
- **多 Head** → 多设备并行提交，即冲突
- **父版本缺失** → `PendingNodes`，该 Commit 暂缓判定（等待网盘同步）

`IsDescendantOf` 沿 ParentIds 向上遍历判断祖先后代关系，用于区分"远端是本地基线的后代（顺序快进）"与"并行提交（冲突）"。

### 3.3 目录状态判定（EvaluateLocalState）

本地目录与已应用 Manifest 逐文件比对：数量 → 大小 → mtime（±2 秒容差）→ SHA256（大小相同但 mtime 变化时复核）。结果 `Clean / Dirty / UnknownBaseline / EmptyOrMissing`。

## 4. 同步决策

`SyncEngine.ScanGameAsync` 每次扫描产出一份决策报告：

| 条件 | 决策 |
|---|---|
| 单一 Head 且是本地基线的后代，本地 Clean/空，游戏未运行 | ✅ `AutoApplyCandidate`，可自动应用 |
| 多 Head（依赖齐备） | ⚠️ `ConflictDetected`，记录冲突等待用户处理 |
| 远端与本地基线互为并行提交 | ⚠️ `ConflictDetected` |
| 本地 Dirty（有未提交修改） | ⚠️ 先备份本地修改，再进入冲突流程 |
| 本地无基线（UnknownBaseline） | ⚠️ 需用户显式确认后才应用远端 |
| 依赖 Object/Manifest 未到齐 | ⏳ `WaitingForFiles` |
| 游戏正在运行 | 🚫 禁止覆盖存档，延迟处理 |

## 5. 冲突处理

三个选项（`ConflictResolver`），处理过程中**任一侧数据都不会被静默丢弃**：

1. **保留本机**：把本机当前存档发布为新 Commit，其 `parentIds` 同时包含所有并行 Head——版本关系就此**收敛**，两台设备此后都基于该新版本快进（方案 7.5 的 V3 模式）。
2. **使用云端**：先把本机当前存档自动备份为 Commit（进版本历史），再应用选定的远端版本，最后发布收敛 Commit（父版本 = 本机备份 + 选定远端）。
3. **分别导出**：本机当前状态与各远端版本分别导出到 `%LOCALAPPDATA%\GameSaveSync\conflict-export\`，用户人工检查后再选 1 或 2 收敛。

## 6. 恢复机制

`RestoreVersionAsync` 的完整链路（"回滚也是新版本"）：

```
1. 完整性校验：目标 Manifest 全部对象逐个 SHA256（禁止部分恢复）
2. 恢复前备份：当前目录自动发布为备份 Commit（数据永不丢）
3. staging：目标版本先完整构建到独立临时目录（不触碰游戏目录）
4. Journal：备份现有目录 → 逐文件替换/删除多余 → 每步落盘 journal JSON
5. 应用：staging → 游戏目录
6. 验证：重新扫描 + 逐文件哈希比对，与 Manifest 一致才算成功
7. 收尾：发布"恢复 Commit"（父版本=备份 Commit），更新已应用版本记录
失败 → 自动回滚：备份目录恢复现场；回滚也失败则保留备份目录等人工处理
启动时 → 扫描未完成恢复（状态 InProgress），一律保守回滚
```

## 7. 本地数据库

SQLite（`%LOCALAPPDATA%\GameSaveSync\state.db`，绝不入云盘），单写者全局串行：

| 表 | 用途 |
|---|---|
| Meta | schemaVersion、仓库路径等键值设置 |
| Devices | 本机设备身份（UUID，克隆环境可"重新生成"） |
| Games / GamePaths | 游戏配置 + **按设备**的存档路径映射（各设备路径可不同） |
| Commits | 已发现 Commit 缓存 |
| AppliedVersions | `lastAppliedCommitId / lastAppliedManifestHash`（本地基线） |
| Conflicts | 未处理冲突记录 |
| SyncTasks | 同步任务，启动时 Running 残留重置为 Pending（崩溃恢复） |
| RestoreJournals | 恢复断点（配合 journal 文件） |
| Snapshots / FileCache | 快照与文件哈希缓存（供增量加速） |

## 8. 安全设计原则

1. **绝不静默覆盖**：任何覆盖本地存档的操作前，当前状态必先进版本历史或备份目录
2. **先校验后应用**：应用远端版本前逐对象完整哈希校验，任何缺失/损坏 → 拒绝应用
3. **路径安全**：相对路径规范化，拒绝绝对路径、盘符、`..` 穿越越界（存储层双重校验）
4. **不跟随 ReparsePoint**：扫描与枚举跳过符号链接/junction，防仓库外泄与循环
5. **schemaVersion 校验**：仓库文件版本不匹配时拒绝操作（为未来升级留门）
6. **冲突副本容忍**：网盘生成的 `xxx (1)` 等非规范命名文件在列举时被忽略
7. **本机数据不入云**：设备 ID、本机路径、进程名只存本地 SQLite
8. **写入原子性**：临时文件 + 校验 + 改名，绝不直接写目标路径

## 9. 测试场景映射

| 场景 | 测试 | 结果 |
|---|---|---|
| T01 新设备首次同步 | `EndToEndSyncTests`（无基线 → 确认应用 → 快进应用，逐文件字节+哈希比对） | ✅ |
| T02 增量对象去重 | `BackupAndRestoreTests`（stats 仅 +1） | ✅ |
| T03 删除文件 + 恢复旧版 | `BackupAndRestoreTests` | ✅ |
| T04 Object 未同步到齐 | `RepositoryIntegrityTests` → WaitingForFiles 拒绝应用 | ✅ |
| T05 Object 内容损坏 | `RepositoryIntegrityTests`（等长/变长破坏均拒绝，本地不覆盖） | ✅ |
| T06 并行提交双版本冲突 | `ConflictDetectionTests`（两侧版本都在仓库） | ✅ |
| T07 本地未提交修改不被覆盖 | `ConflictDetectionTests` | ✅ |
| T08 游戏运行时禁止覆盖 | 逻辑在 `SyncEngine`（UI 侧 `GameProcessMonitorService`）；自动化用例待补 | ⚠️ 逻辑已实现 |
| T09/T18 网盘冲突副本 | `FolderRepositoryStoreTests`（非规范命名文件被忽略） | ✅ |
| T10 恢复含自动备份 | `BackupAndRestoreTests`（恢复产生备份+恢复两个 Commit） | ✅ |
| T11 半成品 Commit 容忍 | `HalfCommitTests`（只发 Manifest/Commit 不写 Object → 不应用） | ✅ |
| T12 并发写同一仓库 | create-if-absent + 原子改名，单测覆盖幂等；多进程压测待补 | ⚠️ 部分 |
| T14 恢复中断回滚 | 机制实现于 `RestoreManager.RollbackPendingRestoreAsync`；自动化用例待补 | ⚠️ 逻辑已实现 |
| T15 单实例互斥 | Desktop `App.xaml.cs` Mutex（按仓库路径区分） | ✅ 实现 |
| T16 路径越界防护 | `PathNormalizerTests` + `FolderRepositoryStoreTests` | ✅ |
| T17 复用共享 gameId 绑定 | `EndToEndSyncTests` | ✅ |
| T19 schema 版本不匹配 | `RepoSchemaTests` | ✅ |
| T20 克隆环境设备 ID 重置 | `RegenerateDeviceIdAsync` + 设置页按钮 | ✅ 实现 |

## 10. FAQ

**支持哪些网盘？**
任何提供**双向同步目录**的服务：百度网盘（同步空间）、坚果云、OneDrive、Dropbox、iCloud Drive 等。V1 通过同步目录工作，不直接调用网盘 API。

**会不会重复占用空间？**
不会。对象按内容 SHA256 寻址，相同内容只存一份；只有真正变化的存档文件会新增对象。一次完整的版本历史 ≈ 每次变化文件的体积之和。

**两台设备同时玩了同一游戏会怎样？**
两边各自产生新版本 → 多 Head 冲突。三个处理选项任选，两侧数据都会保留（详见 §5）。**不会自动合并，也不会丢数据**。

**网盘同步太慢导致对象没到齐怎么办？**
该版本一直显示"等待同步文件"，不会应用残缺版本（依赖完整性校验）。网盘传完后自动满足条件。

**如何换电脑？**
新设备安装 → 绑定同一网盘仓库目录 → "从仓库绑定已有游戏"复用同一 gameId → 映射本机存档路径 → 应用云端版本。

**游戏存档目录被游戏占用（正在写盘）怎么办？**
稳定性检查（两次采样大小/mtime 一致）+ 3 次重试；仍不稳定的文件会中止本次备份而不是产出损坏版本。游戏运行时默认禁止覆盖恢复。
