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
