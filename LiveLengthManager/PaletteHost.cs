using System.Drawing;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Windows;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace LiveLengthManager;

internal static class PaletteHost
{
    private static PaletteSet? _palette;
    private static ManagerPanel? _panel;
    internal static bool IsVisible => _palette?.Visible == true;

    internal static void Show()
    {
        if (_palette == null)
        {
            _panel = new ManagerPanel();
            _palette = new PaletteSet("LiveLength Manager 2027", new Guid("366D5A54-CDA7-40CC-9149-E2710EE64021"))
            {
                Style = PaletteSetStyles.ShowAutoHideButton |
                        PaletteSetStyles.ShowCloseButton |
                        PaletteSetStyles.Snappable,
                MinimumSize = new Size(430, 430),
                Size = new Size(660, 710),
                DockEnabled = DockSides.Left | DockSides.Right
            };
            _panel.Dock = System.Windows.Forms.DockStyle.Fill;
            _palette.Add("LiveLength", _panel);
        }
        _palette.Visible = true;
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

    internal static void Refresh(Document doc)
    {
        if (_panel == null || !IsVisible) return;
        _panel.SetRows(doc, LiveEngine.GetRows(doc));
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
