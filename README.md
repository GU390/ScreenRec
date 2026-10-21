<div align="center">

# ScreenRec 屏幕录像

**Windows 整屏 / 矩形区域录像小工具 —— 单文件，免安装，拖到哪都能跑**

[![Release](https://img.shields.io/badge/下载-Releases-blue)](https://github.com/GU390/ScreenRec/releases)
[![构建](https://img.shields.io/badge/构建-GitHub%20Actions-orange)](https://github.com/GU390/ScreenRec/actions)
[![平台](https://img.shields.io/badge/平台-Windows%2010%2B-lightgrey)](https://github.com/GU390/ScreenRec)
[![许可](https://img.shields.io/badge/许可-MIT-green)](LICENSE)

</div>

---

## 这是什么

一个**单文件 exe**的屏幕录像工具。双击就能录，不需要装 .NET、不需要装解码器、不需要注册驱动。

- **整屏录像** 或 **矩形区域录像**（框选画面任意一块）
- 输出 **MP4 (H.264)**（体积小，能直接发微信/插文档）或 **AVI (MJPEG)**（零依赖，不需要任何外部程序）
- 录制时右上角有**悬浮提示条**：时长、分辨率、帧数、文件大小、实时预览，一键停止
- 全局快捷键 `F9` 开始/停止、`F10` 框选、`Esc` 停止 —— 不用切窗口
- 框选时显示**实时尺寸和坐标**，所见即所得
- **不联网、不写注册表、不装任何东西**，配置就存在 exe 同目录的 `settings.json`

## 下载

👉 **[Releases 页面](https://github.com/GU390/ScreenRec/releases)** 下载 `ScreenRec.exe`

| 文件 | 说明 |
|---|---|
| `ScreenRec.exe` | Windows x64 单文件自包含（约 66 MB），解压即用 |

下载后双击即可运行。**第一次运行会在 exe 同目录生成 `settings.json`**（记录你的参数和输出目录），删掉就恢复默认。

> **想要 MP4 输出？** MP4 模式需要 `ffmpeg.exe`（可选）。
> 把它**放在 `ScreenRec.exe` 同一个文件夹**里，程序会自动找到；
> 或者启动后在界面里点「选择…」手动指定。
> 找不到 ffmpeg 时程序会弹窗提示并自动回落到 AVI 模式，**不会静默失败**。

## 使用方法

1. 双击 `ScreenRec.exe`
2. 选「整屏录制」或「矩形区域」
   - 矩形区域：点「框选区域…」→ 屏幕上铺一层遮罩 → 拖拽框选 → **回车**确认
   - 框选时选区内可以再拖动移动、四角可以缩放
3. 点「开始录像」，再点「停止录像」
4. 文件默认存在**桌面「录像」文件夹**，也可以在界面上改

### 快捷键

| 键 | 作用 |
|---|---|
| `F9` | 开始 / 停止录像 |
| `F10` | 打开区域框选 |
| `Esc` | 停止录像 |
| `回车` | 框选时确认 |
| `R` | 框选时重新框选 |

> **中文输入法开着的时候快捷键失灵？** 不会。
> 本工具的快捷键走的是**系统底层键盘钩子**，读的是真实虚拟键码，输入法拦不住；
> 框选界面上另外还有「确认 / 重新框选 / 取消」三个按钮，鼠标也永远能用。

### 输出格式（三选一）

| 选项 | 产物 | 需要 ffmpeg |
|---|---|---|
| **AVI (MJPEG) · 零依赖** | `.avi` | 否 |
| **MP4 (H.264) · 体积小**（默认） | `.mp4` | 是 |
| **两者都要** | `.mp4` + `.avi` 母版 | 是 |

MP4 模式是**边录边压**，录完立刻出文件，不用等。
体积对比（1080p 实测）：动态画面 6.9 MB → 2.8 MB；静态画面可以小到 1/25 甚至 1/80。

## 常见问题

**Q：界面提示「全局快捷键 F9 / Esc 注册失败」？**
A：说明已经有一个 ScreenRec 在运行（快捷键是系统级资源，同一时间只能被一个进程占用）。关掉另一个实例即可。

**Q：框选出来的区域和我想的不一样？**
A：点界面上的「**坐标自检…**」，它会当场量出你的屏幕缩放系数、遮罩层实际位置、以及每个测试点的换算误差。如果显示「新算法 0 像素」，那录出来的区域就是精确的；界面上选区标签里的「起点 X,Y」是**虚拟屏幕绝对物理坐标**，和录出来的文件完全对得上。

**Q：录不到鼠标指针？**
A：GDI 抓屏本身不含光标，这是 Windows 抓屏接口的固有限制。

**Q：画面右上角有一块是静止的？**
A：那是悬浮提示条所在的位置，被刻意排除在录制区域外（排除区会用录制开始那一刻的画面填充），这样提示条不会出现在录像里。

**Q：受保护视频（DRM）录出来是黑屏？**
A：正常。DRM 视频不允许被捕获。

**Q：多显示器缩放不一样时框选会偏？**
A：单显示器无此问题。多显示器不同缩放时，跨屏框选可能存在偏差 —— 用「坐标自检」确认。

## 系统要求

- Windows 10 / 11，x64
- 无需安装 .NET、无需管理员权限
- MP4 模式需要 `ffmpeg.exe`（可选，仅需含 libx264 的版本）

## 从源码编译

```powershell
git clone https://github.com/GU390/ScreenRec.git
cd ScreenRec
dotnet publish ScreenRec.csproj -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true -p:DebugType=none -o publish
```

需要 .NET 10 SDK。产物在 `publish\ScreenRec.exe`。

本仓库自带 GitHub Actions：推 `v*` 标签或手动触发，会在 `windows-latest` 上自动编译并打包成 Release 产物。

## 隐私

- **不联网**：程序没有任何网络请求
- **不写注册表**：只读取显示设备和系统 JPEG 编码器
- **只写两处**：exe 同目录的 `settings.json`，以及你指定的输出目录
- 所有设置都在你自己电脑上，删掉 `settings.json` 即完全复位

## 许可

MIT，见 [LICENSE](LICENSE)。

## 致谢

- 音视频编码由 [FFmpeg](https://ffmpeg.org/) 承担（可选依赖）
- 封装、界面、录制核心均为本仓库自研实现
