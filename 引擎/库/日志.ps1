# =====================================================================
#  日志.ps1  --  日志/报告保留策略 (dot-source this)
#
#  之前每个脚本每次运行都新开时间戳日志, 从不清理 —— 日志目录会无限膨胀。
#  各脚本在拿到 $LogDir 后调一次 Limit-LogRetention 即可。
# =====================================================================

function Limit-LogRetention {
    <#
      清理过期日志与报告。
        -Dir       日志目录 (清理其中 *.log / *.txt)
        -KeepDays  保留最近多少天 (默认 30)
        -KeepCount 超过天数后如果文件仍然太多, 再按数量截断到这个数 (默认 300)
    #>
    param(
        [Parameter(Mandatory)][string] $Dir,
        [int] $KeepDays  = 30,
        [int] $KeepCount = 300
    )
    try {
        $cutoff = (Get-Date).AddDays(-$KeepDays)

        $targets = @()
        if (Test-Path $Dir) {
            $targets += @(Get-ChildItem $Dir -File -ErrorAction SilentlyContinue |
                          Where-Object { $_.Extension -in @('.log', '.txt') })
        }
        $reportDir = Join-Path (Split-Path $Dir -Parent) '报告'
        if (Test-Path $reportDir) {
            $targets += @(Get-ChildItem $reportDir -File -ErrorAction SilentlyContinue |
                          Where-Object { $_.Extension -in @('.csv', '.json') })
        }

        $old = @($targets | Where-Object { $_.LastWriteTime -lt $cutoff })
        foreach ($f in $old) { Remove-Item -LiteralPath $f.FullName -Force -ErrorAction SilentlyContinue }

        # 天数没到但文件数量失控时, 按时间保留最新的 KeepCount 个
        $rest = @($targets | Where-Object { -not $old.Contains($_) })
        if ($rest.Count -gt $KeepCount) {
            $rest | Sort-Object LastWriteTime -Descending |
                Select-Object -Skip $KeepCount |
                ForEach-Object { Remove-Item -LiteralPath $_.FullName -Force -ErrorAction SilentlyContinue }
        }
    } catch { }   # 清理失败不影响主流程
}
