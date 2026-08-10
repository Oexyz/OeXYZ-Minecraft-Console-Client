using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace OeXYZ.ConsoleClient;

internal static class Theme
{
    public static readonly Color Background = Color.FromArgb(7, 10, 15);
    public static readonly Color Sidebar = Color.FromArgb(11, 15, 22);
    public static readonly Color Surface = Color.FromArgb(16, 22, 32);
    public static readonly Color Raised = Color.FromArgb(23, 31, 44);
    public static readonly Color Tint = Color.FromArgb(29, 39, 54);
    public static readonly Color Border = Color.FromArgb(40, 54, 73);
    public static readonly Color Ink = Color.FromArgb(240, 246, 252);
    public static readonly Color Muted = Color.FromArgb(143, 159, 180);
    public static readonly Color Blue = Color.FromArgb(8, 123, 234);
    public static readonly Color BlueBright = Color.FromArgb(51, 163, 255);
    public static readonly Color Green = Color.FromArgb(54, 211, 153);
    public static readonly Color Amber = Color.FromArgb(255, 189, 46);
    public static readonly Color Danger = Color.FromArgb(255, 95, 86);
    public static readonly Color Dark = Color.FromArgb(5, 8, 13);
    public static readonly Color DarkSurface = Color.FromArgb(13, 18, 27);
    public static readonly Font Body = new("Segoe UI", 9F);
    public static readonly Font Wordmark = new("Bahnschrift SemiBold", 23F, FontStyle.Bold);

    public static Button Button(string text, int width)
    {
        Button button = new()
        {
            Text = text,
            Width = width,
            Height = 34,
            FlatStyle = FlatStyle.Flat,
            BackColor = Raised,
            ForeColor = Ink,
            Font = Body,
            Cursor = Cursors.Hand
        };
        button.FlatAppearance.BorderColor = Border;
        button.FlatAppearance.MouseOverBackColor = Tint;
        button.FlatAppearance.MouseDownBackColor = Color.FromArgb(31, 70, 106);
        return button;
    }

    public static void Primary(Button button)
    {
        button.BackColor = Blue;
        button.ForeColor = Color.White;
        button.FlatAppearance.BorderColor = Blue;
        button.FlatAppearance.MouseOverBackColor = BlueBright;
        button.FlatAppearance.MouseDownBackColor = Color.FromArgb(6, 92, 176);
        button.Font = new Font("Segoe UI", 9F, FontStyle.Bold);
    }

    public static Label Heading(string text, float size) => new()
    {
        Text = text,
        AutoSize = true,
        ForeColor = Ink,
        Font = new Font("Segoe UI", size, FontStyle.Bold)
    };

    public static void Input(Control control)
    {
        control.BackColor = DarkSurface;
        control.ForeColor = Ink;
        if (control is TextBoxBase text) text.BorderStyle = BorderStyle.FixedSingle;
        if (control is ComboBox combo) combo.FlatStyle = FlatStyle.Flat;
    }

    public static void ApplyDarkTitleBar(Form form)
    {
        try
        {
            int enabled = 1;
            if (DwmSetWindowAttribute(form.Handle, 20, ref enabled, sizeof(int)) != 0)
                DwmSetWindowAttribute(form.Handle, 19, ref enabled, sizeof(int));
        }
        catch
        {
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr handle, int attribute, ref int value, int size);
}

internal sealed class LogoControl : Control
{
    public LogoControl()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw | ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        Size = new Size(64, 64);
    }

    protected override void OnPaint(PaintEventArgs eventArgs)
    {
        base.OnPaint(eventArgs);
        DrawLogo(eventArgs.Graphics, ClientRectangle);
    }

    public static void DrawLogo(Graphics graphics, Rectangle bounds)
    {
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        float scale = Math.Min(bounds.Width, bounds.Height) / 64F;
        GraphicsState state = graphics.Save();
        graphics.TranslateTransform(
            bounds.Left + (bounds.Width - 64F * scale) / 2F,
            bounds.Top + (bounds.Height - 64F * scale) / 2F);
        graphics.ScaleTransform(scale, scale);

        using (GraphicsPath background = RoundedRectangle(new RectangleF(0, 0, 64, 64), 17F))
        using (SolidBrush dark = new(Theme.Dark))
            graphics.FillPath(dark, background);
        PointF[] outer =
        [
            new(18, 20.5F), new(32, 12), new(46, 20.5F),
            new(46, 37.5F), new(32, 46), new(18, 37.5F), new(18, 20.5F)
        ];
        using (LinearGradientBrush gradient = new(new PointF(8, 6), new PointF(56, 58), Theme.BlueBright, Color.FromArgb(22, 94, 232)))
        using (Pen blue = new(gradient, 5F) { LineJoin = LineJoin.Round })
            graphics.DrawLines(blue, outer);
        using (Pen light = new(Color.FromArgb(135, 199, 255), 3F) { LineJoin = LineJoin.Round })
        {
            graphics.DrawLine(light, new PointF(18, 20.5F), new PointF(32, 29.1F));
            graphics.DrawLine(light, new PointF(32, 29.1F), new PointF(46, 20.5F));
            graphics.DrawLine(light, new PointF(32, 29.1F), new PointF(32, 46));
        }
        using (SolidBrush green = new(Theme.Green)) graphics.FillEllipse(green, 46, 45, 7, 7);
        graphics.Restore(state);
    }

    private static GraphicsPath RoundedRectangle(RectangleF rectangle, float radius)
    {
        GraphicsPath path = new();
        float diameter = radius * 2F;
        path.AddArc(rectangle.Left, rectangle.Top, diameter, diameter, 180, 90);
        path.AddArc(rectangle.Right - diameter, rectangle.Top, diameter, diameter, 270, 90);
        path.AddArc(rectangle.Right - diameter, rectangle.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(rectangle.Left, rectangle.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
}

internal sealed class WordmarkControl : Control
{
    public WordmarkControl()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw | ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        Size = new Size(170, 48);
    }

    protected override void OnPaint(PaintEventArgs eventArgs)
    {
        eventArgs.Graphics.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        using (SolidBrush text = new(Theme.Ink))
            eventArgs.Graphics.DrawString("OeXYZ", Theme.Wordmark, text, new PointF(0, -2), StringFormat.GenericTypographic);
        Rectangle line = new(2, Height - 8, Math.Min(112, Width - 4), 3);
        using LinearGradientBrush gradient = new(line, Theme.Blue, Theme.Green, LinearGradientMode.Horizontal);
        eventArgs.Graphics.FillRectangle(gradient, line);
    }
}

internal sealed class BrandTabControl : TabControl
{
    public BrandTabControl()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        ItemSize = new Size(168, 36);
        SizeMode = TabSizeMode.Fixed;
        Padding = new Point(16, 5);
    }

    protected override void OnPaintBackground(PaintEventArgs eventArgs) => eventArgs.Graphics.Clear(Theme.Background);

    protected override void OnPaint(PaintEventArgs eventArgs)
    {
        eventArgs.Graphics.Clear(Theme.Background);
        using (Pen divider = new(Theme.Border))
            eventArgs.Graphics.DrawLine(divider, 0, ItemSize.Height + 2, Width, ItemSize.Height + 2);
        for (int index = 0; index < TabCount; index++)
        {
            Rectangle rectangle = GetTabRect(index);
            bool selected = index == SelectedIndex;
            using (SolidBrush background = new(selected ? Theme.Surface : Theme.Sidebar))
                eventArgs.Graphics.FillRectangle(background, rectangle);
            if (selected)
            {
                using SolidBrush accent = new(Theme.Blue);
                eventArgs.Graphics.FillRectangle(accent, rectangle.Left, rectangle.Bottom - 3, rectangle.Width, 3);
            }
            TextRenderer.DrawText(eventArgs.Graphics, TabPages[index].Text, Font, rectangle,
                selected ? Theme.Ink : Theme.Muted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }
    }

    protected override void OnSelectedIndexChanged(EventArgs eventArgs)
    {
        base.OnSelectedIndexChanged(eventArgs);
        Invalidate();
    }
}

internal sealed class BrandListView : ListView
{
    public BrandListView()
    {
        OwnerDraw = true;
        DoubleBuffered = true;
        BorderStyle = BorderStyle.FixedSingle;
        BackColor = Theme.DarkSurface;
        ForeColor = Theme.Ink;
    }

    protected override void OnHandleCreated(EventArgs eventArgs)
    {
        base.OnHandleCreated(eventArgs);
        try { SetWindowTheme(Handle, "DarkMode_Explorer", null); } catch { }
    }

    protected override void OnDrawColumnHeader(DrawListViewColumnHeaderEventArgs eventArgs)
    {
        using (SolidBrush background = new(Theme.Raised)) eventArgs.Graphics.FillRectangle(background, eventArgs.Bounds);
        using (Pen border = new(Theme.Border))
        {
            eventArgs.Graphics.DrawLine(border, eventArgs.Bounds.Left, eventArgs.Bounds.Bottom - 1, eventArgs.Bounds.Right, eventArgs.Bounds.Bottom - 1);
            eventArgs.Graphics.DrawLine(border, eventArgs.Bounds.Right - 1, eventArgs.Bounds.Top, eventArgs.Bounds.Right - 1, eventArgs.Bounds.Bottom);
        }
        Rectangle textBounds = new(eventArgs.Bounds.Left + 8, eventArgs.Bounds.Top, Math.Max(0, eventArgs.Bounds.Width - 12), eventArgs.Bounds.Height);
        using Font headerFont = new(Font, FontStyle.Bold);
        TextRenderer.DrawText(eventArgs.Graphics, eventArgs.Header?.Text ?? string.Empty, headerFont, textBounds, Theme.Muted,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }

    protected override void OnDrawItem(DrawListViewItemEventArgs eventArgs)
    {
        if (View != View.Details) eventArgs.DrawDefault = true;
    }

    protected override void OnDrawSubItem(DrawListViewSubItemEventArgs eventArgs)
    {
        if (eventArgs.Item is null || eventArgs.SubItem is null) return;
        bool selected = eventArgs.Item.Selected;
        Color backgroundColor = selected ? Color.FromArgb(21, 83, 137) :
            eventArgs.ItemIndex % 2 == 0 ? Theme.DarkSurface : Theme.Surface;
        using (SolidBrush background = new(backgroundColor)) eventArgs.Graphics.FillRectangle(background, eventArgs.Bounds);
        Rectangle textBounds = new(eventArgs.Bounds.Left + 8, eventArgs.Bounds.Top, Math.Max(0, eventArgs.Bounds.Width - 12), eventArgs.Bounds.Height);
        TextRenderer.DrawText(eventArgs.Graphics, eventArgs.SubItem.Text, Font, textBounds,
            selected || eventArgs.ColumnIndex == 0 ? Theme.Ink : Theme.Muted,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
    }

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    private static extern int SetWindowTheme(IntPtr handle, string subAppName, string? subIdList);
}
