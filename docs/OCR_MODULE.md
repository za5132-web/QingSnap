# QingSnap OCR 可选模块

QingSnap 基础包不包含 OCR。截图、长截图、标注、贴图和历史记录无需安装本模块即可使用。

## 安装

1. 打开 QingSnap 设置，进入“OCR / 文字识别”。
2. 选择 Tiny 或 Small 模型。
3. 点击“一键安装 OCR”。QingSnap 会在后台下载并安装运行库和所选模型。
4. 校验完成后，状态链会显示“运行库 → 模型 → 可用”。

安装过程中不需要查找或解压 ZIP。如果 `QingSnap-OCR-Module-v48.zip` 已与 `QingSnap.exe` 放在同一目录，程序会优先使用本地模块，适合离线部署。

## 模型选择

- PP-OCRv6 Tiny：下载约 7.4 MB，启动和识别更快，适合常规截图与轻量使用。
- PP-OCRv6 Small：下载约 30.8 MB，复杂中文、表格和中英混排识别更稳。

运行库只需安装一次。Tiny 与 Small 可以分别安装并在设置中切换。

## 卸载

设置页点击“卸载 OCR”会关闭 OCR 引擎并删除运行库与已安装模型。卸载后，QingSnap 自动回到不含 OCR 的基础状态。

运行库和模型默认保存在：

- `%LOCALAPPDATA%\QingSnap\Ocr\Runtime`
- `%LOCALAPPDATA%\QingSnap\Ocr\Models`

创建 `portable.flag` 使用便携数据模式时，这些文件会保存到程序目录的 `Data\Ocr` 下。
