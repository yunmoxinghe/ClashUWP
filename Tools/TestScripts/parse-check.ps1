# ★【1.0.89 · Docs/38 §十四 · F5 同族】包目录名原先写死成 421769c5-…_y0tg80em3rma2（3b11f69 改名之前的家族）
#   ⇒ 改名后每次都在 ReadAllLines 抛"找不到文件"。改成从已装包反查，与 collect-evidence.ps1 同一口径。
$pkg = Get-AppxPackage | Where-Object { ($_.Name -like "*ClashUWP*") -and ($_.Name -notlike "*.Cli") } |
       Sort-Object Version -Descending | Select-Object -First 1
if ($null -eq $pkg) { Write-Error "没找到已装的 ClashUWP 主应用包（按 *ClashUWP* 且非 *.Cli 反查）"; exit 1 }
$p = Join-Path $env:LOCALAPPDATA ("Packages\" + $pkg.PackageFamilyName + "\LocalState\config.yaml")
if (-not (Test-Path -LiteralPath $p)) { Write-Error "config.yaml 不存在：$p"; exit 1 }
$lines = [System.IO.File]::ReadAllLines($p)

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

$ps = FindTop 'proxies'; $pe = FindNext $ps
$node = 0
for ($i = $ps + 1; $i -lt $pe; $i++) { $t = $lines[$i].Trim(); if ($t.StartsWith('- ')) { $node++ } }

$rs = FindTop 'rules'; $re = FindNext $rs
$rule = 0
for ($i = $rs + 1; $i -lt $re; $i++) { $t = $lines[$i].Trim(); if ($t.StartsWith('- ')) { $rule++ } }

$gs = FindTop 'proxy-groups'; $ge = FindNext $gs
$gn = New-Object System.Collections.ArrayList
$pat = '-\s*\{?\s*name:\s*["'']?([^,"''\}]+)'
for ($i = $gs + 1; $i -lt $ge; $i++) {
  $m = [regex]::Match($lines[$i], $pat)
  if ($m.Success) { [void]$gn.Add($m.Groups[1].Value.Trim()) }
}

"proxies lines $ps..$pe -> nodes=$node"
"rules lines $rs..$re -> rules=$rule"
"groups lines $gs..$ge -> count=$($gn.Count)"
$gn | ForEach-Object { "  - [$_]" }

# truth check: real group names from raw text
$truth = New-Object System.Collections.ArrayList
for ($i = $gs + 1; $i -lt $ge; $i++) {
  $t = $lines[$i].Trim()
  if ($t.StartsWith('- name:')) { [void]$truth.Add($t.Substring('- name:'.Length).Trim()) }
}
"truth groups=$($truth.Count)"
