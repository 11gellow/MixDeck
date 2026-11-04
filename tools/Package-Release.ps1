param(
    [string]$OutputRoot = 'artifacts/releases/v0.1',
    [switch]$AllowDirty
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
Push-Location $repo
try {
    $commit = git rev-parse HEAD
    if ($LASTEXITCODE -ne 0) { throw '无法读取 Git 提交。' }
    $dirty = [bool](git status --porcelain)
    if ($dirty -and !$AllowDirty) { throw '请先提交工作区；仅候选验证允许使用 -AllowDirty。' }
    $root = [System.IO.Path]::GetFullPath((Join-Path $repo $OutputRoot))
    if (!$root.StartsWith($repo + [System.IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw '输出目录必须位于项目内。'
    }
    if (Test-Path -LiteralPath $root) { throw "输出目录已存在，请指定新目录：$root" }
    $name = 'MixDeck-v0.1-win-x64'
    $stage = Join-Path $root $name
    New-Item -ItemType Directory -Path $stage -Force | Out-Null
    dotnet publish src/AudioManager.Desktop/AudioManager.Desktop.csproj -c Release -r win-x64 --self-contained true `
        -o $stage --nologo --warnaserror -p:PublishSingleFile=false -p:PublishTrimmed=false -p:DebugType=None -p:DebugSymbols=false
    if ($LASTEXITCODE -ne 0) { throw 'Release 发布失败。' }
    $exe = Join-Path $stage 'AudioManager.Desktop.exe'
    if ((Get-Item -LiteralPath $exe).VersionInfo.FileVersion -ne '0.1.0.0') { throw '程序版本不匹配。' }
    foreach ($required in @('coreclr.dll', 'hostfxr.dll', 'hostpolicy.dll', 'PresentationFramework.dll', 'AudioManager.Desktop.dll')) {
        if (!(Test-Path -LiteralPath (Join-Path $stage $required))) { throw "缺少自包含运行文件：$required" }
    }
    Copy-Item -LiteralPath README.md -Destination (Join-Path $stage 'README.md')
    Copy-Item -LiteralPath docs/releases/v0.1.md -Destination (Join-Path $stage 'RELEASE-NOTES.md')
    # 从本次还原的包中带上运行库/依赖的许可、声明和包元数据。
    $assets = Get-Content src/AudioManager.Desktop/obj/project.assets.json -Raw | ConvertFrom-Json
    $packageRoots = @($assets.packageFolders.PSObject.Properties.Name)
    $deps = Get-Content (Join-Path $stage 'AudioManager.Desktop.deps.json') -Raw | ConvertFrom-Json
    $notices = Join-Path $stage 'ThirdPartyNotices'
    New-Item -ItemType Directory -Path $notices | Out-Null
    Copy-Item -LiteralPath docs/third-party/NAudio-LICENSE.txt -Destination $notices
    foreach ($library in $deps.libraries.PSObject.Properties) {
        if ($library.Value.type -notin @('package', 'runtimepack')) { continue }
        $identity = ($library.Name -replace '^runtimepack\.', '').ToLowerInvariant()
        $cache = $packageRoots | ForEach-Object { Join-Path $_ $identity } | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
        if (!$cache) { throw "找不到依赖许可来源：$identity" }
        $destination = Join-Path $notices ($identity -replace '/', '-')
        New-Item -ItemType Directory -Path $destination | Out-Null
        Get-ChildItem -LiteralPath $cache -File | Where-Object { $_.Name -match 'license|notice|\.nuspec$' } | Copy-Item -Destination $destination
    }
    $files = @(Get-ChildItem -LiteralPath $stage -File -Recurse | ForEach-Object {
        [ordered]@{ path = $_.FullName.Substring($stage.Length + 1); sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
    })
    [ordered]@{
        product = 'MixDeck'; version = '0.1.0'; runtime = 'win-x64'; selfContained = $true
        sourceCommit = "$commit"; sourceDirty = $dirty; createdUtc = [DateTime]::UtcNow.ToString('o'); files = $files
    } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $stage 'release-manifest.json') -Encoding UTF8
    $zip = Join-Path $root "$name.zip"
    Compress-Archive -LiteralPath $stage -DestinationPath $zip -CompressionLevel Optimal
    $hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash
    "$hash  $name.zip" | Set-Content -LiteralPath "$zip.sha256" -Encoding ASCII
    Write-Output "Package: $zip"
    Write-Output "SHA256: $hash"
} finally { Pop-Location }
