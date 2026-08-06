# Persistent Indexing

## 原则

索引系统是本项目的核心。

正常重复启动仍然递归枚举整个 NAS 游戏目录，视为架构失败。

## 三类索引

必须逻辑分离：

1. Vnite 游戏库与元数据索引
2. 游戏文件缓存索引
3. 存档与配置同步索引

## SQLite

默认位置：

```text
%LOCALAPPDATA%\VniteGameCache\index.db
```

至少记录：

- 游戏配置
- 源路径
- 缓存路径
- 源 Revision
- 缓存 Revision
- 文件清单
- 目录指纹
- 存档路径
- 任务状态
- 冲突
- 错误
- Schema Version

## Game Manifest

每个游戏首次导入时生成持久 Manifest。

至少包含：

- schema_version
- game_id
- source_root
- cache_root
- generated_at
- source_revision
- cache_revision
- executable_candidates
- files
- directory_fingerprints
- mutable_paths
- excluded_paths

路径尽量保存为相对路径。

## 文件字段

每个索引文件至少包括：

- game_id
- relative_path
- size
- modified_utc
- file_role
- quick_hash，可选
- full_hash，可选
- source_revision
- cache_revision

## 文件角色

- executable
- immutable_game_data
- mutable_game_data
- save
- config
- screenshot
- mod
- generated_cache
- temporary
- log
- excluded

## Revision

每个游戏维护：

- NAS Source Revision
- SSD Cache Revision

Revision 相同：

- 不递归扫描 NAS
- 不计算完整 Hash
- 不复制文件
- 直接进入启动路径

Revision 不同：

- 优先处理 Change Log
- 没有 Change Log 时只扫描目标目录
- 完整递归扫描仅作为兜底

## 变化检测优先级

1. 已知 Revision
2. 未来 NAS Agent Change Log
3. SQLite Manifest
4. 目录指纹
5. 目标目录增量扫描
6. 完整递归扫描

## Directory Fingerprint

目录指纹可以包含：

- relative_path
- direct_file_count
- direct_directory_count
- aggregate_size
- latest_known_modified_utc

SMB 目录时间戳不能作为绝对事实，只能作为快速筛选信号。

## FileSystemWatcher

可以作为提示机制，但不能作为 SMB 唯一事实来源。

必须考虑：

- NAS 断线
- SMB 通知丢失
- 程序未运行
- 缓冲区溢出
- 批量解压
- 批量重命名

## 扫描策略

首次导入：

- 完整扫描一次
- 生成 Manifest
- 识别启动程序候选
- 识别可能的可变目录
- 保存索引

后续启动：

- 优先读取持久状态
- 缓存有效则直接启动
- 不重复扫盘

游戏更新后：

- 用户手动刷新
- 或未来 NAS Agent 更新 Revision
- 或检测到关键文件变化后执行目标扫描

## Phase 4 Initial Indexing Contract

Phase 4 implements first full indexing only.

Implemented:

- Recursive source directory scan for an existing `games` row.
- `FileEntry` generation.
- `DirectoryFingerprint` generation including root fingerprint.
- Executable candidate scoring.
- Mutable path candidate detection.
- Engine hint detection.
- Hard and suggested Glob exclusion rules.
- Revision transaction replacement.
- Stable UTF-8 JSON Game Manifest.
- Agent CLI entry: `VniteGameCache.Agent index-game --game-id <id>`.
- Cancellation and failure rollback.

Still not implemented:

- Robocopy
- file copy
- SSD cache population
- actual save sync
- LRU
- full Vnite UI
- NAS Agent
- automatic incremental monitoring

## Path Rules

- SQLite stores game-root-relative paths.
- Internal path separator is `/`.
- Root directory fingerprint uses empty `relative_path`.
- File paths must not be `.`, `..`, rooted, drive-rooted, UNC, or escaping the game root.
- File contents are not hashed in Phase 4.
- Directory traversal is recursive but streaming; file contents are never loaded.

## Reparse Point Rule

Default:

```text
follow_reparse_points = false
```

Junctions, symlinks, and mount points are skipped and recorded as warnings.

## Exclusion Rules

Glob matching is case-insensitive on Windows.

Hard Exclude defaults:

- `$RECYCLE.BIN/**`
- `System Volume Information/**`
- `Thumbs.db`
- `desktop.ini`

Suggested Exclude defaults:

- `*.tmp`
- `*.dmp`
- `logs/**`
- `log/**`
- `cache/**`
- `temp/**`
- `tmp/**`

Suggested excludes can be overridden by pattern. They are not deletion rules.

## Classification Priority

Phase 4 classification priority:

```text
Excluded
Executable
Save
Config
Screenshot
Mod
Temporary
Log
GeneratedCache
ImmutableGameData
```

`.dat`, `.pak`, and `.xp3` are not treated as mutable by default.

## Executable Scoring

Positive signals:

- root executable
- `Game.exe`
- `*-Win64-Shipping.exe`
- executable name resembles game directory
- user configured executable path
- matching Unity `*_Data`

Negative signals:

- uninstaller
- installer
- updater
- crash reporter
- `vc_redist`
- `dxsetup`
- `UnityCrashHandler`

The scanner never overwrites `games.executable_path`.

Launch-time translated executable preference is separate from indexing. It checks only the cached executable directory and recognizes bounded language or translation tokens such as `CN`, `CHS`, `SC`, `zh-CN`, `中文`, `简中`, `汉化`, plus bounded AI translation markers such as `AI`, `Claude`, `GPT`, `Gemini`, `DeepSeek`, `Qwen`, `Opus`, and `Sonnet`. Plain substring matching for `ai` is forbidden because names such as `Main.exe` would be false positives. A translated executable that pairs with the persisted executable after removing translation tokens receives the strongest preference.

## Mutable Path Candidates

Detected only as Manifest suggestions:

- `save/`
- `savedata/`
- `saves/`
- `UserData/`
- `config/`
- `screenshots/`
- `mods/`
- `www/save/`

Phase 4 does not write these into `mutable_paths`.

## Revision Transaction

Successful indexing:

1. scan outside SQLite transaction
2. validate scan result
3. replace FileEntry rows
4. replace DirectoryFingerprint rows
5. increment `source_revision`
6. update `last_full_scan_utc`
7. update task state to `Completed`
8. write Manifest atomically

Cancellation or failure:

- no index replacement
- no revision increment
- no manifest overwrite
- task is marked `Cancelled` or `Failed`

## Phase 5.5 Library Discovery

Library Discovery is separate from Game Indexing.

Discovery:

- scans only first-level child directories under configured library roots
- does not recursively index all games
- detects shallow executable candidates
- detects shallow engine hints
- produces a Dry Run result before import

Ignored first-level folder names:

- `Data`
- `www`
- `StreamingAssets`
- `save`
- `savedata`
- `patch`
- `mods`
- `Binaries`
- `Engine`
- `Content`
- `game`
- `renpy`

Candidate statuses:

- `AutoImportable`
- `NeedsConfirmation`
- `Existing`
- `Ignored`
- `Failed`

Automatic import requires:

- one preferred executable
- no competing executable with meaningful score
- confidence at or above the automatic import threshold

Multiple plausible executables are marked `NeedsConfirmation`.

Stable IDs:

- `candidate_id = candidate-<hash(source_root)>`
- `game_id = vgc-<hash(source_root)>`
- `cache_root = <default_cache_root>\<sanitized-folder>-<short-game-id>`

Repeated scans are idempotent by matching normalized `source_root` and internal `game_id`.

## Launch-Time Source Quick Probe

Before a Wrapper launch uses an already-ready cache, Agent performs a bounded source probe instead of recursively scanning the whole source tree.

The probe checks:

- selected indexed sentinel files by existence, length, and last-write time
- root and shallow directory direct file/directory counts
- source availability

Results:

- `Unchanged`: preserve the existing zero-copy launch path
- `Changed`: run the existing transactional full index before cache preparation
- `InsufficientBaseline`: run a full index to establish a reliable baseline
- `Unavailable`: keep an already-ready offline cache launchable; do not replace the previous index

The quick probe is a bounded change detector, not a replacement for the transactional full index. It intentionally avoids a full NAS walk on every launch.
