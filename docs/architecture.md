# Architecture

## 总体架构

```text
Vnite
├── 游戏库
├── 元数据
├── 游戏启动入口
├── 插件 UI
└── 调用 Cache Agent
        │
        ▼
Windows Cache Agent
├── SQLite 持久索引
├── Game Manifest
├── 文件同步计划
├── Robocopy 调度
├── 进程树监控
├── 存档同步
├── 任务恢复
├── LRU 缓存清理
└── 结构化日志
        │
        ├── NAS 游戏主库
        └── 本地 SSD 缓存
```

## 组件职责

### Vnite Plugin

负责：

- 读取当前游戏信息
- 提供右键菜单和设置界面
- 展示缓存与同步状态
- 发起缓存、启动、刷新索引和清理操作
- 调用 Cache Agent
- 显示任务进度和错误

不得负责：

- 大规模复制
- 完整目录 Hash
- 长期后台任务
- 复杂进程监控
- 数据库迁移

### Windows Cache Agent

建议使用 C# 与当前稳定版 .NET。

负责：

- SQLite 数据库
- 文件索引
- Manifest
- 增量同步
- Robocopy 调度
- 进程树监控
- 存档同步
- NAS 离线队列
- LRU 清理
- 中断恢复
- 日志

第一版优先做普通后台进程，不强制做 Windows Service。

### Phase 5.5 Library Import

Phase 5.5 adds a library import layer before indexing and caching:

```text
Vnite Plugin
  ↓
Configure NAS library root and SSD cache root
  ↓
Dry Run: discover first-level game folders
  ↓
Import selected high-confidence candidates
  ↓
Create internal Game records
  ↓
On launch: Wrapper -> Agent -> Index/Cache -> cache_root executable
```

Rules:

- Library discovery scans only first-level child directories.
- Discovery does not recursively index every game in the library.
- Agent remains an internal helper; users should not manually run Agent CLI.
- Vnite Plugin stores configuration and calls Agent.
- Full indexing and cache copy happen only for imported/selected/launched games.
- MTool has no verified target-game CLI. Optional MTool mode therefore recognizes only MTool-generated BAT structure after caching and rebuilds that verified launch template with the currently configured MTool components; Direct remains the default.

Vnite API note:

- Confirmed plugin capabilities include `PluginDB`, limited `GameDB` local value access, and manifest-driven configuration.
- Phase 5.6 confirmed real Vnite 4.11.0 runtime exposes `GameDB.getAllGames`, `setGame`, and `setGameLocal` through the plugin API object.
- The plugin uses those real methods to create full `gameDoc` and `gameLocalDoc` entries.
- Existence checks must use `getAllGames()[gameId]`; `getGame(gameId)` creates an empty document when missing.
- Existing `gameDoc` metadata is preserved; allowed local launcher/path fields may be updated.
- `pending_manifest` remains only a fallback when required real GameDB methods are unavailable.
- Arbitrary MTool command-line guessing remains unsupported. The supported MTool path is limited to recognized generated BAT templates and never writes MTool `gameLib`.

### IPC

插件与 Agent 的通信优先级：

1. Named Pipe
2. 本地 HTTP
3. 子进程 + stdin/stdout

选择标准：

- 实现简单
- 易调试
- 支持进度事件
- 支持取消
- 不暴露外网端口
- Vnite 插件环境可用

## Wrapper Launcher

如果 Vnite 缺少可靠的启动前/退出后钩子，必须使用 Wrapper Launcher：

```text
Vnite
  ↓
Wrapper Launcher
  ↓
检查缓存与索引
  ↓
启动真正游戏
  ↓
监控完整进程树
  ↓
同步存档
```

Wrapper Launcher 是兼容方案，不得假设 Vnite 一定提供完整生命周期 API。

## Phase 6 Productization Update

Phase 6 keeps the same high-level architecture but adds two product layers:

```text
Vnite Plugin
  ├── manifest configuration
  ├── Phase 6 IPC actions
  └── per-game .lnk launcher creation
        ↓
Wrapper Launcher (WinExe, no console)
        ↓
Windows Cache Agent
  ├── cache management
  ├── quarantine
  ├── manual Save Profiles
  └── save backup/import/export/restore
```

The plugin still does not perform heavy file work. All cache and save file operations remain in the Agent.

Newly imported or migrated plugin-managed games use Vnite `launcher.mode=file` pointing at a generated `.lnk`. The shortcut target is Wrapper with `--game-id`, so Vnite does not use script mode and does not open `cmd.exe`.

When adopting existing Vnite entries, the plugin treats configured adopt roots as safety boundaries, not as metadata sources. It expands Windows mapped-drive aliases exposed by `Get-PSDrive` so a configured UNC root such as `\\NAS-SERVER\Games` can match Vnite paths stored as `G:\...`. A root that starts with a single slash, such as `\Category`, is expanded against known mapped or configured drive prefixes before matching.

## Shared Runtime Settings

The Vnite plugin remains the user-facing configuration owner, but it also writes a shared snapshot for non-plugin processes:

```text
%LOCALAPPDATA%\VniteGameCache\config\settings.json
```

Agent, Wrapper and Control Panel read this snapshot before falling back to environment variables or built-in defaults. The snapshot currently carries database path, cache root, save backup root, cleanup thresholds, log root and command mailbox root. This keeps plugin-triggered operations and Control Panel operations on the same cache, backup and threshold configuration.

## Runtime State Protection

Wrapper owns the real game runtime lock:

```text
Local\VniteGameCache.Game.<sha256(game_id)>
```

After the game process starts, Wrapper calls Agent:

```text
runtime-state --game-id <id> --state Running --process-id <pid>
```

When process monitoring finishes, Wrapper calls:

```text
runtime-state --game-id <id> --state Ready --process-id <pid>
```

Cache cleanup uses both SQLite `cache_status` and the named mutex. If SQLite says `Running` but the mutex is absent, cleanup treats it as stale state and repairs the row back to `Ready` or `NotCached` based on cache directory existence.

## Launch Preference Resolution

Wrapper resolves the final launch target only after Agent has prepared a verified local cache:

```text
Direct
  -> use the persisted game executable

TranslationPreferred
  -> prefer a translated EXE beside the persisted executable
  -> otherwise use the persisted executable

MTool
  -> detect a recognized MTool-generated BAT in the cached game directory
  -> use the currently configured MTool loaders through a VGC-owned Junction to cache_root
  -> if no MTool BAT exists, try TranslationPreferred
```

The resolver never edits the NAS game directory or MTool `gameLib`. MTool BAT files are treated as templates only; stale absolute game paths and stale MTool installation paths are not executed directly. If a BAT is present but cannot be safely parsed or the configured MTool components are incomplete, launch stops with an actionable error instead of silently running the original untranslated game.
