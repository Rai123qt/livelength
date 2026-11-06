using System.Text.RegularExpressions;
using System.Text;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;

namespace LiveLengthManager;

internal static class GcData
{
    private const string AppName = "LLGC2027";
    private static readonly Regex NamePattern = new(@"^GC[1-9][0-9]*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    internal static bool IsRegistered(Entity e)
    {
        using var data = e.GetXDataForApplication(AppName);
        return data != null;
    }

    private static (string Name, string Description)? ReadEntry(Entity source)
    {
        using var data = source.GetXDataForApplication(AppName);
        if (data == null) return null;
        string? name = null;
        string description = "";
        foreach (var value in data.AsArray())
        {
            if (value.TypeCode != 1000 || value.Value is not string text) continue;
            if (name == null && NamePattern.IsMatch(text)) name = text.ToUpperInvariant();
            else if (text.StartsWith("D:", StringComparison.Ordinal)) description = text[2..];
        }
        return name == null ? null : (name, description);
    }

    internal static List<GcRow> GetRows(Document doc)
    {
        var result = new List<GcRow>();
        using var tr = doc.Database.TransactionManager.StartOpenCloseTransaction();
        var blocks = (BlockTable)tr.GetObject(doc.Database.BlockTableId, OpenMode.ForRead);
        foreach (ObjectId blockId in blocks)
        {
            var block = (BlockTableRecord)tr.GetObject(blockId, OpenMode.ForRead);
            if (!block.IsLayout) continue;
            foreach (ObjectId id in block)
            {
                try
                {
                    if (tr.GetObject(id, OpenMode.ForRead, false) is not Entity source ||
                        !LengthData.IsSource(source) || ReadEntry(source) is not { } entry) continue;
                    var length = LengthData.GetLength(tr, doc.Database, source.Handle.ToString());
                    if (length == null) continue;
                    result.Add(new GcRow
                    {
                        Name = entry.Name,
                        Description = entry.Description,
                        SourceType = source.GetType().Name.ToUpperInvariant(),
                        Handle = source.Handle.ToString(),
                        LengthMeters = length.Value * LengthData.DefaultFactor
                    });
                }
                catch (System.Exception) { }
            }
        }
        tr.Commit();
        return result.OrderBy(r => int.TryParse(r.Name[2..], out var number) ? number : int.MaxValue)
            .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    internal static string NextName(IEnumerable<GcRow> rows)
    {
        var numbers = rows.Select(r => int.TryParse(r.Name[2..], out var number) ? number : 0).ToHashSet();
        int next = 1;
        while (numbers.Contains(next)) next++;
        return "GC" + next;
    }

    internal static string BuildTemplate(string name, string description) =>
        (string.IsNullOrWhiteSpace(description) ? "" : description.Trim() + "\\P") +
        "L = " + LengthData.Token + " m";

    internal static bool Register(Document doc, ObjectId sourceId, string name, string description, out string message) =>
        Save(doc, sourceId, null, name, description, out message);

    internal static bool Update(Document doc, ObjectId sourceId, string oldName,
        string name, string description, out string message) =>
        Save(doc, sourceId, oldName, name, description, out message);

    private static bool Save(Document doc, ObjectId sourceId, string? oldName,
        string name, string description, out string message)
    {
        name = name.Trim().ToUpperInvariant();
        description = description.Trim();
        if (description.Length > 180 || Encoding.UTF8.GetByteCount("D:" + description) > 250)
        {
            message = "Mô tả dài tối đa 180 ký tự.";
            return false;
        }
        if (!NamePattern.IsMatch(name))
        {
            message = "Mã phải có dạng GC1, GC2, GC3...";
            return false;
        }
        var rows = GetRows(doc);
        if (oldName != null && !rows.Any(row =>
                LengthData.Resolve(doc.Database, row.Handle) == sourceId &&
                row.Name.Equals(oldName, StringComparison.OrdinalIgnoreCase)))
        {
            message = "Mã GC đã thay đổi. Hãy quét lại bảng trước khi sửa.";
            return false;
        }
        if (rows.Any(row => LengthData.Resolve(doc.Database, row.Handle) == sourceId &&
                            !row.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) && oldName == null)
        {
            message = "Đối tượng này đã có mã GC. Hãy sửa mô tả của mã hiện có.";
            return false;
        }
        if (rows.Any(row => row.Name.Equals(name, StringComparison.OrdinalIgnoreCase) &&
                            LengthData.Resolve(doc.Database, row.Handle) != sourceId))
        {
            message = name + " đã được gán cho đối tượng khác.";
            return false;
        }
        try
        {
            using var tr = doc.Database.TransactionManager.StartTransaction();
            if (tr.GetObject(sourceId, OpenMode.ForRead, false) is not Entity source || !LengthData.IsSource(source))
            {
                message = "Chỉ hỗ trợ LINE, PLINE, ARC, SPLINE, ELLIPSE, MLINE hoặc DIM tuyến tính.";
                return false;
            }
            var apps = (RegAppTable)tr.GetObject(doc.Database.RegAppTableId, OpenMode.ForRead);
            if (!apps.Has(AppName))
            {
                apps.UpgradeOpen();
                var app = new RegAppTableRecord { Name = AppName };
                apps.Add(app);
                tr.AddNewlyCreatedDBObject(app, true);
            }
            source.UpgradeOpen();
            using var data = new ResultBuffer(new TypedValue(1001, AppName),
                new TypedValue(1000, name), new TypedValue(1000, "D:" + description));
            source.XData = data;
            tr.Commit();
            LiveEngine.MarkDirty(doc);
            message = "Đã lưu " + name + " vào " + source.GetType().Name + ". Ghi chú GC sẽ theo chiều dài này.";
            return true;
        }
        catch (System.Exception ex)
        {
            message = ex.Message;
            return false;
        }
    }
}
