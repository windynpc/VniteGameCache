# Save Synchronization

## 原则

存档系统与游戏本体缓存系统必须独立。

游戏退出后只扫描已登记的 Mutable Paths（可变路径），不得扫描完整游戏目录。

## 支持路径

- 游戏目录内相对路径
- `%APPDATA%`
- `%LOCALAPPDATA%`
- `%USERPROFILE%\Documents`
- `%USERPROFILE%\Saved Games`
- 用户自定义绝对路径

## 每个路径配置

- path
- sync_mode
- snapshot_enabled
- exclude_patterns
- conflict_policy

## 启动前

- 读取存档索引
- 检查 Pending Queue
- 必要时从 NAS 恢复较新存档
- 不扫描完整游戏本体

## 退出后

1. 等待所有相关游戏进程退出。
2. 延迟 10 至 30 秒。
3. 只扫描登记的可变路径。
4. 检测变化。
5. 创建快照。
6. 同步到 NAS。
7. 更新索引。
8. 同步失败则保留本地数据并进入 Pending Queue。

## 冲突

如果 NAS 和本地都在上次同步后修改：

- 禁止静默覆盖
- 同时保留两个版本
- 创建冲突记录
- 默认把较新版本设为活动候选
- 允许用户恢复任意版本

## 快照

第一版使用 ZIP 即可。

支持：

- 每次退出快照
- 保留最近 N 个版本
- 每日一个版本
- 手动固定
- 恢复历史版本

第一版不实现块级去重。

## Phase 6 Manual Save Profiles

Phase 6 intentionally does not implement automatic bidirectional save synchronization.

Instead, users manually create Save Profiles and run explicit operations:

- validate path
- backup
- export ZIP
- import folder or ZIP
- restore backup

Import and restore always create an automatic backup of the current target first. ZIP import rejects path traversal entries. Supported source types are:

- `GameRelative`
- `AppData`
- `LocalAppData`
- `Documents`
- `SavedGames`
- `Absolute`

For non-absolute source types, `path_template` must resolve inside its configured root. Templates that escape with `..` or rooted paths are rejected. Adding the same resolved path for the same game updates the existing Save Profile instead of creating duplicates.

Automatic exit-time sync, NAS write-back queues, and conflict merge remain future work.

## Vnite 4.14 自动备份与 VGC Save Profiles

Vnite 4.14 支持逐游戏关闭自身的自动存档备份；一次会话的游玩记录若全部因时长过短被忽略，Vnite 也不会触发该自动备份。此开关只控制 Vnite 的原生备份流程。

VGC Save Profiles 是独立的手动操作：用户在 VGC 中选择备份、导入、导出或恢复时才运行；导入和恢复前会备份当前目标存档。VGC 不根据 Vnite 的逐游戏开关启停这些操作，也不调用 Vnite 原生的 `game:backup-save` IPC。当前插件 API 未提供可安全调用该 IPC 的 `invoke` 方法。

VGC 接管现有游戏时保留 Vnite local doc 中的 `path.savePaths` 等非启动字段，不会改动 Vnite 的自动备份设置。两套备份的目录和保留策略各自管理；VGC 自动双向存档同步尚未实现。
