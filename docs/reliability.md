# Reliability and Recovery

## 必须处理的异常

- NAS 启动时不可用
- 游戏运行中 NAS 断线
- 存档同步时 NAS 断线
- 文件复制中断
- Windows 重启
- Vnite 崩溃
- Cache Agent 崩溃
- 用户强制关闭
- SSD 空间不足
- 启动器提前退出
- 存档冲突
- 数据库迁移失败

## 持久任务状态

至少包含：

- Pending
- Checking
- Copying
- Verifying
- Ready
- Launching
- Running
- SyncingSaves
- WaitingForNAS
- Cleaning
- Completed
- Interrupted
- Conflict
- Failed

所有长任务必须记录：

- task_id
- game_id
- 当前阶段
- 开始时间
- 更新时间
- 已处理数量
- 错误
- 重试次数
- 可恢复信息

## 数据安全

- 不静默覆盖冲突存档
- 同步失败时不删除本地存档
- 不删除运行中的缓存
- 不删除 Pending Sync 游戏
- 不默认镜像删除
- 删除操作必须写日志
- 缓存不完整时必须显式标记
- 数据库升级优先迁移，不默认重建索引

## 进程监控

不得只等待初始 PID。

必须支持：

```text
Launcher.exe
  ↓
Game.exe
  ↓
Game-Win64-Shipping.exe
```

检测方式优先级：

1. 进程树
2. 每游戏额外进程名
3. 工作目录
4. 启动时间窗口
5. 退出延迟确认

同名进程防误判：

- Wrapper 启动游戏后，会读取当前游戏的 `cache_root` 作为进程路径边界。
- 初始进程和子进程按进程树追踪。
- 仅靠进程名匹配的额外进程，必须确认其可执行文件位于该 `cache_root` 下。
- 这样可以避免多个 `Game.exe`、`nwjs.exe` 同时运行时互相误判。
- 即使初始发现窗口已结束，只要已观察到的启动器或中继进程仍存活，Wrapper 仍继续发现延迟创建的子进程。

## 崩溃恢复

Agent 初始化数据库后会做轻量恢复：

- 超过 6 小时未更新的 `Checking` / `Copying` / `Verifying` 任务标记为 `Interrupted`。
- 恢复事件写入 stderr JSON：`stale_active_tasks_recovered`。
- 调试时可用 `VGC_STALE_ACTIVE_TASK_MINUTES` 调整阈值。
- 不自动中断 `Running`，运行中保护仍由 SQLite 状态加命名 Mutex 共同判断。
- Agent 已创建的 `PrepareLaunch` 任务不得在错误返回时停留在 `Checking`。模拟失败、索引失败、缓存准备失败和未捕获异常都必须写入 `Failed`，取消写入 `Cancelled`。

## Phase 6 Runtime Cleanup Guard

Cache deletion must not trust SQLite state alone.

Current guard:

- Wrapper holds `Local\VniteGameCache.Game.<sha256(game_id)>` for the whole supervised runtime.
- Agent `runtime-state` records `Running` and later `Ready` in SQLite.
- Cleanup blocks if the mutex exists, even if SQLite has stale or missing runtime state.
- Cleanup repairs stale `Running` rows when no mutex exists, so a crashed Wrapper does not permanently block future maintenance.

## Shared Settings Health

The shared settings snapshot is a single configuration source for Plugin, Agent, Wrapper, and Control Panel.

- A missing snapshot keeps backward-compatible defaults so first installation can start.
- Invalid JSON, unreadable content, or an unsupported schema blocks database/cache-path fallback.
- Control Panel shows the health state and the settings path instead of silently opening a different database.
- Explicit diagnostic environment variables such as `VGC_DB_PATH` still take precedence.

## 日志容量维护

- VGC 日志合计容量上限为 **500 MB（500 × 1024 × 1024 字节）**，不是每个组件各 500 MB。
- Wrapper、插件日志按 UTC 日期记录；单个 JSONL 文件超过 10 MB 时分卷。
- 每次写入 JSONL 日志，以及缓存复制任务结束后，按最后修改时间从旧到新删除日志，直到回到容量上限内。
- 合计范围包括默认 `logs`、`plugin` 目录、配置的日志目录以及当前复制日志目录；历史 `vnite-game-cache.jsonl` 也会纳入。
- 仅清理 VGC 命名的 JSONL、Robocopy 日志和对应进度快照，不清理存档、数据库或配置，不跟随目录链接。
- 清理采用尽力执行：被占用或无权限的文件留待后续重试。并发写入和复制进行中可能暂时超限；日志维护失败不阻断游戏启动或复制。
