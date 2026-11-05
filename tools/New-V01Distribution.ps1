#requires -Version 7.0
param(
    [string]$SourceZip = 'artifacts/releases/v0.1/MixDeck-v0.1-win-x64.zip',
    [string]$OutputDirectory = 'artifacts/releases/v0.1-distribution',
    [string]$IsccPath = "$env:LOCALAPPDATA/Programs/Inno Setup 6/ISCC.exe"
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$sourceTree = '00483ee0e14c16905b554b10b24ca4fbc3a413f6'
$sourceHash = '4F38EB6CC992E1D0AFAEB3B44F7D24FB96E1665677B01E8F85BB63FAC52C5825'
Push-Location $repo
try {
    $tagCommit = git rev-parse 'v0.1^{commit}'
    if ($LASTEXITCODE -ne 0) { throw '无法读取 v0.1 标签。' }
    $tagSourceTree = git rev-parse 'v0.1:src'
    if ($LASTEXITCODE -ne 0 -or $tagSourceTree -ne $sourceTree) { throw 'v0.1 程序源码与原版不同，停止打包。' }
    $zip = (Resolve-Path -LiteralPath $SourceZip).Path
    if ((Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash -ne $sourceHash) { throw '原 v0.1 ZIP 校验不符。' }
    $compiler = (Resolve-Path -LiteralPath $IsccPath).Path
    $root = [IO.Path]::GetFullPath((Join-Path $repo $OutputDirectory))
    if (!$root.StartsWith($repo + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw '输出必须位于仓库内。' }
    if (Test-Path -LiteralPath $root) { throw '输出已存在，请选择新目录；不覆盖历史产物。' }
    $build = Join-Path $root 'build'
    New-Item -ItemType Directory -Path $build -Force | Out-Null
    Expand-Archive -LiteralPath $zip -DestinationPath $build
    $payload = Join-Path $build 'MixDeck-v0.1-win-x64'
    $manifest = Get-Content (Join-Path $payload 'release-manifest.json') -Raw | ConvertFrom-Json
    if ($manifest.sourceDirty -or !$manifest.sourceCommit) { throw '原归档缺少有效来源记录。' }
    foreach ($file in $manifest.files) {
        if ((Get-FileHash -LiteralPath (Join-Path $payload $file.path) -Algorithm SHA256).Hash -ne $file.sha256) { throw "载荷校验失败：$($file.path)" }
    }
    $iconZip = Join-Path $build 'tag-icon.zip'
    git archive --format=zip "--output=$iconZip" v0.1 src/AudioManager.Desktop/Resources/Icons/mixdeck.ico
    if ($LASTEXITCODE -ne 0) { throw '无法从标签导出图标。' }
    Expand-Archive -LiteralPath $iconZip -DestinationPath (Join-Path $build 'tag-icon')
    $icon = Join-Path $build 'tag-icon/src/AudioManager.Desktop/Resources/Icons/mixdeck.ico'
    $compilerMessages = @(& $compiler "/DPayloadDir=$payload" "/DOutputDir=$root" "/DAppIcon=$icon" tools/installer/AudioManager.iss)
    $compileExitCode = $LASTEXITCODE
    $compilerMessages | Set-Content (Join-Path $build 'compiler.log') -Encoding utf8
    if ($compileExitCode -ne 0) { throw "安装程序编译失败：$($compilerMessages -join [Environment]::NewLine)" }
    $compilerVersion = [regex]::Match(($compilerMessages -join "`n"), 'Compiler engine version:[^\r\n]*?(\d+\.\d+\.\d+)').Groups[1].Value
    if (!$compilerVersion) { throw '未能读取编译器版本。' }
    Copy-Item -LiteralPath $zip -Destination (Join-Path $root 'AudioManager-Portable.zip')
    Copy-Item -LiteralPath INSTALL.md -Destination (Join-Path $root 'INSTALL.md')
    $outputs = @('AudioManager-Setup.exe', 'AudioManager-Portable.zip') | ForEach-Object {
        $path = Join-Path $root $_
        [pscustomobject][ordered]@{ name = $_; bytes = (Get-Item -LiteralPath $path).Length; sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash }
    }
    $outputs | ForEach-Object { "$($_.sha256)  $($_.name)" } | Set-Content (Join-Path $root 'SHA256SUMS.txt') -Encoding ascii
    [ordered]@{
        version = '0.1.0'; applicationSourceCommit = $tagCommit; originalArchiveSha256 = $sourceHash
        originalArchiveSourceCommit = $manifest.sourceCommit
        packagingScriptSha256 = (Get-FileHash -LiteralPath $PSCommandPath -Algorithm SHA256).Hash
        installerScriptSha256 = (Get-FileHash -LiteralPath tools/installer/AudioManager.iss -Algorithm SHA256).Hash
        compilerVersion = $compilerVersion
        compilerSha256 = (Get-FileHash -LiteralPath $compiler -Algorithm SHA256).Hash
        createdUtc = [DateTime]::UtcNow.ToString('o'); outputs = @($outputs)
    } | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $root 'distribution-manifest.json') -Encoding utf8
    $outputs | Format-Table name, bytes, sha256 -AutoSize
} finally { Pop-Location }
