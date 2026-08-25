# QingSnap v1–v47 版本档案 / Version archive

## 档案范围 / Archive scope

- 仓库 `main` 保存截至 Preview v47 的完整累计源码、工程文件、测试、文档和发布配置。
- GitHub 的 `preview-history-v1-v47` 历史归档 Release 保存 v1–v47 的可运行预览包。
- v1–v33 开发时没有逐版提交源码快照；本机保留下来的是当时生成的运行包。因此这些版本不会伪造对应源码标签。
- v34 起开始建立公开 Git 历史；v44、v45 和 v47 具有可追溯的公开源码节点。

- The `main` branch contains the complete cumulative source, projects, tests, documentation, and publishing configuration through Preview v47.
- The `preview-history-v1-v47` GitHub Release stores runnable preview packages for v1–v47.
- Per-version source snapshots were not committed during v1–v33; only the original runtime builds survived locally. No artificial source tags are created for those versions.
- Public Git history begins at v34, with traceable public source points for v34, v44, v45, and v47.

## 安全整理 / Archive hygiene

历史包按原版本目录重新归档，但排除了调试符号（`.pdb`）、链接器文件（`.lib`）、构建缓存和本地用户数据。截图历史、设置、OCR 识别内容、日志和模型缓存都不会上传。

Historical packages are rebuilt from the preserved version folders while excluding debug symbols (`.pdb`), linker files (`.lib`), build caches, and local user data. Screenshot history, settings, recognized OCR text, diagnostics, and model caches are not uploaded.

## 使用建议 / Recommendation

历史版本仅用于回溯和比较，不再维护。日常使用请下载最新版本。

Historical builds are retained for reference and comparison only. Use the latest release for day-to-day work.
