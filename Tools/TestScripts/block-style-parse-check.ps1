# 离线复算两件事（不改产品代码，只照抄 SubscriptionView.Parse / LockUrlTestTolerance 的判据）
# 1) 块写法订阅的节点数是否会虚高（嵌套列表项被当成节点）
# 2) 块写法 url-test 组的 tolerance 是否真的被锁死
param([string]$Path = (Join-Path $PSScriptRoot 'fixtures\block-style-sample.yaml'))

$lines = [System.IO.File]::ReadAllLines($Path)

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

"### 文件: $Path"

# ---- ① SubscriptionView.Parse 的节点/规则/组计数 ----
$ps = FindTop 'proxies'; $pe = FindNext $ps
$node = 0; $nodeLines = @()
for ($i = $ps + 1; $i -lt $pe; $i++) {
  $t = $lines[$i].Trim()
  if ($t.StartsWith('- ')) { $node++; $nodeLines += ("    行 {0}: {1}" -f ($i + 1), $t) }
}
$gs = FindTop 'proxy-groups'; $ge = FindNext $gs
$gn = @()
for ($i = $gs + 1; $i -lt $ge; $i++) {
  $m = [regex]::Match($lines[$i], '-\s*\{?\s*name:\s*["'']?([^,"''\}]+)')
  if ($m.Success) { $gn += $m.Groups[1].Value.Trim() }
}
$rs = FindTop 'rules'; $re = FindNext $rs
$rule = 0
for ($i = $rs + 1; $i -lt $re; $i++) { if ($lines[$i].Trim().StartsWith('- ')) { $rule++ } }

"Parser 节点数 = $node  （真值: 顶层代理条目 2 个）"
$nodeLines
"Parser 组 = $($gn.Count)  名称: $($gn -join ' | ')"
"Parser 规则数 = $rule  （真值 1）"

# ---- ② LockUrlTestTolerance 的块写法分支 ----
$work = [System.Collections.Generic.List[string]]::new()
$lines | ForEach-Object { [void]$work.Add($_) }
$locked = '999999'
$gstart = FindTop 'proxy-groups'; $gend = FindNext $gstart
for ($i = $gstart + 1; $i -lt $gend; $i++) {
  $line = $work[$i]
  if ($line.Trim().Length -eq 0) { continue }
  if ($line.TrimStart().StartsWith('type:') -or $line.TrimStart().StartsWith('type :')) {
    if (-not $line.Contains('url-test')) { continue }
    for ($j = $i + 1; $j -lt $gend; $j++) {
      $t = $work[$j].Trim()
      if ($t.StartsWith('- ')) {
        "tolerance 分支: 从 type 行往下扫, 在第 $($j + 1) 行被 '$t' 判成下一个组 → break"
        break
      }
      if ($t.StartsWith('tolerance:')) { $work[$j] = "    tolerance: $locked"; break }
    }
    continue
  }
}
$stillThere = ($work | Select-String -Pattern '^\s*tolerance:' | ForEach-Object { $_.Line.Trim() }) -join ' / '
"结果 tolerance 行: $(if ([string]::IsNullOrEmpty($stillThere)) { '(没有 tolerance 行了)' } else { $stillThere })"
