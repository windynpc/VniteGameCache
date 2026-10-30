# Cache System

Archive-source games use the same persistent SSD cache goal. Their first launch requires per-game extraction and validation; subsequent launches from a Ready cache do not re-extract. Any archive extraction must use a single-game staging directory, reject unsafe member paths, verify the selected executable, and protect local-only mutable data before replacing an existing Ready cache. This flow is planned and must not be routed through the directory-source Robocopy service.

## 基本模型

- NAS：完整游戏主库
- SSD：持久缓存
- 游戏本体：默认 `NAS -> SSD`
- 存档配置：独立同步

缓存完成后，不在退出时立即删除。

## 缓存模式

每个游戏支持：

- Always Cached
- Cache Before Launch
- Run Directly From NAS
- Save Sync Only
- Never Cache

默认：

```text
Cache Before Launch + 保留缓存
```

## 启动策略

### Fast Launch

默认。

本地缓存有效时：

- 立即从 SSD 启动
- 后台检查 NAS Revision
- 不阻塞启动

### Strict Launch

- 启动前检查 NAS
- 应用必要变化
- 确认缓存最新后启动

### Offline Launch

- 不访问 NAS
- 使用已有 SSD 缓存
- 存档进入 Pending Queue
- NAS 恢复后同步

## Robocopy

第一版优先封装 Robocopy，不自研完整复制引擎。

要求：

- 增量复制
- 断点续传
- 有限重试
- 可取消
- 可恢复
- 解析退出码
- 保存日志
- 支持 UNC、中文、空格

不得默认使用 `/MIR`。

删除必须来自明确同步计划，不得因为目标端多出文件就自动删除。

### Phase 5 Robocopy Contract

Phase 5 implements only `NAS -> SSD` game body cache copy.

Implemented Agent CLI:

```text
VniteGameCache.Agent prepare-cache --game-id <id>
```

Supported options:

- `--db-path <path>`
- `--force-refresh`
- `--verification Fast`
- `--minimum-free-space-bytes <bytes>`
- `--cancel-after-ms <ms>`

Copy Plan fields:

- `game_id`
- `source_root`
- `cache_root`
- `source_revision`
- `current_cache_revision`
- `required_copy`
- `reason`
- `estimated_file_count`
- `estimated_total_bytes`
- `executable_relative_path`
- `verification_policy`

Skip rule:

```text
cache_status == Ready
AND source_revision == cache_revision
AND cache executable exists
AND force_refresh == false
```

When the skip rule is true, Robocopy is not started and the result is `CacheAlreadyReady`.

Copy triggers:

- cache root missing
- cache executable missing
- `cache_revision < source_revision`
- `cache_status` is `Incomplete` or `Failed`
- `force_refresh=true`

Default Robocopy arguments:

```text
/E /Z /FFT /R:2 /W:2 /MT:8 /COPY:DAT /DCOPY:T /XJ /NP /TEE /LOG:<path>
```

Forbidden by default:

```text
/MIR /PURGE /MOVE /MOV
```

Exit code policy:

- `0..7`: success or non-fatal differences
- `>=8`: failure

Cancellation:

- terminates Robocopy process tree
- marks task `Cancelled`
- sets `cache_status=Incomplete`
- preserves partial cache files
- does not advance `cache_revision`

Failure:

- marks task `Failed`
- sets `cache_status=Failed`
- does not advance `cache_revision`
- does not modify NAS

Success:

- runs Fast verification
- sets `cache_status=Ready`
- sets `cache_revision=source_revision`

Wrapper behavior:

- Agent `PrepareLaunch` prepares cache when the game exists in SQLite.
- Agent returns optional `launch_executable_path` and `launch_working_directory`.
- Wrapper launches those cache paths instead of the source paths.
- Existing legacy stub behavior remains only for games not yet registered in SQLite.

## 校验等级

### Fast

- 文件大小
- 修改时间

默认。

Phase 5 Fast verification checks:

- cache root exists
- cache executable exists
- indexed non-excluded file count exists in cache
- indexed file sizes match

Phase 5 does not compute full hashes during normal cache copy.

## Phase 5.5 Launch Modes

Per-game launch modes:

- `Direct`
- `MTool`
- `CustomLauncher`

### Direct

Implemented path:

```text
Wrapper -> Agent PrepareLaunch -> Prepare Cache -> cache_root executable
```

### MTool

MTool mode is gated by real compatibility validation.

Validation attempts:

- configured `mtool_executable_path`
- `--help`
- `/?`
- `/help`

If no stable CLI is detected, Phase 5.5 does not guess arguments and reports manual confirmation required.

### CustomLauncher

Reserved for a future phase.

### Standard

- Fast
- 关键可执行文件 Hash
- 关键资源文件 Hash

### Full

- 所有文件 Hash

只用于：

- 用户手动验证
- 检测损坏
- 索引冲突
- 迁移失败
- 缓存异常

## LRU

使用 LRU（最近最少使用）清理。

支持：

- 缓存容量上限
- SSD 最低剩余空间
- 固定缓存
- 最近游玩时间
- 缓存大小

不得清理：

- 正在运行的游戏
- 有待同步存档的游戏
- 有冲突的游戏
- incomplete 状态的缓存
- 固定缓存游戏

## Phase 6 Cache Management

### LRU Suggestions

LRU cleanup supports suggestion mode and optional automatic mode. Automatic cleanup must truly release SSD space; it must not simply move a full game cache into same-disk quarantine.

Space accounting includes:

- Active cache directories.
- Quarantine directories.
- Staging directories.
- In-progress cache directories in `Checking`, `Copying`, or `Verifying`.

Required release is calculated as:

```text
required_by_cache_limit = max(0, total_managed_cache_size - cache_capacity_limit)
required_by_free_space = max(0, minimum_free_space_bytes - current_drive_free_space)
required_release = max(required_by_cache_limit, required_by_free_space)
```

`cache_capacity_limit = 0` disables capacity limit. `minimum_free_space_bytes = 0` disables minimum free-space cleanup.

Protected from cleanup:

- running/copying/checking/verifying games
- pinned games
- `cache_status != Ready`
- missing or inaccessible `source_root`
- missing or inconsistent cache roots

Running protection is double checked:

- Wrapper holds a per-game named mutex while the game is running.
- Wrapper asks Agent to set `cache_status=Running` after launch and `Ready` after process-tree exit.
- Cache cleanup blocks when either the database state is active or the named mutex exists.
- Stale `Running` rows without a mutex are repaired before cleanup decisions.

Candidates are sorted by oldest `last_played_utc`, then larger `cache_size_bytes`, then lower detected user-data risk.

Automatic LRU cleanup:

- Re-reads real disk free space after each deleted candidate.
- Deletes re-creatable game body files instead of moving full caches to same-disk quarantine.
- Preserves detected user data before deletion: Save Profiles, `save`, `savedata`, `saves`, `UserData`, `www/save`, `config`, `mods`, screenshots, and SSD-only files not present in the manifest.
- Stores preserved user data in quarantine and marks the game as requiring manual save backup.
- Stops on protection or deletion failure instead of continuing through later candidates.
- Does not follow Windows reparse points while scanning cache size, detecting SSD-only files, or copying protected user data. A protected path that is itself a reparse point blocks automatic cleanup instead of copying through the link.

Cleanup result fields distinguish `MovedToQuarantineBytes`, `ProtectedUserDataBytes`, `PermanentlyDeletedBytes`, `ActualFreeSpaceIncreaseBytes`, and `EstimatedReclaimableBytes`.

### Quick Refresh

Quick refresh calls the existing Robocopy cache path with force refresh. It does not use `/MIR`, `/PURGE`, `/MOVE`, or `/MOV`, so SSD-only files are preserved.

### Safe Clear and Quarantine

Safe clear checks common save paths and enabled Save Profiles. By default it blocks when possible save data is detected. Forced clear moves the cache directory to:

```text
<cache-root-parent>\Quarantine\<game-id>-<timestamp>
```

Config-like user data is optional:

- Always protected by default: `save`, `savedata`, `saves`, `UserData`, `www/save`, and enabled Save Profiles.
- Optional protection: `config`, `screenshots`, `screenshot`, `mods`, `mod`, `MTool`, `MToolScripts`, `MToolData`, `MCenter`, and `scripts`.
- Optional protection is controlled by shared setting `protect_config_files`; the Vnite plugin UI default is off.

Quarantine restore is allowed only when the original cache root is absent or empty.

Manual safe clear intentionally keeps full-cache quarantine semantics. If quarantine is on the same SSD as the cache root, it is recoverable but does not immediately increase Windows available space. Space is only truly released when quarantine is on another disk/NAS, or when expired quarantine is permanently deleted.

For an archive-backed game, the extracted files may sit under one or more top-level folders inside the cache root. Manual safe clear must also inspect the selected executable's working directory for the existing save-data path hints before it can move the cache to quarantine. Existing directory-backed games keep their current detection scope. This protects common archive layouts such as `<cache>/<archive folder>/save` without scanning the NAS or changing automatic cleanup behavior.

Control Panel maintenance actions must use the same Agent/cache-management safety path. Even test or duplicate-cache cleanup must not call recursive directory deletion directly; it should force a safe clear, create a quarantine record when a cache directory exists, and then remove only the selected test database row.

### Quarantine Retention

`quarantine_retention_days = 0` means never automatically delete quarantine entries.

When retention is greater than zero, expired quarantine cleanup may run from Agent commands, Cache UI, or LRU execution. Entries are permanently deleted only when:

- the quarantine entry is older than the retention window
- it has not been restored
- it has not already been deleted
- no active task exists for the game

Deletion failures keep the database record active so the next cleanup can retry.

### Copy Progress

Robocopy remains the copy engine. While it runs, Agent writes an atomic progress snapshot beside the Robocopy log and updates the task record.

The Control Panel displays:

- estimated percentage
- observed write throughput
- estimated remaining file count
- estimated remaining time
- current observed source file

Transferred bytes come from Windows process I/O counters. File count, current file, percentage, and ETA are explicitly estimates because Robocopy does not expose a stable structured progress API for the selected multithreaded copy mode. The final completed snapshot is replaced with exact indexed totals after verification.

