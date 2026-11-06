using System.Drawing;
using System.Drawing.Drawing2D;
using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Windows.Forms;
using Autodesk.AutoCAD.ApplicationServices;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace LiveLengthManager;

internal sealed class ManagerPanel : UserControl
{
    private sealed class PaletteSelect : Control
    {
        private int _selectedIndex = -1;
        private readonly ContextMenuStrip _menu = new();
        internal List<object> Items { get; } = new();
        internal event EventHandler? SelectedIndexChanged;
        [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        internal int SelectedIndex
        {
            get => _selectedIndex;
            set
            {
                if (value < -1 || value >= Items.Count || value == _selectedIndex) return;
                _selectedIndex = value;
                Invalidate();
                SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
            }
        }
        [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        internal object? SelectedItem
        {
            get => _selectedIndex >= 0 && _selectedIndex < Items.Count ? Items[_selectedIndex] : null;
            set
            {
                var index = value == null ? -1 : Items.FindIndex(item => Equals(item, value));
                if (index == _selectedIndex) Invalidate();
                else SelectedIndex = index;
            }
        }

        internal PaletteSelect()
        {
            Height = 30;
            MinimumSize = new Size(50, 28);
            TabStop = true;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer | ControlStyles.Selectable, true);
            _menu.ShowImageMargin = false;
            Disposed += (_, _) => _menu.Dispose();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var border = Focused ? Color.FromArgb(103, 161, 198) :
                (BackColor.GetBrightness() < .5f ? Color.FromArgb(104, 115, 126) : Color.FromArgb(185, 198, 210));
            using (var brush = new SolidBrush(BackColor)) e.Graphics.FillRectangle(brush, ClientRectangle);
            using (var pen = new Pen(border)) e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
            var text = SelectedItem?.ToString() ?? "Chọn";
            TextRenderer.DrawText(e.Graphics, text, Font, new Rectangle(9, 0, Math.Max(0, Width - 33), Height),
                ForeColor, TextFormatFlags.VerticalCenter | TextFormatFlags.Left |
                TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var arrow = new Pen(ForeColor, 1.5f);
            var x = Width - 17;
            var y = Height / 2 - 1;
            e.Graphics.DrawLines(arrow, new[] { new Point(x - 4, y - 2), new Point(x, y + 2), new Point(x + 4, y - 2) });
        }

        protected override void OnClick(EventArgs e)
        {
            base.OnClick(e);
            Focus();
            if (_menu.Visible) return;
            _menu.Items.Clear();
            _menu.BackColor = BackColor;
            _menu.ForeColor = ForeColor;
            _menu.Font = Font;
            _menu.MinimumSize = new Size(Width, 0);
            for (int i = 0; i < Items.Count; i++)
            {
                var index = i;
                var item = new ToolStripMenuItem(Items[i]?.ToString() ?? "") { BackColor = BackColor,
                    ForeColor = ForeColor, Padding = new Padding(8, 4, 8, 4) };
                item.Click += (_, _) => SelectedIndex = index;
                _menu.Items.Add(item);
            }
            _menu.Show(this, new Point(0, Height));
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Down || e.KeyCode == Keys.Up)
            {
                if (Items.Count > 0)
                    SelectedIndex = Math.Clamp(_selectedIndex + (e.KeyCode == Keys.Down ? 1 : -1), 0, Items.Count - 1);
                e.Handled = true;
            }
            else if (e.KeyCode is Keys.Enter or Keys.Space) { OnClick(EventArgs.Empty); e.Handled = true; }
            base.OnKeyDown(e);
        }
        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
    }

    private sealed class MultilineNoteGrid : DataGridView
    {
        private bool InsertLineBreak(Keys keys)
        {
            if ((keys & Keys.KeyCode) != Keys.Enter || (keys & Keys.Shift) == 0 ||
                EditingControl is not TextBox editor ||
                CurrentCell?.OwningColumn.Name is not ("content" or "noteContent" or "gcDescription")) return false;
            editor.SelectedText = Environment.NewLine;
            return true;
        }

        protected override bool ProcessDialogKey(Keys keyData) =>
            InsertLineBreak(keyData) || base.ProcessDialogKey(keyData);

        protected override bool ProcessDataGridViewKey(KeyEventArgs e) =>
            InsertLineBreak(e.KeyData) || base.ProcessDataGridViewKey(e);

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData) =>
            InsertLineBreak(keyData) || base.ProcessCmdKey(ref msg, keyData);
    }

    private readonly Label _headline = new();
    private readonly Label _drawingName = new();
    private readonly Label _totals = new();
    private readonly Label _details = new();
    private readonly Label _gcDetails = new();
    private readonly Label _gcSourceCount = new();
    private readonly Label _gcNoteCount = new();
    private readonly Label _gcEmpty = new();
    private readonly Label _gcNotesEmpty = new();
    private readonly Label _lengthEmpty = new();
    private readonly Button _gcCreateButton = new();
    private readonly Button _gcLocateButton = new();
    private readonly ToolTip _tips = new();
    private readonly TextBox _search = new();
    private readonly PaletteSelect _status = new();
    private readonly PaletteSelect _layer = new();
    private readonly PaletteSelect _themeSelector = new();
    private readonly MultilineNoteGrid _gcGrid = new();
    private readonly MultilineNoteGrid _gcNotesGrid = new();
    private readonly MultilineNoteGrid _grid = new();
    private readonly List<Button> _navButtons = new();
    private Panel? _selectedPage;
    private List<NoteRow> _all = new();
    private string _activeDoc = "";
    private bool _updatingGcGrid;
    private bool _updatingNoteGrid;
    private bool _darkTheme;

    internal ManagerPanel()
    {
        Font = new Font("Segoe UI", 9f);
        BackColor = Color.FromArgb(246, 248, 251);
        var host = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2,
            Margin = Padding.Empty, Padding = Padding.Empty };
        host.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        host.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var navigation = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false,
            Margin = Padding.Empty, Padding = Padding.Empty };
        var contentHost = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty };
        var lengthTab = new Panel { Dock = DockStyle.Fill, BackColor = BackColor };
        var gcTab = new Panel { Dock = DockStyle.Fill, BackColor = BackColor, Visible = false };
        var settingsTab = new Panel { Dock = DockStyle.Fill, BackColor = BackColor, Visible = false };
        contentHost.Controls.Add(lengthTab);
        contentHost.Controls.Add(gcTab);
        contentHost.Controls.Add(settingsTab);
        lengthTab.BringToFront();
        _selectedPage = lengthTab;
        void AddNavigation(string label, Panel page, int width)
        {
            var button = new Button { Text = label, Width = width, Height = 30,
                Margin = new Padding(0, 0, 2, 0), FlatStyle = FlatStyle.Flat,
                Tag = page,
                Font = new Font("Segoe UI Semibold", 9f, FontStyle.Bold) };
            button.FlatAppearance.BorderSize = 0;
            button.Click += (_, _) =>
            {
                FinishGridEditing();
                foreach (var item in new[] { lengthTab, gcTab, settingsTab }) item.Visible = item == page;
                _selectedPage = page;
                page.BringToFront();
                ApplyTheme(host);
            };
            _navButtons.Add(button);
            navigation.Controls.Add(button);
        }
        AddNavigation("Chiều dài", lengthTab, 88);
        AddNavigation("Giật cấp GC", gcTab, 106);
        AddNavigation("Cài đặt", settingsTab, 82);
        host.Controls.Add(navigation, 0, 0);
        host.Controls.Add(contentHost, 0, 1);
        BuildSettings(settingsTab, host);
        Controls.Add(host);
        var main = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 7,
            Padding = new Padding(10, 10, 10, 10), BackColor = BackColor };
        main.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
        main.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        main.RowStyles.Add(new RowStyle(SizeType.Absolute, 116));
        main.RowStyles.Add(new RowStyle(SizeType.Absolute, 67));
        main.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        main.RowStyles.Add(new RowStyle(SizeType.Absolute, 62));
        main.RowStyles.Add(new RowStyle(SizeType.Absolute, 118));
        lengthTab.Controls.Add(main);

        _headline.Text = "Ghi chú chiều dài";
        _headline.Font = new Font("Segoe UI Semibold", 13f, FontStyle.Bold);
        _headline.Dock = DockStyle.Fill;
        _headline.AutoEllipsis = true;
        _headline.ForeColor = Color.FromArgb(27, 49, 77);
        var titleGroup = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        titleGroup.RowStyles.Add(new RowStyle(SizeType.Absolute, 27));
        titleGroup.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
        _drawingName.Text = "Chưa mở bản vẽ";
        _drawingName.Dock = DockStyle.Fill;
        _drawingName.AutoEllipsis = true;
        _drawingName.Font = new Font("Segoe UI", 8.5f);
        titleGroup.Controls.Add(_headline, 0, 0);
        titleGroup.Controls.Add(_drawingName, 0, 1);
        main.Controls.Add(titleGroup, 0, 0);

        _totals.Dock = DockStyle.Fill;
        _totals.ForeColor = Color.FromArgb(70, 81, 95);
        _totals.Text = "Đang quét bản vẽ...";
        main.Controls.Add(_totals, 0, 1);

        var createActions = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
        createActions.RowStyles.Add(new RowStyle(SizeType.Percent, 33));
        createActions.RowStyles.Add(new RowStyle(SizeType.Percent, 33));
        createActions.RowStyles.Add(new RowStyle(SizeType.Percent, 34));
        var createNew = new Button
        {
            Text = "＋  Tạo MLeader mới",
            Dock = DockStyle.Fill,
            Tag = "primary",
            BackColor = Color.FromArgb(31, 93, 154),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI Semibold", 9f, FontStyle.Bold)
        };
        createNew.FlatAppearance.BorderSize = 0;
        _tips.SetToolTip(createNew, "Có L= trong nội dung: tự liên kết chiều dài tại mũi tên. Không có L=: ghi chú thường, không thêm chiều dài.");
        createNew.Click += (_, _) => AcadApp.DocumentManager.MdiActiveDocument?.SendStringToExecute("LLMCREATE\n", true, false, false);
        createActions.Controls.Add(createNew, 0, 0);
        var create = new Button
        {
            Text = "Gắn ghi chú đã có",
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 2, 0, 6),
            BackColor = Color.White,
            ForeColor = Color.FromArgb(31, 67, 108),
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI Semibold", 9f, FontStyle.Bold)
        };
        create.FlatAppearance.BorderColor = Color.FromArgb(202, 213, 226);
        _tips.SetToolTip(create, "Chọn MLeader để tự nhận đường tại mũi tên; MText cần chọn nguồn.");
        create.Click += (_, _) => RunNewLink();
        createActions.Controls.Add(create, 0, 1);
        var copy = new Button
        {
            Text = "Sao chép ghi chú từ mẫu",
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 2, 0, 6),
            BackColor = Color.White,
            ForeColor = Color.FromArgb(31, 67, 108),
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI Semibold", 9f, FontStyle.Bold)
        };
        copy.FlatAppearance.BorderColor = Color.FromArgb(202, 213, 226);
        _tips.SetToolTip(copy, "Chọn MLeader LiveLength mẫu, rồi đặt mũi tên và vị trí ghi chú mới.");
        copy.Click += (_, _) => AcadApp.DocumentManager.MdiActiveDocument?.SendStringToExecute("LLMCOPY\n", true, false, false);
        createActions.Controls.Add(copy, 0, 2);
        main.Controls.Add(createActions, 0, 2);

        var filters = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 2 };
        filters.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45));
        filters.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 27));
        filters.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 28));
        filters.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
        filters.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        filters.Controls.Add(NewFieldLabel("Tìm nội dung"), 0, 0);
        filters.Controls.Add(NewFieldLabel("Trạng thái"), 1, 0);
        filters.Controls.Add(NewFieldLabel("Layer"), 2, 0);
        _search.Dock = DockStyle.Fill;
        _search.PlaceholderText = "Ví dụ: CV 3x2.5, CAT6...";
        _search.Margin = new Padding(0, 0, 6, 4);
        _search.TextChanged += (_, _) => FilterRows();
        filters.Controls.Add(_search, 0, 1);
        _status.Dock = DockStyle.Fill;
        _status.Margin = new Padding(0, 0, 6, 4);
        _status.Items.AddRange(new object[] { "Tất cả", "Đã liên kết", "Mất liên kết", "Ghi chú thường" });
        _status.SelectedIndex = 0;
        _status.SelectedIndexChanged += (_, _) => FilterRows();
        filters.Controls.Add(_status, 1, 1);
        _layer.Dock = DockStyle.Fill;
        _layer.SelectedIndexChanged += LayerChanged;
        _layer.Items.Add("Tất cả");
        _layer.SelectedIndex = 0;
        filters.Controls.Add(_layer, 2, 1);
        main.Controls.Add(filters, 0, 3);

        _grid.Dock = DockStyle.Fill;
        _grid.AllowUserToAddRows = false;
        _grid.AllowUserToDeleteRows = false;
        _grid.AllowUserToResizeRows = false;
        _grid.ReadOnly = false;
        _grid.MultiSelect = false;
        _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _grid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None;
        _grid.RowHeadersVisible = false;
        _grid.BackgroundColor = Color.White;
        _grid.ShowCellToolTips = false;
        _grid.BorderStyle = BorderStyle.FixedSingle;
        _grid.EnableHeadersVisualStyles = false;
        _grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(226, 232, 241);
        _grid.ColumnHeadersHeight = 32;
        _grid.RowTemplate.Height = 29;
        _grid.Columns.Add(NewCol("seq", "STT", 38));
        _grid.Columns.Add(NewCol("type", "Loại", 75));
        _grid.Columns.Add(NewCol("content", "Nội dung", 260));
        _grid.Columns.Add(NewCol("len", "Dài", 85));
        _grid.Columns.Add(NewCol("state", "Trạng thái", 105));
        _grid.Columns.Add(new DataGridViewComboBoxColumn { Name = "unit", HeaderText = "Đơn vị",
            Width = 58, DataSource = new[] { "", "mm", "m" }, FlatStyle = FlatStyle.Flat,
            DisplayStyleForCurrentCellOnly = true });
        _grid.Columns.Add(NewCol("layer", "Layer", 105));
        _grid.Columns.Add(NewCol("layout", "Layout", 78));
        _grid.Columns.Add(NewCol("xy", "Tọa độ X, Y", 155));
        _grid.Columns.Add(NewCol("handle", "Handle", 90));
        _grid.Columns.Add(NewCol("source", "Nguồn", 90));
        _grid.Columns[2].AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
        _grid.Columns[2].MinimumWidth = 100;
        _grid.Columns[1].Visible = false;
        _grid.Columns[3].Width = 100;
        _grid.Columns[4].Width = 84;
        foreach (DataGridViewColumn column in _grid.Columns)
            column.ReadOnly = column.Name is not ("content" or "unit");
        for (int i = 6; i < _grid.Columns.Count; i++) _grid.Columns[i].Visible = false;
        _grid.CellEndEdit += NoteCellEdited;
        _grid.CellBeginEdit += NoteCellBeginEdit;
        _grid.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (_grid.IsCurrentCellDirty && _grid.CurrentCell?.OwningColumn.Name == "unit")
                _grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
        };
        _grid.EditingControlShowing += ConfigureNoteEditor;
        _grid.CellValueChanged += NoteCellValueChanged;
        _grid.SelectionChanged += (_, _) => UpdateDetails();
        _grid.CellDoubleClick += (_, e) => { if (e.ColumnIndex == 0) RunSelectionCommand("LLMZOOMNOTE"); };
        var lengthArea = new Panel { Dock = DockStyle.Fill };
        lengthArea.Controls.Add(_grid);
        _lengthEmpty.Dock = DockStyle.Fill;
        _lengthEmpty.TextAlign = ContentAlignment.MiddleCenter;
        _lengthEmpty.Font = new Font("Segoe UI Semibold", 10f, FontStyle.Bold);
        _lengthEmpty.ForeColor = Color.FromArgb(92, 101, 113);
        _lengthEmpty.BackColor = Color.White;
        _lengthEmpty.Text = "Chưa có ghi chú chiều dài.\nBấm Tạo MLeader mới để bắt đầu.";
        lengthArea.Controls.Add(_lengthEmpty);
        main.Controls.Add(lengthArea, 0, 4);

        _details.Dock = DockStyle.Fill;
        _details.Padding = new Padding(5);
        _details.Text = "Sửa Nội dung: Enter để lưu, Shift+Enter để xuống dòng.";
        _details.BackColor = Color.White;
        _details.BorderStyle = BorderStyle.FixedSingle;
        main.Controls.Add(_details, 0, 5);

        var buttons = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 6, RowCount = 3,
            Padding = new Padding(0, 3, 0, 0) };
        for (int i = 0; i < 6; i++) buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / 6));
        for (int i = 0; i < 3; i++) buttons.RowStyles.Add(new RowStyle(SizeType.Percent, 100f / 3));
        void Action(string label, EventHandler handler, int column, int row, int span)
        {
            var button = new Button { Text = label, Dock = DockStyle.Fill, Margin = new Padding(2, 2, 2, 3),
                FlatStyle = FlatStyle.Flat, Font = new Font("Segoe UI", 8.5f) };
            button.Click += handler;
            buttons.Controls.Add(button, column, row);
            buttons.SetColumnSpan(button, span);
        }
        Action("Quét lại", (_, _) => ForceRefresh(), 0, 0, 2);
        Action("Zoom ghi chú", (_, _) => RunSelectionCommand("LLMZOOMNOTE"), 2, 0, 2);
        Action("Zoom nguồn", (_, _) => RunSelectionCommand("LLMZOOMSOURCE"), 4, 0, 2);
        Action("Dò nguồn theo MLeader", (_, _) =>
            AcadApp.DocumentManager.MdiActiveDocument?.SendStringToExecute("LLMFIND\n", true, false, false), 0, 1, 3);
        Action("MText → MLeader", (_, _) =>
            AcadApp.DocumentManager.MdiActiveDocument?.SendStringToExecute("LLMMTEXT2LEADER\n", true, false, false), 3, 1, 3);
        Action("Gán chiều dài thứ 2", (_, _) => RunSelectionCommand("LLMSECOND"), 0, 2, 3);
        Action("Xuất CSV", (_, _) => ExportCsv(), 3, 2, 3);
        main.Controls.Add(buttons, 0, 6);

        var gcMain = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 7,
            Padding = new Padding(10), BackColor = BackColor };
        gcMain.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        gcMain.RowStyles.Add(new RowStyle(SizeType.Absolute, 82));
        gcMain.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        gcMain.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        gcMain.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        gcMain.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        gcMain.RowStyles.Add(new RowStyle(SizeType.Absolute, 78));
        gcTab.Controls.Add(gcMain);
        var gcHeading = new Panel { Dock = DockStyle.Fill };
        gcHeading.Controls.Add(new Label { Text = "Giật cấp theo nguồn đã chọn",
            Dock = DockStyle.Fill, ForeColor = Color.FromArgb(27, 49, 77),
            Font = new Font("Segoe UI Semibold", 12f, FontStyle.Bold) });
        gcMain.Controls.Add(gcHeading, 0, 0);
        var gcActions = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2 };
        gcActions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        gcActions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        gcActions.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        gcActions.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        _gcCreateButton.Text = "＋ Tạo MLeader từ GC đã chọn";
        _gcCreateButton.Tag = "primary";
        _gcCreateButton.Dock = DockStyle.Fill;
        _gcCreateButton.BackColor = Color.FromArgb(31, 93, 154);
        _gcCreateButton.ForeColor = Color.White;
        _gcCreateButton.FlatStyle = FlatStyle.Flat;
        _gcCreateButton.FlatAppearance.BorderSize = 0;
        _gcCreateButton.Font = new Font("Segoe UI Semibold", 9f, FontStyle.Bold);
        _gcCreateButton.Click += (_, _) => RunGcCreate();
        _gcCreateButton.Enabled = false;
        gcActions.Controls.Add(_gcCreateButton, 0, 0);
        gcActions.SetColumnSpan(_gcCreateButton, 2);
        var registerGc = new Button { Text = "Đăng ký nguồn mới", Dock = DockStyle.Fill,
            Margin = new Padding(0, 3, 4, 0), BackColor = Color.White, ForeColor = Color.FromArgb(31, 67, 108),
            FlatStyle = FlatStyle.Flat };
        registerGc.FlatAppearance.BorderColor = Color.FromArgb(202, 213, 226);
        registerGc.Click += (_, _) => AcadApp.DocumentManager.MdiActiveDocument?.SendStringToExecute("LLMGCADD\n", true, false, false);
        _tips.SetToolTip(registerGc, "Chọn DIM, LINE, PLINE hoặc MLINE; đặt mã và mô tả vị trí.");
        gcActions.Controls.Add(registerGc, 0, 1);
        _gcLocateButton.Text = "Dò vị trí nguồn";
        _gcLocateButton.Dock = DockStyle.Fill;
        _gcLocateButton.Margin = new Padding(4, 3, 0, 0);
        _gcLocateButton.BackColor = Color.White;
        _gcLocateButton.ForeColor = Color.FromArgb(31, 67, 108);
        _gcLocateButton.FlatStyle = FlatStyle.Flat;
        _gcLocateButton.FlatAppearance.BorderColor = Color.FromArgb(202, 213, 226);
        _gcLocateButton.Click += (_, _) => RunGcCommand("LLMGCLOCATE");
        _gcLocateButton.Enabled = false;
        _tips.SetToolTip(_gcLocateButton, "Zoom tới nguồn và khoanh vùng tạm thời trên bản vẽ.");
        gcActions.Controls.Add(_gcLocateButton, 1, 1);
        gcMain.Controls.Add(gcActions, 0, 1);
        _gcSourceCount.Dock = DockStyle.Fill;
        _gcSourceCount.ForeColor = Color.FromArgb(70, 81, 95);
        _gcSourceCount.Text = "Nguồn GC đã đăng ký";
        gcMain.Controls.Add(_gcSourceCount, 0, 2);
        _gcGrid.Dock = DockStyle.Fill;
        _gcGrid.ReadOnly = false;
        _gcGrid.AllowUserToAddRows = false;
        _gcGrid.AllowUserToDeleteRows = false;
        _gcGrid.RowHeadersVisible = false;
        _gcGrid.MultiSelect = false;
        _gcGrid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _gcGrid.BackgroundColor = Color.White;
        _gcGrid.ShowCellToolTips = false;
        _gcGrid.Columns.Add(NewCol("gcCode", "Mã", 68));
        _gcGrid.Columns.Add(NewCol("gcDescription", "Mô tả vị trí", 190));
        _gcGrid.Columns.Add(NewCol("gcLength", "Dài (m)", 84));
        _gcGrid.Columns[1].AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
        for (int i = 2; i < _gcGrid.Columns.Count; i++) _gcGrid.Columns[i].ReadOnly = true;
        _gcGrid.SelectionChanged += (_, _) => UpdateGcDetails();
        _gcGrid.DefaultCellStyle.WrapMode = DataGridViewTriState.True;
        _gcGrid.CellBeginEdit += GcCellBeginEdit;
        _gcGrid.CellEndEdit += GcCellEdited;
        _gcGrid.EditingControlShowing += ConfigureNoteEditor;
        var gcSourceArea = new Panel { Dock = DockStyle.Fill };
        gcSourceArea.Controls.Add(_gcGrid);
        _gcEmpty.Dock = DockStyle.Fill;
        _gcEmpty.TextAlign = ContentAlignment.MiddleCenter;
        _gcEmpty.Font = new Font("Segoe UI Semibold", 9.5f, FontStyle.Bold);
        _gcEmpty.ForeColor = Color.FromArgb(92, 101, 113);
        _gcEmpty.BackColor = Color.White;
        _gcEmpty.Text = "Chưa có nguồn GC.\nBấm Đăng ký nguồn mới để bắt đầu.";
        gcSourceArea.Controls.Add(_gcEmpty);
        gcMain.Controls.Add(gcSourceArea, 0, 3);
        _gcNoteCount.Dock = DockStyle.Fill;
        _gcNoteCount.ForeColor = Color.FromArgb(70, 81, 95);
        _gcNoteCount.Text = "MLeader GC đã tạo · nhấp đúp để zoom";
        gcMain.Controls.Add(_gcNoteCount, 0, 4);
        _gcNotesGrid.Dock = DockStyle.Fill;
        _gcNotesGrid.ReadOnly = true;
        _gcNotesGrid.AllowUserToAddRows = false;
        _gcNotesGrid.AllowUserToDeleteRows = false;
        _gcNotesGrid.RowHeadersVisible = false;
        _gcNotesGrid.MultiSelect = false;
        _gcNotesGrid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _gcNotesGrid.BackgroundColor = Color.White;
        _gcNotesGrid.ShowCellToolTips = true;
        _gcNotesGrid.Columns.Add(NewCol("noteSeq", "STT", 38));
        _gcNotesGrid.Columns.Add(NewCol("noteCode", "Mã GC", 58));
        _gcNotesGrid.Columns.Add(NewCol("noteContent", "Nội dung", 180));
        _gcNotesGrid.Columns.Add(NewCol("noteLength", "Dài", 74));
        _gcNotesGrid.Columns.Add(NewCol("noteState", "Trạng thái", 88));
        _gcNotesGrid.Columns[2].AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
        _gcNotesGrid.Columns[2].MinimumWidth = 90;
        _gcNotesGrid.CellDoubleClick += (_, e) => { if (e.ColumnIndex == 0) RunGcNoteZoom(); };
        var gcNoteArea = new Panel { Dock = DockStyle.Fill };
        gcNoteArea.Controls.Add(_gcNotesGrid);
        _gcNotesEmpty.Dock = DockStyle.Fill;
        _gcNotesEmpty.TextAlign = ContentAlignment.MiddleCenter;
        _gcNotesEmpty.Font = new Font("Segoe UI Semibold", 9.5f, FontStyle.Bold);
        _gcNotesEmpty.ForeColor = Color.FromArgb(92, 101, 113);
        _gcNotesEmpty.BackColor = Color.White;
        _gcNotesEmpty.Text = "Chưa có MLeader GC.\nChọn một nguồn ở bảng trên để tạo ghi chú.";
        gcNoteArea.Controls.Add(_gcNotesEmpty);
        gcMain.Controls.Add(gcNoteArea, 0, 5);
        _gcDetails.Dock = DockStyle.Fill;
        _gcDetails.Padding = new Padding(5);
        _gcDetails.BorderStyle = BorderStyle.FixedSingle;
        _gcDetails.BackColor = Color.White;
        _gcDetails.Text = "Đăng ký nguồn để tạo ghi chú GC ở bất kỳ vị trí nào.";
        gcMain.Controls.Add(_gcDetails, 0, 6);
        StyleGrid(_gcGrid);
        StyleGrid(_gcNotesGrid);
        StyleGrid(_grid);
        ApplyTheme(host);
    }

    private static string ThemeFile => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "LiveLengthManager2027", "theme.txt");

    private void BuildSettings(Panel page, Control host)
    {
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(18),
            ColumnCount = 1, RowCount = 4 };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(new Label { Text = "Giao diện palette", Dock = DockStyle.Fill,
            Font = new Font("Segoe UI Semibold", 12f, FontStyle.Bold) }, 0, 0);
        layout.Controls.Add(NewFieldLabel("Màu sắc"), 0, 1);
        _themeSelector.Dock = DockStyle.Top;
        _themeSelector.Items.AddRange(new object[] { "Sáng", "Tối" });
        try { _darkTheme = System.IO.File.ReadAllText(ThemeFile).Trim() == "dark"; }
        catch { _darkTheme = false; }
        _themeSelector.SelectedIndex = _darkTheme ? 1 : 0;
        _themeSelector.SelectedIndexChanged += (_, _) =>
        {
            _darkTheme = _themeSelector.SelectedIndex == 1;
            ApplyTheme(host);
            try
            {
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(ThemeFile)!);
                System.IO.File.WriteAllText(ThemeFile, _darkTheme ? "dark" : "light");
            }
            catch { }
        };
        layout.Controls.Add(_themeSelector, 0, 2);
        layout.Controls.Add(new Label { Text = "Đơn vị mm/m được chọn riêng ở từng dòng ghi chú. " +
            "mm hiển thị số nguyên; m hiển thị tối đa 2 chữ số thập phân. " +
            "Nhấp vào ô Nội dung để sửa trực tiếp. Enter: lưu; Shift+Enter: xuống dòng.",
            Dock = DockStyle.Fill, AutoSize = false }, 0, 3);
        page.Controls.Add(layout);
    }

    private void ApplyTheme(Control root)
    {
        var background = _darkTheme ? Color.FromArgb(39, 45, 52) : Color.FromArgb(245, 248, 251);
        var surface = _darkTheme ? Color.FromArgb(49, 56, 64) : Color.White;
        var foreground = _darkTheme ? Color.FromArgb(232, 238, 243) : Color.FromArgb(31, 48, 65);
        var accent = _darkTheme ? Color.FromArgb(66, 116, 150) : Color.FromArgb(35, 99, 150);
        BackColor = background;
        ThemeChildren(root, background, surface, foreground);
        _drawingName.ForeColor = _darkTheme ? Color.FromArgb(165, 181, 193) : Color.FromArgb(91, 108, 125);
        _totals.ForeColor = _drawingName.ForeColor;
        foreach (var button in _navButtons)
        {
            var selected = ReferenceEquals(button.Tag, _selectedPage);
            button.BackColor = selected ? accent : background;
            button.ForeColor = selected ? Color.White : _drawingName.ForeColor;
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderSize = 0;
        }
        foreach (DataGridViewRow row in _grid.Rows)
            if (row.Tag is NoteRow note)
                row.DefaultCellStyle.BackColor = note.State == "Mất liên kết"
                    ? (_darkTheme ? Color.FromArgb(74, 55, 57) : Color.FromArgb(255, 240, 236))
                    : surface;
    }

    private void ThemeChildren(Control control, Color background, Color surface, Color foreground)
    {
        if (control is DataGridView grid)
        {
            grid.BorderStyle = _darkTheme ? BorderStyle.None : BorderStyle.FixedSingle;
            grid.ScrollBars = ScrollBars.Vertical;
            grid.BackgroundColor = surface;
            grid.DefaultCellStyle.BackColor = surface;
            grid.DefaultCellStyle.ForeColor = foreground;
            grid.AlternatingRowsDefaultCellStyle.BackColor = _darkTheme ? Color.FromArgb(54, 62, 70) : Color.FromArgb(249, 251, 253);
            grid.ColumnHeadersDefaultCellStyle.BackColor = _darkTheme ? Color.FromArgb(58, 67, 76) : Color.FromArgb(230, 237, 244);
            grid.ColumnHeadersDefaultCellStyle.ForeColor = foreground;
            grid.DefaultCellStyle.SelectionBackColor = _darkTheme ? Color.FromArgb(66, 106, 135) : Color.FromArgb(211, 228, 245);
            grid.DefaultCellStyle.SelectionForeColor = foreground;
            grid.GridColor = _darkTheme ? Color.FromArgb(69, 78, 87) : Color.FromArgb(231, 236, 242);
            return;
        }
        if (control is PaletteSelect select)
        {
            select.BackColor = surface;
            select.ForeColor = foreground;
            select.Invalidate();
            return;
        }
        if (control is Button button)
        {
            var primary = Equals(button.Tag, "primary");
            button.BackColor = primary ? (_darkTheme ? Color.FromArgb(66, 116, 150) : Color.FromArgb(35, 99, 150)) : surface;
            button.ForeColor = primary ? Color.White : foreground;
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderSize = primary ? 0 : 1;
            button.FlatAppearance.BorderColor = _darkTheme ? Color.FromArgb(86, 98, 109) : Color.FromArgb(198, 211, 224);
            button.FlatAppearance.MouseOverBackColor = primary ? Color.FromArgb(81, 137, 174) :
                (_darkTheme ? Color.FromArgb(62, 72, 82) : Color.FromArgb(233, 240, 247));
        }
        else if (control is TextBox box)
        {
            box.BackColor = surface;
            box.ForeColor = foreground;
            box.BorderStyle = BorderStyle.FixedSingle;
        }
        else if (control is ComboBox combo)
        {
            combo.BackColor = surface;
            combo.ForeColor = foreground;
            combo.FlatStyle = FlatStyle.Flat;
            if (combo.DrawMode != DrawMode.OwnerDrawFixed)
            {
                combo.DrawMode = DrawMode.OwnerDrawFixed;
                combo.DrawItem += (_, e) =>
                {
                    if (e.Index < 0) return;
                    var bg = _darkTheme ? Color.FromArgb(58, 63, 69) : Color.White;
                    var fg = _darkTheme ? Color.FromArgb(231, 234, 238) : Color.FromArgb(27, 49, 77);
                    using var brush = new SolidBrush(bg);
                    e.Graphics.FillRectangle(brush, e.Bounds);
                    TextRenderer.DrawText(e.Graphics, combo.Items[e.Index]?.ToString() ?? "", combo.Font,
                        e.Bounds, fg, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
                };
            }
        }
        else
        {
            if (control is Label label && label.BorderStyle == BorderStyle.FixedSingle && _darkTheme)
                label.BorderStyle = BorderStyle.None;
            control.BackColor = control is Label bordered && bordered.BorderStyle == BorderStyle.FixedSingle ? surface : background;
            control.ForeColor = foreground;
        }
        foreach (Control child in control.Controls) ThemeChildren(child, background, surface, foreground);
    }

    private static Label NewFieldLabel(string text) => new()
    {
        Text = text, Dock = DockStyle.Fill, AutoSize = false,
        ForeColor = Color.FromArgb(92, 101, 113), Font = new Font("Segoe UI", 8f)
    };

    private void FinishGridEditing()
    {
        foreach (var grid in new[] { _grid, _gcGrid, _gcNotesGrid })
        {
            if (grid.IsCurrentCellInEditMode)
            {
                try { grid.EndEdit(); }
                catch { grid.CancelEdit(); }
            }
        }
    }

    private static string FormatDisplay(double? meters, bool millimeters) => !meters.HasValue ? "—" :
        millimeters
            ? Math.Round(meters.Value * 1000d, 0, MidpointRounding.AwayFromZero).ToString("F0", CultureInfo.InvariantCulture) + " mm"
            : meters.Value.ToString("0.##", CultureInfo.InvariantCulture) + " m";

    private static string FormatNoteLengths(NoteRow note)
    {
        var unit = note.Link?.Millimeters == true;
        var first = FormatDisplay(note.LengthMeters, unit);
        return note.Link?.SecondarySourceHandle.Length > 0
            ? first + " / " + FormatDisplay(note.SecondaryLengthMeters, unit) : first;
    }

    private static DataGridViewTextBoxColumn NewCol(string name, string header, int width) => new()
    {
        Name = name, HeaderText = header, Width = width, SortMode = DataGridViewColumnSortMode.NotSortable
    };

    private static void StyleGrid(DataGridView grid)
    {
        grid.BorderStyle = BorderStyle.FixedSingle;
        grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
        grid.GridColor = Color.FromArgb(231, 236, 242);
        grid.EnableHeadersVisualStyles = false;
        grid.ColumnHeadersHeight = 34;
        grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(232, 238, 245);
        grid.ColumnHeadersDefaultCellStyle.ForeColor = Color.FromArgb(41, 64, 91);
        grid.ColumnHeadersDefaultCellStyle.Font = new Font("Segoe UI Semibold", 9f, FontStyle.Bold);
        grid.RowTemplate.Height = 31;
        grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(211, 228, 245);
        grid.DefaultCellStyle.SelectionForeColor = Color.FromArgb(24, 52, 82);
        grid.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(249, 251, 253);
        grid.DefaultCellStyle.Padding = new Padding(3, 0, 3, 0);
    }

    internal void SetRows(Document doc, List<NoteRow> rows)
    {
        if (IsDisposed) return;
        bool switched = _activeDoc != doc.Name;
        _activeDoc = doc.Name;
        _all = rows;
        var oldGc = CurrentGc?.Handle;
        var gcRows = GcData.GetRows(doc);
        _updatingGcGrid = true;
        try
        {
            _gcGrid.CancelEdit();
            _gcGrid.Rows.Clear();
            foreach (var item in gcRows)
            {
                var visibleDescription = item.Description.Replace("\\P", Environment.NewLine, StringComparison.OrdinalIgnoreCase);
                var index = _gcGrid.Rows.Add(item.Name, visibleDescription, FormatDisplay(item.LengthMeters, false));
                _gcGrid.Rows[index].Tag = item;
                _gcGrid.Rows[index].Height = Math.Min(84, 31 + 18 * (visibleDescription.Split('\n').Length - 1));
            }
            _gcGrid.ClearSelection();
            var previous = _gcGrid.Rows.Cast<DataGridViewRow>().FirstOrDefault(r =>
                !switched && (r.Tag as GcRow)?.Handle == oldGc);
            if (previous != null) previous.Selected = true;
            else if (_gcGrid.Rows.Count > 0) _gcGrid.Rows[0].Selected = true;
        }
        finally { _updatingGcGrid = false; }
        _gcSourceCount.Text = $"Nguồn GC · {gcRows.Count}   |   Sửa mô tả: Shift+Enter xuống dòng";
        _gcEmpty.Visible = gcRows.Count == 0;
        if (_gcEmpty.Visible) _gcEmpty.BringToFront();
        else _gcGrid.BringToFront();
        _updatingNoteGrid = true;
        _gcNotesGrid.CancelEdit();
        _gcNotesGrid.Rows.Clear();
        var gcNotes = rows.Where(r => r.Link?.IsGc == true).ToList();
        int gcSeq = 0;
        foreach (var note in gcNotes)
        {
            var code = note.Link?.GcLabel;
            if (string.IsNullOrWhiteSpace(code))
                code = gcRows.FirstOrDefault(source => source.Handle.Equals(note.SourceHandle,
                    StringComparison.OrdinalIgnoreCase))?.Name ?? "—";
            var index = _gcNotesGrid.Rows.Add(++gcSeq, code, note.Description,
                FormatDisplay(note.LengthMeters, note.Link?.Millimeters == true),
                note.State);
            _gcNotesGrid.Rows[index].Tag = note;
            _gcNotesGrid.Rows[index].Cells[2].ToolTipText = note.CurrentText.Replace("\\P", Environment.NewLine);
        }
        _gcNotesGrid.ClearSelection();
        _updatingNoteGrid = false;
        _gcNoteCount.Text = $"MLeader GC · {gcNotes.Count}   |   Sửa mã/mô tả ở bảng nguồn phía trên";
        _gcNotesEmpty.Visible = gcNotes.Count == 0;
        if (_gcNotesEmpty.Visible) _gcNotesEmpty.BringToFront();
        else _gcNotesGrid.BringToFront();
        UpdateGcDetails();
        var old = _layer.SelectedItem?.ToString() ?? "Tất cả";
        var layers = rows.Where(r => r.Link?.IsGc != true).Select(r => r.Layer).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();
        _layer.SelectedIndexChanged -= LayerChanged;
        // Programmatically repopulating can fire SelectionChanged; its final filter
        // run below restores the selected entity handle.
        _layer.Items.Clear();
        _layer.Items.Add("Tất cả");
        foreach (var layer in layers) _layer.Items.Add(layer);
        _layer.SelectedItem = !switched && layers.Contains(old) ? old : "Tất cả";
        _layer.SelectedIndexChanged += LayerChanged;
        _headline.Text = "Ghi chú chiều dài";
        _drawingName.Text = System.IO.Path.GetFileName(doc.Name);
        _tips.SetToolTip(_drawingName, doc.Name);
        FilterRows(switched);
    }

    internal void ClearDocument(string name)
    {
        if (IsDisposed || _activeDoc != name) return;
        _activeDoc = "";
        _all.Clear();
        _updatingGcGrid = true;
        try { _gcGrid.CancelEdit(); _gcGrid.Rows.Clear(); }
        finally { _updatingGcGrid = false; }
        _updatingNoteGrid = true;
        _gcNotesGrid.CancelEdit();
        _gcNotesGrid.Rows.Clear();
        _gcEmpty.Visible = true;
        _gcNotesEmpty.Visible = true;
        _gcEmpty.BringToFront();
        _gcNotesEmpty.BringToFront();
        _gcSourceCount.Text = "Nguồn GC đã đăng ký";
        _gcNoteCount.Text = "MLeader GC đã tạo";
        _gcDetails.Text = "Mở bản vẽ để xem danh mục GC.";
        _grid.CancelEdit();
        _grid.CurrentCell = null;
        _grid.Rows.Clear();
        _updatingNoteGrid = false;
        _lengthEmpty.Visible = true;
        _lengthEmpty.BringToFront();
        _grid.ClearSelection();
        _grid.Invalidate();
        _totals.Text = "Không có bản vẽ đang mở.";
        _headline.Text = "Ghi chú chiều dài";
        _drawingName.Text = "Chưa mở bản vẽ";
        _details.Text = "Mở bản vẽ để xem các ghi chú LiveLength.";
    }

    private void LayerChanged(object? sender, EventArgs e) => FilterRows();

    private void FilterRows(bool clearSelection = false)
    {
        if (IsDisposed) return;
        var selected = clearSelection ? null : CurrentRow?.Handle;
        var term = _search.Text.Trim();
        var state = _status.SelectedIndex;
        var layer = _layer.SelectedItem?.ToString() ?? "Tất cả";
        var lengthRows = _all.Where(r => r.Link?.IsGc != true).ToList();
        var filtered = lengthRows.Where(r =>
            (term.Length == 0 || r.Description.Contains(term, StringComparison.OrdinalIgnoreCase) ||
             r.Handle.Contains(term, StringComparison.OrdinalIgnoreCase) ||
             r.SourceHandle.Contains(term, StringComparison.OrdinalIgnoreCase)) &&
            (state == 0 || (state == 1 && r.State == "Đã liên kết") ||
             (state == 2 && r.State == "Mất liên kết") ||
             (state == 3 && r.State == "Ghi chú thường")) &&
            (layer == "Tất cả" || r.Layer.Equals(layer, StringComparison.OrdinalIgnoreCase))).ToList();
        _updatingNoteGrid = true;
        _grid.SuspendLayout();
        try
        {
            _grid.Rows.Clear();
            int seq = 0;
            foreach (var r in filtered)
            {
                int index = _grid.Rows.Add(++seq, r.Type, r.Description,
                    FormatNoteLengths(r),
                    r.State, r.Link == null ? "" : r.Link.Millimeters ? "mm" : "m", r.Layer, r.Layout,
                    r.HasPosition ? $"{r.Position.X:0.##}, {r.Position.Y:0.##}" : "-",
                    r.Handle, r.SourceHandle);
                var gridRow = _grid.Rows[index];
                gridRow.Tag = r;
                gridRow.Cells[5].ReadOnly = r.Link == null;
                if (r.State == "Mất liên kết")
                    gridRow.DefaultCellStyle.BackColor = _darkTheme ? Color.FromArgb(74, 55, 57) : Color.FromArgb(255, 240, 236);
            }
            _grid.ClearSelection();
            if (selected != null)
                foreach (DataGridViewRow r in _grid.Rows)
                    if ((r.Tag as NoteRow)?.Handle == selected)
                    {
                        r.Selected = true;
                        break;
                    }
        }
        finally { _grid.ResumeLayout(); _updatingNoteGrid = false; }
        int linked = lengthRows.Count(r => r.State == "Đã liên kết");
        int missing = lengthRows.Count(r => r.State == "Mất liên kết");
        int plain = lengthRows.Count(r => r.State == "Ghi chú thường");
        _totals.Text = $"{lengthRows.Count} ghi chú  |  {linked} đã liên kết  |  {missing} mất nguồn  |  {plain} thường" +
            (filtered.Count != lengthRows.Count ? $"  |  {filtered.Count} đang hiện" : "");
        _lengthEmpty.Text = lengthRows.Count == 0
            ? "CHƯA CÓ GHI CHÚ\nTạo MLeader mới để bắt đầu."
            : "Không tìm thấy ghi chú phù hợp bộ lọc.";
        _lengthEmpty.Visible = filtered.Count == 0;
        if (_lengthEmpty.Visible) _lengthEmpty.BringToFront();
        else _grid.BringToFront();
        UpdateDetails();
    }

    private NoteRow? CurrentRow => _grid.SelectedRows.Count > 0 ? _grid.SelectedRows[0].Tag as NoteRow : null;
    private GcRow? CurrentGc => _gcGrid.SelectedRows.Count > 0 ? _gcGrid.SelectedRows[0].Tag as GcRow : null;

    private void UpdateGcDetails()
    {
        var row = CurrentGc;
        _gcCreateButton.Enabled = row != null;
        _gcLocateButton.Enabled = row != null;
        _gcDetails.Text = row == null ? "Chọn một GC để tạo ghi chú hoặc sửa mô tả." :
            $"{row.Name}: {row.Description}\nNguồn: {row.SourceType}  |  Dài: {FormatDisplay(row.LengthMeters, false)}";
    }

    private void GcCellEdited(object? sender, DataGridViewCellEventArgs e)
    {
        if (_updatingGcGrid || e.RowIndex < 0 || e.RowIndex >= _gcGrid.Rows.Count ||
            _gcGrid.Rows[e.RowIndex].Tag is not GcRow old) return;
        var code = Convert.ToString(_gcGrid.Rows[e.RowIndex].Cells[0].Value)?.Trim() ?? "";
        var description = (Convert.ToString(_gcGrid.Rows[e.RowIndex].Cells[1].Value)?.Trim() ?? "")
            .Replace("\r\n", "\\P").Replace("\n", "\\P");
        _gcGrid.Rows[e.RowIndex].Height = Math.Min(84, 31 + 18 * (description.Split("\\P").Length - 1));
        if (code.Equals(old.Name, StringComparison.OrdinalIgnoreCase) && description == old.Description) return;
        var doc = AcadApp.DocumentManager.MdiActiveDocument;
        if (doc == null || _activeDoc != doc.Name) return;
        Commands.PendingGcEdits.Enqueue((doc.Name, old.Handle, old.Name, code, description));
        doc.SendStringToExecute("LLMGCEDIT\n", true, false, false);
    }

    private void GcCellBeginEdit(object? sender, DataGridViewCellCancelEventArgs e)
    {
        if (_updatingGcGrid || e.RowIndex < 0 || e.ColumnIndex != 1) return;
        _gcGrid.Rows[e.RowIndex].Height = 84;
    }

    private void NoteCellBeginEdit(object? sender, DataGridViewCellCancelEventArgs e)
    {
        if (_updatingNoteGrid || e.RowIndex < 0 || sender is not DataGridView grid ||
            grid.Rows[e.RowIndex].Tag is not NoteRow note) return;
        if (e.ColumnIndex == (grid == _grid ? 2 : 1))
        {
            grid.Rows[e.RowIndex].Height = 84;
            grid.Rows[e.RowIndex].Cells[e.ColumnIndex].Value =
                note.CurrentText.Replace("\\P", Environment.NewLine, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static void ConfigureNoteEditor(object? sender, DataGridViewEditingControlShowingEventArgs e)
    {
        if (sender is not DataGridView grid ||
            grid.CurrentCell?.OwningColumn.Name is not ("content" or "noteContent" or "gcDescription") ||
            e.Control is not TextBox editor) return;
        editor.Multiline = true;
        editor.AcceptsReturn = false;
        editor.WordWrap = true;
        editor.ScrollBars = ScrollBars.None;
        editor.KeyDown -= NoteEditorKeyDown;
        editor.KeyDown += NoteEditorKeyDown;
    }

    private static void NoteEditorKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not TextBox editor || e.KeyCode != Keys.Enter || !e.Shift) return;
        editor.SelectedText = Environment.NewLine;
        e.Handled = true;
        e.SuppressKeyPress = true;
    }

    private void NoteCellEdited(object? sender, DataGridViewCellEventArgs e)
    {
        if (_updatingNoteGrid || e.RowIndex < 0 || sender is not DataGridView grid ||
            grid.Rows[e.RowIndex].Tag is not NoteRow old) return;
        var contentColumn = grid == _grid ? 2 : 1;
        if (e.ColumnIndex != contentColumn) return;
        grid.Rows[e.RowIndex].Height = 31;
        var text = Convert.ToString(grid.Rows[e.RowIndex].Cells[contentColumn].Value)?.Trim() ?? "";
        var normalized = text.Replace("\r\n", "\\P").Replace("\n", "\\P");
        if (normalized == old.CurrentText)
        {
            grid.Rows[e.RowIndex].Cells[contentColumn].Value = old.Description;
            return;
        }
        if (text.Length == 0) { grid.Rows[e.RowIndex].Cells[contentColumn].Value = old.Description; return; }
        QueueNoteEdit(old, normalized, null);
    }

    private void NoteCellValueChanged(object? sender, DataGridViewCellEventArgs e)
    {
        if (_updatingNoteGrid || e.RowIndex < 0 || sender is not DataGridView grid ||
            grid.Rows[e.RowIndex].Tag is not NoteRow old) return;
        var unitColumn = grid == _grid ? 5 : 4;
        if (e.ColumnIndex != unitColumn) return;
        var mm = Convert.ToString(grid.Rows[e.RowIndex].Cells[unitColumn].Value) == "mm";
        if (mm == (old.Link?.Millimeters == true)) return;
        QueueNoteEdit(old, null, mm);
    }

    private void QueueNoteEdit(NoteRow note, string? text, bool? millimeters)
    {
        var doc = AcadApp.DocumentManager.MdiActiveDocument;
        if (doc == null || _activeDoc != doc.Name) return;
        Commands.PendingNoteEdits.Enqueue((doc.Name, note.Handle, text, millimeters));
        doc.SendStringToExecute("LLMEDITNOTE\n", true, false, false);
    }

    private void UpdateDetails()
    {
        var r = CurrentRow;
        _details.Text = r == null ? "Sửa Nội dung: Enter để lưu, Shift+Enter để xuống dòng." :
            $"{r.Description}\nDài: {FormatNoteLengths(r)}  |  {r.State}\nEnter: lưu  ·  Shift+Enter: xuống dòng";
    }

    private void ForceRefresh()
    {
        var doc = AcadApp.DocumentManager.MdiActiveDocument;
        if (doc == null) return;
        try
        {
            using (doc.LockDocument())
            {
                LiveEngine.Synchronize(doc);
                PaletteHost.Refresh(doc);
            }
        }
        catch (System.Exception ex) { MessageBox.Show(ex.Message, "LiveLength"); }
    }

    private void RunSelectionCommand(string command)
    {
        var r = CurrentRow;
        if (r == null)
        {
            MessageBox.Show("Hãy chọn một dòng trong danh sách trước.", "LiveLength");
            return;
        }
        var doc = AcadApp.DocumentManager.MdiActiveDocument;
        if (doc == null) return;
        if (_activeDoc != doc.Name || !_all.Contains(r))
        {
            MessageBox.Show("Danh sách thuộc bản vẽ khác. Hãy quét lại bản vẽ hiện tại.", "LiveLength");
            return;
        }
        Commands.PendingHandle = r.Handle;
        Commands.PendingDocName = doc.Name;
        doc.SendStringToExecute(command + "\n", true, false, false);
    }

    private static void RunNewLink()
    {
        var doc = AcadApp.DocumentManager.MdiActiveDocument;
        doc?.SendStringToExecute("LLMLINK\n", true, false, false);
    }

    private void RunGcCreate()
    {
        if (CurrentGc is not { } selected)
        {
            MessageBox.Show("Hãy đăng ký DIM/LINE/PLINE/MLINE và chọn mã GC trong danh sách.", "LiveLength");
            return;
        }
        var doc = AcadApp.DocumentManager.MdiActiveDocument;
        if (doc == null || _activeDoc != doc.Name) return;
        Commands.PendingGcHandle = selected.Handle;
        Commands.PendingGcDocName = doc.Name;
        doc.SendStringToExecute("LLMGCCREATE\n", true, false, false);
    }

    private void RunGcCommand(string command)
    {
        if (CurrentGc is not { } selected) return;
        var doc = AcadApp.DocumentManager.MdiActiveDocument;
        if (doc == null || _activeDoc != doc.Name) return;
        Commands.PendingGcHandle = selected.Handle;
        Commands.PendingGcDocName = doc.Name;
        doc.SendStringToExecute(command + "\n", true, false, false);
    }

    private void RunGcNoteZoom()
    {
        if (_gcNotesGrid.SelectedRows.Count == 0 || _gcNotesGrid.SelectedRows[0].Tag is not NoteRow note) return;
        var doc = AcadApp.DocumentManager.MdiActiveDocument;
        if (doc == null || _activeDoc != doc.Name) return;
        Commands.PendingHandle = note.Handle;
        Commands.PendingDocName = doc.Name;
        doc.SendStringToExecute("LLMZOOMNOTE\n", true, false, false);
    }

    private void ExportCsv()
    {
        var doc = AcadApp.DocumentManager.MdiActiveDocument;
        if (doc == null) return;
        using var dialog = new SaveFileDialog
        {
            Title = "Xuất danh sách LiveLength (mở bằng Excel)",
            Filter = "Excel CSV (*.csv)|*.csv",
            FileName = "LiveLength_" + System.IO.Path.GetFileNameWithoutExtension(doc.Name) + ".csv"
        };
        if (dialog.ShowDialog() != DialogResult.OK) return;
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine("sep=;");
            sb.AppendLine("STT;Nội dung;Chiều dài 1;Chiều dài 2;Đơn vị;Trạng thái");
            int i = 0;
            foreach (var r in _all.Where(r => r.Link?.IsGc != true))
            {
                var cols = new[]
                {
                    (++i).ToString(CultureInfo.InvariantCulture), r.Description,
                    r.LengthMeters.HasValue ? FormatDisplay(r.LengthMeters, r.Link?.Millimeters == true).Split(' ')[0] : "",
                    r.SecondaryLengthMeters.HasValue ? FormatDisplay(r.SecondaryLengthMeters, r.Link?.Millimeters == true).Split(' ')[0] : "",
                    r.Link?.Millimeters == true ? "mm" : "m",
                    r.State
                };
                sb.AppendLine(string.Join(";", cols.Select(Csv)));
            }
            System.IO.File.WriteAllText(dialog.FileName, sb.ToString(), new UTF8Encoding(true));
            MessageBox.Show($"Đã xuất {i} ghi chú:\n{dialog.FileName}", "LiveLength");
        }
        catch (System.Exception ex) { MessageBox.Show(ex.Message, "Không thể xuất CSV"); }
    }

    private static string Csv(string s) => "\"" + s.Replace("\"", "\"\"") + "\"";
}
