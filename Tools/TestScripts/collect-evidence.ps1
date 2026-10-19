<#
ClashUWP 现场取证（只读）—— 1.0.71 / G6 / G7 / DNS 可配

只读脚本：不安装、不连接、不断开、不改任何网络或系统设置。
它只做四件事：读包与 LocalState、读 VPN/NRPT/路由/接口、读日志快照、
（可选）跑一次系统 DNS 解析看 fake-ip 判据。

用法：
  powershell -NoProfile -ExecutionPolicy Bypass -File Tools\TestScripts\collect-evidence.ps1
  powershell -NoProfile -ExecutionPolicy Bypass -File Tools\TestScripts\collect-evidence.ps1 -OutFile C:\temp\clashuwp.txt
  ... -SkipDnsProbe     # 连 DNS 查询都不发
#>
[CmdletBinding()]
param(
    [string]$OutFile = "",
    [switch]$SkipDnsProbe
)

$ErrorActionPreference = "Continue"
$script:out = New-Object System.Collections.Generic.List[string]

function Add-Ev([string]$text = "") { $script:out.Add($text) }

function Add-Section([string]$title) {
    Add-Ev ""
    Add-Ev ("==== " + $title + " ====")
}

function Add-Table([string]$title, $rows) {
    Add-Section $title
    $arr = @($rows)
    if ($arr.Count -eq 0) { Add-Ev "  (无)"; return }
    $text = ($arr | Format-Table -AutoSize | Out-String).TrimEnd()
    foreach ($line in ($text -split "`r?`n")) { Add-Ev ("  " + $line) }
}

Add-Ev ("ClashUWP evidence    " + (Get-Date -Format "yyyy-MM-dd HH:mm:ss"))
Add-Ev ("Host=" + $env:COMPUTERNAME + "  User=" + $env:USERNAME)
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
Add-Ev ("PowerShell=" + $PSVersionTable.PSVersion + "  Admin=" + $isAdmin)

# ---------------------------------------------------------------- 包与 LocalState
Add-Section "包"
$pkgName = "421769c5-17f4-4287-b6c1-193222d21e14"
$pkg = Get-AppxPackage -Name $pkgName -ErrorAction SilentlyContinue |
       Sort-Object Version -Descending | Select-Object -First 1
$localState = ""
if ($null -eq $pkg) {
    Add-Ev ("  未安装包：" + $pkgName)
} else {
    Add-Ev ("  Name=" + $pkg.Name + "  Version=" + $pkg.Version + "  Arch=" + $pkg.Architecture)
    Add-Ev ("  Family=" + $pkg.PackageFamilyName)
    Add-Ev ("  Install=" + $pkg.InstallLocation)
    $localState = Join-Path $env:LOCALAPPDATA ("Packages\" + $pkg.PackageFamilyName + "\LocalState")
    Add-Ev ("  LocalState=" + $localState + "  exists=" + (Test-Path -LiteralPath $localState))
}

function Show-File([string]$name, [string]$path) {
    Add-Section ("文件 " + $name)
    if (-not (Test-Path -LiteralPath $path)) { Add-Ev "  (不存在)"; return }
    $item = Get-Item -LiteralPath $path
    Add-Ev ("  path=" + $path)
    Add-Ev ("  size=" + $item.Length + "  mtime=" + $item.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss"))
    Get-Content -LiteralPath $path -ErrorAction SilentlyContinue |
        ForEach-Object {
            $line = $_
            # 订阅 URL 里带 token：取证文件可能被贴给别人，默认打码。
            if ($name -eq "settings.txt") {
                $line = $line -replace '(?i)^(\s*subUrl:[^=]+=).*$', '$1(redacted)'
            }
            Add-Ev ("  | " + $line)
        }
}

function Show-KernelDnsTun([string]$path) {
    Add-Section "config.kernel.yaml 的 ipv6 / dns / tun 块"
    if (-not (Test-Path -LiteralPath $path)) { Add-Ev "  (不存在)"; return }
    $keep = $false
    foreach ($line in Get-Content -LiteralPath $path -ErrorAction SilentlyContinue) {
        if ($line -match '^[^\s#][^:]*:') {
            $keep = ($line -match '^(ipv6|dns|tun)\s*:')
        }
        if ($keep) { Add-Ev ("  | " + $line) }
    }
}

if ($localState.Length -gt 0) {
    Show-File "settings.txt" (Join-Path $localState "settings.txt")
    Show-File "dns-config.txt" (Join-Path $localState "dns-config.txt")
    Show-File "split-apps.txt" (Join-Path $localState "split-apps.txt")
    Show-File "dataface-plugin.txt（上次连接快照）" (Join-Path $localState "dataface-plugin.txt")
    Show-KernelDnsTun (Join-Path $localState "config.kernel.yaml")

    Add-Section "logs 目录"
    $logDir = Join-Path $localState "logs"
    if (-not (Test-Path -LiteralPath $logDir)) {
        Add-Ev "  (无 logs 目录)"
    } else {
        Get-ChildItem -LiteralPath $logDir -File -ErrorAction SilentlyContinue |
            Sort-Object LastWriteTime -Descending | Select-Object -First 12 |
            ForEach-Object {
                Add-Ev ("  " + $_.Name.PadRight(24) + $_.Length.ToString().PadLeft(10) +
                        "  " + $_.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss"))
            }
        foreach ($name in @("app.log", "plugin.log")) {
            $p = Join-Path $logDir $name
            if (Test-Path -LiteralPath $p) {
                Add-Section ($name + " 最后 80 行")
                Get-Content -LiteralPath $p -Tail 80 -ErrorAction SilentlyContinue |
                    ForEach-Object { Add-Ev ("  | " + $_) }
            }
        }
    }
}

# ---------------------------------------------------------------- VPN profile
Add-Section "VPN 连接状态"
$vpnCurrent = @()
$vpnAll = @()
try { $vpnCurrent = @(Get-VpnConnection -ErrorAction SilentlyContinue) } catch { }
try { $vpnAll = @(Get-VpnConnection -AllUserConnection -ErrorAction SilentlyContinue) } catch { }
Add-Table "CurrentUser 存储" ($vpnCurrent | Select-Object Name, ServerAddress, ConnectionStatus, TunnelType)
Add-Table "AllUser 存储"     ($vpnAll     | Select-Object Name, ServerAddress, ConnectionStatus, TunnelType)

# ---------------------------------------------------------------- NRPT
Add-Section "NRPT 规则（Get-DnsClientNrptRule）"
$nrptRules = @(Get-DnsClientNrptRule -ErrorAction SilentlyContinue)
Add-Table "NRPT rules" ($nrptRules | Select-Object Name, DisplayName,
    @{n='Namespace';   e={(@($_.Namespace) -join ',')}},
    @{n='NameServers'; e={(@($_.NameServers) -join ',')}})

Add-Section "NRPT 生效策略（Get-DnsClientNrptPolicy）"
$nrptPolicy = @(Get-DnsClientNrptPolicy -ErrorAction SilentlyContinue)
Add-Table "NRPT policy" ($nrptPolicy | Select-Object Name,
    @{n='Namespace';   e={(@($_.Namespace) -join ',')}},
    @{n='NameServers'; e={(@($_.NameServers) -join ',')}})

# ---------------------------------------------------------------- 接口 / 地址 / 路由
Add-Section "ClashUWP 接口（IPv4/IPv6）"
$ifs = @(Get-NetIPInterface -ErrorAction SilentlyContinue |
         Where-Object { $_.InterfaceAlias -like "*ClashUWP*" })
Add-Table "接口" ($ifs | Select-Object InterfaceIndex, InterfaceAlias, AddressFamily,
    ConnectionState, InterfaceMetric, NlMtu)

Add-Section "ClashUWP 地址"
$addrs = @(Get-NetIPAddress -ErrorAction SilentlyContinue |
           Where-Object { $_.InterfaceAlias -like "*ClashUWP*" })
Add-Table "地址" ($addrs | Select-Object InterfaceAlias, AddressFamily, IPAddress, PrefixLength)

Add-Section "ClashUWP 路由"
$routes = @(Get-NetRoute -ErrorAction SilentlyContinue |
            Where-Object { $_.InterfaceAlias -like "*ClashUWP*" })
Add-Table "路由" ($routes | Select-Object AddressFamily, DestinationPrefix, NextHop,
    InterfaceIndex, InterfaceAlias, RouteMetric, PolicyStore)

Add-Section "疑似幽灵路由（接口索引已不存在）"
$liveIndexes = @(Get-NetIPInterface -ErrorAction SilentlyContinue |
                 Select-Object -ExpandProperty InterfaceIndex)
$ghost = @(Get-NetRoute -ErrorAction SilentlyContinue |
           Where-Object { $liveIndexes -notcontains $_.InterfaceIndex })
if ($ghost.Count -eq 0) {
    Add-Ev "  (无)"
} else {
    Add-Ev ("  共 " + $ghost.Count + " 条，前 30 条：")
    Add-Table "ghost routes" ($ghost | Select-Object -First 30 |
        Select-Object AddressFamily, DestinationPrefix, NextHop, InterfaceIndex,
        InterfaceAlias, RouteMetric, PolicyStore)
}

# ---------------------------------------------------------------- DNS 探针
if (-not $SkipDnsProbe) {
    Add-Section "系统解析器（fake-ip 判据）"
    foreach ($name in @("www.google.com")) {
        foreach ($type in @("A", "AAAA")) {
            try {
                $res = @(Resolve-DnsName -Name $name -Type $type -QuickTimeout -ErrorAction Stop)
                $ips = @($res | ForEach-Object {
                    if ($_.IPAddress)   { $_.IPAddress }
                    elseif ($_.IP6Address) { $_.IP6Address }
                })
                if ($ips.Count -eq 0) {
                    Add-Ev ("  " + $name + " " + $type + " = (无地址记录)")
                } else {
                    Add-Ev ("  " + $name + " " + $type + " = " + ($ips -join ", "))
                }
            } catch {
                Add-Ev ("  " + $name + " " + $type + " = 失败：" + $_.Exception.Message)
            }
        }
    }
} else {
    Add-Section "系统解析器"
    Add-Ev "  (按 -SkipDnsProbe 跳过)"
}

# ---------------------------------------------------------------- 判读
Add-Section "判读提示"
Add-Ev "  · dataface-plugin.txt 的 DnsIntoTunnel 行应与 NRPT 规则里的 DNS 一致；"
Add-Ev "    不一致 = NRPT 没换（或改了 nrpt-dns 但没断开重连）。"
Add-Ev "  · Ipv6= 行：arm=0 必须是「未下发」；arm>=1 应能看到 v6 地址与 v6 路由。"
Add-Ev "  · Dns= 行：应用 N 项 / 跳过什么 / 拒绝几行，与 dns-config.txt 对照。"
Add-Ev "  · 断开后：NRPT 不应残留 Namespace=. 的规则；ClashUWP 路由应为 0。"
Add-Ev "  · 整机断网时先跑 restore-network.ps1（默认只列清单，-Execute 才清理）。"

$text = ($script:out -join [Environment]::NewLine)
if ($OutFile.Length -gt 0) {
    Set-Content -LiteralPath $OutFile -Value $text -Encoding UTF8 -ErrorAction SilentlyContinue
    Write-Host ("已写入 " + $OutFile)
}
Write-Host $text
