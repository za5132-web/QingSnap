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
        new("F1", "截图从这里开始", "按 F1，框住你想要的内容", "移动鼠标自动识别窗口，也可以自由拖出选区；W/A/S/D 可以逐像素微调准星。"),
        new("R", "快速重用选区", "按 R，恢复上一次截图范围", "截图界面会直接显示上一次的选区蒙层，你可以继续移动、缩放和标注。"),
        new("标注", "让重点更清楚", "画笔、箭头和图形随手可用", "选中标注后可继续调整；鼠标滚轮能修改线条粗细或文字大小。"),
        new("长图", "自动滚动拼接", "保持蒙层，边看边截取长图", "长截图会自动滚动、判断重叠区域并完成拼接，也可以随时切换为手动补截。"),
        new("F3", "贴图像便签一样", "把截图或剪贴板图片贴到桌面", "贴图默认出现在原截图位置；拖到屏幕边缘可缩成缩略窗，移入后平滑展开。"),
        new("Ctrl+C", "贴图也像文档一样可选", "直接拖选图片里的文字", "OCR 会在后台提前准备，不显示分词框；完成选择后按 Ctrl+C 即可复制文字。"),
        new("记录", "每次截图都能再次使用", "在截图记录里搜索、收藏和贴图", "历史窗口保存缩略图并在后台建立文字索引，可按文件名、尺寸或图片文字快速查找。")
    ];

    private readonly WpfRadioButton[] _stepButtons;
    private readonly FrameworkElement[] _demos;
    private int _currentStep;

    public FirstRunTutorialWindow()
    {
        InitializeComponent();
        _stepButtons = [Step0, Step1, Step2, Step3, Step4, Step5, Step6];
        _demos = [Demo0, Demo1, Demo2, Demo3, Demo4, Demo5, Demo6];
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
        StepPositionText.Text = $"{_currentStep + 1:00} / {Steps.Length:00}";
        PreviousButton.IsEnabled = _currentStep > 0;
        NextButton.Content = _currentStep == Steps.Length - 1 ? "开始使用" : "下一步";

        StartAnimation(_currentStep);
    }

    private void StartAnimation(int index)
    {
        if (!SystemParameters.ClientAreaAnimation)
        {
            ShowStaticState(index);
            return;
        }

        switch (index)
        {
            case 0:
                Animate(CaptureSelection, FrameworkElement.WidthProperty, 2.9,
                    (0, 1), (.34, 1), (1.75, 320), (2.55, 320), (2.9, 1));
                Animate(CaptureSelection, FrameworkElement.HeightProperty, 2.9,
                    (0, 1), (.34, 1), (1.75, 225), (2.55, 225), (2.9, 1));
                Animate(CaptureCursor, Canvas.LeftProperty, 2.9,
                    (0, 70), (.34, 70), (1.75, 391), (2.55, 391), (2.9, 70));
                Animate(CaptureCursor, Canvas.TopProperty, 2.9,
                    (0, 45), (.34, 45), (1.75, 271), (2.55, 271), (2.9, 45));
                Animate(CaptureSize, OpacityProperty, 2.9,
                    (0, 0), (1.55, 0), (1.82, 1), (2.55, 1), (2.82, 0), (2.9, 0));
                break;
            case 1:
                Animate(RepeatSelection, OpacityProperty, 2.5,
                    (0, .18), (.42, .18), (1.15, 1), (2.15, 1), (2.5, .18));
                Animate(RepeatBadge, OpacityProperty, 2.5,
                    (0, 0), (.65, 0), (1.15, 1), (2.15, 1), (2.5, 0));
                Animate(RepeatScale, System.Windows.Media.ScaleTransform.ScaleXProperty, 2.5,
                    (0, .94), (.42, .94), (1.15, 1), (2.15, 1), (2.5, .94));
                Animate(RepeatScale, System.Windows.Media.ScaleTransform.ScaleYProperty, 2.5,
                    (0, .94), (.42, .94), (1.15, 1), (2.15, 1), (2.5, .94));
                break;
            case 2:
                Animate(AnnotationScale, System.Windows.Media.ScaleTransform.ScaleXProperty, 2.7,
                    (0, 0), (.45, 0), (1.5, 1), (2.3, 1), (2.7, 0));
                break;
            case 3:
                Animate(LongTranslate, System.Windows.Media.TranslateTransform.YProperty, 3.6,
                    (0, 0), (.5, 0), (2.6, -170), (3.2, -170), (3.6, 0));
                break;
            case 4:
                Animate(PinnedImage, Canvas.LeftProperty, 3.4,
                    (0, 105), (.45, 105), (2.2, 400), (2.82, 400), (3.4, 105));
                Animate(PinnedImage, Canvas.TopProperty, 3.4,
                    (0, 53), (.45, 53), (2.2, 116), (2.82, 116), (3.4, 53));
                Animate(PinnedImage, FrameworkElement.WidthProperty, 3.4,
                    (0, 255), (.45, 255), (2.2, 54), (2.82, 54), (3.4, 255));
                Animate(PinnedImage, FrameworkElement.HeightProperty, 3.4,
                    (0, 190), (.45, 190), (2.2, 78), (2.82, 78), (3.4, 190));
                Animate(PinBadge, OpacityProperty, 3.4,
                    (0, 0), (1.8, 0), (2.18, 1), (2.82, 1), (3.15, 0), (3.4, 0));
                break;
            case 5:
                Animate(OcrSelection, FrameworkElement.WidthProperty, 2.8,
                    (0, 1), (.42, 1), (1.75, 282), (2.42, 282), (2.8, 1));
                break;
        }
    }

    private void ShowStaticState(int index)
    {
        switch (index)
        {
            case 0:
                CaptureSelection.Width = 320;
                CaptureSelection.Height = 225;
                Canvas.SetLeft(CaptureCursor, 391);
                Canvas.SetTop(CaptureCursor, 271);
                CaptureSize.Opacity = 1;
                break;
            case 1:
                RepeatSelection.Opacity = 1;
                RepeatScale.ScaleX = 1;
                RepeatScale.ScaleY = 1;
                RepeatBadge.Opacity = 1;
                break;
            case 2:
                AnnotationScale.ScaleX = 1;
                break;
            case 5:
                OcrSelection.Width = 282;
                break;
        }
    }

    private void StopAnimations()
    {
        CaptureSelection.BeginAnimation(FrameworkElement.WidthProperty, null);
        CaptureSelection.BeginAnimation(FrameworkElement.HeightProperty, null);
        CaptureCursor.BeginAnimation(Canvas.LeftProperty, null);
        CaptureCursor.BeginAnimation(Canvas.TopProperty, null);
        CaptureSize.BeginAnimation(OpacityProperty, null);
        RepeatSelection.BeginAnimation(OpacityProperty, null);
        RepeatBadge.BeginAnimation(OpacityProperty, null);
        RepeatScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, null);
        RepeatScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, null);
        AnnotationScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, null);
        LongTranslate.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, null);
        PinnedImage.BeginAnimation(Canvas.LeftProperty, null);
        PinnedImage.BeginAnimation(Canvas.TopProperty, null);
        PinnedImage.BeginAnimation(FrameworkElement.WidthProperty, null);
        PinnedImage.BeginAnimation(FrameworkElement.HeightProperty, null);
        PinBadge.BeginAnimation(OpacityProperty, null);
        OcrSelection.BeginAnimation(FrameworkElement.WidthProperty, null);

        CaptureSelection.Width = 1;
        CaptureSelection.Height = 1;
        Canvas.SetLeft(CaptureCursor, 70);
        Canvas.SetTop(CaptureCursor, 45);
        CaptureSize.Opacity = 0;
        RepeatSelection.Opacity = 1;
        RepeatBadge.Opacity = 1;
        RepeatScale.ScaleX = 1;
        RepeatScale.ScaleY = 1;
        AnnotationScale.ScaleX = 0;
        LongTranslate.Y = 0;
        Canvas.SetLeft(PinnedImage, 105);
        Canvas.SetTop(PinnedImage, 53);
        PinnedImage.Width = 255;
        PinnedImage.Height = 190;
        PinBadge.Opacity = 0;
        OcrSelection.Width = 1;
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
        string Description);
}
