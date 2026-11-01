# QingSnap v1.1.2

本次为小型稳定性更新，不增加新功能，不更换 OCR 引擎。

## 修复

- 修复按 `F1 → R` 调出历史范围后，移动或缩放并松开鼠标时，选区可能消失或变成零尺寸的问题。
- 同步修正 `Shift+F1` 最近范围入口涉及的空选区状态。
- 正确初始化空矩形，并在新拖动与恢复全屏蒙层时清理残留状态。

感谢 **@zch-soft** 提交的修复：[PR #1](https://github.com/za5132-web/QingSnap/pull/1)。本版本基于独立代码审查、回归测试及用户实际使用验证发布。

## 兼容与升级

- 保留 v1.1.1 的七步动画教程及全部现有功能。
- 快捷键、设置格式、历史数据库、收藏、标签和 OCR 模块不变。
- 下载 `QingSnap-v1.1.2.zip`，退出旧版后解压运行；覆盖升级时请保留原有 `Data`、`portable.flag` 及其他用户数据。
- 基础包不捆绑 .NET 8 桌面运行时或可选 OCR 模块，沿用此前部署方式。
- 使用原 QingSnap 自签名证书；Windows 仍可能提示证书链不受信任。
- 发布包提供 SHA-256 校验文件；程序不自动覆盖自身。

## 验证

- 完整解决方案 Release 编译：0 Warning / 0 Error（本次构建关闭在线 NuGet 漏洞审计，不代表依赖安全扫描结论）。
- 自动化测试：152 / 152 通过，包括 3 项新增的空选区、全屏蒙层复位、历史选区移动释放回归测试。
- 用户已对同一修复的 v1.1.1 本机补丁版完成实际使用验证。
- 本轮未重新覆盖所有多屏、DPI 和第三方软件组合，不宣称完成全环境兼容性重测。

## English

A focused stability update fixing recalled regions disappearing or collapsing after a move/resize gesture. Thanks to @zch-soft for PR #1. Existing features, shortcuts, settings, history metadata, and optional OCR modules remain unchanged.
