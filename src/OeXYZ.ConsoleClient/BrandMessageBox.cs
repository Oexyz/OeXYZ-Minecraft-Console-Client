namespace OeXYZ.ConsoleClient;

internal static class BrandMessageBox
{
    public static DialogResult Show(
        string text,
        string caption,
        MessageBoxButtons buttons,
        MessageBoxIcon icon) =>
        ShowCore(null, text, caption, buttons, icon, MessageBoxDefaultButton.Button1);

    public static DialogResult Show(
        IWin32Window owner,
        string text,
        string caption,
        MessageBoxButtons buttons,
        MessageBoxIcon icon) =>
        ShowCore(owner, text, caption, buttons, icon, MessageBoxDefaultButton.Button1);

    public static DialogResult Show(
        IWin32Window owner,
        string text,
        string caption,
        MessageBoxButtons buttons,
        MessageBoxIcon icon,
        MessageBoxDefaultButton defaultButton) =>
        ShowCore(owner, text, caption, buttons, icon, defaultButton);

    private static DialogResult ShowCore(
        IWin32Window? owner,
        string text,
        string caption,
        MessageBoxButtons buttons,
        MessageBoxIcon icon,
        MessageBoxDefaultButton defaultButton)
    {
        using Form dialog = new()
        {
            Text = caption,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = owner is null ? FormStartPosition.CenterScreen : FormStartPosition.CenterParent,
            MaximizeBox = false,
            MinimizeBox = false,
            ShowInTaskbar = false,
            BackColor = Theme.Background,
            ForeColor = Theme.Ink,
            Font = Theme.Body,
            AutoScaleMode = AutoScaleMode.Dpi
        };

        const int messageWidth = 500;
        Size measured = TextRenderer.MeasureText(
            text,
            Theme.Body,
            new Size(messageWidth, int.MaxValue),
            TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
        int contentHeight = Math.Clamp(measured.Height + 18, 92, 420);
        dialog.ClientSize = new Size(640, contentHeight + 92);
        dialog.Shown += (_, _) => Theme.ApplyDarkTitleBar(dialog);

        TableLayoutPanel content = new()
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Padding = new Padding(24, 22, 24, 8),
            BackColor = Theme.Background
        };
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 54));
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        Label symbol = new()
        {
            Text = Symbol(icon),
            Dock = DockStyle.Top,
            Height = 48,
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = SymbolColor(icon),
            Font = AppFonts.Create(21F, FontStyle.Bold),
            UseMnemonic = false
        };
        RichTextBox message = new()
        {
            Text = text,
            Dock = DockStyle.Fill,
            ReadOnly = true,
            DetectUrls = false,
            WordWrap = true,
            ScrollBars = RichTextBoxScrollBars.Vertical,
            BorderStyle = BorderStyle.None,
            BackColor = Theme.Background,
            ForeColor = Theme.Ink,
            Font = Theme.Body,
            TabStop = false
        };
        content.Controls.Add(symbol, 0, 0);
        content.Controls.Add(message, 1, 0);

        FlowLayoutPanel actions = new()
        {
            Dock = DockStyle.Bottom,
            Height = 62,
            Padding = new Padding(18, 10, 18, 12),
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            BackColor = Theme.Surface
        };
        List<Button> choices = CreateButtons(buttons);
        foreach (Button button in choices.AsEnumerable().Reverse()) actions.Controls.Add(button);
        int defaultIndex = defaultButton switch
        {
            MessageBoxDefaultButton.Button2 => 1,
            MessageBoxDefaultButton.Button3 => 2,
            _ => 0
        };
        Button defaultChoice = choices[Math.Clamp(defaultIndex, 0, choices.Count - 1)];
        Theme.Primary(defaultChoice);
        dialog.AcceptButton = defaultChoice;
        Button? cancelChoice = choices.FirstOrDefault(button =>
            button.DialogResult is DialogResult.Cancel or DialogResult.No);
        if (cancelChoice is not null) dialog.CancelButton = cancelChoice;

        dialog.Controls.Add(content);
        dialog.Controls.Add(actions);
        return owner is null ? dialog.ShowDialog() : dialog.ShowDialog(owner);
    }

    private static List<Button> CreateButtons(MessageBoxButtons buttons)
    {
        (string Text, DialogResult Result)[] definitions = buttons switch
        {
            MessageBoxButtons.YesNo => [("Yes", DialogResult.Yes), ("No", DialogResult.No)],
            MessageBoxButtons.OKCancel => [("OK", DialogResult.OK), ("Cancel", DialogResult.Cancel)],
            _ => [("OK", DialogResult.OK)]
        };
        return definitions.Select(definition =>
        {
            Button button = Theme.Button(definition.Text, 94);
            button.DialogResult = definition.Result;
            return button;
        }).ToList();
    }

    private static string Symbol(MessageBoxIcon icon) => icon switch
    {
        MessageBoxIcon.Error => "×",
        MessageBoxIcon.Warning => "!",
        MessageBoxIcon.Question => "?",
        _ => "i"
    };

    private static Color SymbolColor(MessageBoxIcon icon) => icon switch
    {
        MessageBoxIcon.Error => Theme.Danger,
        MessageBoxIcon.Warning => Theme.Amber,
        MessageBoxIcon.Question => Theme.BlueBright,
        _ => Theme.Green
    };
}
