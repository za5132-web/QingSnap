# QingSnap v1.1 历史分页与缩略图优化报告

完成日期：2026-09-05
范围：第 17 步，仅历史数据访问、增量加载、搜索筛选和缩略图生命周期

## 结果

历史窗口已从“扫描目录并一次创建最近 500 条”升级为：

`SQLite 全量 Metadata → SQL 搜索/筛选/统计 → 每页 80 条 → 滚动预取 → WPF 回收虚拟化 → 可视卡片 280px 解码 → 150 张 LRU`

Release 编译 0 警告、0 错误；自动化测试 143/143 通过。SQLite Schema 仍为 Version 1，图片文件、目录结构、收藏、OCR、标签、来源、迁移、重建和 portable.flag 均保持兼容。

## 原有 500 条限制与内存结构

原限制位于 `HistoryWindow.xaml.cs` 的 `MaximumLoadedItems = 500`，窗口调用 `CaptureHistoryService.LoadSnapshot(500)`：

1. `LoadSnapshot` 递归扫描整个历史目录。
2. 对最近 500 个文件创建 `HistoryItem`。
3. 每个 Model 立即持有一个 `DecodePixelWidth = 260` 的 `BitmapImage`。
4. 每个 Model 同时长期持有完整 `OCRText`。
5. 搜索、日期、长图、收藏和标签筛选均在这 500 条内用 LINQ 完成。

因此 SQLite 即使有更早记录，原 UI 搜索也无法命中。

## 新分页架构

- `HistoryMetadataStore.QueryHistoryAsync` 是统一查询入口。
- `HistoryQuery` 支持 Offset、Limit、SearchText、日期/长图/收藏筛选、Tag 和排序。
- 默认 PageSize 为 80，数据库接口限制单页最大 200。
- 第一页独立执行 `COUNT(*)` 与 `SUM(FileSize)`；后续页不重复扫描统计。
- `HistoryWindow` 距离底部约 20 条时预取下一页。
- `_isLoadingHistory` 防止同一时刻重复加载；`requestVersion + CancellationToken` 阻止旧查询污染新搜索。
- 每页按 Metadata Id 去重，并用独立 `_nextOffset` 推进，失败后允许再次滚动重试。
- 新搜索、日期和标签筛选会取消旧请求、清空已载入页并从第一页重新开始。
- 搜索输入采用 250ms debounce。
- Ctrl+A 明确定义为选择当前已加载结果；Shift 连选也以当前已加载区间为边界。

## Search / Filter SQL

所有值均使用 SQLite 参数，不拼接用户输入。查询保持 `CaptureTime DESC, Id DESC`。

普通搜索词使用 AND 组合，并在每个词内匹配：

- FilePath / FileName
- `宽 × 高 px` 与 `宽x高`
- OCRText
- SourceProcess
- SourceWindowTitle
- 标签名

`tag:` 解析继续复用原 `HistorySearchQuery`，用标签关系表 `EXISTS` 精确匹配。标签下拉筛选同样通过 `HistoryItemTags + Tags` 完成。今天、最近 7 天、IsLongCapture 和 IsFavorite 均进入 SQL WHERE，可以与搜索和标签组合。

普通分页结果不 SELECT OCRText；OCR 正文仅留在数据库中用于 SQL 搜索和 OCR 功能，从而避免 80 条页面 Model 长期持有大文本。

## 缩略图策略

- 历史页返回的 `HistoryItem.Thumbnail` 为 null，不在查询阶段解码图片。
- 卡片进入可视树后才异步读取缩略图。
- `BitmapImage.CacheOption = OnLoad`。
- `DecodePixelWidth = 280`，列表和图标模式共用。
- 解码完成后 `Freeze()`，文件流立即 Dispose，不锁定原文件。
- 长图同样只按 280px 宽度解码，不展开数万像素原图。
- 打开、复制、OCR、贴图和 QR 仍通过 `LoadFullImage` 读取完整质量。
- 文件缺失或损坏时单张卡片失败并写日志，不影响整页；文件确实不存在时异步删除对应 Metadata。

## Thumbnail LRU Cache

- 上限：150 张。
- Key：完整 FilePath + LastWriteTimeUtc + DecodePixelWidth。
- 文件修改后自动形成新 Key，旧缩略图不会继续命中。
- 并发请求同一 Key 时复用同一解码 Task。
- 超限淘汰最久未使用项。
- 删除截图时同步移除该路径缓存。
- 刷新可清空缓存；历史窗口关闭时 Dispose 全部窗口专属缓存。
- 未使用 `GC.Collect()`。

## HistoryItem 变化

分页页面中的 HistoryItem 只持有：路径、文件名、时间、宽高、大小、收藏、是否长图、标签摘要、来源字段和 Metadata Id。Thumbnail 改为可空，普通分页不再填充；SearchText 保留兼容字段，但分页结果为空，不再长期携带完整 OCRText。没有原始 Bitmap、OCR 几何、byte[] 或 Stream 常驻。

## 数据迁移与重建优化

历史后台同步原先调用 `LoadAllAsync`，会连同全部 OCRText 载入内存。现在改为轻量 `LoadMigrationIndexAsync`，只读取 FilePath、FileSize、ImageHash、UpdatedAt。只有检测到新增、变化或移动文件时，才按路径读取该条完整 Metadata 和标签。

数据库损坏重建后仍由目录后台导入；同步完成会通知已打开的历史窗口重新查询第一页，不会一次把重建后的全部记录灌入 UI。

## 性能测试

测试环境为当前开发机，数字是本机结果，不作为所有设备的绝对承诺。

### 10,000 条 SQLite 查询

| 场景 | 时间 |
|---|---:|
| 第一页 80 条 + COUNT/SUM | 21.5 ms |
| 第 50 页 80 条 + COUNT/SUM | 24.1 ms |
| 只存在于深层记录的 OCRText 搜索 | 115.7 ms |
| SourceWindowTitle 搜索 | 146.7 ms |
| Tag 筛选 | 26.4 ms |
| Favorite 筛选 | 11.7 ms |
| LongCapture 筛选 | 13.3 ms |

100 页连续查询共加载 8,000 条：Id 无重复、时间倒序正确；已取消请求抛出取消异常，新搜索结果不混入旧条件。

### 缩略图缓存

使用 2,000 个 320×180 图片 Key、按正式 280px 路径解码：

- 最终 Cache 数量：150。
- 第 1,000 张时测试进程工作集：107.5 MB。
- 第 2,000 张时：103.8 MB。
- 后半段变化：-3.7 MB，未随浏览数量继续线性增长。

### 真实历史窗口

- 当前目录：551 张。
- 冷启动窗口可见：约 2.23 秒（包含应用冷启动和后台目录同步）。
- 首屏稳定后：工作集约 176 MB，私有内存约 88 MB。
- 审查前相同窗口私有内存约 118 MB，本轮首屏下降约 25%。
- 自动滚动加载到 551/551 后，UI Automation 检查视觉树仅有 3～5 个已实例化 ListItem，未创建 551 个卡片控件。
- 全部浏览后工作集约 230 MB、私有内存约 153 MB；增长主要受 150 张缩略图缓存约束。
- 2,000 张浏览的实际 UI 内存趋势使用独立缓存压力测试验证；当前真实用户目录不足 2,000 张，仍建议按下方清单进行一次完整人工确认。

## 修改文件

- `src/QingSnap.App/Models/HistoryQuery.cs`（新增）
- `src/QingSnap.App/Models/HistoryItem.cs`
- `src/QingSnap.App/Services/HistoryMetadataStore.cs`
- `src/QingSnap.App/Services/CaptureHistoryService.cs`
- `src/QingSnap.App/Services/ThumbnailLruCache.cs`（新增）
- `src/QingSnap.App/Views/HistoryWindow.xaml`
- `src/QingSnap.App/Views/HistoryWindow.xaml.cs`
- `tests/QingSnap.Tests/HistoryMetadataPerformanceTests.cs`
- `tests/QingSnap.Tests/ThumbnailLruCacheTests.cs`（新增）
- `docs/QINGSNAP_V1.1_STABILITY_REVIEW.md`
- `docs/QINGSNAP_V1.1_HISTORY_PAGING_REPORT.md`（新增）

## 新增验证

- 10,000 条第一/第 50 页、OCR、来源、标签、收藏、长图和统计查询。
- 100 页连续分页的排序与去重。
- 已取消查询和新搜索隔离。
- 2,000 次缩略图访问的 LRU 上限与内存趋势。
- 历史目录移动后收藏、OCR、来源、标签和截图坐标继续保留。
- 真实窗口首次加载、滚动增量加载和视觉树虚拟化。
- 初始化阶段下拉框 SelectionChanged 与后台 Metadata 完成通知的竞态回归。

## 已知限制

1. 当前采用 OFFSET 分页。10,000 条下第 50 页仍约 24ms；未来达到几十万条时可再评估 keyset pagination，本版本没有必要引入额外复杂度。
2. OCR / 来源普通包含搜索使用 SQLite 扫描，没有升级 Schema 或引入 FTS。10,000 条为约 116～147ms，配合 250ms 防抖可接受；若历史达到 50,000～100,000 且 OCR 文本很长，可在后续 Schema migration 中评估 FTS5。
3. Ctrl+A 和 Shift 连选只覆盖已加载结果，避免误操作数据库中全部 10,000 张；UI 状态文字已明确这一点。
4. 真实 2,000 张 UI 滚动仍建议使用隔离历史目录进行一次人工验证；自动化已验证 2,000 次正式尺寸缩略图访问后缓存和内存不线性增长。

## 人工测试清单

1. 在隔离目录准备 10,000 张 PNG/JPG/BMP 混合图片，并复制对应测试 Metadata。
2. 打开历史窗口，记录首次可见时间、首屏工作集和私有内存。
3. 连续滚动到 2,000 条，确认页间无重复、排序稳定、滚动无明显卡顿。
4. 在 1,000 和 2,000 条位置记录内存；继续往返滚动，确认达到缓存上限后趋于稳定。
5. 搜索只存在于第 8,000 条附近的 OCR 文本、标签、SourceWindowTitle 和 SourceProcess。
6. 组合验证搜索 + 今天/7天/长图/收藏/标签。
7. 快速连续输入搜索词，确认旧结果不会闪回或混入。
8. 跨已加载分页范围 Shift 连选；Ctrl+A 只选择当前已加载结果。
9. 批量收藏、取消收藏、添加/移除标签、复制路径和删除到回收站。
10. 外部删除图片后滚动到对应记录并刷新，确认占位、日志和 Metadata 清理。
11. 模拟数据库损坏并重建，确认窗口始终只载入第一页。
12. 重启后复核收藏、OCR、标签、来源和再次截取坐标。
13. 在 portable.flag 模式重复分页、搜索、删除、重建和历史目录迁移。

## 建议

建议进入下一阶段内存压力测试。分页、全量搜索和缩略图缓存已达到功能完成状态；正式发布前仍应在隔离的 10,000 张真实图片目录中完成一次 2,000 条 UI 连续滚动，并用任务管理器或性能分析器记录 WPF 原生图像内存和句柄趋势。
