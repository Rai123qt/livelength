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
    private static string Title => "LiveLength Manager 2027 · v" + Updater.CurrentText;
    internal static event Action? CollapsedChanged;

    /// <summary>Auto-hide: docked, the palette folds into a tab on the screen edge and opens on hover;
    /// floating, it rolls up to its title bar. L= keeps updating either way.</summary>
    internal static bool Collapsed
    {
        get { try { return _palette?.AutoRollUp == true; } catch (System.Exception) { return false; } }
        set
        {
            if (_palette == null) return;
            try { _palette.AutoRollUp = value; }
            catch (System.Exception ex) { PluginLog.Write("auto-hide: " + ex.Message); }
            CollapsedChanged?.Invoke();
        }
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
            _palette.Add("LiveLength", _panel);
        }
        // AutoCAD restores a palette set's saved title by GUID; set it so the version is current.
        try { _palette.Name = Title; } catch (System.Exception) { }
        _palette.Visible = true;
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
