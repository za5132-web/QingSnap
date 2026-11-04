using System.Drawing;
using System.Windows.Forms;

namespace QingSnap.App.Services;

/// <summary>The tray is WinForms; WPF resource lookup alone cannot style its menu.</summary>
internal sealed class ThemeToolStripRenderer(ThemeService theme) : ToolStripProfessionalRenderer
{
    internal Color GetColor(string key)
    {
        var color = theme.GetColor(key);
        return Color.FromArgb(255, color.R, color.G, color.B);
    }

    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
    {
        using var brush = new SolidBrush(GetColor("ThemePopupBrush"));
        e.Graphics.FillRectangle(brush, e.AffectedBounds);
    }

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        using var pen = new Pen(GetColor("ThemeBorderStrongBrush"));
        e.Graphics.DrawRectangle(pen, 0, 0, Math.Max(0, e.ToolStrip.Width - 1), Math.Max(0, e.ToolStrip.Height - 1));
    }

    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        if (!e.Item.Selected || !e.Item.Enabled)
        {
            return;
        }
        using var brush = new SolidBrush(GetColor("ThemeSelectedBrush"));
        e.Graphics.FillRectangle(brush, new Rectangle(3, 1, Math.Max(0, e.Item.Width - 6), Math.Max(0, e.Item.Height - 2)));
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = GetColor(e.Item.Enabled ? "ThemeTextBrush" : "ThemeSubtleTextBrush");
        base.OnRenderItemText(e);
    }

    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        using var pen = new Pen(GetColor("ThemeBorderStrongBrush"));
        e.Graphics.DrawLine(pen, 10, e.Item.Height / 2, e.Item.Width - 10, e.Item.Height / 2);
    }
}
