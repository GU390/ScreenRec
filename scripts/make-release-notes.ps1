# 生成 Release 说明（含本次产物的 SHA256），供 softprops/action-gh-release 的 body_path 使用。
# 用法: pwsh -File scripts/make-release-notes.ps1
param(
  [string]$Exe = 'out\ScreenRec.exe',
  [string]$Out = 'release-notes.md'
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $Exe)) { throw "找不到产物: $Exe" }
$hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $Exe).Hash.ToLower()
$size = [math]::Round((Get-Item -LiteralPath $Exe).Length / 1MB, 2)

$lines = @(
  '### ScreenRec 屏幕录像',
  '',
  'Windows 整屏 / 矩形区域屏幕录像小工具。',
  '',
  '**下载下面的 `ScreenRec.exe`，双击即用** —— 不需要装 .NET，不需要装解码器，不需要管理员权限。',
  '',
  '| 特性 | 说明 |',
  '|---|---|',
  '| 输出格式 | MP4 (H.264) 或 AVI (MJPEG)，可三选一（默认 MP4） |',
  '| 区域框选 | 拖拽框选任意矩形，实时显示像素尺寸与绝对坐标 |',
  '| 悬浮提示条 | 时长 / 分辨率 / 帧数 / 文件大小 / 实时预览，一键停止 |',
  '| 快捷键 | `F9` 开始/停止、`F10` 框选、`Esc` 停止 —— 中文输入法下同样有效 |',
  '| 隐私 | 不联网、不写注册表，配置只存在 exe 同目录的 `settings.json` |',
  '',
  '**MP4 模式需要 `ffmpeg.exe`**（可选依赖）：把它放在 `ScreenRec.exe` 同一个文件夹里即可；',
  '找不到时程序会弹窗提示并自动回落到 AVI，不会静默失败。AVI 模式完全零依赖。',
  '',
  '### 校验',
  '',
  'SHA256 (`ScreenRec.exe`):',
  '```',
  $hash,
  '```',
  '',
  "大小：$size MB",
  '',
  '构建来源：GitHub Actions 云端编译（`windows-latest` / .NET 10 SDK）。',
  '云端编译后会自动跑一次命令行自测（录 3 秒并校验产物），通过才发布。',
  '',
  '源码：https://github.com/GU390/ScreenRec'
)

Set-Content -LiteralPath $Out -Value ($lines -join "`n") -Encoding utf8
Write-Host "release notes -> $Out"
Write-Host "sha256 = $hash"
Write-Host "size   = $size MB"
