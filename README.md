# Vnite Game Cache

Vnite Game Cache（VGC）是一个面向 Windows、Vnite 与 NAS/HDD 游戏库的本地
SSD 持久缓存工具。

> [!IMPORTANT]
> 本项目是面向 [Vnite](https://github.com/ximu3/vnite) 的第三方扩展工具，
> 不是 Vnite 官方项目，也不包含 Vnite 本体。使用 VGC 前请先安装 Vnite。

正常启动链路：

```text
Vnite
→ VGC 快捷方式
→ Wrapper
→ Agent
→ NAS/HDD 到 SSD 缓存
→ 从 cache_root 启动游戏
```

## 主要功能

- 将 NAS/HDD 游戏按需缓存到本地 SSD。
- 相同 Revision 再次启动时跳过完整复制。
- 批量接管 Vnite 已有游戏，并保留标题、封面、标签和存档路径。
- 支持 Direct、翻译 EXE 优先和已识别 MTool 模板三种启动偏好。
- 提供中文 VGC 控制台、接管诊断、手动修复和批量操作。
- 提供 LRU 清理建议、可选安全自动清理、Quarantine 与恢复。
- 提供手动 Save Profile、存档导入、导出、备份和恢复。
- 使用 SQLite Schema 4 保存索引、缓存与接管状态。

## 当前状态

当前公开版本为 `0.2.9-beta.1`。核心缓存与接管链路已在真实 Vnite 环境
验证，但仍属于 Beta，请先使用少量游戏验证配置，不要直接对整个游戏库
执行批量清理。

## 系统要求

- Windows 10/11 x64
- [Vnite 4.x](https://github.com/ximu3/vnite)
- [.NET 8 Desktop Runtime x64](https://dotnet.microsoft.com/download/dotnet/8.0)
- NAS 使用 SMB/UNC 或 Windows 映射盘均可

## 安装

1. 安装并确认 [Vnite](https://github.com/ximu3/vnite) 可以正常运行。
2. 从本项目的 [GitHub Releases](https://github.com/windynpc/VniteGameCache/releases)
   下载 `VniteGameCache-*-win-x64.zip`。
3. 解压到固定目录，例如 `E:\VniteGameCache`，后续不要随意移动。
4. 在 PowerShell 中运行：

```powershell
Set-ExecutionPolicy -Scope Process Bypass -Force
.\scripts\install-vnite-plugin.ps1
```

5. 完全退出并重新启动 Vnite。
6. 在插件配置中填写：
   - Wrapper：解压目录下 `bin\VniteGameCache.Wrapper.exe`
   - Agent：解压目录下 `bin\VniteGameCache.Agent.exe`
   - VGC 控制台：解压目录下 `bin\VniteGameCache.ControlPanel.exe`
   - NAS 游戏库根目录
   - 本地 SSD 缓存根目录

## 安全建议

- 第一次使用先关闭自动清理。
- 先接管并测试 1–3 个游戏，再扩大范围。
- `Direct` 是默认且最稳妥的启动模式。
- `Quarantine` 与缓存位于同一磁盘时，移动到隔离区不会立即增加可用空间。
- 自动清理不会处理 Pinned、Running、Copying、Verifying 或无法确认用户数据
  安全的缓存。
- VGC 不修改 NAS 游戏本体；接管只修改 Vnite 本地启动字段，并保存恢复信息。

## 已知限制

- 不支持自动双向存档同步；当前仅提供手动存档操作。
- MTool 只使用已识别的模板与已验证组件，不猜测未公开 CLI。
- 远程桌面或虚拟显示环境可能导致部分旧游戏 Direct3D 初始化失败；这与缓存
  路由是否成功是两个独立问题。
- 未签名的 Beta 可执行文件可能触发 Windows SmartScreen。

## 关联项目与问题反馈

- Vnite 本体、Vnite 安装或 Vnite 通用功能：
  [ximu3/vnite](https://github.com/ximu3/vnite)
- VGC 缓存、接管、Wrapper、Agent 或控制台问题：
  [windynpc/VniteGameCache Issues](https://github.com/windynpc/VniteGameCache/issues)

提交 VGC 问题时，请附上 Vnite 版本、VGC 版本、启动模式、错误截图以及
VGC 最近一次启动日志。请勿公开上传存档、账号信息或包含隐私的完整日志。

## 从源码构建

```powershell
dotnet build VniteGameCache.sln -c Release
dotnet test VniteGameCache.sln -c Release --no-build

Push-Location src\VniteGameCache.Plugin
npm test
npm run build
Pop-Location
```

生成 Windows 发布包：

```powershell
.\scripts\build-release.ps1 -Version 0.2.9-beta.1
```

如果 `dotnet` 不在 `PATH`：

```powershell
.\scripts\build-release.ps1 `
  -Version 0.2.9-beta.1 `
  -DotNetPath C:\path\to\dotnet.exe
```

输出位于 `release\`。

## 项目结构

- `src/VniteGameCache.Plugin`：Vnite 插件与中文配置 UI
- `src/VniteGameCache.Wrapper`：Vnite 启动入口与进程树监督
- `src/VniteGameCache.Agent`：索引、缓存、清理和存档操作
- `src/VniteGameCache.ControlPanel`：中文控制台与修复入口
- `src/VniteGameCache.Persistence`：SQLite 与 Schema Migration
- `tests`：自动化回归测试
- `docs`：架构与行为说明

## License

MIT。第三方组件见 `THIRD-PARTY-NOTICES.md`。

## Vnite 4.14 兼容性（2026-10-21）

本机 Vnite 4.14.0 下，0.2.9-beta.1 插件已正常激活，并从 Vnite 实际启动两款中文/日文路径游戏；Wrapper 和 Agent 均从当前发布目录运行，游戏进程位于 SSD 缓存目录。源码、发布插件和已安装插件的哈希已核对一致；插件测试及 118 项 .NET 测试通过。

Vnite 逐游戏自动存档备份与 VGC 手动 Save Profiles 相互独立，详见 [存档说明](docs/save-sync.md)。中文路径游戏的 Vnite“停止游戏”按钮验收按用户要求暂停，不能据此宣称该按钮已兼容。本次只更新文档和本地发布包说明，程序二进制及原有 `v0.2.9-beta.1` 下载资产不变。
