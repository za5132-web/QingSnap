using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
using WpfKeyEventArgs = System.Windows.Input.KeyEventArgs;
using WpfRadioButton = System.Windows.Controls.RadioButton;

namespace QingSnap.App.Views;

public partial class FirstRunTutorialWindow : Window
{
    private static readonly TutorialStep[] Steps =
    [
        new(
            "F1", "开始截图", "按 F1，框住你想要的内容", "移动鼠标可直接选中窗口，也可以拖出任意范围。",
            "开始截图", "F1 唤出截图",
            [new("F1", "唤出截图"), new("单击", "采用智能选区"), new("拖动", "自由框选"), new("Esc", "取消")]),
        new(
            "精确", "调准边界", "精准到每1px", "拖动边角快速调整，需要时再用尺寸、比例和键盘微调。",
            "精确选区", "像素、比例与尺寸",
            [new("W/A/S/D", "微调 1px"), new("Shift", "拖角保持比例"), new("Alt", "拖角中心缩放"), new("锁头", "固定尺寸")]),
        new(
            "F2", "选择与编辑", "画出重点，随性修改", "添加箭头、形状或文字，再选择、移动和批量调整。",
            "标注编辑", "选择、撤销与重做",
            [new("F2", "进入选择/编辑"), new("Shift", "单击多选"), new("Ctrl+Z", "撤销"), new("Ctrl+Y", "重做")]),
        new(
            "长图", "继续向下截", "一屏放不下，就让 QingSnap 接着截", "选择滚动区域后自动拼接，复杂页面也可以切换为手动补截。",
            "截取长图", "自动滚动或手动补截",
            [new("长图按钮", "自动滚动"), new("手动模式", "逐屏补截"), new("Enter", "提前完成"), new("Esc", "取消")]),
        new(
            "F3", "贴到桌面", "贴贴贴到桌面 收收收到边缘", "按 F3 贴出最近图片，暂时不用时按 M 或直接摔到屏幕边缘。",
            "贴到桌面", "F3 贴图，M 收纳",
            [new("F3", "循环最近 5 张"), new("滚轮", "缩放贴图"), new("M", "收纳/恢复"), new("Esc", "关闭贴图")]),
        new(
            "识别", "提取图片信息", "图片里的文字和链接，也可以直接使用", "像文档一样拖选文字；识别二维码后，直接点击图片上的结果。",
            "提取信息", "选字与二维码",
            [new("拖动", "选择图片文字"), new("Ctrl+C", "复制所选内容"), new("二维码", "悬停查看"), new("单击热点", "主动打开链接")],
            "OCR 为可选离线模块，首次真正使用时再提示安装。"),
        new(
            "历史", "找回并复用", "历史记录？远比你想的强大", "按文字、标签或来源搜索，再次贴图或回到原位置重新截图。",
            "搜索复用", "历史与再次截取",
            [new("搜索", "文字/标签/来源"), new("贴图", "再次对照"), new("R", "循环最近 5 个选区"), new("再次截取", "回到原位置")])
    ];

    private readonly WpfRadioButton[] _stepButtons;
    private readonly FrameworkElement[] _demos;
    private int _currentStep;

    public FirstRunTutorialWindow()
    {
        InitializeComponent();
        _stepButtons = [Step0, Step1, Step2, Step3, Step4, Step5, Step6];
        _demos = [Demo0, Demo1, Demo2, Demo3, Demo4, Demo5, Demo6];
        VersionText.Text = GetDisplayVersion();
        for (var i = 0; i < _stepButtons.Length; i++)
        {
            _stepButtons[i].Content = Steps[i].StepName;
            _stepButtons[i].ToolTip = Steps[i].StepHint;
        }

        Loaded += (_, _) => SelectStep(0);
        Closed += (_, _) => StopAnimations();
    }

    private void SelectStep(int index)
    {
        _currentStep = Math.Clamp(index, 0, Steps.Length - 1);
        var step = Steps[_currentStep];

        StopAnimations();
        for (var i = 0; i < _demos.Length; i++)
        {
            _demos[i].Visibility = i == _currentStep ? Visibility.Visible : Visibility.Collapsed;
        }

        if (_stepButtons[_currentStep].IsChecked != true)
        {
            _stepButtons[_currentStep].IsChecked = true;
        }

        StepKeyText.Text = step.Key;
        StepKickerText.Text = step.Kicker;
        StepTitleText.Text = step.Title;
        StepDescriptionText.Text = step.Description;
        ShortcutTipsItems.ItemsSource = step.ShortcutTips;
        OcrOptionalNote.Text = step.OptionalNote ?? string.Empty;
        OcrOptionalNote.Visibility = string.IsNullOrWhiteSpace(step.OptionalNote)
            ? Visibility.Collapsed
            : Visibility.Visible;
        StepPositionText.Text = $"{_currentStep + 1:00} / {Steps.Length:00}";
        PreviousButton.IsEnabled = _currentStep > 0;
        NextButton.Content = _currentStep == Steps.Length - 1 ? "开始使用" : "下一步";

        StartAnimation(_currentStep);
    }

    private void StartAnimation(int index)
    {
        if (!SystemParameters.ClientAreaAnimation)
        {
            StartReducedMotion(index);
            return;
        }

        switch (index)
        {
            case 0:
                const double captureDuration = 3.8;
                Animate(CaptureCursor, Canvas.LeftProperty, captureDuration,
                    (0, 34), (.62, 214), (1.12, 214), (1.42, 86), (2.52, 366), (3.48, 366), (3.8, 34));
                Animate(CaptureCursor, Canvas.TopProperty, captureDuration,
                    (0, 34), (.62, 108), (1.12, 108), (1.42, 60), (2.52, 236), (3.48, 236), (3.8, 34));
                Animate(SmartRecognitionFrame, OpacityProperty, captureDuration,
                    (0, 0), (.48, 0), (.72, 1), (1.12, 1), (1.38, 0), (3.8, 0));
                Animate(CaptureSmartHint, OpacityProperty, captureDuration,
                    (0, 0), (.65, 0), (.83, 1), (1.12, 1), (1.34, 0), (3.8, 0));
                Animate(CaptureDimmer, OpacityProperty, captureDuration,
                    (0, 0), (1.3, 0), (1.52, 1), (3.5, 1), (3.8, 0));
                Animate(CaptureSelection, FrameworkElement.WidthProperty, captureDuration,
                    (0, 1), (1.42, 1), (2.52, 280), (3.5, 280), (3.8, 1));
                Animate(CaptureSelection, FrameworkElement.HeightProperty, captureDuration,
                    (0, 1), (1.42, 1), (2.52, 176), (3.5, 176), (3.8, 1));
                Animate(CaptureDragHint, OpacityProperty, captureDuration,
                    (0, 0), (1.3, 0), (1.5, 1), (2.45, 1), (2.7, 0), (3.8, 0));
                Animate(CaptureSize, OpacityProperty, captureDuration,
                    (0, 0), (2.5, 0), (2.76, 1), (3.5, 1), (3.74, 0), (3.8, 0));
                Animate(CaptureToolbar, OpacityProperty, captureDuration,
                    (0, 0), (2.58, 0), (2.84, 1), (3.5, 1), (3.74, 0), (3.8, 0));
                break;
            case 1:
                const double precisionDuration = 4.0;
                Animate(PrecisionSelection, FrameworkElement.WidthProperty, precisionDuration,
                    (0, 230), (.25, 230), (.86, 280), (1.3, 280), (1.82, 304), (3.7, 304), (4, 230));
                Animate(PrecisionSelection, FrameworkElement.HeightProperty, precisionDuration,
                    (0, 144), (.25, 144), (.86, 172), (1.3, 172), (1.82, 171), (3.7, 171), (4, 144));
                Animate(PrecisionSelection, Canvas.LeftProperty, precisionDuration,
                    (0, 70), (2.42, 70), (2.94, 95), (3.34, 95), (3.4, 96), (3.7, 96), (4, 70));
                Animate(PrecisionSelection, Canvas.TopProperty, precisionDuration,
                    (0, 60), (2.42, 60), (2.94, 72), (3.7, 72), (4, 60));
                Animate(PrecisionCursor, Canvas.LeftProperty, precisionDuration,
                    (0, 296), (.86, 346), (1.3, 346), (1.82, 374), (2.42, 374), (2.94, 399), (3.4, 400), (3.7, 400), (4, 296));
                Animate(PrecisionCursor, Canvas.TopProperty, precisionDuration,
                    (0, 199), (.86, 227), (1.3, 227), (1.82, 226), (2.42, 226), (2.94, 238), (3.7, 238), (4, 199));
                Animate(PrecisionGeometryPanel, OpacityProperty, precisionDuration,
                    (0, .72), (.42, .72), (.66, 1), (1.02, 1), (1.2, .78), (3.7, .78), (4, .72));
                Animate(PrecisionRatioBadge, OpacityProperty, precisionDuration,
                    (0, .64), (1.02, .64), (1.28, 1), (2.05, 1), (2.26, .78), (3.7, .78), (4, .64));
                Animate(PrecisionHandles, OpacityProperty, precisionDuration,
                    (0, 1), (2.12, 1), (2.4, .16), (3.7, .16), (4, 1));
                Animate(PrecisionLockBadge, OpacityProperty, precisionDuration,
                    (0, 0), (2.08, 0), (2.38, 1), (3.7, 1), (4, 0));
                Animate(PrecisionMagnifier, OpacityProperty, precisionDuration,
                    (0, 0), (3.02, 0), (3.28, 1), (3.7, 1), (4, 0));
                Animate(PrecisionPixelMove, System.Windows.Media.TranslateTransform.XProperty, precisionDuration,
                    (0, 0), (3.32, 0), (3.42, 1), (3.7, 1), (4, 0));
                AnimateText(PrecisionRatioText, precisionDuration, (0, "自由"), (1.25, "16:9"), (4, "自由"));
                AnimateText(PrecisionXText, precisionDuration, (0, "70"), (2.68, "83"), (2.94, "95"), (3.4, "96"), (4, "70"));
                AnimateText(PrecisionYText, precisionDuration, (0, "60"), (2.68, "66"), (2.94, "72"), (4, "60"));
                AnimateText(PrecisionWText, precisionDuration, (0, "230"), (.55, "254"), (.86, "280"), (1.58, "292"), (1.82, "304"), (4, "230"));
                AnimateText(PrecisionHText, precisionDuration, (0, "144"), (.55, "157"), (.86, "172"), (1.58, "164"), (1.82, "171"), (4, "144"));
                break;
            case 2:
                const double annotationDuration = 4.0;
                Animate(AnnotationArrowTool, OpacityProperty, annotationDuration,
                    (0, .42), (.12, 1), (.54, 1), (.72, .42), (4, .42));
                Animate(AnnotationRectangleTool, OpacityProperty, annotationDuration,
                    (0, .42), (.48, .42), (.66, 1), (1.02, 1), (1.18, .42), (4, .42));
                Animate(AnnotationTextTool, OpacityProperty, annotationDuration,
                    (0, .42), (.92, .42), (1.1, 1), (1.42, 1), (1.58, .42), (4, .42));
                Animate(AnnotationArrow, OpacityProperty, annotationDuration,
                    (0, 0), (.16, 0), (.62, 1), (3.72, 1), (4, 0));
                Animate(AnnotationScale, System.Windows.Media.ScaleTransform.ScaleXProperty, annotationDuration,
                    (0, 0), (.16, 0), (.62, 1), (3.72, 1), (4, 0));
                Animate(AnnotationScale, System.Windows.Media.ScaleTransform.ScaleYProperty, annotationDuration,
                    (0, 0), (.16, 0), (.62, 1), (3.72, 1), (4, 0));
                Animate(AnnotationRectangleGroup, OpacityProperty, annotationDuration,
                    (0, 0), (.58, 0), (1.04, 1), (3.72, 1), (4, 0));
                Animate(AnnotationText, OpacityProperty, annotationDuration,
                    (0, 0), (1.02, 0), (1.42, 1), (3.72, 1), (4, 0));
                Animate(AnnotationSingleSelection, OpacityProperty, annotationDuration,
                    (0, 0), (1.56, 0), (1.78, 1), (2.18, 1), (2.35, 0), (4, 0));
                Animate(AnnotationRectangleMove, System.Windows.Media.TranslateTransform.XProperty, annotationDuration,
                    (0, 0), (1.78, 0), (2.16, 20), (3.72, 20), (4, 0));
                Animate(AnnotationMultiSelection, OpacityProperty, annotationDuration,
                    (0, 0), (2.18, 0), (2.42, 1), (3.72, 1), (4, 0));
                Animate(AnnotationGroupMove, System.Windows.Media.TranslateTransform.XProperty, annotationDuration,
                    (0, 0), (2.42, 0), (2.78, 18), (3.08, 18), (3.28, 0), (3.5, 0), (3.7, 18), (4, 0));
                Animate(AnnotationActionBadge, OpacityProperty, annotationDuration,
                    (0, 0), (1.5, 0), (1.7, 1), (3.72, 1), (4, 0));
                AnimateText(AnnotationActionText, annotationDuration,
                    (0, "F2 · 选择"), (2.18, "Shift · 多选"), (3.05, "Ctrl+Z · 撤销"), (3.48, "Ctrl+Y · 重做"), (4, "F2 · 选择"));
                break;
            case 3:
                const double longDuration = 4.0;
                Animate(LongCaptureButton, OpacityProperty, longDuration,
                    (0, .55), (.18, 1), (.62, 1), (.84, .55), (3.72, .55), (4, .55));
                Animate(LongTranslate, System.Windows.Media.TranslateTransform.YProperty, longDuration,
                    (0, 0), (.68, 0), (1.42, -72), (1.62, -72), (2.3, -144), (3.72, -144), (4, 0));
                Animate(LongOverlapLine, OpacityProperty, longDuration,
                    (0, 0), (1.22, 0), (1.4, 1), (1.63, 1), (1.82, 0), (2.08, 0), (2.28, 1), (2.48, 0), (4, 0));
                Animate(LongResult, OpacityProperty, longDuration,
                    (0, 0), (2.62, 0), (2.94, 1), (3.72, 1), (4, 0));
                Animate(LongViewport, OpacityProperty, longDuration,
                    (0, 1), (2.62, 1), (2.94, .34), (3.72, .34), (4, 1));
                Animate(LongAutoStatus, OpacityProperty, longDuration,
                    (0, 0), (2.82, 0), (3.08, 1), (3.72, 1), (4, 0));
                Animate(LongManualStatus, OpacityProperty, longDuration,
                    (0, 0), (3.02, 0), (3.28, .82), (3.72, .82), (4, 0));
                AnimateText(LongProgressText, longDuration, (0, "1 / 3"), (1.34, "2 / 3"), (2.22, "3 / 3"), (4, "1 / 3"));
                break;
            case 4:
                const double pinDuration = 4.4;
                Animate(PinStateBadge, OpacityProperty, pinDuration,
                    (0, 0), (.18, 1), (.72, 1), (.94, 0), (4.4, 0));
                Animate(PinCursor, Canvas.LeftProperty, pinDuration,
                    (0, 58), (.42, 154), (1.2, 248), (1.62, 248), (2.18, 406), (2.66, 406), (2.96, 374), (3.18, 374), (3.62, 248), (4.12, 248), (4.4, 58));
                Animate(PinCursor, Canvas.TopProperty, pinDuration,
                    (0, 42), (.42, 94), (1.2, 122), (1.62, 122), (2.18, 126), (2.66, 126), (2.96, 122), (3.18, 122), (3.62, 122), (4.12, 122), (4.4, 42));
                Animate(PinnedImage, Canvas.LeftProperty, pinDuration,
                    (0, 92), (.42, 92), (1.2, 160), (1.72, 160), (2.28, 410), (2.62, 410), (2.94, 322), (3.22, 322), (3.62, 160), (4.12, 160), (4.4, 92));
                Animate(PinnedImage, Canvas.TopProperty, pinDuration,
                    (0, 55), (.42, 55), (1.2, 70), (1.72, 70), (2.28, 94), (2.62, 94), (2.94, 82), (3.22, 82), (3.62, 70), (4.12, 70), (4.4, 55));
                Animate(PinnedImage, FrameworkElement.WidthProperty, pinDuration,
                    (0, 250), (1.2, 250), (1.66, 225), (1.78, 225), (2.28, 28), (2.62, 28), (2.94, 110), (3.22, 110), (3.62, 225), (4.12, 225), (4.4, 250));
                Animate(PinnedImage, FrameworkElement.HeightProperty, pinDuration,
                    (0, 165), (1.2, 165), (1.66, 149), (1.78, 149), (2.28, 70), (2.62, 70), (2.94, 85), (3.22, 85), (3.62, 149), (4.12, 149), (4.4, 165));
                Animate(PinWheelBadge, OpacityProperty, pinDuration,
                    (0, 0), (1.18, 0), (1.42, 1), (1.7, 1), (1.9, 0), (4.4, 0));
                Animate(PinMKeycap, OpacityProperty, pinDuration,
                    (0, 0), (1.62, 0), (1.84, 1), (2.18, 1), (2.42, 0), (4.4, 0));
                Animate(PinEdgeRail, OpacityProperty, pinDuration,
                    (0, 0), (2.02, 0), (2.28, 1), (3.2, 1), (3.58, 0), (4.4, 0));
                Animate(PinBadge, OpacityProperty, pinDuration,
                    (0, 0), (2.08, 0), (2.28, 1), (3.22, 1), (3.52, 0), (4.4, 0));
                Animate(PinBadge, Canvas.LeftProperty, pinDuration,
                    (0, 411), (2.62, 411), (2.94, 397), (3.22, 397), (3.62, 366), (4.4, 411));
                Animate(PinBadge, Canvas.TopProperty, pinDuration,
                    (0, 99), (2.62, 99), (2.94, 87), (3.22, 87), (3.62, 75), (4.4, 99));
                Animate(PinHoverHint, OpacityProperty, pinDuration,
                    (0, 0), (2.68, 0), (2.92, 1), (3.22, 1), (3.48, 0), (4.4, 0));
                Animate(PinClickPulse, OpacityProperty, pinDuration,
                    (0, 0), (3.04, 0), (3.14, 1), (3.32, 0), (4.4, 0));
                break;
            case 5:
                const double extractDuration = 4.8;
                Animate(OcrScene, OpacityProperty, extractDuration,
                    (0, 1), (2.0, 1), (2.28, 0), (4.58, 0), (4.8, 1));
                Animate(OcrSelection, FrameworkElement.WidthProperty, extractDuration,
                    (0, 1), (.28, 1), (1.02, 258), (2.05, 258), (2.28, 1), (4.8, 1));
                Animate(OcrCursor, Canvas.LeftProperty, extractDuration,
                    (0, 7), (.28, 7), (1.02, 270), (2.05, 270), (4.8, 7));
                Animate(OcrCopyKeycap, OpacityProperty, extractDuration,
                    (0, 0), (.86, 0), (1.08, 1), (1.48, 1), (1.7, 0), (4.8, 0));
                Animate(OcrCopiedToast, OpacityProperty, extractDuration,
                    (0, 0), (1.22, 0), (1.48, 1), (2.0, 1), (2.24, 0), (4.8, 0));
                Animate(QrScene, OpacityProperty, extractDuration,
                    (0, 0), (2.0, 0), (2.28, 1), (4.58, 1), (4.8, 0));
                Animate(QrEntryButton, OpacityProperty, extractDuration,
                    (0, .58), (2.38, .58), (2.6, 1), (2.88, 1), (3.08, .72), (4.8, .58));
                Animate(QrCursorMove, System.Windows.Media.TranslateTransform.XProperty, extractDuration,
                    (0, 0), (2.82, 0), (3.32, -150), (4.12, -150), (4.8, 0));
                Animate(QrCursorMove, System.Windows.Media.TranslateTransform.YProperty, extractDuration,
                    (0, 0), (2.82, 0), (3.32, 86), (4.12, 86), (4.8, 0));
                Animate(QrCursor, OpacityProperty, extractDuration,
                    (0, 1), (4.12, 1), (4.3, 0), (4.58, 0), (4.8, 1));
                Animate(QrHotspot, OpacityProperty, extractDuration,
                    (0, 0), (2.62, 0), (2.9, 1), (4.28, 1), (4.56, 0), (4.8, 0));
                Animate(QrSummary, OpacityProperty, extractDuration,
                    (0, 0), (3.28, 0), (3.52, 1), (4.02, 1), (4.26, 0), (4.8, 0));
                Animate(QrOpenHint, OpacityProperty, extractDuration,
                    (0, 0), (3.92, 0), (4.18, 1), (4.56, 1), (4.8, 0));
                break;
            case 6:
                const double historyDuration = 5.4;
                Animate(HistoryThumb0, OpacityProperty, historyDuration,
                    (0, 0), (.16, 1), (5.16, 1), (5.4, 0));
                Animate(HistoryThumb1, OpacityProperty, historyDuration,
                    (0, 0), (.32, 0), (.5, 1), (5.16, 1), (5.4, 0));
                Animate(HistoryThumb2, OpacityProperty, historyDuration,
                    (0, 0), (.62, 0), (.8, 1), (5.16, 1), (5.4, 0));
                AnimateText(HistorySearchText, historyDuration,
                    (0, "搜索截图"), (.94, "方"), (1.18, "方案"), (5.16, "方案"), (5.4, "搜索截图"));
                Animate(HistoryCard0, OpacityProperty, historyDuration,
                    (0, 1), (1.32, 1), (1.62, 0), (5.16, 0), (5.4, 1));
                Animate(HistoryCard2, OpacityProperty, historyDuration,
                    (0, 1), (1.32, 1), (1.62, 0), (5.16, 0), (5.4, 1));
                Animate(HistoryOffscreenHint, OpacityProperty, historyDuration,
                    (0, 0), (1.46, 0), (1.72, 1), (2.18, 1), (2.4, 0), (5.4, 0));
                Animate(HistoryWorkTag, OpacityProperty, historyDuration,
                    (0, .55), (2.0, .55), (2.22, 1), (3.02, 1), (5.4, .55));
                Animate(HistorySourceBadge, OpacityProperty, historyDuration,
                    (0, 0), (2.22, 0), (2.48, 1), (3.04, 1), (5.4, 0));
                Animate(HistoryPinAction, OpacityProperty, historyDuration,
                    (0, .65), (2.62, .65), (2.82, 1), (3.08, 1), (3.28, .65), (5.4, .65));
                Animate(HistoryScene, OpacityProperty, historyDuration,
                    (0, 1), (3.08, 1), (3.34, 0), (5.16, 0), (5.4, 1));
                Animate(HistoryDesktopScene, OpacityProperty, historyDuration,
                    (0, 0), (3.08, 0), (3.34, 1), (3.72, 1), (3.96, 0), (5.4, 0));
                Animate(HistoryCaptureScene, OpacityProperty, historyDuration,
                    (0, 0), (3.68, 0), (3.96, 1), (5.16, 1), (5.4, 0));
                Animate(HistoryRKeycap, OpacityProperty, historyDuration,
                    (0, 0), (3.88, 0), (4.1, 1), (5.16, 1), (5.4, 0));
                Animate(HistorySelectionA, OpacityProperty, historyDuration,
                    (0, 0), (4.06, 0), (4.2, 1), (4.4, 0), (5.4, 0));
                Animate(HistorySelectionB, OpacityProperty, historyDuration,
                    (0, 0), (4.28, 0), (4.42, 1), (4.62, 0), (5.4, 0));
                Animate(HistorySelectionC, OpacityProperty, historyDuration,
                    (0, 0), (4.5, 0), (4.66, 1), (5.16, 1), (5.4, 0));
                Animate(HistoryRecaptureBadge, OpacityProperty, historyDuration,
                    (0, 0), (4.62, 0), (4.84, 1), (5.16, 1), (5.4, 0));
                Animate(NextButton, OpacityProperty, historyDuration,
                    (0, 1), (4.78, 1), (4.92, .55), (5.1, 1), (5.4, 1));
                break;
        }
    }

    private void StartReducedMotion(int index)
    {
        ShowStaticState(index);

        switch (index)
        {
            case 0:
                Animate(SmartRecognitionFrame, OpacityProperty, 3.2,
                    (0, 0), (.3, 1), (.9, 1), (1.1, 0), (3.2, 0));
                Animate(CaptureSelection, OpacityProperty, 3.2,
                    (0, 0), (.9, 0), (1.25, 1), (3.2, 1));
                Animate(CaptureSize, OpacityProperty, 3.2,
                    (0, 0), (1.75, 0), (2.1, 1), (3.2, 1));
                Animate(CaptureToolbar, OpacityProperty, 3.2,
                    (0, 0), (1.75, 0), (2.1, 1), (3.2, 1));
                break;
            case 1:
                Animate(PrecisionGeometryPanel, OpacityProperty, 3.2,
                    (0, 0), (.3, 1), (3.2, 1));
                Animate(PrecisionRatioBadge, OpacityProperty, 3.2,
                    (0, 0), (.9, 0), (1.25, 1), (3.2, 1));
                Animate(PrecisionLockBadge, OpacityProperty, 3.2,
                    (0, 0), (1.7, 0), (2.05, 1), (3.2, 1));
                Animate(PrecisionMagnifier, OpacityProperty, 3.2,
                    (0, 0), (2.2, 0), (2.55, 1), (3.2, 1));
                break;
            case 2:
                Animate(AnnotationArrow, OpacityProperty, 3.2,
                    (0, 0), (.25, 1), (3.2, 1));
                Animate(AnnotationRectangleGroup, OpacityProperty, 3.2,
                    (0, 0), (.7, 0), (1.05, 1), (3.2, 1));
                Animate(AnnotationText, OpacityProperty, 3.2,
                    (0, 0), (.7, 0), (1.05, 1), (3.2, 1));
                Animate(AnnotationMultiSelection, OpacityProperty, 3.2,
                    (0, 0), (1.6, 0), (1.95, 1), (3.2, 1));
                Animate(AnnotationActionBadge, OpacityProperty, 3.2,
                    (0, 0), (2.2, 0), (2.55, 1), (3.2, 1));
                break;
            case 3:
                Animate(LongOverlapLine, OpacityProperty, 3.2,
                    (0, 0), (.3, 1), (.9, 1), (1.1, 0), (3.2, 0));
                Animate(LongResult, OpacityProperty, 3.2,
                    (0, 0), (1.05, 0), (1.4, 1), (3.2, 1));
                Animate(LongAutoStatus, OpacityProperty, 3.2,
                    (0, 0), (1.75, 0), (2.1, 1), (3.2, 1));
                Animate(LongManualStatus, OpacityProperty, 3.2,
                    (0, 0), (2.15, 0), (2.5, .82), (3.2, .82));
                break;
            case 4:
                Animate(PinStateBadge, OpacityProperty, 3.2,
                    (0, 0), (.25, 1), (.85, 1), (1.05, 0), (3.2, 0));
                Animate(PinMKeycap, OpacityProperty, 3.2,
                    (0, 0), (.82, 0), (1.15, 1), (3.2, 1));
                Animate(PinBadge, OpacityProperty, 3.2,
                    (0, 0), (1.45, 0), (1.78, 1), (3.2, 1));
                Animate(PinHoverHint, OpacityProperty, 3.2,
                    (0, 0), (2.0, 0), (2.35, 1), (3.2, 1));
                break;
            case 5:
                Animate(OcrScene, OpacityProperty, 3.4,
                    (0, 1), (1.08, 1), (1.4, 0), (3.4, 0));
                Animate(QrScene, OpacityProperty, 3.4,
                    (0, 0), (1.08, 0), (1.4, 1), (3.4, 1));
                Animate(QrHotspot, OpacityProperty, 3.4,
                    (0, 0), (1.65, 0), (1.98, 1), (3.4, 1));
                Animate(QrSummary, OpacityProperty, 3.4,
                    (0, 0), (2.15, 0), (2.48, 1), (3.4, 1));
                Animate(QrOpenHint, OpacityProperty, 3.4,
                    (0, 0), (2.62, 0), (2.95, 1), (3.4, 1));
                break;
            case 6:
                Animate(HistoryScene, OpacityProperty, 3.6,
                    (0, 1), (.92, 1), (1.22, 0), (3.6, 0));
                Animate(HistoryDesktopScene, OpacityProperty, 3.6,
                    (0, 0), (.92, 0), (1.22, 1), (1.82, 1), (2.12, 0), (3.6, 0));
                Animate(HistoryCaptureScene, OpacityProperty, 3.6,
                    (0, 0), (1.82, 0), (2.12, 1), (3.6, 1));
                Animate(HistoryRecaptureBadge, OpacityProperty, 3.6,
                    (0, 0), (2.35, 0), (2.68, 1), (3.6, 1));
                break;
        }
    }

    private void ShowStaticState(int index)
    {
        switch (index)
        {
            case 0:
                CaptureDimmer.Opacity = 1;
                CaptureSelection.Width = 280;
                CaptureSelection.Height = 176;
                CaptureSelection.Opacity = 1;
                Canvas.SetLeft(CaptureCursor, 366);
                Canvas.SetTop(CaptureCursor, 236);
                CaptureSize.Opacity = 1;
                CaptureToolbar.Opacity = 1;
                break;
            case 1:
                PrecisionSelection.Width = 304;
                PrecisionSelection.Height = 171;
                Canvas.SetLeft(PrecisionSelection, 96);
                Canvas.SetTop(PrecisionSelection, 72);
                Canvas.SetLeft(PrecisionCursor, 400);
                Canvas.SetTop(PrecisionCursor, 238);
                PrecisionXText.Text = "96";
                PrecisionYText.Text = "72";
                PrecisionWText.Text = "304";
                PrecisionHText.Text = "171";
                PrecisionRatioText.Text = "16:9";
                PrecisionHandles.Opacity = .16;
                PrecisionGeometryPanel.Opacity = 1;
                PrecisionRatioBadge.Opacity = 1;
                PrecisionLockBadge.Opacity = 1;
                PrecisionMagnifier.Opacity = 1;
                PrecisionPixelMove.X = 1;
                break;
            case 2:
                AnnotationScale.ScaleX = 1;
                AnnotationScale.ScaleY = 1;
                AnnotationArrow.Opacity = 1;
                AnnotationRectangleGroup.Opacity = 1;
                AnnotationRectangleMove.X = 20;
                AnnotationText.Opacity = 1;
                AnnotationMultiSelection.Opacity = 1;
                AnnotationGroupMove.X = 18;
                AnnotationActionBadge.Opacity = 1;
                AnnotationActionText.Text = "Ctrl+Y · 重做";
                break;
            case 3:
                LongTranslate.Y = -144;
                LongProgressText.Text = "3 / 3";
                LongViewport.Opacity = .34;
                LongResult.Opacity = 1;
                LongAutoStatus.Opacity = 1;
                LongManualStatus.Opacity = .82;
                break;
            case 4:
                Canvas.SetLeft(PinnedImage, 322);
                Canvas.SetTop(PinnedImage, 82);
                PinnedImage.Width = 110;
                PinnedImage.Height = 85;
                Canvas.SetLeft(PinCursor, 374);
                Canvas.SetTop(PinCursor, 122);
                PinEdgeRail.Opacity = 1;
                PinMKeycap.Opacity = 1;
                Canvas.SetLeft(PinBadge, 397);
                Canvas.SetTop(PinBadge, 87);
                PinBadge.Opacity = 1;
                PinHoverHint.Opacity = 1;
                break;
            case 5:
                OcrSelection.Width = 258;
                OcrScene.Opacity = 1;
                QrScene.Opacity = 0;
                OcrCopiedToast.Opacity = 1;
                QrHotspot.Opacity = 1;
                QrSummary.Opacity = 1;
                QrOpenHint.Opacity = 1;
                break;
            case 6:
                HistoryThumb0.Opacity = 1;
                HistoryThumb1.Opacity = 1;
                HistoryThumb2.Opacity = 1;
                HistorySearchText.Text = "方案";
                HistoryCard0.Opacity = 0;
                HistoryCard2.Opacity = 0;
                HistoryWorkTag.Opacity = 1;
                HistorySourceBadge.Opacity = 1;
                HistoryOffscreenHint.Opacity = 1;
                HistoryScene.Opacity = 1;
                HistoryDesktopScene.Opacity = 0;
                HistoryCaptureScene.Opacity = 0;
                HistorySelectionC.Opacity = 1;
                HistoryRKeycap.Opacity = 1;
                HistoryRecaptureBadge.Opacity = 1;
                break;
        }
    }

    private void StopAnimations()
    {
        SmartRecognitionFrame.BeginAnimation(OpacityProperty, null);
        CaptureDimmer.BeginAnimation(OpacityProperty, null);
        CaptureSelection.BeginAnimation(FrameworkElement.WidthProperty, null);
        CaptureSelection.BeginAnimation(FrameworkElement.HeightProperty, null);
        CaptureSelection.BeginAnimation(OpacityProperty, null);
        CaptureCursor.BeginAnimation(Canvas.LeftProperty, null);
        CaptureCursor.BeginAnimation(Canvas.TopProperty, null);
        CaptureSmartHint.BeginAnimation(OpacityProperty, null);
        CaptureDragHint.BeginAnimation(OpacityProperty, null);
        CaptureSize.BeginAnimation(OpacityProperty, null);
        CaptureToolbar.BeginAnimation(OpacityProperty, null);
        PrecisionSelection.BeginAnimation(FrameworkElement.WidthProperty, null);
        PrecisionSelection.BeginAnimation(FrameworkElement.HeightProperty, null);
        PrecisionSelection.BeginAnimation(Canvas.LeftProperty, null);
        PrecisionSelection.BeginAnimation(Canvas.TopProperty, null);
        PrecisionCursor.BeginAnimation(Canvas.LeftProperty, null);
        PrecisionCursor.BeginAnimation(Canvas.TopProperty, null);
        PrecisionGeometryPanel.BeginAnimation(OpacityProperty, null);
        PrecisionRatioBadge.BeginAnimation(OpacityProperty, null);
        PrecisionHandles.BeginAnimation(OpacityProperty, null);
        PrecisionLockBadge.BeginAnimation(OpacityProperty, null);
        PrecisionMagnifier.BeginAnimation(OpacityProperty, null);
        PrecisionPixelMove.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, null);
        PrecisionRatioText.BeginAnimation(TextBlock.TextProperty, null);
        PrecisionXText.BeginAnimation(TextBlock.TextProperty, null);
        PrecisionYText.BeginAnimation(TextBlock.TextProperty, null);
        PrecisionWText.BeginAnimation(TextBlock.TextProperty, null);
        PrecisionHText.BeginAnimation(TextBlock.TextProperty, null);
        AnnotationArrowTool.BeginAnimation(OpacityProperty, null);
        AnnotationRectangleTool.BeginAnimation(OpacityProperty, null);
        AnnotationTextTool.BeginAnimation(OpacityProperty, null);
        AnnotationArrow.BeginAnimation(OpacityProperty, null);
        AnnotationScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, null);
        AnnotationScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, null);
        AnnotationRectangleGroup.BeginAnimation(OpacityProperty, null);
        AnnotationText.BeginAnimation(OpacityProperty, null);
        AnnotationSingleSelection.BeginAnimation(OpacityProperty, null);
        AnnotationRectangleMove.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, null);
        AnnotationMultiSelection.BeginAnimation(OpacityProperty, null);
        AnnotationGroupMove.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, null);
        AnnotationActionBadge.BeginAnimation(OpacityProperty, null);
        AnnotationActionText.BeginAnimation(TextBlock.TextProperty, null);
        LongCaptureButton.BeginAnimation(OpacityProperty, null);
        LongTranslate.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, null);
        LongOverlapLine.BeginAnimation(OpacityProperty, null);
        LongResult.BeginAnimation(OpacityProperty, null);
        LongViewport.BeginAnimation(OpacityProperty, null);
        LongAutoStatus.BeginAnimation(OpacityProperty, null);
        LongManualStatus.BeginAnimation(OpacityProperty, null);
        LongProgressText.BeginAnimation(TextBlock.TextProperty, null);
        PinStateBadge.BeginAnimation(OpacityProperty, null);
        PinCursor.BeginAnimation(Canvas.LeftProperty, null);
        PinCursor.BeginAnimation(Canvas.TopProperty, null);
        PinnedImage.BeginAnimation(Canvas.LeftProperty, null);
        PinnedImage.BeginAnimation(Canvas.TopProperty, null);
        PinnedImage.BeginAnimation(FrameworkElement.WidthProperty, null);
        PinnedImage.BeginAnimation(FrameworkElement.HeightProperty, null);
        PinWheelBadge.BeginAnimation(OpacityProperty, null);
        PinMKeycap.BeginAnimation(OpacityProperty, null);
        PinEdgeRail.BeginAnimation(OpacityProperty, null);
        PinBadge.BeginAnimation(OpacityProperty, null);
        PinBadge.BeginAnimation(Canvas.LeftProperty, null);
        PinBadge.BeginAnimation(Canvas.TopProperty, null);
        PinHoverHint.BeginAnimation(OpacityProperty, null);
        PinClickPulse.BeginAnimation(OpacityProperty, null);
        OcrScene.BeginAnimation(OpacityProperty, null);
        OcrSelection.BeginAnimation(FrameworkElement.WidthProperty, null);
        OcrCursor.BeginAnimation(Canvas.LeftProperty, null);
        OcrCopyKeycap.BeginAnimation(OpacityProperty, null);
        OcrCopiedToast.BeginAnimation(OpacityProperty, null);
        QrScene.BeginAnimation(OpacityProperty, null);
        QrEntryButton.BeginAnimation(OpacityProperty, null);
        QrCursorMove.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, null);
        QrCursorMove.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, null);
        QrCursor.BeginAnimation(OpacityProperty, null);
        QrHotspot.BeginAnimation(OpacityProperty, null);
        QrSummary.BeginAnimation(OpacityProperty, null);
        QrOpenHint.BeginAnimation(OpacityProperty, null);
        HistoryThumb0.BeginAnimation(OpacityProperty, null);
        HistoryThumb1.BeginAnimation(OpacityProperty, null);
        HistoryThumb2.BeginAnimation(OpacityProperty, null);
        HistorySearchText.BeginAnimation(TextBlock.TextProperty, null);
        HistoryCard0.BeginAnimation(OpacityProperty, null);
        HistoryCard2.BeginAnimation(OpacityProperty, null);
        HistoryOffscreenHint.BeginAnimation(OpacityProperty, null);
        HistoryWorkTag.BeginAnimation(OpacityProperty, null);
        HistorySourceBadge.BeginAnimation(OpacityProperty, null);
        HistoryPinAction.BeginAnimation(OpacityProperty, null);
        HistoryScene.BeginAnimation(OpacityProperty, null);
        HistoryDesktopScene.BeginAnimation(OpacityProperty, null);
        HistoryCaptureScene.BeginAnimation(OpacityProperty, null);
        HistoryRKeycap.BeginAnimation(OpacityProperty, null);
        HistorySelectionA.BeginAnimation(OpacityProperty, null);
        HistorySelectionB.BeginAnimation(OpacityProperty, null);
        HistorySelectionC.BeginAnimation(OpacityProperty, null);
        HistoryRecaptureBadge.BeginAnimation(OpacityProperty, null);
        NextButton.BeginAnimation(OpacityProperty, null);

        SmartRecognitionFrame.Opacity = 0;
        CaptureDimmer.Opacity = 0;
        CaptureSelection.Width = 1;
        CaptureSelection.Height = 1;
        CaptureSelection.Opacity = 1;
        Canvas.SetLeft(CaptureCursor, 34);
        Canvas.SetTop(CaptureCursor, 34);
        CaptureSmartHint.Opacity = 0;
        CaptureDragHint.Opacity = 0;
        CaptureSize.Opacity = 0;
        CaptureToolbar.Opacity = 0;
        PrecisionSelection.Width = 230;
        PrecisionSelection.Height = 144;
        Canvas.SetLeft(PrecisionSelection, 70);
        Canvas.SetTop(PrecisionSelection, 60);
        Canvas.SetLeft(PrecisionCursor, 296);
        Canvas.SetTop(PrecisionCursor, 199);
        PrecisionGeometryPanel.Opacity = .72;
        PrecisionRatioBadge.Opacity = .64;
        PrecisionHandles.Opacity = 1;
        PrecisionLockBadge.Opacity = 0;
        PrecisionMagnifier.Opacity = 0;
        PrecisionPixelMove.X = 0;
        PrecisionRatioText.Text = "自由";
        PrecisionXText.Text = "70";
        PrecisionYText.Text = "60";
        PrecisionWText.Text = "230";
        PrecisionHText.Text = "144";
        AnnotationArrowTool.Opacity = .42;
        AnnotationRectangleTool.Opacity = .42;
        AnnotationTextTool.Opacity = .42;
        AnnotationArrow.Opacity = 0;
        AnnotationScale.ScaleX = 0;
        AnnotationScale.ScaleY = 0;
        AnnotationRectangleGroup.Opacity = 0;
        AnnotationText.Opacity = 0;
        AnnotationSingleSelection.Opacity = 0;
        AnnotationRectangleMove.X = 0;
        AnnotationMultiSelection.Opacity = 0;
        AnnotationGroupMove.X = 0;
        AnnotationActionBadge.Opacity = 0;
        AnnotationActionText.Text = "F2 · 选择";
        LongCaptureButton.Opacity = .55;
        LongTranslate.Y = 0;
        LongOverlapLine.Opacity = 0;
        LongResult.Opacity = 0;
        LongViewport.Opacity = 1;
        LongAutoStatus.Opacity = 0;
        LongManualStatus.Opacity = 0;
        LongProgressText.Text = "1 / 3";
        PinStateBadge.Opacity = 0;
        Canvas.SetLeft(PinCursor, 58);
        Canvas.SetTop(PinCursor, 42);
        Canvas.SetLeft(PinnedImage, 92);
        Canvas.SetTop(PinnedImage, 55);
        PinnedImage.Width = 250;
        PinnedImage.Height = 165;
        PinWheelBadge.Opacity = 0;
        PinMKeycap.Opacity = 0;
        PinEdgeRail.Opacity = 0;
        Canvas.SetLeft(PinBadge, 411);
        Canvas.SetTop(PinBadge, 99);
        PinBadge.Opacity = 0;
        PinHoverHint.Opacity = 0;
        PinClickPulse.Opacity = 0;
        OcrScene.Opacity = 1;
        OcrSelection.Width = 1;
        Canvas.SetLeft(OcrCursor, 7);
        OcrCopyKeycap.Opacity = 0;
        OcrCopiedToast.Opacity = 0;
        QrScene.Opacity = 0;
        QrEntryButton.Opacity = .58;
        QrCursorMove.X = 0;
        QrCursorMove.Y = 0;
        QrCursor.Opacity = 1;
        QrHotspot.Opacity = 0;
        QrSummary.Opacity = 0;
        QrOpenHint.Opacity = 0;
        HistoryThumb0.Opacity = 0;
        HistoryThumb1.Opacity = 0;
        HistoryThumb2.Opacity = 0;
        HistorySearchText.Text = "搜索截图";
        HistoryCard0.Opacity = 1;
        HistoryCard2.Opacity = 1;
        HistoryOffscreenHint.Opacity = 0;
        HistoryWorkTag.Opacity = .55;
        HistorySourceBadge.Opacity = 0;
        HistoryPinAction.Opacity = .65;
        HistoryScene.Opacity = 1;
        HistoryDesktopScene.Opacity = 0;
        HistoryCaptureScene.Opacity = 0;
        HistoryRKeycap.Opacity = 0;
        HistorySelectionA.Opacity = 0;
        HistorySelectionB.Opacity = 0;
        HistorySelectionC.Opacity = 0;
        HistoryRecaptureBadge.Opacity = 0;
        NextButton.Opacity = 1;
    }

    private static void Animate(
        FrameworkElement target,
        DependencyProperty property,
        double durationSeconds,
        params (double Time, double Value)[] frames) =>
        target.BeginAnimation(property, CreateLoop(durationSeconds, frames));

    private static void Animate(
        System.Windows.Media.Animation.Animatable target,
        DependencyProperty property,
        double durationSeconds,
        params (double Time, double Value)[] frames) =>
        target.BeginAnimation(property, CreateLoop(durationSeconds, frames));

    private static DoubleAnimationUsingKeyFrames CreateLoop(
        double durationSeconds,
        IEnumerable<(double Time, double Value)> frames)
    {
        var animation = new DoubleAnimationUsingKeyFrames
        {
            Duration = TimeSpan.FromSeconds(durationSeconds),
            RepeatBehavior = RepeatBehavior.Forever
        };
        foreach (var (time, value) in frames)
        {
            animation.KeyFrames.Add(new SplineDoubleKeyFrame(
                value,
                KeyTime.FromTimeSpan(TimeSpan.FromSeconds(time)),
                new KeySpline(.35, .05, .2, 1)));
        }

        return animation;
    }

    private static void AnimateText(
        TextBlock target,
        double durationSeconds,
        params (double Time, string Value)[] frames)
    {
        var animation = new StringAnimationUsingKeyFrames
        {
            Duration = TimeSpan.FromSeconds(durationSeconds),
            RepeatBehavior = RepeatBehavior.Forever
        };
        foreach (var (time, value) in frames)
        {
            animation.KeyFrames.Add(new DiscreteStringKeyFrame(
                value,
                KeyTime.FromTimeSpan(TimeSpan.FromSeconds(time))));
        }

        target.BeginAnimation(TextBlock.TextProperty, animation);
    }

    private static string GetDisplayVersion()
    {
        var assembly = typeof(FirstRunTutorialWindow).Assembly;
        var informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion?
            .Split('+', 2)[0]
            .Trim();
        var version = string.IsNullOrWhiteSpace(informational)
            ? assembly.GetName().Version?.ToString(3) ?? string.Empty
            : informational;

        return version.StartsWith('v') ? version : $"v{version}";
    }

    private void OnStepChecked(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized || sender is not WpfRadioButton button || !int.TryParse(button.Tag?.ToString(), out var step))
        {
            return;
        }

        SelectStep(step - 1);
    }

    private void OnPreviousClick(object sender, RoutedEventArgs e) => SelectStep(_currentStep - 1);

    private void OnNextClick(object sender, RoutedEventArgs e)
    {
        if (_currentStep == Steps.Length - 1)
        {
            Close();
            return;
        }

        SelectStep(_currentStep + 1);
    }

    private void OnFinishClick(object sender, RoutedEventArgs e) => Close();

    internal void RunNavigationStress(int cycles)
    {
        for (var cycle = 0; cycle < Math.Max(0, cycles); cycle++)
        {
            for (var step = 0; step < Steps.Length; step++)
            {
                SelectStep(step);
            }

            for (var step = Steps.Length - 2; step >= 0; step--)
            {
                SelectStep(step);
            }
        }
    }

    private void OnTitleBarMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void OnPreviewKeyDown(object sender, WpfKeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
        else if (e.Key == Key.Left)
        {
            SelectStep(_currentStep - 1);
            e.Handled = true;
        }
        else if (e.Key == Key.Right || e.Key == Key.Enter)
        {
            OnNextClick(this, new RoutedEventArgs());
            e.Handled = true;
        }
    }

    private sealed record TutorialStep(
        string Key,
        string Kicker,
        string Title,
        string Description,
        string StepName,
        string StepHint,
        IReadOnlyList<TutorialShortcut> ShortcutTips,
        string? OptionalNote = null);

    private sealed class TutorialShortcut
    {
        public TutorialShortcut(string key, string text)
        {
            Text = text;
            KeyParts = CreateKeyParts(key);
        }

        public string Text { get; }

        public IReadOnlyList<TutorialKeyPart> KeyParts { get; }

        private static IReadOnlyList<TutorialKeyPart> CreateKeyParts(string key)
        {
            var separator = key.Contains('/') ? '/' : key.Contains('+') ? '+' : '\0';
            var parts = separator == '\0'
                ? [key]
                : key.Split(separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            return parts
                .Select((part, index) => new TutorialKeyPart(part, index == 0 ? string.Empty : separator.ToString()))
                .ToArray();
        }
    }

    private sealed record TutorialKeyPart(string Text, string Separator);
}
