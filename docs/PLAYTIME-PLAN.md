# 游戏时长记录与统计报表 — 设计方案

> 状态：待评审
> 日期：2026-11-25
> 关联：`docs/WIKI.md`（架构分层）、`docs/ROADMAP.md`（阶段规划）

## 1. 目标

自动记录每款游戏的运行会话（开始/结束时间），并在游戏页提供统计报表：

- 总游戏时长、会话次数、平均/最长单次
- **第一次游戏时间**（最早 `StartedAt`）
- **最后一次游戏时间**（最近已结束会话的 `EndedAt`）
- 最近会话明细与 CSV 导出

## 2. 已确认决策

| 决策点 | 结论 |
|---|---|
| 短会话阈值 | **< 60 秒的会话不计入统计**（仍记录入表，查询层过滤，阈值未来可调） |
| App 退出时游戏在跑 | **会话记到 App 退出时刻**，结束原因标记为 `AppClosed` |
| 报表位置 | **并入游戏页**，不新增页签 |
| 趋势图 | **不做**（无按日/周图表，仅汇总数字与明细列表） |
| 数据存储 | 本机 SQLite，不入共享仓库（延续"本机数据不入云"原则） |

## 3. 采集设计

复用现有 [GameProcessMonitorService](../src/GameSaveSync.Desktop/Services/GameProcessMonitorService.cs) 的 5 秒轮询与 `WasRunning` 状态转换检测，新增 `PlaySessionRecorder` 服务：

- **开始**：轮询发现 `false → true` → 新建会话（`StartedAt` = 本次轮询时刻）
- **结束**：轮询发现 `true → false` → 回填 `EndedAt`、计算 `DurationSeconds`
- **心跳**：每轮轮询对进行中会话更新 `LastSeenAt`（崩溃恢复用）
- **接入方式**：沿用现有 `BackupAsync` 回调注入模式，监控服务新增统一回调 `OnSessionEventAsync(gameId, eventType, timestamp)`，由 App 启动时装配到 Recorder

时间精度约 ±5 秒（轮询间隔），满足时长统计需求。

## 4. 数据模型

### 4.1 新表 PlaySessions

```sql
CREATE TABLE IF NOT EXISTS PlaySessions (
    SessionId      TEXT PRIMARY KEY NOT NULL,  -- UUID
    GameId         TEXT NOT NULL,
    StartedAt      TEXT NOT NULL,             -- ISO8601 UTC
    EndedAt        TEXT,                      -- NULL = 进行中
    LastSeenAt     TEXT NOT NULL,             -- 最近心跳
    EndReason      INTEGER NOT NULL DEFAULT 0,-- 见枚举
    DurationSeconds INTEGER NOT NULL DEFAULT 0
);
CREATE INDEX IF NOT EXISTS IX_PlaySessions_GameId ON PlaySessions(GameId);
CREATE INDEX IF NOT EXISTS IX_PlaySessions_StartedAt ON PlaySessions(StartedAt);
```

### 4.2 EndReason 枚举

| 值 | 含义 | 场景 |
|---|---|---|
| 0 | Running | 进行中 |
| 1 | NormalExit | 正常退出（监控检测到进程退出） |
| 2 | AppClosed | App 退出时游戏仍在运行 |
| 3 | Interrupted | 崩溃/断电中断（启动时孤儿收尾） |

### 4.3 迁移

沿用 [Schema.cs](../src/GameSaveSync.Data/SqliteDatabase/Schema.cs) 现有模式：`Schema.CurrentVersion` 1 → 2，新表用 `CREATE TABLE IF NOT EXISTS` 幂等创建，老库升级无数据回填需求。

### 4.4 新增 PlaySessionRepository

方法：

- `InsertAsync` — 新会话
- `FinishAsync(sessionId, endedAt, endReason)` — 回填结束
- `TouchLastSeenAsync(sessionId, timestamp)` — 心跳
- `CloseOrphansAsync(now)` — 启动时收尾残留会话（`EndedAt IS NULL` → `EndedAt = LastSeenAt`, `EndReason = Interrupted`）
- `CloseAllAsync(now, endReason)` — App 退出时统一收尾
- `GetStatsAsync(gameId)` — 聚合统计（见下）
- `GetRecentSessionsAsync(gameId, topN)` — 明细
- `GetAllSessionsAsync()` — CSV 导出用

### 4.5 统计查询 SQL 草案

```sql
SELECT COUNT(*)              AS SessionCount,
       SUM(DurationSeconds)  AS TotalSeconds,
       AVG(DurationSeconds)  AS AvgSeconds,
       MAX(DurationSeconds)  AS MaxSeconds,
       MIN(StartedAt)        AS FirstPlayedAt,
       MAX(EndedAt)         AS LastPlayedAt
FROM PlaySessions
WHERE GameId = @gameId
  AND EndReason IN (1, 2, 3)      -- 仅已结束会话
  AND DurationSeconds >= 60;      -- 短会话阈值
```

明细（最近 10 条）：

```sql
SELECT SessionId, StartedAt, EndedAt, DurationSeconds, EndReason
FROM PlaySessions
WHERE GameId = @gameId AND EndReason IN (1, 2, 3)
ORDER BY StartedAt DESC LIMIT 10;
```

## 5. 边界场景

| 场景 | 处理 |
|---|---|
| App 退出时游戏仍在运行 | `App.OnExit` → `CloseAllAsync(now, AppClosed)`，`EndedAt` = App 退出时刻（已确认） |
| 崩溃/断电 | 启动时 `CloseOrphansAsync`：残留会话以 `LastSeenAt` 收尾，标记 `Interrupted` |
| 短会话（误启动/启动器瞬时进程） | 照常入库；统计查询统一过滤 `DurationSeconds >= 60` |
| 未配置进程名的游戏 | 无法监控、不产生会话；统计区显示"—" |
| 删除游戏 | 会话数据保留（不级联删除）；报表只展示现有游戏 |
| 时钟异常/回拨 | 若 `EndedAt < StartedAt` 则夹紧到 `StartedAt`，防止负时长 |
| 休眠期间游戏退出 | 恢复后检测到退出，`EndedAt` 为恢复轮询时刻，时长可能虚高；V1 接受，记入已知限制 |
| 心跳写库频率 | 每运行游戏每 5 秒一次 UPDATE，SQLite 本地可承受；V1 直接写库，简单可靠 |

## 6. 报表 UI（并入游戏页）

### 6.1 布局

在游戏页每个游戏卡片/行新增**"时长统计"区块**：

- 总时长（如 `36.5 小时`）、会话次数、平均单次
- 第一次游戏时间、最后一次游戏时间
- 选中/展开该游戏可查看**最近会话明细**（开始/结束/时长/结束原因，最近 10 条）

### 6.2 数据流

`GamesViewModel` → `GameSaveSyncAppService` 新增 API（`GetGamePlayStatsAsync` / `GetRecentSessionsAsync` / `ExportSessionsCsvAsync`）→ `PlaySessionRepository`。维持"UI 不直接碰 Data 层"的分层原则。

刷新时机：游戏页加载时查询；监控服务报告会话结束时通知刷新（复用现有 `GameStateChanged` 通知链路）。

### 6.3 CSV 导出

游戏页工具栏"导出统计数据"按钮 → `SaveFileDialog` → UTF-8 带 BOM（Excel 直开）。
列：`GameId, 游戏名, SessionId, StartedAt, EndedAt, DurationSeconds, EndReason`，导出全部游戏全部会话。

### 6.4 主题

新 UI 元素适配深浅主题（ThemeService 现有资源体系）。

## 7. 改动清单

| 文件 | 类型 | 内容 |
|---|---|---|
| `src/GameSaveSync.Data/SqliteDatabase/Schema.cs` | 改 | 版本 1→2 + 建表 + 索引 |
| `src/GameSaveSync.Data/Repositories/PlaySessionRepository.cs` | 新 | CRUD + 聚合查询 |
| `src/GameSaveSync.Desktop/Services/GameProcessMonitorService.cs` | 改 | 状态转换点触发注入的会话回调 |
| `src/GameSaveSync.Desktop/Services/PlaySessionRecorder.cs` | 新 | 开始/结束/心跳/孤儿收尾/退出收尾 |
| `src/GameSaveSync.Desktop/App.xaml.cs` | 改 | 装配 Recorder；OnExit 收尾 |
| `src/GameSaveSync.Sync/GameSaveSyncAppService.cs` | 改 | 统计查询 API |
| `src/GameSaveSync.Desktop/ViewModels/GamesViewModel.cs` | 改 | 统计属性、加载与导出命令 |
| 游戏页 View（Views 下对应 XAML） | 改 | 统计区块 + 会话明细 + 导出按钮 |
| 测试工程 | 新 | Recorder 状态机与 Repository 聚合单测 |

约 4 个新增 + 5 个修改。

## 8. 实施顺序

1. **Data 层**：Schema v2 + PlaySessionRepository + 单测
2. **采集**：PlaySessionRecorder + 监控服务挂钩 + App 装配（含 OnExit/启动孤儿收尾）+ 单测
3. **门面 API**：GameSaveSyncAppService 统计查询
4. **UI**：游戏页统计区块、会话明细、CSV 导出、主题适配
5. **验收**：`dotnet build` 0 警告 0 错误；全部测试通过（现有 86 + 新增）

## 9. 测试要点

- Recorder 状态机：正常开始/结束；App 退出收尾（AppClosed）；崩溃孤儿恢复（Interrupted，EndedAt=LastSeenAt）；短会话照常入库
- Repository 聚合：`<60s` 过滤；进行中会话不计入统计；首次/最后时间正确；空数据返回零值
- 集成：模拟轮询序列（运行→退出→再运行），验证会话链完整

## 10. 隐私与未来扩展

时长数据**只存本机 SQLite，不入共享仓库**，多设备各自记录、不跨设备合并。

未来扩展（可入 ROADMAP P2）：仿照 commits 的 append-only 模式发布 `games/<gameId>/sessions/<device前8位>/<sessionId>.json`，实现多设备时长合并统计，仓库协议无需大改。
