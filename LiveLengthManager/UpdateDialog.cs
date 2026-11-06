using System.Drawing;
using System.Text;
using System.Windows.Forms;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace LiveLengthManager;

// Update popup with the release notes shown in-app. Modeless, one instance, follows Updater state.
internal sealed class UpdateDialog : Form
{
    private static UpdateDialog? _open;
    private readonly Label _title = new();
    private readonly Label _subtitle = new();
    private readonly TextBox _notes = new();
    private readonly Label _status = new();
    private readonly Button _primary = new();
    private readonly Button _later = new();
    private readonly Button _skip = new();
    private readonly LinkLabel _web = new();
    private readonly Action _changed;

    private sealed class AcadOwner : IWin32Window
    {
        public IntPtr Handle { get; init; }
    }

    internal static void Open()
    {
        if ((Updater.Latest ?? Updater.Published) == null) return;
        if (_open is { IsDisposed: false })
        {
            _open.Refresh(true);
            _open.Activate();
            return;
        }
        _open = new UpdateDialog();
        try { _open.Show(new AcadOwner { Handle = AcadApp.MainWindow.Handle }); }
        catch (System.Exception) { _open.Show(); }
    }

    private UpdateDialog()
    {
        var dark = ThemeIsDark();
        var back = dark ? Color.FromArgb(39, 45, 52) : Color.FromArgb(245, 248, 251);
        var surface = dark ? Color.FromArgb(49, 56, 64) : Color.White;
        var fore = dark ? Color.FromArgb(232, 238, 243) : Color.FromArgb(31, 48, 65);
        var muted = dark ? Color.FromArgb(165, 181, 193) : Color.FromArgb(91, 108, 125);
        var accent = dark ? Color.FromArgb(66, 116, 150) : Color.FromArgb(35, 99, 150);

        Text = "LiveLength Manager — Cập nhật";
        Font = new Font("Segoe UI", 9f);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(460, 400);
        BackColor = back;
        ForeColor = fore;

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6, Padding = new Padding(16, 14, 16, 12) };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        Controls.Add(layout);

        _title.Dock = DockStyle.Fill;
        _title.Font = new Font("Segoe UI Semibold", 13f, FontStyle.Bold);
        layout.Controls.Add(_title, 0, 0);
        _subtitle.Dock = DockStyle.Fill;
        _subtitle.ForeColor = muted;
        layout.Controls.Add(_subtitle, 0, 1);
        layout.Controls.Add(new Label { Text = "Có gì mới", Dock = DockStyle.Fill, TextAlign = ContentAlignment.BottomLeft,
            Font = new Font("Segoe UI Semibold", 9.5f, FontStyle.Bold) }, 0, 2);
        _notes.Dock = DockStyle.Fill;
        _notes.Multiline = true;
        _notes.ReadOnly = true;
        _notes.ScrollBars = ScrollBars.Vertical;
        _notes.BorderStyle = BorderStyle.FixedSingle;
        _notes.BackColor = surface;
        _notes.ForeColor = fore;
        _notes.Font = new Font("Segoe UI", 9.5f);
        layout.Controls.Add(_notes, 0, 3);

        var statusRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
        statusRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        statusRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _status.Dock = DockStyle.Fill;
        _status.TextAlign = ContentAlignment.MiddleLeft;
        _status.AutoEllipsis = true;
        statusRow.Controls.Add(_status, 0, 0);
        _web.Text = "Xem trên GitHub";
        _web.AutoSize = true;
        _web.Anchor = AnchorStyles.Right;
        _web.LinkColor = accent;
        _web.ActiveLinkColor = accent;
        _web.LinkClicked += (_, _) => OpenUrl((Updater.Latest ?? Updater.Published)?.PageUrl);
        statusRow.Controls.Add(_web, 1, 0);
        layout.Controls.Add(statusRow, 0, 4);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft,
            Margin = Padding.Empty, Padding = new Padding(0, 6, 0, 0) };
        foreach (var button in new[] { _primary, _later, _skip })
        {
            button.FlatStyle = FlatStyle.Flat;
            button.AutoSize = true;
            button.MinimumSize = new Size(96, 30);
            button.Margin = new Padding(6, 0, 0, 0);
            button.BackColor = surface;
            button.ForeColor = fore;
            button.FlatAppearance.BorderColor = dark ? Color.FromArgb(86, 98, 109) : Color.FromArgb(198, 211, 224);
            buttons.Controls.Add(button);
        }
        _primary.BackColor = accent;
        _primary.ForeColor = Color.White;
        _primary.FlatAppearance.BorderSize = 0;
        _later.Text = "Để sau";
        _skip.Text = "Bỏ qua bản này";
        _primary.Click += (_, _) => PrimaryClicked();
        _later.Click += (_, _) => Close();
        _skip.Click += (_, _) =>
        {
            if (Updater.Latest is { } release) Updater.Skip(release.Version);
            Close();
        };
        layout.Controls.Add(buttons, 0, 5);
        AcceptButton = _primary;
        CancelButton = _later;

        _changed = () =>
        {
            if (IsDisposed || !IsHandleCreated) return;
            try { BeginInvoke(new Action(() => Refresh(false))); } catch (InvalidOperationException) { }
        };
        Updater.Changed += _changed;
        FormClosed += (_, _) =>
        {
            Updater.Changed -= _changed;
            if (ReferenceEquals(_open, this)) _open = null;
        };
        Refresh(true);
    }

    private void PrimaryClicked()
    {
        switch (Updater.State)
        {
            case UpdateState.Available when Updater.LoaderPresent: Updater.DownloadInBackground(); break;
            case UpdateState.Available: OpenUrl(Updater.Latest?.PageUrl); break;
            case UpdateState.Failed: Updater.DownloadInBackground(); break;
            default: Close(); break;
        }
    }

    private void Refresh(bool notes)
    {
        var current = Updater.State == UpdateState.None || Updater.Latest == null;
        var release = current ? Updater.Published ?? Updater.Latest : Updater.Latest;
        if (release == null) return;
        var latest = $"v{release.Version.Major}.{release.Version.Minor}.{release.Version.Build}";
        _title.Text = current ? "Đang dùng bản mới nhất" : Updater.State == UpdateState.Ready ? $"Đã tải {latest}" : $"Có bản mới {latest}";
        _subtitle.Text = $"Đang dùng v{Updater.CurrentText}";
        if (notes)
        {
            var text = PlainNotes(release.Notes);
            _notes.Text = text.Length > 0 ? text : "Chưa có ghi chú cho bản này. Bấm \"Xem trên GitHub\" để xem chi tiết.";
            _notes.SelectionStart = 0;
        }
        var loader = Updater.LoaderPresent;
        (_status.Text, _primary.Text, _primary.Enabled, _skip.Visible, _later.Text) = Updater.State switch
        {
            UpdateState.Available when loader => ("Tải về chạy ngầm; bản mới dùng từ lần mở AutoCAD sau.", "Cập nhật", true, true, "Để sau"),
            UpdateState.Available => ("Máy này cần cài lại bằng bộ cài mới (một lần).", "Tải bộ cài", true, true, "Để sau"),
            UpdateState.Downloading => ("Đang tải…", "Đang tải…", false, false, "Ẩn"),
            UpdateState.Ready => ("Đã tải xong. Đóng và mở lại AutoCAD để dùng bản mới.", "Đóng", true, false, "Đóng"),
            UpdateState.Failed => ("Không tải được: " + Updater.Error, "Thử lại", true, false, "Đóng"),
            _ => ($"Thay đổi của {latest}.", "Đóng", true, false, "Đóng")
        };
        _later.Visible = Updater.State is not (UpdateState.Ready or UpdateState.None);
        _status.ForeColor = Updater.State == UpdateState.Failed ? Color.FromArgb(203, 64, 52) : _subtitle.ForeColor;
    }

    // Release notes are Markdown from CHANGELOG.md; show them as readable plain text.
    internal static string PlainNotes(string markdown)
    {
        var sb = new StringBuilder();
        foreach (var raw in markdown.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.TrimEnd().Replace("**", "").Replace("`", "");
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith('#'))
            {
                if (sb.Length > 0) sb.AppendLine();
                sb.AppendLine(trimmed.TrimStart('#').Trim().ToUpperInvariant());
            }
            else if (trimmed.StartsWith("- ") || trimmed.StartsWith("* "))
                sb.AppendLine(new string(' ', line.Length - trimmed.Length) + "•  " + trimmed[2..]);
            else if (trimmed.Length > 0 || (sb.Length > 0 && !sb.ToString().EndsWith(Environment.NewLine + Environment.NewLine)))
                sb.AppendLine(line);
        }
        // Collapse runs of blank lines left between sections.
        return System.Text.RegularExpressions.Regex.Replace(sb.ToString(), @"(\r?\n){3,}", Environment.NewLine + Environment.NewLine).Trim();
    }

    private static bool ThemeIsDark()
    {
        try
        {
            return System.IO.File.ReadAllText(System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "LiveLengthManager2027", "theme.txt")).Trim() == "dark";
        }
        catch { return false; }
    }

    internal static void OpenUrl(string? url)
    {
        if (string.IsNullOrEmpty(url)) return;
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (System.Exception ex) { MessageBox.Show(ex.Message, "LiveLength"); }
    }
}
