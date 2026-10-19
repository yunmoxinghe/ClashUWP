<#
ClashUWP 网络恢复（兜底）—— 清理「孤儿 NRPT + 幽灵路由」

背景：VPN 会话收尾不干净时，Windows 可能留下
  · 一条 Namespace="." 的根 NRPT 规则，把整机解析指向已死的隧道 DNS；
  · 挂在已消失接口上的 ClashUWP 路由。
两者叠加=整机断网。本脚本只清这两类残留。

★ 它不做的事（刻意不做）：
  · 不安装/卸载/重装 MSIX，不打包，不启动应用；
  · 不连接/断开 VPN，不删 VPN profile，不改 DNS 服务器，不重置网络栈；
  · 不禁用/重启任何物理网卡（那会连带打断蓝牙等）。
★ 默认只读：打印「将清理什么」，不改任何东西。
★ -Execute 才动手，需要管理员 PowerShell。

用法：
  # 1) 先看（不需要管理员）
  powershell -NoProfile -ExecutionPolicy Bypass -File Tools\TestScripts\restore-network.ps1

  # 2) 执行（管理员）
  powershell -NoProfile -ExecutionPolicy Bypass -File Tools\TestScripts\restore-network.ps1 -Execute

  # 3) 名字匹配不到、但确实只剩根 NRPT 时（核选项：删所有 NRPT 规则）
  ... -Execute -AllNrpt

  # 4) VPN 仍显示 Connected 但整机已断（确实要强拆路由时）
  ... -Execute -Force
#>
[CmdletBinding()]
param(
    [switch]$Execute,
    [switch]$AllNrpt,
    [switch]$Force
)

$ErrorActionPreference = "Continue"

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

Write-Host "== ClashUWP 网络恢复 =="
Write-Host ("模式: " + $(if ($Execute) { "EXECUTE（会修改）" } else { "DRY-RUN（只读）" }) + "   Admin=" + $isAdmin)
Write-Host ""

if ($Execute -and -not $isAdmin) {
    Write-Host "需要管理员权限才能删除 NRPT 规则/路由。请以管理员身份重开 PowerShell 后重跑。" -ForegroundColor Red
    exit 2
}

# ---------------------------------------------------------------- VPN 状态
$connected = @()
try {
    $connected += @(Get-VpnConnection -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -like "*ClashUWP*" -and $_.ConnectionStatus -eq "Connected" })
    $connected += @(Get-VpnConnection -AllUserConnection -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -like "*ClashUWP*" -and $_.ConnectionStatus -eq "Connected" })
} catch { }

if ($connected.Count -gt 0) {
    Write-Host ("检测到 ClashUWP VPN 仍为 Connected：" + (($connected | ForEach-Object { $_.Name }) -join ", ")) -ForegroundColor Yellow
    Write-Host "  正常做法是先断开 VPN 再清理；若整机已断、只有 -Force 能救，请自行确认后再加 -Force。" -ForegroundColor Yellow
    Write-Host ""
}

# ---------------------------------------------------------------- NRPT 候选
$tunnelDns = @("223.5.5.5", "119.29.29.29")
$namePattern = "ClashUWP|421769c5-17f4-4287-b6c1-193222d21e14"
$allRules = @(Get-DnsClientNrptRule -ErrorAction SilentlyContinue)
$nrptCandidates = @()
$seenNrpt = @{}

foreach ($rule in $allRules) {
    $label = ("" + $rule.Name + " " + $rule.DisplayName + " " + $rule.Comment)
    $isRoot = (@($rule.Namespace) -contains ".")
    $nsHit = @(@($rule.NameServers) | Where-Object { $tunnelDns -contains $_ }).Count -gt 0
    $take = $false
    if ($AllNrpt) { $take = $true }
    elseif ($isRoot -and (($label -match $namePattern) -or $nsHit)) { $take = $true }

    if ($take -and -not $seenNrpt.ContainsKey($rule.Name)) {
        $seenNrpt[$rule.Name] = $true
        $nrptCandidates += $rule
    }
}

Write-Host ("NRPT 规则总数: " + $allRules.Count + "   候选: " + $nrptCandidates.Count)
foreach ($rule in $nrptCandidates) {
    Write-Host ("  [NRPT] " + $rule.Name +
                "  ns=" + ((@($rule.Namespace)) -join ",") +
                "  dns=" + ((@($rule.NameServers)) -join ",") +
                "  display=" + $rule.DisplayName) -ForegroundColor Yellow
}
if ($nrptCandidates.Count -eq 0 -and $allRules.Count -gt 0) {
    Write-Host "  没匹配到 ClashUWP 名字/默认隧道 DNS。若确认是残留，请人工核对后用 -AllNrpt。" -ForegroundColor DarkYellow
}

# ---------------------------------------------------------------- 路由候选
$routeCandidates = @()
$seenRoute = @{}
$routeSources = @(
    @(Get-NetRoute -ErrorAction SilentlyContinue),
    @(Get-NetRoute -PolicyStore PersistentStore -ErrorAction SilentlyContinue)
)
foreach ($table in $routeSources) {
    foreach ($rt in $table) {
        if ($rt.InterfaceAlias -notlike "*ClashUWP*") { continue }
        $key = ("" + $rt.InterfaceIndex + "|" + $rt.DestinationPrefix + "|" + $rt.NextHop + "|" + $rt.PolicyStore)
        if ($seenRoute.ContainsKey($key)) { continue }
        $seenRoute[$key] = $true
        $routeCandidates += $rt
    }
}

Write-Host ("ClashUWP 路由候选: " + $routeCandidates.Count)
foreach ($rt in $routeCandidates) {
    Write-Host ("  [ROUTE] " + $rt.AddressFamily + " " + $rt.DestinationPrefix +
                " -> " + $rt.NextHop + "  if=" + $rt.InterfaceIndex +
                " (" + $rt.InterfaceAlias + ")  store=" + $rt.PolicyStore) -ForegroundColor Yellow
}

if ($nrptCandidates.Count -eq 0 -and $routeCandidates.Count -eq 0) {
    Write-Host ""
    Write-Host "未发现 ClashUWP 相关残留（NRPT 根规则 / ClashUWP 路由）。" -ForegroundColor Green
    Write-Host "若网络仍异常：先跑 collect-evidence.ps1 取证，检查物理网卡/DNS 设置；"
    Write-Host "不要直接做「网络重置」，它会清掉更多状态。"
    exit 0
}

if (-not $Execute) {
    Write-Host ""
    Write-Host "DRY-RUN：以上只是清单，没有修改任何东西。"
    Write-Host "确认后以管理员运行：" -ForegroundColor Yellow
    Write-Host "  powershell -NoProfile -ExecutionPolicy Bypass -File Tools\TestScripts\restore-network.ps1 -Execute" -ForegroundColor Yellow
    exit 0
}

if ($connected.Count -gt 0 -and -not $Force) {
    Write-Host ""
    Write-Host "检测到 ClashUWP VPN 仍为 Connected，未做任何修改。" -ForegroundColor Red
    Write-Host "先断开 VPN；若确认要强拆，加 -Force 重跑。" -ForegroundColor Red
    exit 3
}

# ---------------------------------------------------------------- 执行
$failed = 0

Write-Host ""
Write-Host "开始清理..."
foreach ($rule in $nrptCandidates) {
    try {
        Remove-DnsClientNrptRule -Name $rule.Name -Force -ErrorAction Stop
        Write-Host ("  [删] NRPT " + $rule.Name) -ForegroundColor Yellow
    } catch {
        $failed++
        Write-Host ("  [失败] NRPT " + $rule.Name + "：" + $_.Exception.Message) -ForegroundColor Red
    }
}

foreach ($rt in $routeCandidates) {
    try {
        Remove-NetRoute -InputObject $rt -Confirm:$false -ErrorAction Stop
        Write-Host ("  [删] ROUTE " + $rt.DestinationPrefix + " if=" + $rt.InterfaceIndex) -ForegroundColor Yellow
    } catch {
        $failed++
        Write-Host ("  [失败] ROUTE " + $rt.DestinationPrefix + " if=" + $rt.InterfaceIndex +
                    "：" + $_.Exception.Message) -ForegroundColor Red
    }
}

try { Clear-DnsClientCache -ErrorAction SilentlyContinue } catch { }

# ---------------------------------------------------------------- 复核
Start-Sleep -Seconds 1
$leftRules = @(Get-DnsClientNrptRule -ErrorAction SilentlyContinue | Where-Object {
    $l = ("" + $_.Name + " " + $_.DisplayName + " " + $_.Comment)
    $root = (@($_.Namespace) -contains ".")
    $ns = @(@($_.NameServers) | Where-Object { $tunnelDns -contains $_ }).Count -gt 0
    if ($AllNrpt) { $true } else { $root -and (($l -match $namePattern) -or $ns) }
})
$leftRoutes = @(Get-NetRoute -ErrorAction SilentlyContinue |
    Where-Object { $_.InterfaceAlias -like "*ClashUWP*" })

Write-Host ""
Write-Host ("复核：剩余 NRPT 候选=" + $leftRules.Count + "  剩余 ClashUWP 路由=" + $leftRoutes.Count)

$probe = ""
try {
    $r = @(Resolve-DnsName -Name "www.baidu.com" -QuickTimeout -ErrorAction Stop |
           ForEach-Object { if ($_.IPAddress) { $_.IPAddress } elseif ($_.IP6Address) { $_.IP6Address } })
    if ($r.Count -gt 0) { $probe = ($r -join ",") } else { $probe = "(无地址记录)" }
} catch {
    $probe = "失败：" + $_.Exception.Message
}
Write-Host ("DNS 解析自检 www.baidu.com = " + $probe)

if ($failed -gt 0 -or $leftRules.Count -gt 0 -or $leftRoutes.Count -gt 0) {
    Write-Host "仍有残留或删除失败；请把上面的输出发给开发者，不要反复跑网络重置。" -ForegroundColor Red
    exit 1
}

Write-Host "清理完成：未发现 ClashUWP 残留。" -ForegroundColor Green
exit 0
