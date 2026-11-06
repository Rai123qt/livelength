using System.Drawing;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Windows;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace LiveLengthManager;

internal static class PaletteHost
{
    private static PaletteSet? _palette;
    private static ManagerPanel? _panel;
    private static TrayItem? _tray;
    internal static bool IsVisible => _palette?.Visible == true;
    private static string Title => "LiveLength Manager 2027  v" + Updater.CurrentText;
    internal static event Action? CollapsedChanged;
    // AutoRollUp only works on a floating palette set; docked auto-hide (anchoring) cannot be
    // switched on from .NET, and a docked width change is ignored. Docked, collapsing hides the
    // palette; AutoCAD's own auto-hide button on the docked title bar folds it into the edge.
    internal static bool Collapsed
    {
        get { try { return _palette?.AutoRollUp == true; } catch (System.Exception) { return false; } }
        set
        {
            if (_palette == null) return;
            if (value) Collapse();
            else Expand();
            CollapsedChanged?.Invoke();
        }
    }

    private static bool Docked
    {
        get { try { return _palette!.Dock != DockSides.None; } catch (System.Exception) { return false; } }
    }

    private static void Collapse()
    {
        if (!Docked)
        {
            try
            {
                _palette!.AutoRollUp = true;
                return;
            }
            catch (System.Exception ex) { PluginLog.Write("auto-roll-up: " + ex.Message); }
        }
        try { _palette!.Visible = false; }
        catch (System.Exception ex) { PluginLog.Write("hide: " + ex.Message); return; }
        AcadApp.DocumentManager.MdiActiveDocument?.Editor.WriteMessage(
            "\nLiveLength: đã ẩn bảng, L= vẫn tự cập nhật. Gõ LLMGR (hoặc LLMTOGGLE) để mở lại." +
            "\nMuốn bảng thu vào cạnh màn hình khi đang dính cạnh: bấm nút tự ẩn (auto-hide) trên thanh tiêu đề của bảng.");
    }

    // Each step is independent so one failing call can never leave the palette unusable.
    private static void Expand()
    {
        try { if (_palette!.AutoRollUp) _palette.AutoRollUp = false; }
        catch (System.Exception ex) { PluginLog.Write("expand roll-up: " + ex.Message); }
        try { if (!_palette!.Visible) _palette.Visible = true; }
        catch (System.Exception ex) { PluginLog.Write("expand show: " + ex.Message); }
    }

    internal static void Show()
    {
        if (_palette == null)
        {
            _panel = new ManagerPanel();
            _palette = new PaletteSet(Title, new Guid("366D5A54-CDA7-40CC-9149-E2710EE64021"))
            {
                Style = PaletteSetStyles.ShowAutoHideButton |
                        PaletteSetStyles.ShowCloseButton |
                        PaletteSetStyles.Snappable,
                MinimumSize = new Size(430, 430),
                Size = new Size(660, 710),
                DockEnabled = DockSides.Left | DockSides.Right
            };
            _panel.Dock = System.Windows.Forms.DockStyle.Fill;
            _palette.StateChanged += (_, _) => { try { CollapsedChanged?.Invoke(); } catch (System.Exception) { } };
            // Restoring the saved layout also restores the saved title; put the version back afterwards.
            _palette.Load += (_, _) => SetTitle();
            _palette.Add("LiveLength", _panel);
        }
        _palette.Visible = true;
        // AutoCAD restores the saved title (by GUID) on first show, so the version is set after it.
        SetTitle();
        try { EnsureTray(); } catch (System.Exception ex) { PluginLog.Write("tray: " + ex.Message); }
        var doc = AcadApp.DocumentManager.MdiActiveDocument;
        if (doc != null)
        {
            try
            {
                using (doc.LockDocument()) Refresh(doc);
                SelectImplied(doc);
            }
            catch (System.Exception) { }
        }
    }

    private static void SetTitle()
    {
        try { if (_palette != null && _palette.Name != Title) _palette.Name = Title; }
        catch (System.Exception ex) { PluginLog.Write("title: " + ex.Message); }
    }

    internal static void Toggle()
    {
        if (IsVisible) _palette!.Visible = false;
        else Show();
    }

    // Status-bar icon: hides/shows the palette. Live L= updates keep running while hidden.
    internal static void EnsureTray()
    {
        if (_tray != null) return;
        var item = new TrayItem
        {
            ToolTipText = $"LiveLength Manager v{Updater.CurrentText} — bấm để hiện/ẩn bảng (L= vẫn tự cập nhật khi ẩn)",
            Icon = CreateIcon(),
            Visible = true
        };
        item.MouseDown += (_, _) =>
        {
            try { Toggle(); } catch (System.Exception) { }
        };
        AcadApp.StatusBar.TrayItems.Add(item);
        AcadApp.StatusBar.Update();
        _tray = item;
        PluginLog.Write("tray icon added");
    }

    private static Icon CreateIcon()
    {
        using var bitmap = new Bitmap(16, 16);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            using var back = new SolidBrush(Color.FromArgb(35, 99, 150));
            g.FillRectangle(back, 1, 1, 14, 14);
            using var font = new Font("Segoe UI", 8.5f, FontStyle.Bold);
            using var white = new SolidBrush(Color.White);
            g.DrawString("L", font, white, new RectangleF(0, 0, 16, 16),
                new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center });
        }
        return Icon.FromHandle(bitmap.GetHicon());
    }

    internal static void Refresh(Document doc)
    {
        if (_panel == null || !IsVisible) return;
        _panel.SetRows(doc, LiveEngine.GetRows(doc));
    }

    internal static void Refresh(Document doc, PaletteDelta delta)
    {
        if (_panel == null || !IsVisible || delta.IsEmpty) return;
        // Partial refresh reads only the affected notes / GC sources and patches grid rows.
        if (delta.Full || !_panel.ApplyDelta(doc, delta.Notes, LiveEngine.GetRowsFor(doc, delta.Notes),
                delta.GcSources, GcData.GetRowsFor(doc, delta.GcSources)))
            Refresh(doc);
    }

    internal static void SelectImplied(Document doc)
    {
        if (_panel == null || !IsVisible) return;
        var selection = doc.Editor.SelectImplied();
        if (selection.Status != Autodesk.AutoCAD.EditorInput.PromptStatus.OK) return;
        foreach (var id in selection.Value.GetObjectIds())
        {
            if (!id.IsNull && !id.IsErased && _panel.SelectHandle(doc.Name, id.Handle.ToString()))
                return;
        }
    }

    internal static void ClearDocument(string name)
    {
        _panel?.ClearDocument(name);
    }

}
