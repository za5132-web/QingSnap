# QingSnap（轻截）

> 轻量、聪明、离线优先的 Windows 截图与长截图工具。  
> A lightweight, intelligent, offline-first screenshot and scrolling-capture tool for Windows.

[下载最新预览版 / Download the latest preview](https://github.com/za5132-web/QingSnap/releases/latest) · [更新日志 / Changelog](CHANGELOG.md)

QingSnap 面向 Windows 10/11，使用 .NET 8 与 WPF 独立开发。它把区域截图、智能选区、自动长截图、本地 OCR、专业标注、贴图和历史记录整合在一个简洁的桌面工具中。

QingSnap is a Windows 10/11 desktop utility built independently with .NET 8 and WPF. It combines region capture, smart selection, automatic scrolling capture, local OCR, annotation, pinned images, and screenshot history in one focused app.

## 产品亮点 / Highlights

### 1. 智能选区，而不只是画一个框

鼠标悬停时自动识别底层窗口和标准控件，单击即可采用识别范围；需要精确控制时仍可自由拖动选区，并通过四边、四角或数值输入调整位置和尺寸。放大镜会实时显示像素、屏幕坐标和 HEX 颜色。

Smart selection detects windows and standard controls under the pointer. Click to use the detected region, drag freely for manual capture, resize from any edge or corner, or enter exact X/Y/width/height values. The live magnifier also shows pixel details, screen coordinates, and HEX colors.

### 2. 更可靠的自动长截图

自动模式会模拟滚动、等待画面稳定、识别页面底部并完成拼接。匹配失败时会回到最后成功位置并缩小滚动步长；还可撤销最后一段后重新补截。对于动态页面，可随时改用手动逐屏模式。

Automatic scrolling capture waits for visual stability, detects the end of a page, and stitches frames together. When matching fails, QingSnap returns to the last successful position and retries with a smaller scroll step. Manual capture remains available for animated or irregular pages.

它还能识别稀疏页面中的固定标题栏和底部操作栏，让固定区域在最终长图中只保留一次。

It can also detect fixed headers and bottom action bars on sparse pages, keeping those fixed regions only once in the final image.

### 3. 真正本地的高精度 OCR

默认使用 PP-OCRv6 Small 进行离线识别，首次使用时下载约 30.8 MB 模型；模型不可用时自动回退到 Windows OCR。图片和识别内容不会上传到云端。

Offline OCR uses PP-OCRv6 Small by default. The approximately 30.8 MB model is downloaded on first use, with automatic fallback to Windows OCR when necessary. Images and recognized text are never uploaded.

OCR 会在程序空闲时后台预热，并直接读取截图像素，避免额外的 PNG 编解码。同一图片的识别结果可复用，减少等待和重复计算。

The OCR engine preheats while the app is idle and reads screenshot pixels directly, avoiding unnecessary PNG encoding and decoding. Results for the same image are reused to reduce latency and repeated work.

### 4. 贴图也能直接选中文字

普通贴图和长图贴图都能使用 OCR 的逐字/逐词位置。无需进入额外模式，直接拖选图片中的文字并按 `Ctrl+C` 复制；空白区域仍可用于拖动贴图。

Pinned images, including long captures, retain word-level OCR positions. Drag directly across text and press `Ctrl+C` to copy it, while blank areas remain available for moving the pinned image.

### 5. 完整的截图标注工作流

支持画笔、箭头、直线、矩形、椭圆、文字、马赛克、高亮、模糊和序号。标注可移动、缩放、复制粘贴、调整端点、二次编辑文字及改变图层顺序。

Annotation tools include pen, arrow, line, rectangle, ellipse, text, mosaic, highlight, blur, and numbered markers. Objects can be moved, resized, copied, reordered, and edited after creation.

### 6. 从截图到整理的一站式体验

截图可自动复制并保存到历史记录。历史窗口提供缩略图、日期筛选、复制、打开和删除；任意历史图片都可再次 OCR 或贴到桌面。长图贴图会自动使用阅读窗口，支持滚轮浏览、阅读进度和完整概览。

Captures can be copied and saved to history automatically. The history window provides thumbnails, date filtering, copy, open, and delete actions. Any saved image can be recognized again or pinned to the desktop. Long images use a dedicated reading window with scrolling, progress indication, and overview mode.

## 快速使用 / Quick start

| 操作 / Action | 快捷键 / Shortcut |
| --- | --- |
| 区域截图 / Region capture | `F1` |
| 贴出剪贴板图片或最近截图 / Pin clipboard image or latest capture | `F3` |
| 微调截图十字准星 / Nudge the capture crosshair | `W` `A` `S` `D`（每次 1 px / 1 px per press） |
| 重新显示上次截图范围 / Restore the previous capture region | `Shift+F1` |
| 确认截图 / Confirm capture | `Enter` 或双击 / or double-click |
| OCR | `Ctrl+O` |
| 复制 / Copy | `Ctrl+C` |
| 保存 / Save | `Ctrl+S` |
| 读取上次选区 / Restore previous selection in overlay | `R` |
| 复制当前 HEX 颜色 / Copy current HEX color | `I` |
| 取消 / Cancel | `Esc` 或鼠标右键 / or right-click |

截图工具栏可直接启动自动长截图、OCR、贴图、复制、保存或完成截图。自动与手动长截图也可以从系统托盘菜单启动。

The capture toolbar provides direct access to automatic scrolling capture, OCR, pinning, copy, save, and confirmation. Automatic and manual scrolling capture can also be started from the system tray.

## 更多功能 / More features

- 自定义全局快捷键与延时截图 / Custom global hotkeys and delayed capture
- 自动复制、输出格式及 JPEG 质量设置 / Auto-copy, output format, and JPEG quality settings
- 历史目录与保留天数管理 / Configurable history directory and retention period
- 长截图参数与标注默认样式 / Scrolling-capture options and annotation defaults
- 可选显示关闭按钮；`Esc` 始终有效，启用后截图工具栏最右侧常驻 X，贴图右上角 X 自动渐隐 / Optional close buttons while `Esc` always remains available
- 贴图拖动、滚轮缩放、适屏、1:1 显示及再次复制 / Drag, zoom, fit-to-screen, 1:1 view, and recopy for pinned images
- 普通与长图贴图可按 `M` 暂存为缩略图标签；标签拖动后自动靠边，只露出提示条，悬停展开，单击或再次按 `M` 原位恢复 / Press `M` to stash a pin as an edge-snapping thumbnail that peeks from the edge, expands on hover, and restores in place on click
- 将展开的贴图拖到屏幕侧边松开，可用缓出动画直接缩成当前位置的缩略窗；大图位置与缩略窗停靠位置分别记忆 / Drop an expanded pin at a screen side to animate it into a thumbnail while remembering expanded and docked positions separately
- 单实例运行与系统托盘菜单 / Single-instance operation and system tray menu
- 浅色、深色任务栏均清晰可辨的专用托盘图标 / Dedicated tray icon designed for light and dark taskbars
- 剪贴板被占用时自动等待并友好提示 / Automatic clipboard retry with user-friendly errors
- Per-Monitor V2 DPI 感知 / Per-Monitor V2 DPI awareness
- 无原生标题栏的统一深色设置界面 / Consistent dark settings UI with a custom title bar

## 下载与运行 / Download and run

1. 从 [Releases](https://github.com/za5132-web/QingSnap/releases) 下载最新 ZIP。
2. 解压到任意文件夹。
3. 运行 `QingSnap.exe`。

1. Download the latest ZIP from [Releases](https://github.com/za5132-web/QingSnap/releases).
2. Extract it to any folder.
3. Run `QingSnap.exe`.

系统要求 / Requirements:

- Windows 10/11 64-bit
- [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0)

当前版本为预览版，程序尚未进行代码签名，Windows 首次运行时可能显示未知发布者提示。

This is a preview build and is currently unsigned, so Windows may show an unknown-publisher warning on first launch.

## 数据与隐私 / Data and privacy

- 截图记录默认保存在 `%LOCALAPPDATA%\QingSnap\History`
- OCR 模型保存在 `%LOCALAPPDATA%\QingSnap\Models`
- OCR 图片与识别文本不会上传
- 模型可在设置中心下载、校验、删除，或切换为 Windows OCR

- Screenshot history is stored in `%LOCALAPPDATA%\QingSnap\History` by default.
- OCR models are stored in `%LOCALAPPDATA%\QingSnap\Models`.
- OCR images and recognized text are not uploaded.
- Models can be downloaded, verified, removed, or replaced with Windows OCR from Settings.

## 自动长截图建议 / Scrolling capture tips

请只选择内容滚动区域，尽量排除固定标题栏、侧栏和悬浮动画。自动模式会显示逐屏进度，并在连续检测到页面不再移动时停止。按 `Enter` 可提前停止，按 `Esc` 可取消；自动停止后仍可手动补截并完成拼接。

Select only the scrollable content area when possible, excluding fixed headers, sidebars, and floating animations. Automatic mode displays frame-by-frame progress and stops when the page no longer moves. Press `Enter` to stop early or `Esc` to cancel; manual frames can still be added before final stitching.

## 从源码构建 / Build from source

```powershell
dotnet restore QingSnap.sln
dotnet build QingSnap.sln
dotnet run --project src\QingSnap.App\QingSnap.App.csproj
```

主要技术 / Main technologies:

- C# / .NET 8
- WPF + Windows Forms interoperability
- RapidOcrNet / PP-OCRv6 Small
- Native Windows capture, clipboard, hotkey, and DPI APIs

项目还包含 `tools/QingSnap.OcrBench`，用于本地 OCR 性能与识别效果测试。

The repository also includes `tools/QingSnap.OcrBench` for local OCR performance and recognition testing.

## 项目状态 / Project status

QingSnap 仍处于快速迭代的预览阶段。下一阶段将继续验证多显示器混合缩放场景，并完善历史记录搜索、标签和批量整理。

QingSnap is an actively developed preview. Planned work includes broader mixed-DPI multi-monitor testing plus history search, tags, and batch organization.

开发过程由 Codex 协助完成。 / Development is assisted by Codex.

## 许可 / License

当前未授予开源许可。源代码仅供查看；如需使用、修改或再发布，请先取得作者授权。第三方组件信息见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。

No open-source license is currently granted. The source is available for viewing only; permission is required for use, modification, or redistribution. See [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) for third-party components.
