# GameSaveSync — 本地单机游戏存档同步工具

为单机游戏提供 **Steam 云存档式** 的多设备同步与版本管理：通过用户自选网盘（百度网盘等）的**双向同步目录**交换数据，不依赖任何自建服务器。核心原则：**绝不静默覆盖用户存档**。

## 功能特性

- **内容寻址存储**：存档文件按 SHA256 去重存储，未变化的文件零重复占用空间
- **版本历史**：每次备份生成不可变 Commit，可浏览任意版本的完整文件清单
- **一键恢复**：恢复任意历史版本，恢复前自动把当前存档备份进版本历史（回滚也是新版本）
- **多设备冲突处理**：检测到并行版本时提供三个选项（保留本机 / 使用云端 / 分别导出检查），不自动删数据
- **依赖完整性校验**：版本"可见"≠"可应用"，应用前校验全部依赖文件存在且哈希一致
- **游戏退出自动备份**：监控游戏进程，退出后延迟自动快照
- **游戏启动器**：添加游戏时可选指定游戏 exe，在列表中一键启动 / 停止游戏并实时显示运行状态
- **游戏隐藏与主题**：可隐藏暂时不玩的游戏；外观支持跟随系统 / 浅色 / 深色三种主题
- **仓库统计**：设置页实时显示仓库对象数与总体积，便于评估去重效果
- **本机数据不入云**：设备 ID、游戏路径等仅存本机 SQLite，共享仓库中无隐私信息

## 技术栈

.NET 10 · C# · WPF (MVVM, CommunityToolkit.Mvvm) · SQLite (Microsoft.Data.Sqlite) · Serilog · xUnit

## 快速开始

### 构建

```powershell
dotnet build GameSaveSync.sln
```

### 运行

```powershell
dotnet run --project src\GameSaveSync.Desktop
```

首次启动后在 **设置** 页绑定仓库目录（建议为网盘同步目录下的 `GameSaveVault` 文件夹）。更换同步目录时用 **迁移仓库到新目录…**：会把全部历史版本复制并校验到新目录后自动切换绑定，仓库 ID 保持不变；迁移成功后可选是否清理旧目录。然后：

1. **游戏页**：添加游戏（名称 + 存档目录，可选进程名与游戏程序 exe）；或在其他设备已建过游戏时，直接"从仓库绑定"。配置了游戏程序后可一键启动 / 停止游戏，列表实时显示运行状态
2. **版本历史页**：手动"立即备份"或查看版本、恢复历史版本
3. **同步状态页**：扫描远端状态、应用远端新版本、处理冲突

### 测试

```powershell
dotnet test GameSaveSync.sln
```

共 86 个用例：72 单元测试 + 14 集成测试（双临时目录模拟 A/B 两台设备共享同一仓库）。

## 目录结构

```
GameSaveSync/
├── GameSaveSync.sln
├── 游戏存档同步.md            # 技术方案（需求与设计）
├── docs/
│   ├── WIKI.md               # 项目知识库（架构/协议/算法）
│   └── ROADMAP.md            # 开发进展与后续工作
├── src/
│   ├── GameSaveSync.Core/        # 仓库协议模型、确定性序列化、Commit DAG、路径规范化
│   ├── GameSaveSync.Storage/    # IRepositoryStore + 文件夹适配器 + 元数据读写
│   ├── GameSaveSync.FileSystem/  # 扫描 / SHA256 / 目录监听
│   ├── GameSaveSync.Snapshot/   # ObjectStore（去重）+ SnapshotBuilder（稳定性检查）
│   ├── GameSaveSync.Data/        # 本地 SQLite（11 张表）
│   ├── GameSaveSync.Sync/        # Commit/SyncEngine/ConflictResolver/RestoreManager/门面
│   └── GameSaveSync.Desktop/     # WPF UI + 托盘 + 进程监控
└── tests/
    ├── GameSaveSync.UnitTests/
    └── GameSaveSync.IntegrationTests/
```

## 运行时数据位置（均为本机，不进云盘）

| 内容 | 位置 |
|---|---|
| 本地数据库 | `%LOCALAPPDATA%\GameSaveSync\state.db` |
| 日志 | `%LOCALAPPDATA%\GameSaveSync\logs\` |
| 恢复 Journal / 临时区 | `%LOCALAPPDATA%\GameSaveSync\journals\`、`temp\` |
| 共享仓库 | 用户指定的网盘同步目录（如 `…\BaiduSyncdisk\GameSaveVault`） |

## 工作原理（30 秒版）

存档目录 → 稳定快照（SHA256）→ 缺失对象写入 `objects/` → 确定性 Manifest → 校验依赖完整 → 最后发布不可变 Commit。多设备通过扫描仓库内全部 Commit 计算 Head（无共享 current 指针），单一 Head 且本地无修改时自动快进应用，多 Head 即冲突交由用户处理。

详见 [项目 Wiki](docs/WIKI.md) 与 [开发进展与后续工作](docs/ROADMAP.md)。
