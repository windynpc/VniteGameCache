# Roadmap

## Phase 1：Vnite 能力调研

- 检查仓库
- 检查 SDK
- 检查示例
- 确认生命周期 API
- 确认 UI 扩展点
- 确认外部进程能力
- 确认持久化与通信能力
- 判断是否需要 Wrapper Launcher

不写完整业务代码。

## Phase 2：项目骨架

- Vnite Plugin
- Cache Agent
- Shared Contracts
- Tests
- 构建脚本
- 日志目录
- 配置目录

## Phase 3：SQLite 与模型

- Schema
- Migration
- Repository
- Game Config
- File Manifest
- Task State
- Save Index

## Phase 4：首次索引

- 完整扫描
- Manifest
- 启动程序候选
- 排除规则
- 目录指纹

## Phase 5：缓存复制

- Robocopy 封装
- 进度
- 取消
- 重试
- 恢复
- 状态更新

## Phase 6：启动与进程监控

- Wrapper Launcher
- 参数转发
- 工作目录
- 进程树
- 退出确认

## Phase 7：存档同步

- 自定义路径
- 退出扫描
- ZIP 快照
- NAS 回写
- Pending Queue
- 冲突记录

## Phase 8：无变化快速启动

核心验收：

- 第二次启动不递归扫描 NAS
- 不完整 Hash
- 不复制未变化文件
- NAS 离线可启动缓存

## Phase 9：Vnite UI

- 菜单
- 配置
- 状态
- 进度
- 错误

## Phase 10：异常恢复与测试

- NAS 断线
- 文件复制中断
- Agent 崩溃
- Windows 重启
- 启动器提前退出
- 存档冲突
- SSD 空间不足
