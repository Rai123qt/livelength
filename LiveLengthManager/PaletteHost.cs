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
    private const int CompactWidth = 44;
    private static readonly Size ExpandedMinimum = new(430, 430);
    private static bool _compact;
    private static int _expandedWidth = 660;

    // AutoRollUp only works for a floating palette set, and docked auto-hide (anchoring) cannot be
    // set from .NET. Docked, "collapse" therefore narrows the palette to a thin strip instead.
    internal static bool Collapsed
    {
        get
        {
            if (_compact) return true;
            try { return _palette?.AutoRollUp == true; } catch (System.Exception) { return false; }
        }
        set
        {
            if (_palette == null || _panel == null) return;
            try
            {
                if (value) Collapse();
                else Expand();
            }
            catch (System.Exception ex) { PluginLog.Write("collapse: " + ex.Message); }
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
            _palette!.AutoRollUp = true;
            return;
        }
        _expandedWidth = Math.Max(ExpandedMinimum.Width, _palette!.Size.Width);
        _compact = true;
        _panel!.SetCompact(true);
        _palette.MinimumSize = new Size(CompactWidth, 200);
        _palette.Size = new Size(CompactWidth, _palette.Size.Height);
        if (_palette.Size.Width > CompactWidth + 80)
        {
            // This AutoCAD build ignored the docked width: hide instead and say how to come back.
            PluginLog.Write($"docked resize ignored (width {_palette.Size.Width}); hiding palette");
            Expand();
            _palette.Visible = false;
            AcadApp.DocumentManager.MdiActiveDocument?.Editor.WriteMessage(
                "\nLiveLength: đã ẩn bảng (L= vẫn tự cập nhật). Gõ LLMGR hoặc LLMTOGGLE để mở lại.");
        }
    }

    private static void Expand()
    {
        _palette!.AutoRollUp = false;
        if (!_compact) return;
        _compact = false;
        _panel!.SetCompact(false);
        _palette.MinimumSize = ExpandedMinimum;
        _palette.Size = new Size(_expandedWidth, _palette.Size.Height);
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
