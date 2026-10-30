# Architecture Decisions

## ADR：Wrapper 使用 Windows 作业对象约束本次启动的进程链

Vnite 的停止按钮可能直接结束 Wrapper；仅在 Wrapper 内轮询进程树无法在该时刻清理派生游戏进程。Wrapper 保持现有 EXE、BAT 模板和参数解析方式，仅将本次启动的主进程和辅助进程加入启用 `KILL_ON_JOB_CLOSE` 的 Windows Job Object。进程监控同时查询作业对象内的活跃进程数，避免路径或名称不同的子进程仍运行时过早结束 Wrapper。Wrapper 被结束或正常退出时，系统关闭该作业对象并结束其中仍运行的进程及其子进程。若作业对象创建或分配失败，启动明确报错；极短命进程在分配前退出时会记录 `game_process_job_attached=unavailable`，不能宣称停止链路已通过。此改动不改变公共接口、数据库、Manifest、配置或迁移格式。仍需用实际 Vnite 停止按钮检查 MTool 游戏的完整进程树。

## ADR：翻译 EXE 标记将 zh 与 cn 独立匹配

用户要求 `zh` 和 `cn` 各自可作为翻译 EXE 标记，大小写均可。继续要求分隔符边界，兼容无分隔符的末尾后缀，避免误匹配普通文件名内部字母；保留 `CHS`、`SC` 等既有标记。只调整 Wrapper 的文件名候选判断及对应控制台检测结果，不改变公共接口、数据库或配置格式，现有记录无需迁移。文件名命中只表示启动候选，翻译内容与游戏兼容性仍待实际验证。

## ADR：控制台可翻译状态沿用 Wrapper 解析结果

将原“MTool 能力”列改为“可翻译状态”，同列显示 MTool BAT/EXE 与翻译启动程序候选。翻译候选调用现有 `LaunchPreferenceResolver.SelectPreferredTranslationExecutable`，优先检查已存在的缓存主程序及同目录文件；缓存主程序不存在时才检查源目录。状态只代表文件与命名规则检测，明确标注“启动待验证”，不宣称译文有效或停止按钮可结束其派生进程。扫描继续在后台执行，不修改游戏记录、配置或启动链路；无需迁移。

## ADR：MTool 模式优先使用已识别 BAT，翻译模式可回退到 MTool_Game.exe

用户改为要求 MTool 模式恢复已识别 BAT 优先。仅在没有可识别 BAT 时回退到翻译模式；翻译模式先选择与原主程序配对的翻译 EXE，没有配对候选时可选择同目录的 `MTool_Game.exe`，最后回退原程序。`MTool_Game.exe` 是游戏自带的独立启动程序，不能声称它使用当前配置的新版 MTool 组件。BAT 存在但无效时仍报错，不静默降级。不修改数据库、Manifest、配置或 MTool `gameLib`；现有记录无需迁移。MTool_Game.exe 派生游戏进程的停止行为仍未通过端到端验收。

## ADR：启动前预检复用 Wrapper 解析器

控制台仅对已缓存游戏预览有效启动模式、目标程序和原因。预检调用现有 `LaunchPreferenceResolver`，以不写盘的 Junction 实现代替运行时创建；不启动程序、不更改数据库或游戏文件。未缓存或缓存程序缺失时返回“待缓存后确认”，不依据 NAS 原目录承诺最终结果。BAT 启动时实际 Junction 创建、进程启动和游戏兼容性仍由真实启动验收确认。无需迁移已有记录。

## ADR-001：选择 Vnite 作为主要前端

原因：

- 更适合本地游戏与 Galgame 元数据
- 可以统一管理 NAS 游戏入口
- 允许通过插件扩展
- 项目重点不是替代游戏库前端，而是增加缓存与同步层

## ADR-002：NAS 是主库，SSD 是持久缓存

不采用“启动时移动，退出后完整移回”。

原因：

- 完整回传浪费带宽和机械盘写入
- 容易发生中断和版本冲突
- 游戏本体通常不变
- 存档体积远小于游戏本体

## ADR-003：使用 SQLite 持久索引

原因：

- 避免每次递归扫盘
- 支持中断恢复
- 支持 Revision
- 支持数据库迁移
- 适合单机本地应用

## ADR-004：游戏本体与存档分离

原因：

- 游戏本体大且通常只读
- 存档小但必须双向同步
- 两者一致性和冲突策略完全不同

## ADR-005：第一版使用 Robocopy

原因：

- Windows 原生
- 支持断点和重试
- 不必自研复制引擎
- 可尽快验证核心闭环

限制：

- 不默认 `/MIR`
- 必须封装退出码与日志
- 删除由同步计划控制

## ADR-006：第一版不做 NAS Agent

原因：

- 会显著增加部署和协议复杂度
- 本地 SQLite + 手动刷新已经能解决大部分 Galgame 场景
- 先验证核心价值

后续可增加 Linux inotify Agent 与 Revision Change Log。

## ADR-007：Wrapper Launcher 作为兼容方案

如果 Vnite 缺少完整启动生命周期 API，使用 Wrapper Launcher。

不得为了追求“纯插件”而依赖不存在或不稳定的内部接口。

## ADR-008：Phase 3 使用 SQLite + 显式 SQL

原因：

- 数据模型较小，显式 SQL 更容易审计迁移和事务。
- 避免为早期原型引入 ORM 复杂度。
- 便于在 Agent CLI、Wrapper 回归和测试中直接使用同一持久层。

迁移方式：

- 使用 `migration_history` 记录 schema version。
- 遇到未来 schema version 时拒绝启动。
- 迁移失败不删除或重建数据库。

## ADR-009：数据库枚举使用稳定字符串

原因：

- 避免 C# enum 数值重排破坏持久数据。
- Manifest 与 SQLite 更易人工检查。

影响：

- SQLite 使用 CHECK constraint 限制合法值。
- JSON Manifest 中 enum 也输出字符串。

## ADR-010：路径原样保存，FileEntry 只保存相对路径

原因：

- UNC、中文、空格和长路径必须可 round-trip。
- FileEntry 应与 `source_root` 解耦，便于后续缓存路径映射。

规则：

- `games.source_root`、`games.cache_root` 保存用户配置原始语义。
- `file_entries.relative_path` 使用 `/`。
- 文件相对路径不得是绝对路径、UNC、盘符路径或路径逃逸。

## ADR-011：Agent 启动时只初始化数据库并记录 PrepareLaunch 任务

原因：

- Phase 3 只验证数据库初始化集成。
- 不在启动链路中提前实现扫描、复制或同步。

影响：

- Phase 2.5 Wrapper → Agent → TestGame 链路保持兼容。

## ADR-012：Phase 4 使用独立 Indexing 工程

原因：

- 扫描、分类、Manifest 生成和持久化提交需要清晰边界。
- Agent 只负责 CLI 入口和进程交互，不承载扫描规则。
- Persistence 仍只提供 SQLite 基础能力。

影响：

- 新增 `VniteGameCache.Indexing`。
- 新增 `VniteGameCache.Indexing.Tests`。

## ADR-013：首次索引使用事务替换而不是增量修改

原因：

- Phase 4 没有 NAS Agent Change Log。
- 首次完整扫描的正确性优先于增量复杂度。
- 失败和取消必须保留旧索引。

规则：

- 扫描在事务外完成。
- 成功后短事务替换 `file_entries` 和 `directory_fingerprints`。
- 只有事务成功才递增 `source_revision`。
- 失败或取消不覆盖旧 Manifest。

## ADR-014：Mutable Path 候选只写入 Manifest

原因：

- 自动写正式 `mutable_paths` 会污染用户配置。
- Save/config/mods/screenshots 目录需要用户确认同步策略。

影响：

- Phase 4 只输出 `mutable_path_candidates`。
- Phase 5 仍不得假设候选就是正式同步配置。

## ADR-015：Phase 5 使用 Robocopy 执行 NAS 到 SSD 缓存复制

原因：

- Windows 原生命令，支持 UNC、中文、空格和断点续传。
- `/Z` 能支持取消后的再次补齐。
- 第一版不需要自研复制引擎。

参数：

```text
/E /Z /FFT /R:2 /W:2 /MT:8 /COPY:DAT /DCOPY:T /XJ /NP /TEE /LOG:<path>
```

限制：

- 不默认使用 `/MIR`、`/PURGE`、`/MOVE`、`/MOV`。
- 不删除 cache 端多余文件。
- 不修改 NAS。
- Robocopy exit code `0..7` 视为成功或非致命差异，`>=8` 视为失败。

## ADR-016：cache_revision 只在 Fast 验证成功后提升

原因：

- Robocopy 成功退出不等于缓存一定可启动。
- 取消或失败后误标 Ready 会导致 Wrapper 启动损坏缓存。

规则：

- 成功复制后执行 Fast 验证。
- Fast 验证通过后设置 `cache_status=Ready` 和 `cache_revision=source_revision`。
- 取消设置 `cache_status=Incomplete`。
- Robocopy 或验证失败设置 `cache_status=Failed`。

## ADR-017：Wrapper 使用 Agent 返回的 cache_root 启动路径

原因：

- Vnite 传入 Wrapper 的原始路径通常是 NAS `source_root`。
- Phase 5 目标是缓存 Ready 后从 SSD `cache_root` 启动。

迁移方式：

- `PrepareLaunchResponse` 增加可选字段 `launch_executable_path` 和 `launch_working_directory`。
- 旧 Agent 响应不包含这些字段时，Wrapper 仍使用原始请求路径。
- 当 Agent 返回缓存路径时，Wrapper 重新验证文件和工作目录后启动。

## ADR-018：Phase 5.5 拆分 Library Discovery 与 Game Indexing

原因：

- 用户需要一次配置 NAS 根目录，而不是逐个游戏填写 `source_root`。
- 直接递归索引整个 NAS 游戏库会重新引入大规模扫盘问题。

规则：

- Library Discovery 只扫描一级子目录。
- Dry Run 先输出将创建、更新、忽略和需要确认的对象。
- Game Indexing 只对确认导入、自动高置信导入或用户启动的游戏执行。

## ADR-019：内部 game_id 和 cache_root 基于 source_root 稳定生成

原因：

- 文件夹名可能变化。
- 显示名称可能被用户编辑。
- 同名游戏必须避免 cache 冲突。

规则：

- `game_id = vgc-<hash(source_root)>`
- `cache_root = <default_cache_root>\<sanitized-folder>-<short-game-id>`
- 重复扫描优先按 normalized `source_root` 匹配。

## ADR-020：Vnite 自动创建能力采用运行时探测和 fallback

原因：

- Phase 1 只确认了 `PluginDB` 和有限 `GameDB` local value API。
- 未确认稳定公开的 `GameDB.createGame` / `addGame` / `importGame` API。

规则：

- 插件运行时探测 `GameDB.createGame/addGame/importGame`。
- 若不存在，插件只创建内部 Game 记录并记录 pending import 结果。
- 不伪造 Vnite API，不覆盖用户手动编辑的元数据。

## ADR-021：MTool 不猜测命令行参数

原因：

- MTool CLI 兼容性必须基于真实验证。
- 错误参数可能导致无法启动或启动错误游戏。

规则：

- 只验证用户配置的真实 MTool 可执行文件。
- 尝试 `--help`、`/?`、`/help`。
- 未检测到稳定 CLI 时，不生成自动 MTool 启动命令。

## ADR-022：Phase 5.6 使用真实 GameDB setGame/setGameLocal 导入

原因：

- 真实 Vnite 4.11.0 运行时验证 `api.GameDB` 暴露 `getAllGames`、`setGame`、`setGameLocal`。
- 未发现 `createGame` / `addGame` / `importGame` 作为插件 GameDB 方法。
- `pending_manifest` fallback 不是自动导入完成。

规则：

- 自动导入使用完整 `gameDoc` 和 `gameLocalDoc` 结构。
- 是否已存在必须用 `getAllGames()[gameId]` 判断。
- 不用 `getGame(gameId)` 判断存在性，因为缺失时它会创建空文档。
- 已存在 `gameDoc` 时不覆盖用户可见元数据。
- 允许更新本地 launcher/path 这类插件管理字段。

## ADR-023：MTool 状态拆分为可执行验证与启动协议验证

原因：

- Phase 5.6 找到 `E:\MTool\Tool\MTool.exe`，但 `--help`、`-h`、`/?` 均未返回 CLI 帮助。
- 未发现 `mtool` 协议注册或文件关联。
- `MTool.exe <game.exe>` 未向 `gameLib` 自动添加测试游戏。

规则：

- `ExecutableValidated` 只表示 MTool 路径存在。
- `LaunchProtocolUnknown` 表示不能自动传入游戏。
- 只有实测可把缓存中的游戏交给 MTool 并成功启动，才能标记 `LaunchSucceeded`。
- 在此之前不得宣称 MTool 自动启动兼容。

## ADR-024：Phase 5.8 不把源路径启动视为缓存启动完成

原因：

- 真实 Vnite UAT 证明当前自动导入可以创建并持久化游戏条目。
- 但导入后的 `launcher.fileConfig.path` 仍是 NAS/source executable。
- 普通 Vnite 点击这种条目不会调用 Wrapper，也不会触发 cache prepare。
- 后端 Wrapper/Agent 可以从 `cache_root` 启动，但这不等同于真实 Vnite 点击已完成。

规则：

- 只有 Vnite 游戏条目真实启动 Wrapper，并由 Wrapper/Agent 返回 `cache_root` 启动路径时，才能标记点击缓存启动完成。
- 导入后的 launcher/path 字段必须被视为插件管理字段，但不能覆盖用户元数据。
- 首次点击必须自动保证索引存在，或给出明确的可操作错误。
- Phase 6 不应基于未完成的点击缓存启动链路开始用户级存档同步。

## ADR-025：Vnite 启动入口改为 Script Wrapper + game_id

原因：

- Phase 5.9 检查真实 Vnite launcher 代码后确认 file launcher 使用 `shell.openPath(fileConfig.path)`，不会传递 `fileConfig.args`。
- Wrapper 必须收到内部 `game_id`，否则无法从 SQLite 读取真实 `source_root`、`cache_root`、可执行文件、工作目录和参数。
- 继续让 Vnite 直接启动 NAS/source exe 会绕过缓存链路。

规则：

- 插件管理的 Vnite 游戏使用 `launcher.mode=script`。
- `scriptConfig.workingDirectory` 必须是 Wrapper 所在目录。
- `scriptConfig.command` 必须执行 `VniteGameCache.Wrapper.exe --game-id <internal-game-id>`。
- `fileConfig.path`、`fileConfig.args` 和 `path.gamePath` 可同步写入 Wrapper 信息用于兼容和可见性，但不作为真实参数传递依据。
- Vnite 只传 `game_id`；路径、参数和缓存状态只从 SQLite 读取。
- Wrapper 缓存失败时不得静默回退到 NAS/source exe。

## ADR-026：插件管理启动字段必须可迁移、备份和恢复

原因：

- Phase 5.5/5.8 已经创建过指向 source exe 的插件管理条目。
- 直接覆盖本地启动字段会丢失原始启动配置，影响回滚和排障。
- 非本插件管理的 Vnite 游戏不能被修改。

规则：

- 迁移只处理带 `managed_by_vnite_game_cache=true` 标记的条目。
- Dry Run 不写入任何 Vnite 字段。
- Apply 前必须把原始 local launch document 保存到 PluginDB。
- Restore 只恢复有备份的插件管理条目。
- 不覆盖用户标题、封面、标签或其他用户元数据。

## ADR-027：Wrapper 可在 Ready cache 情况下跳过 source 存在性检查

原因：

- 用户目标包含 NAS 离线但 SSD 缓存已就绪时仍可启动。
- 数据库驱动的 `--game-id` 启动不需要 Vnite 传 source 路径。
- 如果 cache revision 已匹配且 cache executable 存在，要求 source root 在线会制造不必要失败。

规则：

- 默认 `LaunchRequest.require_source_executable=true`，保持旧请求兼容。
- `--game-id` 生成的请求设置 `require_source_executable=false`。
- 只有当缓存计划需要复制时，Cache Plan 才要求 source root 存在。
- NAS 离线且 cache 不完整时仍必须失败，不允许回退 source。

## ADR-028：Vnite Renderer 可见性使用 GameDB change + game:added 刷新

原因：

- Vnite Renderer 通过 PouchDB `db:doc-changed` 同步 `game` 和 `game-local` store。
- Vnite Librarybar 还监听 `game:added`，并调用 `refreshGameList()` 重新计算列表显示。
- Phase 5.9 插件直接写 `GameDB.setGame` / `setGameLocal`，但没有发出 `game:added`，导致 late import 后 Renderer 可见性不稳定。

规则：

- 插件创建新的 Vnite `gameDoc` 后必须通过真实插件 API `api.eventBus.emit('game:added', ...)` 通知 Vnite。
- 对已存在的插件管理条目，更新 local launch 字段后也可以重发 `game:added` 作为刷新信号。
- 不使用 DOM 注入、私有 renderer store 修改或伪造 UI 条目。
- 可见性诊断可以读取 `GameDB`、`GameDBLocal` 和配置过滤条件，但不得覆盖用户元数据。

## ADR-029：UAT 配置不得依赖 validation-config 覆盖已有 PluginDB

原因：

- 早期 `validation-config.json` 用于 UAT 首次 seed，容易在新安装或重装时把本机测试路径写回 PluginDB。
- 产品插件不得依赖测试配置文件填充用户配置。
- Phase 5.10 UAT 发现旧 `libraryRoot` / `wrapperLauncherPath` 会导致导入和迁移仍使用旧阶段路径。

规则：

- 产品插件不再读取或打包 `validation-config.json`。
- UAT 或迁移验证必须显式设置本次测试需要的 `libraryRoot`、`defaultCacheRoot`、`wrapperLauncherPath` 和 `agentStubPath`。
- 不把测试用强制覆盖逻辑加入产品插件。

## ADR-030：Phase 6 使用手动 Save Profile 而不是自动同步

原因：

- 自动双向同步需要退出检测、冲突策略、Pending Queue 和更完整 UI，超出本阶段范围。
- 手动备份/导入/导出/恢复可以先验证用户路径模型和数据安全策略。
- 导入和恢复前自动备份当前存档，能避免静默覆盖。

规则：

- 不自动同步存档。
- 低置信度候选必须由用户确认后才写入正式 Save Profile。
- 导入前和恢复前必须自动备份当前目标路径。
- ZIP 导入必须拒绝路径逃逸。
- 非 `Absolute` Save Profile 的 `path_template` 必须解析在对应根目录内。
- 同一游戏、同一 `source_type`、同一解析路径重复添加时更新已有 Save Profile，不创建重复记录。

## ADR-031：缓存删除默认进入 quarantine

原因：

- 游戏目录内可能存在存档、配置、截图、mod 或用户手动放入的文件。
- 直接删除不可恢复，不适合早期产品化阶段。

规则：

- 安全清除默认检测可能存档和 Save Profile。
- 检测到风险时默认阻止。
- 强制清除也必须移动到 quarantine，而不是永久删除。
- 恢复时目标 cache root 必须不存在或为空。
- `config`、`screenshots`、`mods` 等非存档用户目录只在 `protect_config_files=true` 时作为阻止清理的风险处理。
- 默认关闭配置文件保护，避免大量游戏因普通配置目录导致清理和重建过度保守。
- 控制台维护入口也必须复用安全清除路径，不得为了清理测试记录直接递归删除缓存目录。

## ADR-032：Vnite 启动入口从 script 切换为 .lnk file launcher

原因：

- Phase 5.9 的 script launcher 能传递 `--game-id`，但真实 Vnite 会打开 `cmd.exe`。
- Vnite file launcher 不传 `fileConfig.args`，但 Windows `.lnk` 可以持久保存 target arguments。
- `.lnk` 由插件生成，目标为 Wrapper，参数只包含内部 `game_id`。

规则：

- 新导入或迁移的插件管理游戏使用 `launcher.mode=file`。
- `fileConfig.path` 和 `path.gamePath` 指向每游戏 `.lnk`。
- `.lnk` 工作目录为 Wrapper 目录。
- 非插件管理游戏不修改。
- 原 ADR-025 的 script 方案仅作为历史方案，不再作为 Phase 6 首选。

## ADR-033：运行中保护同时使用 SQLite 状态和命名 Mutex

原因：

- 只看 `games.cache_status=Running` 会受 Wrapper 崩溃、Agent 写入失败或旧版本状态影响。
- 只看进程名容易误判 NW.js、RPG Maker 等多进程游戏。
- Wrapper 已经是启动链路的唯一入口，最适合持有 per-game 运行锁。

规则：

- Wrapper 运行游戏期间持有 `Local\VniteGameCache.Game.<sha256(game_id)>`。
- Wrapper 通过 Agent `runtime-state` 将游戏状态写为 `Running`，退出后写回 `Ready`。
- 缓存清理必须同时检查 SQLite 状态和命名 Mutex。
- 如果 SQLite 为 `Running` 但 Mutex 不存在，视为 stale running state 并修复，避免清理功能永久卡住。

迁移方式：

- 不新增数据库字段。
- 旧记录继续可用。
- 新 Wrapper/Agent 生效后开始写入真实运行状态。

## ADR-034：插件配置写出共享 settings 快照

原因：

- 插件配置保存在 Vnite `PluginDB`，但 Agent、Wrapper 和 Control Panel 不能直接读取。
- 之前 Agent/控制台各自使用环境变量、LocalAppData 或硬编码默认值，导致缓存目录、备份目录和阈值不一致。

规则：

- 插件在激活和调用 Agent 前写出 `%LOCALAPPDATA%\VniteGameCache\config\settings.json`。
- Agent、Wrapper、Control Panel 优先读取该快照。
- 插件处理控制台命令时也优先读取共享快照中的 `commands_root`，避免控制台和插件使用不同命令目录。
- 控制台写入命令文件必须使用临时文件加原子替换；插件遇到损坏命令 JSON 时记录错误并停止处理该请求。
- `VGC_DB_PATH`、`VGC_MIN_FREE_BYTES` 等环境变量继续作为调试覆盖。
- 快照缺失时保持旧默认路径，保证升级不阻塞启动。
- 快照存在但 JSON 无效、无法读取或 schema 不受支持时，Agent/Wrapper/Control Panel 必须报错，不得静默回退到另一套数据库或缓存目录。
- Control Panel 必须显示共享配置健康状态，便于定位配置损坏。

迁移方式：

- 不修改 Vnite `PluginDB` 键名。
- 用户保存一次插件配置或重启 Vnite 后会自动生成共享快照。

## ADR-035：同名进程追踪必须限定在对应 cache_root

原因：

- Galgame、RPG Maker、NW.js 游戏常见 `Game.exe`、`nwjs.exe` 等同名进程。
- 只按进程名和启动时间窗口匹配，会把另一个游戏的同名进程误认为当前游戏，导致 Vnite 状态、运行中保护或退出判断不准。
- Wrapper 已能从 SQLite 读取当前游戏的 `cache_root`，可以把同名进程候选限定在当前缓存目录内。

规则：

- 初始进程和已观察到的子进程继续按进程树追踪。
- 通过额外进程名匹配到的候选进程，必须满足可执行文件路径位于当前游戏 `cache_root` 下。
- 无法读取候选进程可执行文件路径时，不把它作为“同名进程”纳入当前游戏。
- 如果数据库不可用，Wrapper 退回使用启动工作目录作为最小路径边界。

## ADR-036：Agent 启动时恢复过期 Checking / Copying / Verifying 任务

原因：

- Windows 重启、Vnite 崩溃或 Agent 被强制结束后，数据库里可能残留 `Checking` / `Copying` / `Verifying` 任务。
- 这些任务不是当前真实运行任务，会误导控制台和后续维护判断。
- 直接把所有活动任务中断风险太高，大型游戏复制时间可能很长。

规则：

- Agent 每次初始化数据库时检查过期活动任务。
- 默认只把超过 6 小时未更新的 `Checking` / `Copying` / `Verifying` 标记为 `Interrupted`。
- 可通过 `VGC_STALE_ACTIVE_TASK_MINUTES` 调试覆盖阈值。
- 不自动改 `Running` 或其他状态，避免误伤真实启动链路。
- 已创建的启动准备任务必须在所有错误出口写入终态，不能依赖过期任务恢复作为正常错误处理。

## ADR-037：自动接管使用事件优先加低频兜底

原因：

- Vnite 已确认公开 `eventBus` 存在，但未确认稳定的 before-launch 或游戏路径保存前拦截 hook。
- 高频定时批处理会在大型 Vnite 库中重复读取 GameDB，并造成无意义启动噪音。
- 只依赖事件触发风险较高：不同 Vnite 版本可能不暴露 `eventBus.on`，并且 `game:launched` 已经晚于启动前缓存。

规则：

- 插件激活后执行一次延迟启动协调扫描。
- 如果运行时 `eventBus.on` 可用，插件监听 `game:added` 和 `game:launched`，延迟 5 秒触发一次接管扫描，作为主加速路径。
- 保留每 15 分钟一次的低频协调扫描，处理事件缺失或不同 Vnite 版本兼容问题。
- 自动接管批量大小严格使用用户配置，并限制在 1 到 100；无效值回退为 25。
- Agent `game-upsert` 必须按 `game_id` 和 `vnite_game_id` 合并已有记录；存在旧内部 `vgc-*` 记录时保留内部主键，避免同一 Vnite 条目触发唯一约束失败。
- 事件触发不得替代 Wrapper 启动链路，不承担启动前缓存。
- 忽略插件自身发出的 `game:added`，避免自触发循环。
- 如果 `eventBus.on` 不可用，只记录诊断并继续使用定时批处理。

## ADR-038：自动 LRU 必须真实释放 SSD 空间

原因：

- 将完整缓存移动到同一 SSD 的 quarantine 只改变目录位置，不会增加 Windows 可用空间。
- 自动清理的目标是防止 SSD 被缓存占满，必须以真实 `DriveInfo.AvailableFreeSpace` 为准。
- 手动安全清除和自动 LRU 的用户意图不同：手动清除优先可恢复，自动 LRU 优先释放可重新生成的游戏本体空间。

规则：

- 自动 LRU 统计 Active Cache、Quarantine、Staging 和 InProgress。
- 自动 LRU 同时使用缓存容量上限和最低剩余空间阈值，取两者需要释放空间的最大值。
- 自动 LRU 每删除一个候选后重新读取真实磁盘可用空间，达到目标立即停止。
- 自动 LRU 不把完整游戏本体长期移动到同盘 quarantine；只保留检测到的用户数据。
- Save Profile、常见存档目录、配置、Mod、截图和 manifest 外新增文件必须先保护，保护失败则停止清理。
- 自动 LRU、手动清除和过期 quarantine 删除在任何统计或删除前必须拒绝空路径、盘符根目录、共享根目录和 ReparsePoint 目标。
- 手动安全清除继续将完整缓存移动到 quarantine，并允许恢复。
- `quarantine_retention_days=0` 表示永不自动永久删除；大于 0 时过期 quarantine 可自动永久删除。

迁移方式：

- 不新增数据库 schema。
- 继续复用 `quarantine_entries` 记录手动隔离和自动 LRU 保留的用户数据。
- 旧 quarantine 记录会按保留期规则参与后续永久删除。

## ADR-039：Ready 缓存启动前使用有界源目录快速探测

原因：

- 只比较数据库 revision 无法发现用户在 NAS 上直接更新的游戏文件。
- 每次启动递归扫描大型 NAS 游戏目录会破坏零复制快速启动体验。

规则：

- Agent 在缓存准备前检查有限的索引哨兵文件和浅层目录计数。
- 检测到变化或基线不足时，调用现有事务式完整索引，再执行缓存计划。
- 源目录离线时不得破坏旧索引，也不得阻止已验证 Ready 缓存离线启动。
- 快速探测不承诺识别所有深层且不影响哨兵的变化；它是低成本防陈旧机制，不替代完整索引。

## ADR-040：复制进度使用原子快照并明确标记估算值

原因：

- 首次缓存大游戏时没有可见进度，用户容易误判程序卡死。
- 多线程 Robocopy 没有稳定的结构化进度协议。

规则：

- Agent 使用 Windows 进程 I/O 计数获取实际写入字节，并解析 Robocopy 输出补充当前文件信息。
- 任务进度和日志旁 `.progress.json` 快照持续更新；写入必须原子替换。
- 百分比、文件数和 ETA 在复制期间标记为估算；验证完成后写入精确索引总量。
- 进度采集失败不得使复制任务失败。

## ADR-041：延迟子进程发现跟随已观察进程生命周期

原因：

- 部分启动器、区域工具和 NW.js 中继会在初始发现窗口结束后才创建真实游戏子进程。
- 固定时间窗口结束后立即停止发现会让 Vnite 过早判定游戏退出。

规则：

- 初始发现窗口结束后，只要已观察到的进程仍存活或处于稳定退出宽限期，Wrapper 继续发现其后代。
- 额外同名进程仍必须满足 `cache_root` 路径边界，不放宽防误判保护。

## ADR-042：Existing-game adoption uses `.lnk` and preserves local user fields

原因：

- Phase 6 文档要求插件管理游戏使用 per-game `.lnk` file launcher，避免 Vnite script launcher 弹出 `cmd.exe`。
- 旧实现仍生成 script launcher，导致已接管条目会被反复判断为需要重写。
- Vnite local doc 里还包含存档路径、截图路径和 Magpie 等用户本地设置，接管时不能清空。

规则：

- 新导入、迁移和接管已有游戏都写入 `launcher.mode=file`，`path.gamePath` 与 `launcher.fileConfig.path` 指向 per-game `.lnk`。
- `.lnk` 目标为 Wrapper，参数只包含 `--game-id=<canonical-game-id>`，不得使用单引号或默认附加 `--vnite-id` / `--source-root`。
- 接管基于现有 local doc 局部修改启动字段，保留 `path.savePaths`、`path.screenshotPath`、`launcher.useMagpie` 和其他非接管字段。
- 接管根目录匹配会展开 Windows mapped-drive alias，例如 `\\FNAS\Game` 对应 `G:\` 时两者都可匹配。
- 单斜杠开头的分类根，例如 `\Gal SLG Game`，会按已知盘符展开为 `G:\Gal SLG Game` 这类候选。

## ADR-043：Cache cleanup must not follow reparse points

原因：

- Junction、symlink 或其他 ReparsePoint 可能指向缓存目录外。
- LRU 统计、Manifest 外新增文件检测或用户数据复制如果跟随链接，可能误统计、误保护甚至触碰不属于当前游戏缓存的文件。
- Robocopy 已使用 `/XJ`，缓存管理侧也必须保持同一安全语义。

规则：

- 缓存大小统计、Manifest 外新增文件检测和 protected user data 复制都跳过 ReparsePoint。
- 如果待保护路径本身是 ReparsePoint，自动 LRU 停止并保留原缓存，交由人工处理。
- 删除缓存根时仍只作用于当前 cache root；ReparsePoint 目标不得作为当前游戏缓存内容处理。

## ADR-044：用户级硬化优先输出可诊断状态

原因：

- Vnite 插件、Agent、Wrapper 和控制台跨进程运行，真实问题常表现为“没有接管”“没有启动”“看起来无响应”。
- 继续盲目增加自动行为会放大误接管和误删风险。
- 对最终用户更有价值的是明确知道当前插件是否安装、配置是否一致、接管为什么跳过、下一步要修什么。

规则：

- 控制台提供“自检报告”，只检查配置、文件存在性、日志和诊断状态，不启动游戏、不复制缓存、不删除文件。
- 自动接管每次运行都写入 `adopt-existing-latest.json`，并带有 `status`、`reason`、`message` 和 `error`。
- 只有 `needs_action`、`error` 或 `unavailable` 状态才保留 `adopt-existing-actionable.json`；最新运行正常时删除旧 actionable 诊断，避免旧问题误导用户。
- 控制台“打开接管诊断”输出面向用户的中文报告，列出接管根目录、候选、跳过原因和可操作建议。
- 控制台打开目录不得自动创建缺失的游戏目录；缺失目录只提示，不写入文件系统。

迁移方式：

- 不修改数据库 schema。
- 不改变 Wrapper 启动链路、缓存复制策略或 Vnite 元数据保护规则。
- 旧诊断文件会在下一次插件接管扫描后被新格式覆盖。

## ADR-045：启动偏好在缓存完成后解析，Direct 保持默认

原因：

- 同一游戏目录可能同时包含日文原版、中文或 AI 翻译 EXE。
- MTool 生成的 `与工具一同启动.bat` 会保存 MTool 安装路径和 `MTool_Game_Tmp` Junction；游戏移动后这些绝对路径可能过期。
- 本机 MTool 0.67.1 的真实文件证明 BAT 可作为启动模板，但没有可验证的“传入游戏并自动更新”命令行协议。
- 直接执行陈旧 BAT 可能从旧目录启动，破坏“真实游戏必须从 cache_root 启动”的保证。

规则：

- `Direct` 继续作为默认启动模式，行为保持不变。
- 新增 `TranslationPreferred`：只在 Agent 返回的缓存可执行文件所在目录选择翻译 EXE；没有可靠候选时使用原缓存 EXE。
- `MTool` 改为 MTool BAT 优先模式：只识别带有 MTool 启动结构的 BAT，不执行任意批处理。
- MTool 模式使用用户配置的当前 MTool 根目录下 `loaders\\inject.exe`、对应 loader DLL 和 `nw.exe`，不沿用 BAT 中陈旧的 MTool 路径。
- MTool 模式创建 VGC 自有、仅指向当前 `cache_root` 的 ASCII Junction，不修改 MTool 全局 Junction、`gameLib` 或 NAS 文件。
- 存在 MTool BAT 但解析、组件验证或 Junction 创建失败时必须中止并记录错误；没有 MTool BAT 时可退回 `TranslationPreferred`。
- 翻译关键字必须按边界匹配；`AI` 不允许使用不区分边界的普通子串匹配。

迁移方式：

- 共享 `settings.json` schema 1 增加可选 `default_launch_mode` 和 `mtool_executable_path`，旧设置文件继续按 `Direct` 运行。
- 不修改 SQLite schema，不改已保存的 `games.executable_path`。
- 旧的 `CustomLauncher` 配置值按 `Direct` 兼容处理，不再显示为新用户选项。

## ADR-046：清除接管属性与删除插件记录必须分离

原因：

- 真实库中已经出现 SQLite / PluginDB 记录被删除，但 Vnite GameLocal 仍残留 Wrapper、`.lnk`、`--game-id` 和追踪字段的状态。
- 如果继续把“删除插件记录”“永久忽略”“解除接管”和“重新检测”混用，用户无法判断当前操作会删除什么，也会导致自动接管误判。
- 用户需要临时清除错误接管并重新生成，而不是永久跳过该游戏。

规则：

- `仅删除插件记录` 只删除 VGC SQLite 记录和可确认归属的 VGC 快捷方式，不代表永久忽略。
- `删除插件记录并清除接管属性（推荐）` 会同时清理 Vnite GameLocal 中的 VGC 启动、脚本、追踪和 Wrapper 字段。
- `永久忽略` 只表示长期跳过该游戏的管理意图，必须和删除、清除、解除接管保持独立。
- 清除接管属性必须保留 Vnite 游戏条目、标题、封面、标签、分类、游戏根目录、截图路径、存档路径和源游戏文件。
- 正常接管必须同时具备 Vnite GameLocal、SQLite、PluginDB 和 `.lnk` / `--game-id` 四条链路；任一缺失即标记为接管残留。

迁移方式：

- 不修改 SQLite schema。

## ADR：删除游戏记录前保护存档索引并说明残留

原因：删除 `games` 行会级联删除 Save Profile 和备份索引，但磁盘上的备份 ZIP 仍存在；仅删除 SQLite 与快捷方式还可能让 Vnite 留下失效的启动字段。“完全重置”实际上沿用仅删除记录流程，名称会误导用户。

规则：

- 有 Save Profile 的游戏不得执行删除 VGC 记录，直到有可恢复的单游戏存档索引迁移方案；已有备份 ZIP 不自动删除。
- 与 Vnite 条目关联的游戏通过既有“清除接管属性并删除记录”路径处理，先清理 GameLocal 与 PluginDB，再删除 SQLite；仅删除记录保留给没有 Vnite 关联的孤立记录。
- 清除接管请求中的显式选中游戏同时包含健康接管项与接管残留项；不能只从残留列表执行，否则健康游戏会出现“请求已提交但零处理”。
- 对压缩包来源同时清理 `archive_path` 的 PluginDB 反向映射；Agent 删除失败必须作为失败报告，不得声称已删除。
- 删除预览列明 SQLite、快捷方式、缓存、Manifest、存档备份、Vnite 条目与 NAS 源文件的处理范围；移除误导性的“完全重置”别名。
- 清除缓存继续复用现有安全清理和 Quarantine，不把它暗中并入记录删除。

影响与迁移：不更改数据库、Manifest、配置或 IPC 格式；已有游戏数据无需迁移。旧菜单名称只发生界面文案变化。
- 新增接管残留诊断 JSON，由控制台展示并引导用户执行单个或批量清除。
- 批量清除前必须由用户确认；运行中的游戏不可清除。

## ADR-047：接管一致性检查统一到 TakeoverConsistencyService

原因：

- 真实库中存在 Vnite 启动字段、SQLite 记录、PluginDB 映射和 `.lnk` 快捷方式不一致的组合状态。
- 单独执行“接管”“清除残留”或“删除记录”不能说明 Wrapper、settings、SQLite 和 PluginDB 是否来自同一运行环境。
- 批量接管不能因为单个游戏快捷方式创建失败或启动程序待确认而中断后续游戏。

规则：

- 控制台统一提供 `检查接管一致性`、`一键安全修复` 和 `批量重新接管`。
- 诊断结果统一写入 `takeover-consistency-latest.json`，包含 Wrapper 路径、Wrapper Hash、settings、SQLite、PluginDB、统计和逐项状态。
- Wrapper Auto Rebind 只允许按 Game ID、Vnite ID 或 SourceRoot 唯一命中；禁止按游戏名自动绑定，禁止创建重复 Game ID。
- 快捷方式必须使用统一清理后的文件名；可读名称创建失败时回退到 `GameId.lnk`。
- 批量接管逐游戏隔离失败并输出统计，不允许静默跳过真正失败项。

迁移方式：

- 不修改 SQLite schema。
- Phase 8.2.2 后新快捷方式只保留 `--game-id=<canonical-game-id>`；旧 `--vnite-id` 和 `--source-root` 参数仅作为 Wrapper 兼容读取，不再由插件默认生成。
- 不删除 NAS/G 盘源游戏、Vnite 标题/封面/标签、存档路径或用户存档。

## ADR-048：快捷方式参数契约统一为 canonical `--game-id=<guid>`

原因：

- 真实 `.lnk` 中出现 `--game-id '57137a85-c50a-40d4-951e-d0fa9978be83'`，Windows 不会把单引号当作标准命令行引号，Wrapper 收到带单引号的 ID 后查询失败。
- Phase 8.2 曾计划在快捷方式中附加 `--vnite-id` 和 `--source-root`，但这会扩大参数契约，增加迁移和解析维护面。
- Control Panel、Wrapper 和一致性检查必须使用同一套参数解析与 GameRecord 解析逻辑，避免入口行为不一致。

规则：

- 新建和重建的 VGC 快捷方式统一使用 `--game-id=<canonical-guid>`。
- `LaunchArgumentCodec` 是唯一参数构造和解析入口；解析兼容空格形式、双引号 GUID、旧单引号 GUID 和旧双引号参数键，但输出必须规范化为 Guid `D` 格式。
- `WrapperGameResolver` 是 Wrapper、Control Panel 和一致性检查共同使用的 GameRecord 解析入口；未经规范化的原始参数不得直接查询 SQLite。
- 快捷方式创建或迁移后必须回读 `TargetPath`、`Arguments` 和 `WorkingDirectory`，并确认参数可解析到唯一 GameRecord 后才视为健康。
- 迁移只处理 VGC 管理的 `.lnk`；不重建数据库、不改变有效 Game ID / Vnite ID、不删除源游戏、缓存或用户存档。

迁移方式：

- 真实环境已备份旧快捷方式到 `%LOCALAPPDATA%\VniteGameCache\backups\shortcut-contract-*`。
- 已将旧单引号、旧双引号参数键以及带冗余参数的 VGC `.lnk` 统一迁移为 canonical 格式。
- Wrapper 仍保留旧 `--vnite-id` / `--source-root` 兼容解析，但插件不再默认写入这些参数。

## ADR-049：VGC 只作为 MTool 适配层

原因：

- 真实 MTool 批处理已经包含旧 MTool 路径和 `MTool_Game_Tmp` Junction，直接执行会破坏缓存目录启动保证。
- MTool 的 MCenter、脚本和 Mods 属于 MTool 生态，VGC 没有稳定公开协议可以成为 MTool 内部脚本或 Mod 管理器。
- 用户需要可视化确认 MTool 是否生效，而不是让启动链路隐式失败。

规则：

- VGC 负责启动前缓存、模板识别、当前 MTool 组件校验和缓存副本启动。
- VGC 不写入 MTool `gameLib`，不修改 NAS 原游戏目录，不猜测未验证的 MTool CLI。
- 控制台显示选中游戏的 MTool 模板、模板目标程序和当前 MTool 组件状态，并提供打开 MTool/MCenter、链接目录和模板位置的入口。
- `protect_config_files=true` 时，缓存清理把常见 MTool 脚本/数据目录作为可选用户数据风险处理；默认仍关闭以避免误保护大量游戏本体。

迁移方式：

- 不修改 SQLite schema。
- 不改变 Direct 默认启动模式。
- 现有 MTool 模式继续使用已识别 BAT 模板；新增内容只增加诊断、入口和可选保护规则。

## ADR-050：启动程序候选与 MTool Bridge 保持可选薄集成

原因：

- 真实游戏目录中经常同时存在原版 EXE、汉化 EXE、AI 翻译 EXE、启动器、配置工具、卸载器、MTool BAT 和辅助工具。
- 继续把所有启动状态塞进 `games` 会让“候选文件”和“当前生效启动配置”混在一起，导致用户不知道 Vnite 最终会运行什么。
- MTool/MCenter 提供了 `@AutoLoad` 和 `getGameCWD()` 脚本模板能力，但尚未确认稳定通信能力，不能把 Bridge 当成缓存启动强依赖。

规则：

- Schema 4 新增 `game_launch_candidates`，用于保存候选启动程序、角色、评分、置信度、忽略状态、Primary 状态和验证状态。
- `games` 仍是 Wrapper 读取的有效 Launch Profile；候选表只辅助用户选择和诊断。
- TranslationPreferred 只优先选择可解释的翻译 EXE 关键词，例如独立的 ZH、CN、CHS、SC、中文、简中、汉化、AI、GPT、Claude、DeepSeek。
- MTool BAT 只能作为 `MToolTemplate`，不得被 Direct 模式当作普通主程序。
- VGC MTool Bridge 只能作为可选、用户可见、可禁用的脚本包；默认不写 MTool `gameLib`，不修改 MTool 内部数据，不修改 NAS 原游戏文件。
- Bridge 未经真实 Gate M0 验证时状态必须视为 pending/unknown，启动链路降级到既有 Thin Adapter。

迁移方式：

- 不重建旧 Phase 8.1/8.2/8.2.2 数据和快捷方式。
- 只新增候选表并复用现有 `LaunchArgumentCodec`、`WrapperGameResolver`、GameRecord upsert、接管一致性和 MTool 薄适配。

## ADR-051：新库导入 Game ID 使用稳定 GUID

原因：

- 现有目录库导入按源路径生成 `vgc-` 加 16 位哈希，但 `LaunchArgumentCodec`、Wrapper 和插件快捷方式回读要求标准 GUID，可能造成新条目可见却无法启动。
- 归档游戏导入将复用同一 GameDB 和 Wrapper 链路，必须先统一新条目的身份格式。

规则：

- 新导入游戏以规范化源路径的 SHA-256 前 16 字节生成稳定 GUID 文本，重复发现同一路径仍得到同一 ID。
- 现有 VGC SQLite、Vnite GameDB 和快捷方式中的游戏 ID 保持原样；不执行全库重建或自动迁移。
- 归档源在单游戏 Vnite 启动 Gate 通过后单独建模，不能把归档文件直接伪装成目录并交给 Robocopy。

迁移方式：

- 仅影响尚未导入的新游戏；已导入游戏按源路径识别为 Existing，继续复用原 ID。
- 增加 Game ID 可由 `Guid.TryParse` 和 `LaunchArgumentCodec` 接受的测试，并用隔离测试游戏验证完整启动链路。

## ADR-052：压缩包源使用独立表和单游戏暂存解压

- Schema 5 新增 `archive_sources`，旧目录游戏没有该行；Agent 在目录索引和 Robocopy 之前按此行分支。
- Vnite 游戏条目仍使用 Wrapper 快捷方式；空缓存目录可先显示游戏，首次启动才解压。
- ZIP、7z、RAR 由已安装的 7-Zip 预览与解压。加密、危险路径、链接条目和主程序不明的压缩包不自动导入。
- 解压先进入 E 盘同卷单游戏 staging，核验文件数、大小和主程序后切换；已有非空缓存不自动替换，保护存档与 MOD。
- 旧目录源记录与文件索引不迁移。正式安装必须让 Wrapper、Agent、ControlPanel 和插件同时支持 Schema 5。

## ADR-053：压缩包自动发现仅处理明确候选

原因：

- 每次手动选择单个压缩包不适合持续增加游戏的 NAS 库；全库递归预览又会增加 NAS 负载。
- 压缩包内可能有多个 EXE、安装器或辅助工具；错误自动添加会产生错误的 Vnite 条目。

规则：

- 自动发现是一次性开启的可选功能，默认关闭；只枚举已配置的 NAS 游戏库根目录第一层文件，每 15 分钟至多处理一批新文件，不递归解压或扫描游戏内容。
- 自动导入仅限 ZIP、7z、RAR 中安全且只有一个可识别非安装器 EXE 的压缩包；多个候选、危险条目和识别失败项记录为待确认，仍可用现有单游戏导入界面处理。
- 压缩包路径和文件大小、修改时间用作发现检查点；重复扫描不得反复预览或创建重复游戏。自动导入仍复用现有 Agent、GameDB、Wrapper 和单游戏压缩包校验链路。
- 自动导入的标题暂取压缩包文件名，用户可在 Vnite 修改；不得据此自动匹配网络元数据。若需要确认标题，使用手动导入。

影响与迁移：

- 新增插件布尔设置，不改变 SQLite Schema、现有游戏、缓存、快捷方式和存档；安装后需由用户开启一次。
- 旧库没有自动发现检查点，首次开启会检查根目录第一层；已有相同源路径通过现有稳定 Game ID 复用，不全库重建。
- Ready 标记可增加可选的准备耗时毫秒字段，供控制台显示首次解压的实际时间；旧标记缺少该字段时显示“未记录”，无需重新解压或迁移。

## ADR-054：单游戏压缩包更新必须先保护旧缓存

原因：压缩包可能在导入后被替换；旧缓存中可能已有存档和 MOD，直接重解压会丢失这些文件。

规则：

- 再次导入同一路径时先预览新包并对比原记录的文件数、展开大小、压缩大小、修改时间和主程序。
- 内容未变化时复用现有记录；内容变化且旧缓存非空时拒绝更新，并提示使用现有单游戏安全清理/隔离流程处理旧缓存。
- 只有旧缓存为空时，才更新该游戏的压缩包元数据与所选主程序；不创建新 Game ID，也不批量改变其他游戏。

影响与迁移：不改 Schema 或现有 Ready 标记；更新是用户单游戏确认操作，旧数据由现有隔离/恢复能力保护。

## ADR-055：删除记录前必须保留活动隔离缓存的恢复索引

原因：`quarantine_entries.game_id` 使用 `ON DELETE CASCADE`。若缓存先移入 Quarantine、随后删除游戏记录，隔离目录仍在磁盘上，恢复索引却消失。

规则：控制台的单游戏、批量及“清除接管并删除记录”入口，以及 Agent 的 `game-delete` 最终入口，都必须在删除前拒绝仍有活动 Quarantine 项的游戏。拒绝时显示隔离路径和恢复/按保留期清理的操作方向。不得自动永久删除隔离目录，也不得绕过存档保护。清除缓存与删除记录暂不合并为一个按钮。

影响与迁移：只收紧删除前检查；不改数据库、Manifest、配置或命令格式。已有隔离项保持原状，需按既有恢复或保留期清理流程处理后才能删记录。
## ADR-056：清除接管请求按请求时间回报完成状态

ControlPanel 写入 `clear-takeover-request.json` 后，Vnite 插件完成处理时写入 `clear-takeover-result.json`，回传相同的 `requested_utc`、成功/失败状态和处理数量。ControlPanel 仅接受本次请求的结果，等待期间保持维护模式；超时只报告未确认，不推断成功。该回执仅用于显示结果，不改变现有清除和回滚流程。

## ADR-057：控制命令在完成前禁止重复提交并回传请求 ID

原因：压缩包导入和清除接管使用固定请求/结果文件名。插件取走请求文件后仍可能继续处理，第二次提交会覆盖结果，控制台无法确认哪个结果属于哪个操作。

规则：ControlPanel 给新请求写入 GUID `request_id`，先写带相同 ID 的 `pending` 结果，再写请求；请求文件存在或近期结果仍为 `pending` 时拒绝同类重复提交。插件处理完成后原子替换结果文件并回传 ID。已有无 ID 的请求仍可处理；不修改 Vnite GameDB、SQLite、Manifest 或游戏数据。控制台重启后对十五分钟内的 `pending` 阻止新提交，过期项明确显示为未确认，不视为成功；未消费的请求文件仍持续阻止覆盖。

影响与迁移：仅扩展本地 JSON 命令/结果字段；旧版请求不需要迁移。运行中更新需重启 Vnite 才能使用新回执。
