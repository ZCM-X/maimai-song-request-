# 更新曲目别名库 tools/aliases.txt（数据来自 MuNET 公共 API）
# 用法:  pwsh -File tools/update-aliases.ps1 -ApiKey <你的MuNETKey> [-Proxy http://127.0.0.1:7890]
# 说明:  key 只在本机使用, 不要提交进仓库。
param(
  [Parameter(Mandatory=$true)][string]$ApiKey,
  [string]$BaseUrl = "https://api.mumur.net:42081",
  [string]$Proxy   = "",
  [string]$OutFile = (Join-Path $PSScriptRoot "..\src\aliases.txt")
)
$p = @{}
if ($Proxy) { $p['Proxy'] = $Proxy }
$r = Invoke-WebRequest "$BaseUrl/v1/mai2/music" -Headers @{ 'X-API-Key' = $ApiKey; 'User-Agent' = 'SongRequest-aliases-updater' } -TimeoutSec 120 -UseBasicParsing @p
$list = ($r.Content | ConvertFrom-Json).musicData
$sb = New-Object Text.StringBuilder
[void]$sb.AppendLine("# 曲目别名库 —— 制表符分隔: 曲名 <Tab> 别名 <Tab> 别名 ...")
[void]$sb.AppendLine("# 数据来源: MuNET 公共 API /v1/mai2/music ( https://api.mumur.net )")
[void]$sb.AppendLine("# 原始社区别名由 clansty 的 SongSearch 项目维护, 感谢作者与贡献者")
[void]$sb.AppendLine("# 可以直接编辑本文件, 保存后刷新网页即生效。 # 开头是注释")
$songs = 0; $ali = 0
foreach ($m in ($list | Sort-Object id)) {
  $a = @($m.aliases | Where-Object { $_ -and $_.Trim().Length -gt 0 -and $_ -ne $m.name })
  if ($a.Count -eq 0) { continue }
  [void]$sb.AppendLine($m.name + "`t" + ($a -join "`t")); $songs++; $ali += $a.Count
}
[IO.File]::WriteAllText($OutFile, $sb.ToString(), (New-Object Text.UTF8Encoding $false))
Write-Host "写入 $OutFile : $songs 首 / $ali 条别名"
