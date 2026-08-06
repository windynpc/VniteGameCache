# Vnite 游戏缓存

## 安装要求

- Windows 10/11 x64
- Vnite 4.x
- .NET 8 Desktop Runtime x64

.NET 下载地址：

https://dotnet.microsoft.com/download/dotnet/8.0

## 安装步骤

1. 将整个压缩包解压到固定目录，例如 `E:\VniteGameCache`。
2. 不要只复制单个 EXE，也不要在安装后移动 `bin` 目录。
3. 在 PowerShell 中进入解压目录并运行：

```powershell
Set-ExecutionPolicy -Scope Process Bypass -Force
.\scripts\install-vnite-plugin.ps1
```

4. 完全退出并重新启动 Vnite。
5. 打开 Vnite 插件配置，设置：
   - Wrapper 启动器路径：`bin\VniteGameCache.Wrapper.exe`
   - Agent 路径：`bin\VniteGameCache.Agent.exe`
   - VGC 控制台路径：`bin\VniteGameCache.ControlPanel.exe`
   - NAS 游戏库根目录
   - 默认 SSD 缓存根目录

## 首次使用

- 启动模式保持“直接启动”。
- 自动清理先保持关闭。
- 先接管一个游戏并执行首次缓存。
- 第二次启动应显示缓存已就绪，不再完整复制。
- 确认无误后再扩大接管范围。

## 数据位置

默认运行状态保存在：

```text
%LOCALAPPDATA%\VniteGameCache
```

这里可能包含 SQLite 数据库、日志、诊断、快捷方式、Quarantine 和存档备份。
卸载插件前请先备份需要保留的数据。

## 注意

- VGC 不会修改 NAS 游戏本体。
- 未签名的 Beta 可执行文件可能触发 SmartScreen。
- 远程桌面环境下部分旧游戏可能出现 Direct3D 初始化失败。
- MTool 是可选薄集成；没有可靠模板时会使用既有安全回退策略。
