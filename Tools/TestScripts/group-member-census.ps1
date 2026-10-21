# 按内核 /proxies 的口径，把本机生效订阅的"组 / 真节点 / 内置策略"分开统计，
# 看 UI 的 GroupPicker 计数与 MemberList 列表里混进了什么。
# ★【1.0.89 · Docs/38 §十四 · F5 同族】包目录名原先写死成 <c>421769c5-…_y0tg80em3rma2</c>，那是
#   <c>3b11f69</c>（1.0.74→1.0.75）改名<b>之前</b>的家族 ⇒ 改名后这个脚本每次都在 ReadAllLines 抛
#   "找不到文件"，看起来像"没有 config.yaml"，其实是"在找一个不存在的包目录"。
#   改成从已装包反查（排除 CLI 那个包），与 collect-evidence.ps1 同一口径。
$pkg = Get-AppxPackage | Where-Object { ($_.Name -like "*ClashUWP*") -and ($_.Name -notlike "*.Cli") } |
       Sort-Object Version -Descending | Select-Object -First 1
if ($null -eq $pkg) { Write-Error "没找到已装的 ClashUWP 主应用包（按 *ClashUWP* 且非 *.Cli 反查）"; exit 1 }
$dir = Join-Path $env:LOCALAPPDATA ("Packages\" + $pkg.PackageFamilyName + "\LocalState")
$cfg = Join-Path $dir "config.yaml"
if (-not (Test-Path -LiteralPath $cfg)) { Write-Error "config.yaml 不存在：$cfg"; exit 1 }
$lines = [System.IO.File]::ReadAllLines($cfg)

function FindTop([string]$k) {
  for ($i = 0; $i -lt $lines.Count; $i++) {
    $l = $lines[$i]
    if ($l.Length -eq 0 -or [char]::IsWhiteSpace($l[0])) { continue }
    $t = $l.TrimEnd()
    if ($t -eq ($k + ':') -or $t.StartsWith($k + ': ')) { return $i }
  }
  return -1
}
function FindNext([int]$s) {
  for ($i = $s + 1; $i -lt $lines.Count; $i++) {
    $l = $lines[$i]
    if ($l.Length -eq 0 -or [char]::IsWhiteSpace($l[0])) { continue }
    return $i
  }
  return $lines.Count
}

# 真节点名（proxies 段顶层条目）
$ps = FindTop 'proxies'; $pe = FindNext $ps
$realNodes = [System.Collections.Generic.HashSet[string]]::new()
for ($i = $ps + 1; $i -lt $pe; $i++) {
  $m = [regex]::Match($lines[$i], '-\s*\{?\s*name:\s*["'']?([^,"''\}]+)')
  if ($m.Success) { [void]$realNodes.Add($m.Groups[1].Value.Trim()) }
}

# 组名（proxy-groups 段）
$gs = FindTop 'proxy-groups'; $ge = FindNext $gs
$groupNames = [System.Collections.Generic.HashSet[string]]::new()
for ($i = $gs + 1; $i -lt $ge; $i++) {
  $m = [regex]::Match($lines[$i], '-\s*\{?\s*name:\s*["'']?([^,"''\}]+)')
  if ($m.Success) { [void]$groupNames.Add($m.Groups[1].Value.Trim()) }
}

$builtin = @('GLOBAL','DIRECT','REJECT','REJECT-DROP','PASS','PASS-RULE','COMPATIBLE')

"真节点 $($realNodes.Count) 个 / 组 $($groupNames.Count) 个"
''
# 逐组成员分类（块写法：组名行之后 proxies: 下的 "- xxx"）
$i = $gs + 1
while ($i -lt $ge) {
  $t = $lines[$i].Trim()
  if ($t.StartsWith('- name:')) {
    $gname = $t.Substring('- name:'.Length).Trim()
    $members = @()
    for ($j = $i + 1; $j -lt $ge; $j++) {
      $s = $lines[$j].Trim()
      if ($s.StartsWith('- name:')) { break }
      if ($s.StartsWith('- ')) { $members += $s.Substring(2).Trim() }
    }
    $nodes = @($members | Where-Object { $realNodes.Contains($_) })
    $nested = @($members | Where-Object { $groupNames.Contains($_) })
    $pol = @($members | Where-Object { $builtin -contains $_ })
    $other = @($members | Where-Object { -not ($realNodes.Contains($_) -or $groupNames.Contains($_) -or ($builtin -contains $_)) })
    $sp = if ($gname.Contains(' ')) { '含空格' } else { '无空格' }
    '{0}  UI计数={1} 真节点={2} 嵌套组={3} 内置策略={4} 其他={5} 组名{6}' -f $gname, $members.Count, $nodes.Count, $nested.Count, $pol.Count, $other.Count, $sp
    if ($nested.Count) { '    嵌套组: ' + ($nested -join ' | ') }
    if ($pol.Count) { '    内置策略: ' + ($pol -join ' | ') }
    if ($other.Count) { '    其他: ' + ($other -join ' | ') }
    $i = $j
  } else { $i++ }
}
