# 更新日志 / Changelog

本文件记录 QingSnap 各公开预览版本的重要变化。
This file documents notable changes in each public QingSnap preview release.

## [Preview v44] - 2026-08-24

### 新增 / Added

- 普通贴图与长图贴图现在都可以按 `M` 暂存为屏幕侧边的缩略图标签；标签会自动靠近最近的屏幕边缘，平时仅露出提示条，悬停时展开，单击或再次按 `M` 即可恢复原来的贴图位置。
  Normal and long-image pins can now be stashed as edge-docked thumbnail tabs with `M`. Tabs snap to the nearest screen edge, peek when idle, expand on hover, and return to their previous position with a click or another press of `M`.
- 展开的贴图可直接拖到屏幕左侧或右侧并松开，通过缓出动画缩成当前位置的缩略窗；完整贴图位置与缩略窗停靠位置会分别记忆。
  Drag an expanded pin to either screen edge and release it to collapse the pin into a thumbnail with an eased animation. Expanded and docked positions are remembered separately.
- 新增关闭交互设置：可选择仅使用 `Esc`，或同时在截图工具栏和贴图上显示 `X` 按钮；贴图关闭按钮会自动渐隐并在悬停时重新出现。
  Added a close-interaction preference: use `Esc` only, or also show `X` buttons on the capture toolbar and pinned images. The pin button fades automatically and reappears on hover.
- 截图时可使用 `W`、`A`、`S`、`D` 将十字准星每次移动 1 像素，便于精确取边。
  The capture crosshair can now be nudged one pixel at a time with `W`, `A`, `S`, and `D` for precise edge selection.

### 改进 / Improved

- 优化“截图后立即贴图”的切换流程：贴图完成首帧绘制后再关闭截图遮罩，减少窗口切换时的闪烁和空白帧。
  Improved the capture-to-pin transition by keeping the capture overlay until the pin presents its first frame, reducing flashes and blank frames.
- 改进普通贴图的滚轮缩放：按滚轮步数连续缩放，以贴图中心为锚点，并使用即时的线性缩放渲染，使交互更稳定。
  Improved mouse-wheel zoom for normal pins with multi-step scaling, center anchoring, and immediate linear rendering for steadier interaction.
- 改进 Per-Monitor V2 高 DPI 初始化、贴图首次定位和像素对齐，在多显示器及不同缩放比例下显示更稳定。
  Improved Per-Monitor V2 high-DPI initialization, initial pin placement, and pixel alignment for more stable rendering across monitors and scale factors.
- 贴图未指定来源位置时会居中显示；截图快捷键现在可正确处理系统键、输入法处理键和死键映射。
  Pins without a source position now open centered, while capture shortcuts correctly resolve system, IME-processed, and dead-key events.

### 修复 / Fixed

- 修复中文输入法可能抢占截图窗口快捷键的问题，同时保留文字标注编辑框中的输入法支持。
  Fixed Chinese IME interference with capture shortcuts while preserving IME input inside text annotation editors.
- 修复部分 DPI 环境下贴图首次显示、边框尺寸和缩放窗口位置不够稳定的问题。
  Fixed unstable first-frame presentation, border sizing, and scaled-window placement in some DPI configurations.

## [Preview v34] - 2026-08-19

- QingSnap 首个公开预览版本。
  Initial public preview of QingSnap.
- 包含区域截图、智能选区、自动与手动长截图、离线 OCR、截图标注、贴图、历史记录和系统托盘支持。
  Included region capture, smart selection, automatic and manual scrolling capture, offline OCR, annotation, pinned images, history, and system tray support.

[Preview v44]: https://github.com/za5132-web/QingSnap/compare/preview-v34...preview-v44
[Preview v34]: https://github.com/za5132-web/QingSnap/releases/tag/preview-v34
