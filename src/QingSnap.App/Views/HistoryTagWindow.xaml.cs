using System.Windows;
using System.Windows.Input;
using QingSnap.App.Services;

namespace QingSnap.App.Views;

public enum HistoryTagEditMode
{
    Add,
    Remove
}

public partial class HistoryTagWindow : Window
{
    private readonly HistoryTagEditMode _mode;

    public HistoryTagWindow(
        HistoryTagEditMode mode,
        IEnumerable<string> availableTags,
        IEnumerable<string> currentTags)
    {
        InitializeComponent();
        _mode = mode;
        var current = currentTags.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var choices = (mode == HistoryTagEditMode.Add
                ? availableTags.Where(tag => !current.Contains(tag))
                : current)
            .Select(HistoryMetadataStore.NormalizeTagName)
            .Where(tag => tag.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(tag => tag, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        TagList.ItemsSource = choices;
        EmptyTagsText.Visibility = choices.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

        if (mode == HistoryTagEditMode.Add)
        {
            WindowTitleText.Text = "添加标签";
            IntroText.Text = "输入新标签，或从已有标签中选择。多个新标签可用逗号分隔。";
            ListLabelText.Text = "已有标签 · 可多选";
            ApplyButton.Content = "添加";
            Loaded += (_, _) => CustomTagsBox.Focus();
        }
        else
        {
            WindowTitleText.Text = "移除标签";
            IntroText.Text = "选择要从这张截图移除的标签；图片本身不会被删除。";
            ListLabelText.Text = "当前标签 · 可多选";
            CustomTagsBox.Visibility = Visibility.Collapsed;
            ApplyButton.Content = "移除";
            Loaded += (_, _) => TagList.Focus();
        }
    }

    public bool Accepted { get; private set; }

    public IReadOnlyList<string> SelectedTags { get; private set; } = [];

    private void OnApplyClick(object sender, RoutedEventArgs e) => Accept();

    private void Accept()
    {
        var selected = TagList.SelectedItems
            .OfType<string>()
            .ToList();
        if (_mode == HistoryTagEditMode.Add)
        {
            selected.AddRange(CustomTagsBox.Text.Split(
                [',', '，', ';', '；', '\r', '\n'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }

        SelectedTags = selected
            .Select(HistoryMetadataStore.NormalizeTagName)
            .Where(tag => tag.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (SelectedTags.Count == 0)
        {
            ValidationText.Text = _mode == HistoryTagEditMode.Add
                ? "请输入或选择至少一个标签。"
                : "请选择至少一个要移除的标签。";
            ValidationText.Visibility = Visibility.Visible;
            return;
        }

        Accepted = true;
        Close();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => Close();

    private void OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            Accept();
            e.Handled = true;
        }
    }

    private void OnTitleBarMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }
}
