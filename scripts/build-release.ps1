param(
    [string]$Version = '0.2.10-beta.1',
    [string]$Configuration = 'Release',
    [string]$DotNetPath = 'dotnet',
    [string]$NpmPath = 'npm.cmd'
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$artifactRoot = Join-Path $repoRoot 'artifacts\release'
$releaseRoot = Join-Path $repoRoot 'release'
$packageName = "VniteGameCache-v$Version-win-x64"
$packageRoot = Join-Path $artifactRoot $packageName
$zipPath = Join-Path $releaseRoot "$packageName.zip"
$zipHashPath = "$zipPath.sha256"

function Resolve-DotNetExecutable {
    param([string]$Candidate)

    if (Test-Path -LiteralPath $Candidate) {
        return (Resolve-Path -LiteralPath $Candidate).Path
    }

    $command = Get-Command $Candidate -ErrorAction SilentlyContinue
    if ($null -eq $command) {
        throw "找不到 dotnet。请使用 -DotNetPath 指定 dotnet.exe。"
    }

    return $command.Source
}

function Resolve-CommandExecutable {
    param(
        [string]$Candidate,
        [string]$DisplayName
    )

    if (Test-Path -LiteralPath $Candidate) {
        return (Resolve-Path -LiteralPath $Candidate).Path
    }

    $command = Get-Command $Candidate -ErrorAction SilentlyContinue
    if ($null -eq $command) {
        throw "找不到 $DisplayName。请使用对应参数指定可执行文件路径。"
    }

    return $command.Source
}

function Reset-Directory {
    param([string]$Path)

    $fullRepoRoot = [System.IO.Path]::GetFullPath($repoRoot)
    $fullPath = [System.IO.Path]::GetFullPath($Path)
    if (-not $fullPath.StartsWith($fullRepoRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "拒绝清理仓库外目录：$fullPath"
    }

    if (Test-Path -LiteralPath $fullPath) {
        Remove-Item -LiteralPath $fullPath -Recurse -Force
    }
    New-Item -ItemType Directory -Path $fullPath -Force | Out-Null
}

function Invoke-DotNet {
    param([string[]]$Arguments)

    & $script:dotnetExe @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet 命令失败：dotnet $($Arguments -join ' ')"
    }
}

$script:dotnetExe = Resolve-DotNetExecutable -Candidate $DotNetPath
$npmExe = Resolve-CommandExecutable -Candidate $NpmPath -DisplayName 'npm'

Reset-Directory -Path $artifactRoot
New-Item -ItemType Directory -Path $releaseRoot -Force | Out-Null
if (Test-Path -LiteralPath $zipPath) {
    Remove-Item -LiteralPath $zipPath -Force
}
if (Test-Path -LiteralPath $zipHashPath) {
    Remove-Item -LiteralPath $zipHashPath -Force
}

Push-Location $repoRoot
try {
    Invoke-DotNet @('restore', 'VniteGameCache.sln')
    Invoke-DotNet @('build', 'VniteGameCache.sln', '-c', $Configuration, '--no-restore')
    Invoke-DotNet @('test', 'VniteGameCache.sln', '-c', $Configuration, '--no-build')

    Push-Location (Join-Path $repoRoot 'src\VniteGameCache.Plugin')
    try {
        & $npmExe test
        if ($LASTEXITCODE -ne 0) {
            throw '插件 npm test 失败。'
        }
        & $npmExe run build
        if ($LASTEXITCODE -ne 0) {
            throw '插件 npm build 失败。'
        }
    } finally {
        Pop-Location
    }

    $publishRoot = Join-Path $artifactRoot 'publish'
    $projects = @(
        'src\VniteGameCache.Agent\VniteGameCache.Agent.csproj',
        'src\VniteGameCache.Wrapper\VniteGameCache.Wrapper.csproj',
        'src\VniteGameCache.ControlPanel\VniteGameCache.ControlPanel.csproj'
    )

    foreach ($project in $projects) {
        $projectName = [System.IO.Path]::GetFileNameWithoutExtension($project)
        $output = Join-Path $publishRoot $projectName
        Invoke-DotNet @(
            'publish',
            $project,
            '-c', $Configuration,
            '-r', 'win-x64',
            '--self-contained', 'false',
            '-o', $output,
            "-p:Version=$Version",
            '-p:DebugType=None',
            '-p:DebugSymbols=false'
        )
    }

    $binRoot = Join-Path $packageRoot 'bin'
    $pluginRoot = Join-Path $packageRoot 'plugin'
    $scriptRoot = Join-Path $packageRoot 'scripts'
    New-Item -ItemType Directory -Path $binRoot, $pluginRoot, $scriptRoot -Force | Out-Null

    Get-ChildItem -LiteralPath $publishRoot -Directory | ForEach-Object {
        Copy-Item -Path (Join-Path $_.FullName '*') -Destination $binRoot -Recurse -Force
    }

    Get-ChildItem -LiteralPath $binRoot -Recurse -File -Filter *.pdb |
        Remove-Item -Force

    $runtimeRoot = Join-Path $binRoot 'runtimes'
    if (Test-Path -LiteralPath $runtimeRoot) {
        Get-ChildItem -LiteralPath $runtimeRoot -Directory |
            Where-Object { $_.Name -ne 'win-x64' } |
            Remove-Item -Recurse -Force
    }

    Copy-Item -LiteralPath 'src\VniteGameCache.Plugin\index.js' -Destination $pluginRoot
    Copy-Item -LiteralPath 'src\VniteGameCache.Plugin\package.json' -Destination $pluginRoot
    Copy-Item -LiteralPath 'src\VniteGameCache.Plugin\package.json' -Destination (Join-Path $pluginRoot 'manifest.json')
    Copy-Item -LiteralPath 'scripts\install-vnite-plugin.ps1' -Destination $scriptRoot
    Copy-Item -LiteralPath 'packaging\README.zh-CN.md' -Destination $packageRoot
    Copy-Item -LiteralPath 'LICENSE' -Destination $packageRoot
    Copy-Item -LiteralPath 'THIRD-PARTY-NOTICES.md' -Destination $packageRoot

    $checksumFile = Join-Path $packageRoot 'SHA256SUMS.txt'
    $checksumLines = Get-ChildItem -LiteralPath $packageRoot -Recurse -File |
        Where-Object { $_.FullName -ne $checksumFile } |
        Sort-Object FullName |
        ForEach-Object {
            $relativePath = $_.FullName.Substring($packageRoot.Length + 1).Replace('\', '/')
            "$(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256 | Select-Object -ExpandProperty Hash)  $relativePath"
        }
    [System.IO.File]::WriteAllLines($checksumFile, $checksumLines, [System.Text.UTF8Encoding]::new($false))

    Compress-Archive -LiteralPath $packageRoot -DestinationPath $zipPath -CompressionLevel Optimal
    $zipHash = Get-FileHash -LiteralPath $zipPath -Algorithm SHA256
    [System.IO.File]::WriteAllText(
        $zipHashPath,
        "$($zipHash.Hash)  $([System.IO.Path]::GetFileName($zipPath))`n",
        [System.Text.UTF8Encoding]::new($false)
    )

    Write-Host ''
    Write-Host "发布包: $zipPath" -ForegroundColor Green
    Write-Host "SHA256: $($zipHash.Hash)"
} finally {
    Pop-Location
}
