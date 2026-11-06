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
        internal Action? EnterAction;

        private bool HandleEnter(Keys keys)
        {
            if (EnterAction == null || keys != Keys.Enter || IsCurrentCellInEditMode || CurrentRow == null) return false;
            EnterAction();
            return true;
        }

        private bool InsertLineBreak(Keys keys)
        {
            if ((keys & Keys.KeyCode) != Keys.Enter || (keys & Keys.Shift) == 0 ||
                EditingControl is not TextBox editor ||
                CurrentCell?.OwningColumn.Name is not ("content" or "noteContent" or "gcDescription")) return false;
            editor.SelectedText = Environment.NewLine;
            return true;
        }

        protected override bool ProcessDialogKey(Keys keyData) =>
            InsertLineBreak(keyData) || HandleEnter(keyData) || base.ProcessDialogKey(keyData);

        protected override bool ProcessDataGridViewKey(KeyEventArgs e) =>
            InsertLineBreak(e.KeyData) || HandleEnter(e.KeyData) || base.ProcessDataGridViewKey(e);

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData) =>
            InsertLineBreak(keyData) || base.ProcessCmdKey(ref msg, keyData);
    }

    private readonly Label _drawingName = new();
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
    private List<GcRow> _gcRows = new();
    private string _activeDoc = "";
    private bool _updatingGcGrid;
    private bool _updatingNoteGrid;
    private bool _darkTheme;

    internal ManagerPanel()
    {
        Font = new Font("Segoe UI", 9f);
        BackColor = Color.FromArgb(246, 248, 251);
        var host = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3,
            Margin = Padding.Empty, Padding = Padding.Empty };
        host.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        host.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        host.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var navigation = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1,
            Margin = Padding.Empty, Padding = new Padding(6, 4, 6, 0) };
        for (int i = 0; i < 3; i++) navigation.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / 3));
        // Thin divider under the tab strip.
        navigation.Paint += (_, e) =>
        {
            using var pen = new Pen(_darkTheme ? Color.FromArgb(69, 78, 87) : Color.FromArgb(214, 222, 232));
            e.Graphics.DrawLine(pen, 0, navigation.Height - 1, navigation.Width, navigation.Height - 1);
        };
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
            var button = new Button { Text = label, Dock = DockStyle.Fill,
                Margin = new Padding(0, 0, 2, 0), FlatStyle = FlatStyle.Flat,
                Tag = page, Cursor = Cursors.Hand,
                Font = new Font("Segoe UI Semibold", 9.5f, FontStyle.Bold) };
            button.FlatAppearance.BorderSize = 0;
            // Selected tab: accent text with an underline instead of a filled block.
            button.Paint += (_, e) =>
            {
                if (!ReferenceEquals(button.Tag, _selectedPage)) return;
                using var brush = new SolidBrush(_darkTheme ? Color.FromArgb(110, 170, 210) : Color.FromArgb(35, 99, 150));
                e.Graphics.FillRectangle(brush, 10, button.Height - 3, button.Width - 20, 3);
            };
            button.Click += (_, _) =>
            {
                FinishGridEditing();
                foreach (var item in new[] { lengthTab, gcTab, settingsTab }) item.Visible = item == page;
                _selectedPage = page;
                page.BringToFront();
                ApplyTheme(host);
            };
            _navButtons.Add(button);
            navigation.Controls.Add(button, _navButtons.Count - 1, 0);
        }
        AddNavigation("Chiều dài", lengthTab, 88);
        AddNavigation("Giật cấp GC", gcTab, 106);
        AddNavigation("Cài đặt", settingsTab, 82);
        host.Controls.Add(navigation, 0, 0);
        BuildUpdateBanner();
        host.Controls.Add(_banner, 0, 1);
        host.Controls.Add(contentHost, 0, 2);
        BuildSettings(settingsTab, host);
        Controls.Add(host);
        var main = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6,
            Padding = new Padding(10, 8, 10, 8), BackColor = BackColor };
        main.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));   // drawing + tools
        main.RowStyles.Add(new RowStyle(SizeType.Absolute, 74));   // create actions
        main.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));   // search + layer
        main.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));   // quick status filters
        main.RowStyles.Add(new RowStyle(SizeType.Percent, 100));   // grid
        main.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));   // row actions
        lengthTab.Controls.Add(main);

        var header = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Margin = Padding.Empty };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 84));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 88));
        _drawingName.Text = "Chưa mở bản vẽ";
        _drawingName.Dock = DockStyle.Fill;
        _drawingName.AutoEllipsis = true;
        _drawingName.TextAlign = ContentAlignment.MiddleLeft;
        _drawingName.Font = new Font("Segoe UI Semibold", 10.5f, FontStyle.Bold);
        header.Controls.Add(_drawingName, 0, 0);
        var refresh = SmallButton("Quét lại");
        _tips.SetToolTip(refresh, "Đồng bộ và quét lại toàn bộ bản vẽ (F5).");
        refresh.Click += (_, _) => ForceRefresh();
        header.Controls.Add(refresh, 1, 0);
        var tools = SmallButton("Công cụ ▼");
        var toolsMenu = new ContextMenuStrip { ShowImageMargin = false };
        toolsMenu.Items.Add("Dò nguồn theo MLeader", null, (_, _) => RunDocumentCommand("LLMFIND"));
        var autoUpdate = new ToolStripMenuItem("Tự động cập nhật L=") { CheckOnClick = true };
        autoUpdate.CheckedChanged += (_, _) => LiveEngine.SetEnabled(autoUpdate.Checked);
        toolsMenu.Items.Add(autoUpdate);
        toolsMenu.Opening += (_, _) => autoUpdate.Checked = LiveEngine.Enabled;
        tools.Click += (_, _) => toolsMenu.Show(tools, new Point(0, tools.Height));
        Disposed += (_, _) => toolsMenu.Dispose();
        header.Controls.Add(tools, 2, 0);
        main.Controls.Add(header, 0, 0);

        // Every create action stays one click: primary on its own row, the rest side by side.
        var createActions = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 2, Margin = Padding.Empty };
        for (int i = 0; i < 3; i++) createActions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / 3));
        createActions.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        createActions.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var createNew = new Button
        {
            Text = "＋  Tạo MLeader mới",
            Dock = DockStyle.Fill,
            Tag = "primary",
            Margin = new Padding(0, 2, 0, 3),
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI Semibold", 9.5f, FontStyle.Bold)
        };
        createNew.FlatAppearance.BorderSize = 0;
        _tips.SetToolTip(createNew, "Có L= trong nội dung: tự liên kết chiều dài tại mũi tên. Không có L=: ghi chú thường.");
        createNew.Click += (_, _) => RunDocumentCommand("LLMCREATE");
        createActions.Controls.Add(createNew, 0, 0);
        createActions.SetColumnSpan(createNew, 3);
        void Secondary(string label, string tip, string command, int column)
        {
            var button = new Button { Text = label, Dock = DockStyle.Fill, FlatStyle = FlatStyle.Flat,
                Margin = new Padding(column == 0 ? 0 : 2, 1, column == 2 ? 0 : 2, 1), AutoEllipsis = true };
            _tips.SetToolTip(button, tip);
            button.Click += (_, _) => RunDocumentCommand(command);
            createActions.Controls.Add(button, column, 1);
        }
        Secondary("Gắn ghi chú có sẵn", "Chọn MLeader để tự nhận đường tại mũi tên; MText cần chọn nguồn.", "LLMLINK", 0);
        Secondary("Sao chép từ mẫu", "Chọn MLeader LiveLength mẫu, rồi đặt mũi tên và vị trí ghi chú mới.", "LLMCOPY", 1);
        Secondary("MText → MLeader", "Tạo MLeader từ nội dung một MText; MText gốc được giữ lại.", "LLMMTEXT2LEADER", 2);
        main.Controls.Add(createActions, 0, 1);

        var filters = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
        filters.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        filters.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        _search.Dock = DockStyle.Fill;
        _search.PlaceholderText = "Tìm nội dung hoặc handle… (Ctrl+F)";
        _search.Margin = new Padding(0, 6, 6, 0);
        _search.TextChanged += (_, _) => FilterRows();
        _search.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Escape) return;
            _search.Text = "";
            e.SuppressKeyPress = true;
        };
        filters.Controls.Add(_search, 0, 0);
        _layer.Dock = DockStyle.Fill;
        _layer.Margin = new Padding(0, 4, 0, 2);
        _layer.SelectedIndexChanged += LayerChanged;
        _layer.Items.Add("Tất cả");
        _layer.SelectedIndex = 0;
        _tips.SetToolTip(_layer, "Lọc theo layer");
        filters.Controls.Add(_layer, 1, 0);
        main.Controls.Add(filters, 0, 2);

        // Status filter state lives in _status; the chips are its visible control.
        _status.Items.AddRange(new object[] { "Tất cả", "Đã liên kết", "Mất liên kết", "Ghi chú thường" });
        _status.SelectedIndex = 0;
        _status.SelectedIndexChanged += (_, _) => FilterRows();
        // Fixed equal columns: chip widths never depend on their text, so nothing shifts when filtering.
        _chips.Dock = DockStyle.Fill;
        _chips.ColumnCount = 4;
        _chips.RowCount = 1;
        _chips.Margin = Padding.Empty;
        _chips.Padding = new Padding(0, 3, 0, 1);
        for (int i = 0; i < 4; i++)
        {
            _chips.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25f));
            var index = i;
            var chip = new Button { Tag = "chip", Dock = DockStyle.Fill, AutoEllipsis = true,
                FlatStyle = FlatStyle.Flat, Margin = new Padding(i == 0 ? 0 : 3, 0, i == 3 ? 0 : 3, 0),
                Font = new Font("Segoe UI", 8.5f), Cursor = Cursors.Hand };
            chip.Click += (_, _) => _status.SelectedIndex = _status.SelectedIndex == index ? 0 : index;
            _chipButtons.Add(chip);
            _chips.Controls.Add(chip, i, 0);
        }
        UpdateChips(0, 0, 0, 0, 0);
        main.Controls.Add(_chips, 0, 3);

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
        _grid.ShowCellToolTips = true;
        _grid.BorderStyle = BorderStyle.FixedSingle;
        _grid.EnableHeadersVisualStyles = false;
        _grid.Columns.Add(NewCol("seq", "STT", 38));
        _grid.Columns.Add(NewCol("type", "Loại", 75));
        _grid.Columns.Add(NewCol("content", "Nội dung", 260));
        _grid.Columns.Add(NewCol("len", "Dài", 100));
        _grid.Columns.Add(NewCol("state", "Trạng thái", 96));
        _grid.Columns.Add(new DataGridViewComboBoxColumn { Name = "unit", HeaderText = "Đơn vị",
            Width = 58, DataSource = new[] { "m", "mm" }, FlatStyle = FlatStyle.Flat,
            DisplayStyleForCurrentCellOnly = true });
        _grid.Columns.Add(NewCol("layer", "Layer", 105));
        _grid.Columns.Add(NewCol("layout", "Layout", 78));
        _grid.Columns.Add(NewCol("xy", "Tọa độ X, Y", 155));
        _grid.Columns.Add(NewCol("handle", "Handle", 90));
        _grid.Columns.Add(NewCol("source", "Nguồn", 90));
        _grid.Columns.Add(NewCol("style", "Kiểu chữ", 110));
        _grid.Columns["style"]!.DisplayIndex = 6;
        _grid.DataError += (_, e) => e.ThrowException = false;
        _grid.Columns[2].AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
        _grid.Columns[2].MinimumWidth = 100;
        _grid.Columns[3].DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
        _grid.Columns[0].DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;
        foreach (DataGridViewColumn column in _grid.Columns)
            column.ReadOnly = column.Name is not ("content" or "unit");
        LoadColumnVisibility();
        _grid.CellEndEdit += NoteCellEdited;
        _grid.CellBeginEdit += NoteCellBeginEdit;
        _grid.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (_grid.IsCurrentCellDirty && _grid.CurrentCell?.OwningColumn.Name == "unit")
                _grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
        };
        _grid.EditingControlShowing += ConfigureNoteEditor;
        _grid.CellValueChanged += NoteCellValueChanged;
        _grid.CellPainting += PaintStateCell;
        // Content is edited in place and unit is a dropdown; every other cell zooms.
        _grid.CellDoubleClick += (_, e) =>
        {
            if (e.RowIndex >= 0 && e.ColumnIndex >= 0 &&
                _grid.Columns[e.ColumnIndex].Name is not ("content" or "unit"))
                RunSelectionCommand("LLMZOOMNOTE");
        };
        _grid.EnterAction = () => RunSelectionCommand("LLMZOOMNOTE");
        _grid.CellMouseDown += ShowRowMenu;
        _grid.ColumnHeaderMouseClick += (_, e) => { if (e.Button == MouseButtons.Right) ShowColumnMenu(); };
        BuildRowMenu();
        BuildColumnMenu();
        _flashTimer.Interval = 1200;
        _flashTimer.Tick += (_, _) => EndFlash();
        Disposed += (_, _) => { _flashTimer.Dispose(); _rowMenu.Dispose(); };
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

        var buttons = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 1,
            Padding = new Padding(0, 6, 0, 0), Margin = Padding.Empty };
        for (int i = 0; i < 4; i++) buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25f));
        void Action(string label, string tip, EventHandler handler, int column)
        {
            var button = new Button { Text = label, Dock = DockStyle.Fill, FlatStyle = FlatStyle.Flat, AutoEllipsis = true,
                Margin = new Padding(column == 0 ? 0 : 2, 0, column == 3 ? 0 : 2, 0) };
            _tips.SetToolTip(button, tip);
            button.Click += handler;
            buttons.Controls.Add(button, column, 0);
        }
        Action("Zoom ghi chú", "Zoom tới ghi chú đang chọn (Enter hoặc nhấp đúp).", (_, _) => RunSelectionCommand("LLMZOOMNOTE"), 0);
        Action("Zoom nguồn", "Zoom tới đường nguồn của ghi chú đang chọn.", (_, _) => RunSelectionCommand("LLMZOOMSOURCE"), 1);
        Action("Chiều dài 2", "Gán nguồn cho vị trí L= thứ hai của ghi chú đang chọn.", (_, _) => RunSelectionCommand("LLMSECOND"), 2);
        Action("Xuất CSV", "Xuất danh sách ghi chú chiều dài để mở bằng Excel.", (_, _) => ExportCsv(), 3);
        main.Controls.Add(buttons, 0, 5);

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
        _tips.SetToolTip(registerGc, "Chọn DIM, LINE, PLINE, ARC, SPLINE, ELLIPSE hoặc MLINE; đặt mã và mô tả vị trí.");
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
            ColumnCount = 1, RowCount = 8 };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
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
        var followArrow = new CheckBox { Text = "Mũi tên MLeader bám theo nguồn khi nguồn đổi kích thước / di chuyển",
            Dock = DockStyle.Fill, Checked = ArrowFollow.Enabled };
        followArrow.CheckedChanged += (_, _) => ArrowFollow.Enabled = followArrow.Checked;
        _tips.SetToolTip(followArrow, "Khi LINE/PLINE/ARC... bị kéo ngắn, kéo dài hoặc di chuyển, đầu mũi tên được đặt lại lên nguồn ở cùng vị trí tương đối.");
        layout.Controls.Add(followArrow, 0, 3);
        layout.Controls.Add(new Label { Text = "Cập nhật", Dock = DockStyle.Fill, TextAlign = ContentAlignment.BottomLeft,
            Font = new Font("Segoe UI Semibold", 12f, FontStyle.Bold) }, 0, 4);
        var autoCheck = new CheckBox { Text = "Tự kiểm tra bản mới khi mở AutoCAD (tối đa 1 lần/ngày)",
            Dock = DockStyle.Fill, Checked = Updater.AutoCheck };
        autoCheck.CheckedChanged += (_, _) => Updater.AutoCheck = autoCheck.Checked;
        layout.Controls.Add(autoCheck, 0, 5);
        var versionRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
        versionRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        versionRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
        _updateStatus.Dock = DockStyle.Fill;
        _updateStatus.TextAlign = ContentAlignment.MiddleLeft;
        _updateStatus.AutoEllipsis = true;
        _updateStatus.Text = $"Đang dùng v{Updater.CurrentText}";
        versionRow.Controls.Add(_updateStatus, 0, 0);
        var checkNow = new Button { Text = "Kiểm tra ngay", Dock = DockStyle.Fill, FlatStyle = FlatStyle.Flat,
            Margin = new Padding(4, 3, 0, 3) };
        checkNow.Click += (_, _) =>
        {
            _manualCheck = true;
            _updateStatus.Text = "Đang kiểm tra…";
            Updater.CheckInBackground(true);
        };
        versionRow.Controls.Add(checkNow, 1, 0);
        layout.Controls.Add(versionRow, 0, 6);
        layout.Controls.Add(new Label { Text = "Đơn vị mm/m được chọn riêng ở từng dòng ghi chú. " +
            "mm hiển thị số nguyên; m hiển thị tối đa 2 chữ số thập phân." + Environment.NewLine + Environment.NewLine +
            "Nhấp vào ô Nội dung để sửa trực tiếp. Enter: lưu; Shift+Enter: xuống dòng." + Environment.NewLine +
            "Phím tắt: F5 quét lại · Ctrl+F tìm · Enter zoom ghi chú." + Environment.NewLine +
            "Chuột phải vào dòng để có thêm thao tác; chuột phải vào tiêu đề cột để ẩn/hiện cột.",
            Dock = DockStyle.Fill, AutoSize = false }, 0, 7);
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
        foreach (var button in _navButtons)
        {
            var selected = ReferenceEquals(button.Tag, _selectedPage);
            button.BackColor = background;
            button.ForeColor = selected ? (_darkTheme ? Color.FromArgb(150, 200, 235) : accent) : _drawingName.ForeColor;
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderSize = 0;
            button.FlatAppearance.MouseOverBackColor = _darkTheme ? Color.FromArgb(48, 56, 64) : Color.FromArgb(234, 240, 247);
            button.Invalidate();
        }
        _drawingName.ForeColor = foreground;
        foreach (DataGridViewRow row in _grid.Rows)
            if (row.Tag is NoteRow note) row.DefaultCellStyle.BackColor = NoteBack(note);
        StyleChips();
        UpdateBanner();
        root.Invalidate(true);
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

    private readonly TableLayoutPanel _chips = new();
    private readonly List<Button> _chipButtons = new();
    private readonly ContextMenuStrip _rowMenu = new() { ShowImageMargin = false };
    private readonly System.Windows.Forms.Timer _flashTimer = new();
    private readonly HashSet<string> _flash = new(StringComparer.OrdinalIgnoreCase);

    private static Button SmallButton(string text) => new()
    {
        Text = text, Dock = DockStyle.Fill, FlatStyle = FlatStyle.Flat, Margin = new Padding(4, 3, 0, 3)
    };

    private static void RunDocumentCommand(string command) =>
        AcadApp.DocumentManager.MdiActiveDocument?.SendStringToExecute(command + "\n", true, false, false);

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.F5) { ForceRefresh(); return true; }
        if (keyData == (Keys.Control | Keys.F))
        {
            if (_navButtons.Count > 0 && !ReferenceEquals(_selectedPage, _navButtons[0].Tag)) _navButtons[0].PerformClick();
            _search.Focus();
            _search.SelectAll();
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    private Color StateColor(string state) => state switch
    {
        "Đã liên kết" => _darkTheme ? Color.FromArgb(96, 196, 132) : Color.FromArgb(39, 140, 82),
        "Mất liên kết" => _darkTheme ? Color.FromArgb(236, 120, 108) : Color.FromArgb(203, 64, 52),
        _ => _darkTheme ? Color.FromArgb(150, 162, 174) : Color.FromArgb(128, 138, 150)
    };

    private static string ShortState(string state) => state switch
    {
        "Đã liên kết" => "Liên kết",
        "Mất liên kết" => "Mất nguồn",
        _ => "Thường"
    };

    private Color NoteBack(NoteRow note) => _flash.Contains(note.Handle)
        ? (_darkTheme ? Color.FromArgb(88, 82, 50) : Color.FromArgb(255, 245, 205))
        : note.State == "Mất liên kết"
            ? (_darkTheme ? Color.FromArgb(74, 55, 57) : Color.FromArgb(255, 240, 236))
            : Color.Empty;

    private void PaintStateCell(object? sender, DataGridViewCellPaintingEventArgs e)
    {
        if (e.RowIndex < 0 || e.ColumnIndex < 0 || _grid.Columns[e.ColumnIndex].Name != "state" ||
            _grid.Rows[e.RowIndex].Tag is not NoteRow note || e.Graphics is not { } g || e.CellStyle is not { } style) return;
        var selected = (e.State & DataGridViewElementStates.Selected) != 0;
        e.PaintBackground(e.CellBounds, selected);
        var size = 8;
        var dot = new Rectangle(e.CellBounds.X + 8, e.CellBounds.Y + (e.CellBounds.Height - size) / 2, size, size);
        var smoothing = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var brush = new SolidBrush(StateColor(note.State))) g.FillEllipse(brush, dot);
        g.SmoothingMode = smoothing;
        var text = new Rectangle(dot.Right + 6, e.CellBounds.Y, Math.Max(0, e.CellBounds.Right - dot.Right - 8), e.CellBounds.Height);
        TextRenderer.DrawText(g, ShortState(note.State), style.Font, text,
            selected ? style.SelectionForeColor : style.ForeColor,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
        e.Handled = true;
    }

    private void UpdateChips(int all, int linked, int missing, int plain, int shown)
    {
        if (_chipButtons.Count < 4) return;
        _chipButtons[0].Text = $"Tất cả {all}";
        _tips.SetToolTip(_chipButtons[0], shown != all ? $"Đang hiện {shown}/{all} ghi chú" : "Bỏ lọc trạng thái");
        _chipButtons[1].Text = $"● {linked} liên kết";
        _chipButtons[2].Text = $"● {missing} mất nguồn";
        _chipButtons[3].Text = $"● {plain} thường";
        StyleChips();
    }

    private void StyleChips()
    {
        var accent = _darkTheme ? Color.FromArgb(66, 116, 150) : Color.FromArgb(35, 99, 150);
        var background = _darkTheme ? Color.FromArgb(39, 45, 52) : Color.FromArgb(245, 248, 251);
        var border = _darkTheme ? Color.FromArgb(86, 98, 109) : Color.FromArgb(206, 216, 227);
        var states = new[] { "", "Đã liên kết", "Mất liên kết", "Ghi chú thường" };
        for (int i = 0; i < _chipButtons.Count; i++)
        {
            var chip = _chipButtons[i];
            var active = _status.SelectedIndex == i;
            chip.BackColor = active ? accent : background;
            chip.ForeColor = active ? Color.White
                : i == 0 ? (_darkTheme ? Color.FromArgb(205, 214, 222) : Color.FromArgb(70, 81, 95)) : StateColor(states[i]);
            chip.FlatAppearance.BorderSize = 1;
            chip.FlatAppearance.BorderColor = active ? accent : border;
            chip.FlatAppearance.MouseOverBackColor = active ? accent
                : (_darkTheme ? Color.FromArgb(55, 64, 73) : Color.FromArgb(233, 240, 247));
        }
    }

    private void BuildRowMenu()
    {
        ToolStripMenuItem Item(string text, string keys, Action action)
        {
            var item = new ToolStripMenuItem(text) { ShortcutKeyDisplayString = keys };
            item.Click += (_, _) => action();
            _rowMenu.Items.Add(item);
            return item;
        }
        Item("Zoom ghi chú", "Enter", () => RunSelectionCommand("LLMZOOMNOTE"));
        var zoomSource = Item("Zoom nguồn", "", () => RunSelectionCommand("LLMZOOMSOURCE"));
        _rowMenu.Items.Add(new ToolStripSeparator());
        var relink = Item("Gắn lại nguồn", "", () => RunSelectionCommand("LLMRELINK"));
        var second = Item("Gán chiều dài thứ 2", "", () => RunSelectionCommand("LLMSECOND"));
        Item("Chép nội dung", "", () =>
        {
            if (CurrentRow is not { } row) return;
            try { Clipboard.SetText(row.CurrentText.Replace("\\P", Environment.NewLine, StringComparison.OrdinalIgnoreCase)); }
            catch (System.Exception) { }
        });
        _rowMenu.Items.Add(new ToolStripSeparator());
        var style = new ToolStripMenuItem("Đổi kiểu chữ");
        var toTnr = new ToolStripMenuItem("TNR — Times New Roman (Unicode)");
        var toVn = new ToolStripMenuItem("VNROMANS — SHX (TCVN3)");
        toTnr.Click += (_, _) => { Commands.PendingStyleVnromans = false; RunSelectionCommand("LLMSTYLE"); };
        toVn.Click += (_, _) => { Commands.PendingStyleVnromans = true; RunSelectionCommand("LLMSTYLE"); };
        style.DropDownItems.Add(toTnr);
        style.DropDownItems.Add(toVn);
        _rowMenu.Items.Add(style);
        _rowMenu.Items.Add(new ToolStripSeparator());
        var unlink = Item("Hủy liên kết", "", () =>
        {
            if (MessageBox.Show("Hủy liên kết chiều dài của ghi chú này? Nội dung hiện tại được giữ nguyên.",
                    "LiveLength", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                RunSelectionCommand("LLMUNLINK");
        });
        _rowMenu.Opening += (_, e) =>
        {
            var row = CurrentRow;
            if (row == null) { e.Cancel = true; return; }
            var linked = row.Link != null;
            zoomSource.Enabled = linked && row.SourceHandle is { Length: > 0 } and not "0";
            relink.Enabled = linked;
            second.Enabled = linked;
            unlink.Enabled = linked;
            toTnr.Checked = !row.Tcvn3;
            toVn.Checked = row.Tcvn3;
            toTnr.Enabled = row.Tcvn3;
            toVn.Enabled = !row.Tcvn3;
        };
    }

    private void ShowRowMenu(object? sender, DataGridViewCellMouseEventArgs e)
    {
        if (e.Button != MouseButtons.Right || e.RowIndex < 0) return;
        FinishGridEditing();
        var row = _grid.Rows[e.RowIndex];
        if (!row.Selected)
        {
            _grid.ClearSelection();
            row.Selected = true;
            var cell = row.Cells.Cast<DataGridViewCell>().FirstOrDefault(c => c.Visible);
            if (cell != null) _grid.CurrentCell = cell;
        }
        _rowMenu.Show(Cursor.Position);
    }

    private static string ColumnFile => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "LiveLengthManager2027", "columns.txt");

    private static readonly string[] OptionalColumns = { "type", "unit", "style", "layer", "layout", "xy", "handle", "source" };

    private void LoadColumnVisibility()
    {
        var visible = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "unit", "style" };
        try
        {
            if (System.IO.File.Exists(ColumnFile))
                visible = new HashSet<string>(System.IO.File.ReadAllText(ColumnFile)
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), StringComparer.OrdinalIgnoreCase);
        }
        catch { }
        foreach (var name in OptionalColumns) _grid.Columns[name]!.Visible = visible.Contains(name);
    }

    private readonly ContextMenuStrip _columnMenu = new();

    private void BuildColumnMenu()
    {
        foreach (var name in OptionalColumns)
        {
            var column = _grid.Columns[name]!;
            var item = new ToolStripMenuItem(column.HeaderText) { CheckOnClick = true };
            item.CheckedChanged += (_, _) =>
            {
                if (column.Visible == item.Checked) return;
                column.Visible = item.Checked;
                try
                {
                    System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(ColumnFile)!);
                    System.IO.File.WriteAllText(ColumnFile, string.Join(",",
                        OptionalColumns.Where(n => _grid.Columns[n]!.Visible)));
                }
                catch { }
            };
            _columnMenu.Items.Add(item);
        }
        _columnMenu.Opening += (_, _) =>
        {
            for (int i = 0; i < OptionalColumns.Length; i++)
                ((ToolStripMenuItem)_columnMenu.Items[i]).Checked = _grid.Columns[OptionalColumns[i]]!.Visible;
        };
        Disposed += (_, _) => _columnMenu.Dispose();
    }

    private void ShowColumnMenu()
    {
        FinishGridEditing();
        _columnMenu.Show(Cursor.Position);
    }

    private void StartFlash(DataGridViewRow row)
    {
        if (row.Tag is not NoteRow note) return;
        _flash.Add(note.Handle);
        row.DefaultCellStyle.BackColor = NoteBack(note);
        _flashTimer.Stop();
        _flashTimer.Start();
    }

    private void EndFlash()
    {
        _flashTimer.Stop();
        if (_flash.Count == 0) return;
        var handles = _flash.ToList();
        _flash.Clear();
        foreach (DataGridViewRow row in _grid.Rows)
            if (row.Tag is NoteRow note && handles.Contains(note.Handle, StringComparer.OrdinalIgnoreCase))
                row.DefaultCellStyle.BackColor = NoteBack(note);
    }

    private readonly TableLayoutPanel _banner = new();
    private readonly Label _bannerText = new();
    private readonly Button _bannerAction = new();
    private readonly Button _bannerNotes = new();
    private readonly Button _bannerClose = new();
    private readonly Label _updateStatus = new();
    private UpdateState? _dismissed;
    private bool _manualCheck;

    private void BuildUpdateBanner()
    {
        _banner.Dock = DockStyle.Fill;
        _banner.AutoSize = true;
        _banner.ColumnCount = 4;
        _banner.RowCount = 1;
        _banner.Margin = new Padding(10, 6, 10, 0);
        _banner.Padding = new Padding(8, 3, 4, 3);
        _banner.Visible = false;
        _banner.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _banner.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _banner.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _banner.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 28));
        _bannerText.Dock = DockStyle.Fill;
        _bannerText.TextAlign = ContentAlignment.MiddleLeft;
        _bannerText.AutoEllipsis = true;
        _banner.Controls.Add(_bannerText, 0, 0);
        foreach (var (button, column) in new[] { (_bannerAction, 1), (_bannerNotes, 2), (_bannerClose, 3) })
        {
            button.FlatStyle = FlatStyle.Flat;
            button.AutoSize = column != 3;
            button.Height = 26;
            button.Dock = column == 3 ? DockStyle.Fill : DockStyle.None;
            button.Margin = new Padding(3, 0, 0, 0);
            _banner.Controls.Add(button, column, 0);
        }
        _bannerAction.Tag = "primary";
        _bannerNotes.Text = "Xem thay đổi";
        _bannerClose.Text = "✕";
        _tips.SetToolTip(_bannerClose, "Ẩn thông báo");
        _bannerAction.Click += (_, _) =>
        {
            if (Updater.State == UpdateState.Available && Updater.LoaderPresent) Updater.DownloadInBackground();
            else if (Updater.State == UpdateState.Failed) Updater.CheckInBackground(true);
            else OpenUrl(Updater.Latest?.PageUrl);
        };
        _bannerNotes.Click += (_, _) => OpenUrl(Updater.Latest?.PageUrl);
        _bannerClose.Click += (_, _) => { _dismissed = Updater.State; UpdateBanner(); };
        Action onChanged = () =>
        {
            if (IsDisposed || !IsHandleCreated) return;
            try { BeginInvoke(new Action(UpdateBanner)); } catch (InvalidOperationException) { }
        };
        Updater.Changed += onChanged;
        Disposed += (_, _) => Updater.Changed -= onChanged;
        HandleCreated += (_, _) => UpdateBanner();
    }

    private static void OpenUrl(string? url)
    {
        if (string.IsNullOrEmpty(url)) return;
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (System.Exception ex) { MessageBox.Show(ex.Message, "LiveLength"); }
    }

    private void UpdateBanner()
    {
        if (IsDisposed) return;
        var state = Updater.State;
        var latest = Updater.Latest?.Version is { } v ? $"v{v.Major}.{v.Minor}.{v.Build}" : "";
        _updateStatus.Text = state switch
        {
            UpdateState.Available => $"Đang dùng v{Updater.CurrentText} · có bản mới {latest}",
            UpdateState.Downloading => $"Đang tải {latest}…",
            UpdateState.Ready => $"Đã tải {latest} · dùng khi mở lại AutoCAD",
            UpdateState.Failed => "Không kiểm tra/tải được: " + Updater.Error,
            _ => _manualCheck ? $"v{Updater.CurrentText} là bản mới nhất." : $"Đang dùng v{Updater.CurrentText}"
        };
        if (state is UpdateState.None || (state == UpdateState.Failed && !_manualCheck) || _dismissed == state)
        {
            _banner.Visible = false;
            return;
        }
        var loader = Updater.LoaderPresent;
        (_bannerText.Text, _bannerAction.Text, _bannerAction.Visible, _bannerNotes.Visible) = state switch
        {
            UpdateState.Available when loader => ($"Có bản mới {latest}", "Cập nhật", true, true),
            UpdateState.Available => ($"Có bản mới {latest} — cài lại bằng bộ cài mới", "Tải bộ cài", true, false),
            UpdateState.Downloading => ($"Đang tải {latest}…", "", false, false),
            UpdateState.Ready => ($"Đã tải {latest}. Mở lại AutoCAD để dùng bản mới.", "", false, true),
            _ => ("Không cập nhật được: " + Updater.Error, "Thử lại", true, false)
        };
        _tips.SetToolTip(_bannerText, _bannerText.Text);
        var dark = _darkTheme;
        _banner.BackColor = state == UpdateState.Failed
            ? (dark ? Color.FromArgb(74, 55, 57) : Color.FromArgb(255, 240, 236))
            : (dark ? Color.FromArgb(44, 66, 84) : Color.FromArgb(225, 238, 250));
        _bannerText.ForeColor = dark ? Color.FromArgb(232, 238, 243) : Color.FromArgb(24, 52, 82);
        foreach (var b in new[] { _bannerNotes, _bannerClose })
        {
            b.BackColor = _banner.BackColor;
            b.ForeColor = _bannerText.ForeColor;
            b.FlatAppearance.BorderSize = 0;
        }
        _bannerAction.BackColor = dark ? Color.FromArgb(66, 116, 150) : Color.FromArgb(35, 99, 150);
        _bannerAction.ForeColor = Color.White;
        _bannerAction.FlatAppearance.BorderSize = 0;
        _banner.Visible = true;
    }

    internal void SetRows(Document doc, List<NoteRow> rows)
    {
        if (IsDisposed) return;
        bool switched = _activeDoc != doc.Name;
        _activeDoc = doc.Name;
        _all = rows;
        _gcRows = GcData.GetRows(doc);
        Render(doc, switched);
    }

    /// <summary>Merges re-read rows for the given handles. Returns false when a full refresh is needed.</summary>
    internal bool ApplyDelta(Document doc, ICollection<string> noteHandles, List<NoteRow> notes,
        ICollection<string> gcHandles, List<GcRow> gcRows)
    {
        if (IsDisposed || _activeDoc != doc.Name) return false;
        if (noteHandles.Count > 0)
        {
            var replaced = new HashSet<string>(noteHandles, StringComparer.OrdinalIgnoreCase);
            _all = LiveEngine.SortRows(_all.Where(r => !replaced.Contains(r.Handle)).Concat(notes));
        }
        if (gcHandles.Count > 0)
        {
            var replaced = new HashSet<string>(gcHandles, StringComparer.OrdinalIgnoreCase);
            _gcRows = GcData.Sort(_gcRows.Where(r => !replaced.Contains(r.Handle)).Concat(gcRows));
        }
        Render(doc, false);
        return true;
    }

    // Reuses the existing grid rows when the same items are shown in the same order, so a
    // live refresh only rewrites changed cells and keeps scroll position, selection and an
    // open editor. Returns false when the caller must rebuild the grid.
    private static bool UpdateInPlace<T>(DataGridView grid, IReadOnlyList<T> items, Func<T, string> key,
        Func<T, int, object[]> values, Action<DataGridViewRow, T> decorate,
        Action<DataGridViewRow>? changed = null) where T : class
    {
        if (grid.Rows.Count != items.Count) return false;
        for (int i = 0; i < items.Count; i++)
            if (grid.Rows[i].Tag is not T old || !key(old).Equals(key(items[i]), StringComparison.OrdinalIgnoreCase))
                return false;
        for (int i = 0; i < items.Count; i++)
        {
            var row = grid.Rows[i];
            row.Tag = items[i];
            if (grid.IsCurrentCellInEditMode && grid.CurrentCell?.RowIndex == i) continue;
            decorate(row, items[i]);
            var cellValues = values(items[i], i);
            var any = false;
            for (int c = 0; c < cellValues.Length && c < row.Cells.Count; c++)
                if (!Equals(row.Cells[c].Value, cellValues[c])) { row.Cells[c].Value = cellValues[c]; any = true; }
            if (any) changed?.Invoke(row);
        }
        return true;
    }

    private static string GcDescription(GcRow item) =>
        item.Description.Replace("\\P", Environment.NewLine, StringComparison.OrdinalIgnoreCase);

    private void Render(Document doc, bool switched)
    {
        var rows = _all;
        var gcRows = _gcRows;
        var oldGc = CurrentGc?.Handle;
        _updatingGcGrid = true;
        try
        {
            void DecorateGc(DataGridViewRow row, GcRow item) =>
                row.Height = Math.Min(84, 31 + 18 * (GcDescription(item).Split('\n').Length - 1));
            if (switched || !UpdateInPlace(_gcGrid, gcRows, r => r.Handle,
                    (item, _) => new object[] { item.Name, GcDescription(item), FormatDisplay(item.LengthMeters, false) },
                    DecorateGc))
            {
                _gcGrid.CancelEdit();
                _gcGrid.Rows.Clear();
                foreach (var item in gcRows)
                {
                    var index = _gcGrid.Rows.Add(item.Name, GcDescription(item), FormatDisplay(item.LengthMeters, false));
                    _gcGrid.Rows[index].Tag = item;
                    DecorateGc(_gcGrid.Rows[index], item);
                }
                _gcGrid.ClearSelection();
                var previous = _gcGrid.Rows.Cast<DataGridViewRow>().FirstOrDefault(r =>
                    !switched && (r.Tag as GcRow)?.Handle == oldGc);
                if (previous != null) previous.Selected = true;
                else if (_gcGrid.Rows.Count > 0) _gcGrid.Rows[0].Selected = true;
            }
        }
        finally { _updatingGcGrid = false; }
        _gcSourceCount.Text = $"Nguồn GC · {gcRows.Count}   |   Sửa mô tả: Shift+Enter xuống dòng";
        _gcEmpty.Visible = gcRows.Count == 0;
        if (_gcEmpty.Visible) _gcEmpty.BringToFront();
        else _gcGrid.BringToFront();
        _updatingNoteGrid = true;
        var gcNotes = rows.Where(r => r.Link?.IsGc == true).ToList();
        object[] GcNoteValues(NoteRow note, int i)
        {
            var code = note.Link?.GcLabel;
            if (string.IsNullOrWhiteSpace(code))
                code = gcRows.FirstOrDefault(source => source.Handle.Equals(note.SourceHandle,
                    StringComparison.OrdinalIgnoreCase))?.Name ?? "—";
            return new object[] { i + 1, code, note.Description,
                FormatDisplay(note.LengthMeters, note.Link?.Millimeters == true), note.State };
        }
        void DecorateGcNote(DataGridViewRow row, NoteRow note) =>
            row.Cells[2].ToolTipText = note.CurrentText.Replace("\\P", Environment.NewLine);
        try
        {
            if (switched || !UpdateInPlace(_gcNotesGrid, gcNotes, r => r.Handle, GcNoteValues, DecorateGcNote))
            {
                _gcNotesGrid.CancelEdit();
                _gcNotesGrid.Rows.Clear();
                for (int i = 0; i < gcNotes.Count; i++)
                {
                    var index = _gcNotesGrid.Rows.Add(GcNoteValues(gcNotes[i], i));
                    _gcNotesGrid.Rows[index].Tag = gcNotes[i];
                    DecorateGcNote(_gcNotesGrid.Rows[index], gcNotes[i]);
                }
                _gcNotesGrid.ClearSelection();
            }
        }
        finally { _updatingNoteGrid = false; }
        _gcNoteCount.Text = $"MLeader GC · {gcNotes.Count}   |   Sửa mã/mô tả ở bảng nguồn phía trên";
        _gcNotesEmpty.Visible = gcNotes.Count == 0;
        if (_gcNotesEmpty.Visible) _gcNotesEmpty.BringToFront();
        else _gcNotesGrid.BringToFront();
        UpdateGcDetails();
        var old = _layer.SelectedItem?.ToString() ?? "Tất cả";
        var layers = rows.Where(r => r.Link?.IsGc != true).Select(r => r.Layer).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();
        if (switched || !_layer.Items.Cast<object>().Skip(1).Select(o => o.ToString()).SequenceEqual(layers))
        {
            _layer.SelectedIndexChanged -= LayerChanged;
            // Programmatically repopulating can fire SelectionChanged; its final filter
            // run below restores the selected entity handle.
            _layer.Items.Clear();
            _layer.Items.Add("Tất cả");
            foreach (var layer in layers) _layer.Items.Add(layer);
            _layer.SelectedItem = !switched && layers.Contains(old) ? old : "Tất cả";
            _layer.SelectedIndexChanged += LayerChanged;
        }
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
        _flash.Clear();
        UpdateChips(0, 0, 0, 0, 0);
        _drawingName.Text = "Chưa mở bản vẽ";
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
        object[] NoteValues(NoteRow r, int i) => new object[] { i + 1, r.Type, r.Description,
            FormatNoteLengths(r),
            r.State, r.Link == null ? "" : r.Link.Millimeters ? "mm" : "m", r.Layer, r.Layout,
            r.HasPosition ? $"{r.Position.X:0.##}, {r.Position.Y:0.##}" : "-",
            r.Handle, r.SourceHandle, r.Style };
        void DecorateNote(DataGridViewRow gridRow, NoteRow r)
        {
            // Plain notes have no unit: a text cell avoids an empty choice in the dropdown.
            var unit = r.Link == null ? "" : r.Link.Millimeters ? "mm" : "m";
            if (r.Link == null && gridRow.Cells[5] is DataGridViewComboBoxCell)
                gridRow.Cells[5] = new DataGridViewTextBoxCell { Value = unit };
            else if (r.Link != null && gridRow.Cells[5] is not DataGridViewComboBoxCell)
                gridRow.Cells[5] = new DataGridViewComboBoxCell { DataSource = new[] { "m", "mm" },
                    FlatStyle = FlatStyle.Flat, DisplayStyleForCurrentCellOnly = true, Value = unit };
            gridRow.Cells[5].ReadOnly = r.Link == null;
            var back = NoteBack(r);
            if (gridRow.DefaultCellStyle.BackColor != back) gridRow.DefaultCellStyle.BackColor = back;
            gridRow.Cells[2].ToolTipText = r.CurrentText.Replace("\\P", Environment.NewLine, StringComparison.OrdinalIgnoreCase) +
                Environment.NewLine + Environment.NewLine + "Nhấp để sửa · Enter: lưu · Shift+Enter: xuống dòng";
            gridRow.Cells[4].ToolTipText = r.State;
            gridRow.Cells[11].ToolTipText = "Kiểu MLeader / Text style. Font trong ngoặc là font ghi đè ngay trong nội dung.";
        }
        try
        {
            if (clearSelection || !UpdateInPlace(_grid, filtered, r => r.Handle, NoteValues, DecorateNote, StartFlash))
            {
                _grid.Rows.Clear();
                for (int i = 0; i < filtered.Count; i++)
                {
                    int index = _grid.Rows.Add(NoteValues(filtered[i], i));
                    _grid.Rows[index].Tag = filtered[i];
                    DecorateNote(_grid.Rows[index], filtered[i]);
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
        }
        finally { _grid.ResumeLayout(); _updatingNoteGrid = false; }
        int linked = lengthRows.Count(r => r.State == "Đã liên kết");
        int missing = lengthRows.Count(r => r.State == "Mất liên kết");
        int plain = lengthRows.Count(r => r.State == "Ghi chú thường");
        UpdateChips(lengthRows.Count, linked, missing, plain, filtered.Count);
        _lengthEmpty.Text = lengthRows.Count == 0
            ? "CHƯA CÓ GHI CHÚ\nTạo MLeader mới để bắt đầu."
            : "Không tìm thấy ghi chú phù hợp bộ lọc.";
        _lengthEmpty.Visible = filtered.Count == 0;
        if (_lengthEmpty.Visible) _lengthEmpty.BringToFront();
        else _grid.BringToFront();
    }

    private NoteRow? CurrentRow => _grid.SelectedRows.Count > 0 ? _grid.SelectedRows[0].Tag as NoteRow : null;
    private GcRow? CurrentGc => _gcGrid.SelectedRows.Count > 0 ? _gcGrid.SelectedRows[0].Tag as GcRow : null;

    internal bool SelectHandle(string documentName, string handle)
    {
        if (IsDisposed || !string.Equals(_activeDoc, documentName, StringComparison.OrdinalIgnoreCase)) return false;
        var note = _all.FirstOrDefault(r => r.Handle.Equals(handle, StringComparison.OrdinalIgnoreCase));
        if (note == null) return false;
        bool isGc = note.Link?.IsGc == true;
        int tabIndex = isGc ? 1 : 0;
        if (_selectedPage != _navButtons[tabIndex].Tag) _navButtons[tabIndex].PerformClick();
        if (!isGc)
        {
            _search.Text = "";
            _status.SelectedIndex = 0;
            _layer.SelectedIndex = 0;
            FilterRows();
        }
        var grid = isGc ? _gcNotesGrid : _grid;
        foreach (DataGridViewRow row in grid.Rows)
        {
            if ((row.Tag as NoteRow)?.Handle.Equals(handle, StringComparison.OrdinalIgnoreCase) != true) continue;
            grid.ClearSelection();
            row.Selected = true;
            grid.CurrentCell = row.Cells[0];
            if (row.Index >= 0) grid.FirstDisplayedScrollingRowIndex = row.Index;
            if (isGc) UpdateGcDetails();
            return true;
        }
        return false;
    }

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
        // VNROMANS notes are edited in Unicode in the palette and stored as TCVN3.
        if (note.Tcvn3 && text != null) text = Tcvn3.FromUnicode(text, out _);
        Commands.PendingNoteEdits.Enqueue((doc.Name, note.Handle, text, millimeters));
        doc.SendStringToExecute("LLMEDITNOTE\n", true, false, false);
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
            MessageBox.Show("Hãy đăng ký DIM/LINE/PLINE/ARC/SPLINE/ELLIPSE/MLINE và chọn mã GC trong danh sách.", "LiveLength");
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
            // UTF-16 + tab: Excel keeps Vietnamese text and splits columns on any locale.
            // A "sep=" line would make Excel ignore the BOM and garble diacritics.
            sb.AppendLine("STT\tNội dung\tChiều dài 1\tChiều dài 2\tĐơn vị\tTrạng thái");
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
                sb.AppendLine(string.Join("\t", cols.Select(Csv)));
            }
            System.IO.File.WriteAllText(dialog.FileName, sb.ToString(), Encoding.Unicode);
            MessageBox.Show($"Đã xuất {i} ghi chú:\n{dialog.FileName}", "LiveLength");
        }
        catch (System.Exception ex) { MessageBox.Show(ex.Message, "Không thể xuất CSV"); }
    }

    private static string Csv(string s) => "\"" + s.Replace("\"", "\"\"") + "\"";
}
