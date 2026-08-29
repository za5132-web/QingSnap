using System.Windows;
using System.Windows.Input;
using WpfKeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace QingSnap.App.Views;

public partial class NumberAnnotationEditWindow : Window
{
    public NumberAnnotationEditWindow(int currentValue)
    {
        InitializeComponent();
        NumberBox.Text = currentValue.ToString();
        Loaded += (_, _) =>
        {
            NumberBox.Focus();
            NumberBox.SelectAll();
        };
    }

    public int Value { get; private set; }

    private void OnConfirmClick(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(NumberBox.Text.Trim(), out var value) || value is < 1 or > 9999)
        {
            ErrorText.Text = "请输入 1–9999 之间的整数";
            NumberBox.Focus();
            NumberBox.SelectAll();
            return;
        }

        Value = value;
        DialogResult = true;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;

    private void OnTitleBarMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void OnPreviewKeyDown(object sender, WpfKeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            OnConfirmClick(this, new RoutedEventArgs());
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            DialogResult = false;
            e.Handled = true;
        }
    }
}
