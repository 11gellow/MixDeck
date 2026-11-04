param(
    [string[]]$Path = @((Join-Path $env:LOCALAPPDATA 'MixDeck/diagnostics/startup.jsonl.previous'),
                        (Join-Path $env:LOCALAPPDATA 'MixDeck/diagnostics/startup.jsonl'))
)
$ErrorActionPreference = 'Stop'
# 只读统计；手动启动、缺登录时间、重复启动和不完整记录均不进入速度比较。
$events = @(
    foreach ($file in $Path) {
        if (Test-Path -LiteralPath $file) {
            foreach ($line in Get-Content -LiteralPath $file) {
                try { $line | ConvertFrom-Json -ErrorAction Stop }
                catch { Write-Warning "忽略 $file 中不完整的 JSON 行。" }
            }
        }
    }
)
$runs = @(
    foreach ($group in ($events | Group-Object RunId)) {
        $entry = $group.Group | Where-Object Stage -eq 'app-entry' | Select-Object -First 1
        $backend = $group.Group | Where-Object Stage -eq 'startup-backend' | Select-Object -First 1
        $ready = $group.Group | Where-Object Stage -eq 'listener-ready' | Select-Object -First 1
        $pass = $group.Group | Where-Object Stage -eq 'initial-target-pass-complete' | Select-Object -First 1
        $restore = $group.Group | Where-Object Stage -eq 'first-restore-readback' | Select-Object -First 1
        if ($null -eq $entry -or $entry.Detail -ne 'tray' -or $backend.Detail -notin @('run','task')) { continue }
        [pscustomobject]@{
            RunId=$group.Name; Build=$entry.Build; Mode=$backend.Detail; LogonAt=$entry.LogonAt
            ProcessCreatedAt=$entry.ProcessCreatedAt
            ProcessAfterLogonMs=$(if ($null -ne $entry.LogonAt) { ([datetimeoffset]$entry.ProcessCreatedAt - [datetimeoffset]$entry.LogonAt).TotalMilliseconds })
            ListenerAfterLogonMs=$ready.SinceLogonMs; ListenerAfterProcessMs=$ready.SinceProcessMs
            InitialTargetPassAfterLogonMs=$pass.SinceLogonMs
            FirstRestoreAfterLogonMs=$restore.SinceLogonMs
            Complete=($null -ne $ready -and $null -ne $pass -and $null -ne $entry.LogonAt)
        }
    }
)
Write-Output '逐次记录（托盘参数只能识别候选登录启动；须排除手动 --tray）'
$runs | Sort-Object ProcessCreatedAt | Format-Table -AutoSize
function Median($values) {
    $sorted = @($values | Sort-Object)
    if ($sorted.Count -eq 0) { return $null }
    $middle = [int][math]::Floor($sorted.Count / 2)
    if ($sorted.Count % 2) { return $sorted[$middle] }
    return ($sorted[$middle - 1] + $sorted[$middle]) / 2
}
$summaries = @(
    # 相同构建比较，且同一登录会话只取第一个候选运行，避免重复打开造成虚假样本。
    foreach ($group in ($runs | Where-Object Complete | Group-Object Build)) {
        $samples = @($group.Group | Sort-Object ProcessCreatedAt | Group-Object LogonAt | ForEach-Object { $_.Group | Select-Object -First 1 })
        foreach ($mode in @('run','task')) {
            $selected = @($samples | Where-Object Mode -eq $mode)
            [pscustomobject]@{
                Build=$group.Name; Mode=$mode; Count=$selected.Count
                MedianListenerMs=(Median @($selected.ListenerAfterLogonMs))
                MedianInitialTargetPassMs=(Median @($selected.InitialTargetPassAfterLogonMs))
                RestoreSampleCount=@($selected | Where-Object { $null -ne $_.FirstRestoreAfterLogonMs }).Count
                MedianFirstRestoreMs=(Median @($selected.FirstRestoreAfterLogonMs | Where-Object { $null -ne $_ }))
                ReadyForComparison=($selected.Count -ge 3)
            }
        }
    }
)
Write-Output '同构建、不同登录会话的中位数（两组均至少 3 次；不自动判定配置/听感验收）'
$summaries | Format-Table -AutoSize
