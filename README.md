# Vnite Game Cache

Vnite Game Cache 是一个面向 Windows 与 NAS 游戏库的本地缓存、持久索引和存档同步项目。

主要使用场景：

- 游戏本体存放在 NAS
- Windows 电脑使用本地 SSD 作为持久缓存
- 通过 Vnite 管理本地游戏、封面、元数据和启动入口
- 游戏类型主要包括 Galgame、RPG Maker、Unity 单机游戏、独立游戏和 DRM-free 游戏
- 首次启动时允许完整索引和复制
- 后续重复启动不得递归扫描整个 NAS 游戏目录
- 游戏退出后只同步存档、配置和其他已登记的可变数据

本项目不是简单的文件复制脚本，而是一个 Persistent Game Cache（持久游戏缓存层）。

## 开发入口

Codex 开始工作前必须先阅读：

1. `AGENTS.md`
2. `docs/vision.md`
3. `docs/architecture.md`
4. `docs/indexing.md`
5. `docs/cache.md`
6. `docs/save-sync.md`
7. `docs/reliability.md`
8. `docs/roadmap.md`
9. `tasks/phase-1-research.md`

## 当前状态

Phase 5.10 已完成真实 Renderer 可见与鼠标点击验收：自动导入的插件管理条目可在 Vnite 中搜索、可见、重启后仍存在，并可通过真实鼠标点击进入 Wrapper 缓存链路。

已验证：

- 真实 Vnite 可加载最新 `vnite-game-cache` 插件。
- UNC 游戏库一级目录 Dry Run 可识别自动导入、待确认和忽略项。
- 真实 Vnite `GameDB.setGame` / `setGameLocal` 可创建、更新并持久化游戏条目。
- 新导入的插件管理游戏会写入 Wrapper 启动字段，并通过 `--game-id` 读取 SQLite 中的真实游戏配置。
- 已导入但仍指向 source exe 的插件管理条目可 Dry Run、迁移、备份并恢复原始启动字段。
- 真实 Vnite 主启动服务进入 Wrapper 后，首次启动可完成 NAS 到 SSD 缓存并从 `cache_root` 启动。
- 第二次启动返回 `robocopy_called=false`，继续从 `cache_root` 启动。
- Vnite 重启后 Wrapper 启动字段仍持久化。
- Renderer 搜索 `VGC Renderer Test A` 可找到自动导入条目。
- 真实鼠标点击可见条目后，首次启动缓存到 SSD 并从 `cache_root` 启动。
- 第二次真实鼠标点击返回 `CacheAlreadyReady`，并由 Agent 计划确认 `robocopy_called=false`。

仍需收口：

- 插件还缺少面向最终用户的完整 Dry Run / Import / Migrate 点击界面。
- Vnite `script` 启动模式会打开 `cmd.exe` 窗口，后续需要 UX 收口。
- MTool 仍没有可用的目标游戏 CLI；可选的 MTool 模式优先解析已识别的 BAT 模板并使用当前配置的 MTool 组件。没有可识别 BAT 时回退到翻译模式，该模式也可选择缓存目录中的 `MTool_Game.exe`。Wrapper 已增加 Windows 作业对象以约束本次启动的进程链；MTool_Game.exe 派生进程的 Vnite 停止按钮仍需真实验收。

最新结论见：

- `docs/research/phase-5.10-renderer-visibility-click-results.md`

## Phase 6 Current State

Phase 6 adds productization features around the completed cache launch chain:

- LRU cleanup suggestions and optional auto cleanup that deletes re-creatable game body data, preserves detected user data, and rechecks real SSD free space.
- Quick refresh cache without deleting SSD-only files.
- Safe cache clear to quarantine and restore.
- Expired quarantine cleanup; `0` retention days means never auto-delete.
- Manual Save Profiles with backup, import, export, and restore.
- Wrapper no longer needs a visible console window, and plugin-managed Vnite entries use per-game `.lnk` launchers instead of script-mode `cmd.exe`.
- Wrapper/Agent now mark real game runtime state, and cleanup checks both SQLite state and a per-game named mutex before touching cache.
- Plugin settings are mirrored to `%LOCALAPPDATA%\VniteGameCache\config\settings.json`, so Agent and Control Panel use the same cache root, backup root and cleanup thresholds as the Vnite plugin.
- Wrapper launch preparation now performs a bounded source quick probe. Unchanged games keep the zero-copy fast path, while detected source changes trigger the existing transactional full index before cache preparation.
- Invalid or unsupported shared settings no longer silently fall back to another database or cache root; Control Panel reports the configuration health state.
- Robocopy progress is streamed to the Control Panel with estimated percentage, throughput, remaining files and ETA.
- Automatic adoption is event-first, runs one startup reconciliation, and keeps a low-frequency 15-minute fallback scan.
- Wrapper process supervision keeps discovering delayed child processes while the observed launcher chain is still alive.
- Existing-game adoption writes per-game `.lnk` launchers instead of script launchers, preserves Vnite save paths and other local user fields, and expands mapped-drive aliases such as `\\FNAS\Game` to `G:\` when Windows exposes the mapping.
- Cache cleanup and user-data detection skip Windows reparse points, so Junctions or symlinks inside a cache are not followed during size calculation or protected-data copying.
- Control Panel now includes a self-check report and readable adoption diagnostics. Failed or skipped automatic adoption is written to `%LOCALAPPDATA%\VniteGameCache\diagnostics`, and stale actionable diagnostics are cleared when the latest run is clean.
- Direct remains the default launch mode. Optional `TranslationPreferred` selects a paired ZH/CN/CHS/SC/中文/简中/汉化 or AI-model EXE beside the cached executable, then falls back to a game-local `MTool_Game.exe`; optional `MTool` first uses recognized BAT templates with the current MTool loaders and falls back to `TranslationPreferred` when no BAT is recognized.
- Control Panel shows per-game MTool executable/template/component diagnostics and provides shortcuts to open MTool/MCenter, MTool link root, and selected game MTool template; VGC treats MTool BAT files as templates only.

Still out of scope:

- Automatic bidirectional save synchronization.
- NAS Agent and automatic incremental monitoring.
- Complex embedded UI and generic MTool target-game CLI guessing.

Latest Phase 6 results:

- `docs/research/phase-6-productization-save-profiles-results.md`

## Phase 7 Control Panel UX

Phase 7 focuses on recovery, reconciliation and large-library operations after the core cache launch chain is usable:

- Control Panel shows clickable status cards, common views, status icons, dynamic right-click actions and a multi-select batch bar.
- Large libraries can be filtered by pending, unmanaged, uncached, cached, failed, invalid path, duplicate, ignored, suspended and recently played states.
- Batch actions reuse existing backend paths for adoption requests, cache preparation, diagnostics, cache clearing and management intent changes.
- State backup, restore and last-operation undo are exposed from the settings/diagnostics page.
- Release validation reports are written to `dist/current/validation`.

Latest Phase 7 results:

- `docs/research/phase-7.3-batch-onboarding-operations-results.md`

## Phase 8 Recovery

Phase 8.1 adds explicit recovery for VGC takeover residue:

- `TakeoverResidue` is reported when Vnite still contains Wrapper / `.lnk` / `--game-id` fields but SQLite, PluginDB or shortcut identity is incomplete.
- Control Panel can run Dry Run, clear takeover properties for one or many games, and clear then re-adopt.
- Deletion semantics are split: `仅删除插件记录` is temporary VGC data removal, while `删除插件记录并清除接管属性（推荐）` also restores Vnite launch fields to a re-detectable state.
- Clear takeover keeps Vnite metadata, root path, cover, tags, category, save paths, source game files and saves.

当前控制台删除入口的安全限制：关联 Vnite 的游戏使用“解除 VGC 接管并删记录（保留缓存）”，先清除 VGC 启动字段，再删除 VGC 记录；“仅删孤立 VGC 记录（高级）”只用于没有 Vnite 关联的记录。有 Save Profile 的游戏会被阻止删除记录，以保留存档备份索引。缓存、Manifest、备份 ZIP、日志和诊断报告不会随记录删除；清除缓存需单独执行，并受存档保护与 Quarantine 规则约束。

Latest Phase 8.1 results:

- `docs/research/phase-8.1-clear-takeover-properties-results.md`

Phase 8.2 unifies takeover consistency checks and safe re-adoption:

- Control Panel exposes `检查接管一致性`, `一键安全修复`, and `批量重新接管`.
- The plugin writes `takeover-consistency-latest.json` with environment, shortcut, SQLite, PluginDB and adoption statistics.
- Wrapper shortcuts now include `--game-id`, `--vnite-id`, and `--source-root`, allowing unique Auto Rebind when Vnite IDs change.
- Batch adoption isolates per-game failures instead of aborting the whole batch.
- Shortcut names are sanitized with Windows reserved-name and length handling, with `GameId.lnk` fallback.

Latest Phase 8.2 results:

- `docs/research/phase-8.2-takeover-consistency-results.md`


## Phase 8 Executable Manager

Phase 8.0 adds a per-game Executable Manager:

- Scans EXE/BAT/CMD/LNK candidates for one selected game instead of scanning the whole library.
- Separates launch candidates from the effective `games` launch profile used by Wrapper.
- Prioritizes translation EXE candidates with independent ZH/CN/CHS/SC/中文/简中/汉化/AI/GPT/Claude/DeepSeek-style keywords when requested (case-insensitive for Latin markers).
- Treats MTool `与工具一同启动.bat` as an MTool template, not as a Direct-mode game executable.
- Blocks obvious installers, uninstallers, updaters, crash tools and dangerous helpers from automatic selection.
- Adds an optional versioned VGC MTool Bridge script package using `// @AutoLoad` and `getGameCWD()` for future capability probing; it is not required for cache launch.

Latest Phase 8.0 results:

- `docs/research/phase-8.0-executable-manager-results.md`
