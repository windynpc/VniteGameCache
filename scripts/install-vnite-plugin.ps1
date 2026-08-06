param(
    [string]$VnitePluginRoot = "$env:APPDATA\vnite\app\plugins\vnite-game-cache"
)

$ErrorActionPreference = 'Stop'

$packageRoot = Split-Path -Parent $PSScriptRoot
$pluginSource = Join-Path $packageRoot 'plugin'
$binRoot = Join-Path $packageRoot 'bin'

$requiredFiles = @(
    (Join-Path $pluginSource 'index.js'),
    (Join-Path $pluginSource 'package.json'),
    (Join-Path $pluginSource 'manifest.json'),
    (Join-Path $binRoot 'VniteGameCache.Wrapper.exe'),
    (Join-Path $binRoot 'VniteGameCache.Agent.exe'),
    (Join-Path $binRoot 'VniteGameCache.ControlPanel.exe')
)

$missingFiles = $requiredFiles | Where-Object { -not (Test-Path -LiteralPath $_) }
if ($missingFiles.Count -gt 0) {
    throw "发布包不完整，缺少文件：`n$($missingFiles -join "`n")"
}

$dotnetCommand = Get-Command dotnet -ErrorAction SilentlyContinue
if ($null -eq $dotnetCommand) {
    Write-Warning '.NET 8 Desktop Runtime 未在 PATH 中找到。若程序无法启动，请安装 .NET 8 Desktop Runtime x64。'
} else {
    $runtimes = & $dotnetCommand.Source --list-runtimes 2>$null
    $hasNetCore = $runtimes -match '^Microsoft\.NETCore\.App 8\.'
    $hasDesktop = $runtimes -match '^Microsoft\.WindowsDesktop\.App 8\.'
    if (-not ($hasNetCore -and $hasDesktop)) {
        Write-Warning '.NET 8 Desktop Runtime x64 不完整，ControlPanel 或 Wrapper 可能无法启动。'
    }
}

New-Item -ItemType Directory -Path $VnitePluginRoot -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $pluginSource 'index.js') -Destination $VnitePluginRoot -Force
Copy-Item -LiteralPath (Join-Path $pluginSource 'package.json') -Destination $VnitePluginRoot -Force
Copy-Item -LiteralPath (Join-Path $pluginSource 'manifest.json') -Destination $VnitePluginRoot -Force

Write-Host ''
Write-Host 'Vnite 游戏缓存插件已安装或更新。' -ForegroundColor Green
Write-Host "插件目录: $VnitePluginRoot"
Write-Host "Wrapper:  $(Join-Path $binRoot 'VniteGameCache.Wrapper.exe')"
Write-Host "Agent:    $(Join-Path $binRoot 'VniteGameCache.Agent.exe')"
Write-Host "控制台:   $(Join-Path $binRoot 'VniteGameCache.ControlPanel.exe')"
Write-Host ''
Write-Host '请完全退出并重新启动 Vnite，然后填写以上三个路径。'
