using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace LiveLengthManager;

internal static class LengthData
{
    internal const string AppName = "LLEN2026";
    internal const string Token = "{LEN}";
    internal const string SecondToken = "{LEN2}";
    internal const double DefaultFactor = 0.001;
    internal const int DefaultDecimals = 2;
    internal static bool HasLengthField(string text) => Regex.IsMatch(text,
        @"(?:(?<![A-Za-z0-9_])|(?<=\\P))L\s*=",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex ValueRegex = new(
        @"(?:(?<![A-Za-z0-9_])|(?<=\\P))L\s*=\s*(?<num>\?\?|[-+]?(?:\d+(?:[.,]\d*)?|[.,]\d+))",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex DuplicateTailRegex = new(
        @"(?<empty>\\P[ \t]*L[ \t]*=[ \t]*)\\P[ \t]*L[ \t]*=[ \t]*(?:\?\?|[-+]?(?:\d+(?:[.,]\d*)?|[.,]\d+))[ \t]*m[ \t]*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    internal static bool IsNote(Entity e) => e is MText || e is MLeader ml && ml.ContentType == ContentType.MTextContent || e is DBText;
    internal static bool IsSource(Entity e) => e is Line or Polyline or Polyline2d or Polyline3d or Arc or Spline or Ellipse
        or Mline or AlignedDimension or RotatedDimension;

    internal static bool HasAppData(Entity e)
    {
        using var buffer = e.GetXDataForApplication(AppName);
        return buffer != null;
    }

    internal static string? ReadText(Entity e)
    {
        return e switch
        {
            MText m => m.Contents,
            MLeader ml when ml.ContentType == ContentType.MTextContent => ml.MText?.Contents,
            DBText t => t.TextString,
            _ => null
        };
    }

    internal static bool WriteText(Entity e, string text)
    {
        switch (e)
        {
            case MText m: m.Contents = text; return true;
            case MLeader ml when ml.ContentType == ContentType.MTextContent:
                // MLeader.MText returns a copy: assign it back after editing.
                var height = ml.TextHeight;
                using (var mtext = ml.MText)
                {
                    if (mtext == null) return false;
                    mtext.Contents = text;
                    ml.MText = mtext;
                }
                ml.TextHeight = height;
                return true;
            case DBText t: t.TextString = text; return true;
            default: return false;
        }
    }

    internal static LinkData? ReadLink(Entity e)
    {
        using var buffer = e.GetXDataForApplication(AppName);
        if (buffer == null) return null;
        string? handle = null;
        string secondaryHandle = "";
        double? factor = null;
        int? decimals = null;
        bool templateMode = false;
        bool isGc = false;
        bool millimeters = false;
        string gcLabel = "";
        var template = new StringBuilder();
        foreach (var tv in buffer.AsArray())
        {
            if (tv.TypeCode == 1000 && tv.Value is string value)
            {
                if (!templateMode && value.StartsWith("H:", StringComparison.Ordinal))
                    handle = value[2..];
                else if (!templateMode && value.StartsWith("H2:", StringComparison.Ordinal))
                    secondaryHandle = value[3..];
                else if (!templateMode && value == "MODE:GC")
                    isGc = true;
                else if (!templateMode && value == "UNIT:MM")
                    millimeters = true;
                else if (!templateMode && value.StartsWith("GC:", StringComparison.Ordinal))
                    gcLabel = value[3..];
                else if (!templateMode && value == "TEMPLATE:")
                    templateMode = true;
                else if (templateMode)
                    template.Append(value);
            }
            else if (tv.TypeCode == 1040 && tv.Value != null)
                factor = Convert.ToDouble(tv.Value, CultureInfo.InvariantCulture);
            else if (tv.TypeCode == 1070 && tv.Value != null)
                decimals = Convert.ToInt32(tv.Value, CultureInfo.InvariantCulture);
        }
        if (string.IsNullOrWhiteSpace(handle) || factor == null || decimals == null ||
            decimals is < 0 or > 8 || factor <= 0 || !template.ToString().Contains(Token, StringComparison.Ordinal)) return null;
        return new LinkData { SourceHandle = handle, Factor = factor.Value, Decimals = decimals.Value,
            Template = template.ToString(), IsGc = isGc, GcLabel = gcLabel, Millimeters = millimeters,
            SecondarySourceHandle = secondaryHandle };
    }

    internal static bool IsPlainNote(Entity e)
    {
        using var buffer = e.GetXDataForApplication(AppName);
        return buffer?.AsArray().Any(v => v.TypeCode == 1000 &&
            string.Equals(v.Value as string, "MODE:PLAIN", StringComparison.Ordinal)) == true;
    }

    internal static void MarkPlain(Entity e)
    {
        using var buffer = new ResultBuffer(new TypedValue(1001, AppName),
            new TypedValue(1000, "MODE:PLAIN"));
        e.XData = buffer;
    }

    internal static void EnsureRegApp(Database db, Transaction tr)
    {
        var table = (RegAppTable)tr.GetObject(db.RegAppTableId, OpenMode.ForRead);
        if (table.Has(AppName)) return;
        table.UpgradeOpen();
        var record = new RegAppTableRecord { Name = AppName };
        table.Add(record);
        tr.AddNewlyCreatedDBObject(record, true);
    }

    internal static void WriteLink(Entity e, LinkData data)
    {
        var values = new List<TypedValue>
        {
            new(1001, AppName),
            new(1000, "H:" + data.SourceHandle),
            new(1040, data.Factor),
            new(1070, (short)data.Decimals)
        };
        if (data.Millimeters) values.Add(new TypedValue(1000, "UNIT:MM"));
        if (data.SecondarySourceHandle.Length > 0)
            values.Add(new TypedValue(1000, "H2:" + data.SecondarySourceHandle));
        if (data.IsGc)
        {
            values.Add(new TypedValue(1000, "MODE:GC"));
            values.Add(new TypedValue(1000, "GC:" + data.GcLabel));
        }
        values.Add(new TypedValue(1000, "TEMPLATE:"));
        // Each 50 UTF-16 code units is at most 200 UTF-8 bytes (XData string <=255 bytes).
        for (var i = 0; i < data.Template.Length; i += 50)
            values.Add(new TypedValue(1000, data.Template.Substring(i, Math.Min(50, data.Template.Length - i))));
        using var buffer = new ResultBuffer(values.ToArray());
        e.XData = buffer;
    }

    internal static void Unlink(Entity e)
    {
        // Setting an app-only ResultBuffer clears this application's XData.
        using var buffer = new ResultBuffer(new TypedValue(1001, AppName));
        e.XData = buffer;
    }

    internal static string TemplateFor(string current, bool singleLine, bool millimeters = false)
    {
        if (current.Contains(Token, StringComparison.Ordinal)) return EnsureUnits(current, millimeters);
        var replaced = ValueRegex.Replace(current, m =>
        {
            var num = m.Groups["num"];
            return m.Value[..(num.Index - m.Index)] + Token + m.Value[(num.Index - m.Index + num.Length)..];
        });
        // "L = " with empty value.
        var withEmptyValues = Regex.Replace(replaced,
            @"(?:(?<![A-Za-z0-9_])|(?<=\\P))L\s*=\s*(?=(?:\\P|$|\n))",
            m => m.Value + Token,
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (withEmptyValues.Contains(Token, StringComparison.Ordinal)) return EnsureUnits(withEmptyValues, millimeters);
        return current;
    }

    internal static string EnsureUnits(string template, bool millimeters) => Regex.Replace(template,
        @"(?<field>(?:(?<![A-Za-z0-9_])|(?<=\\P))L\s*=\s*\{LEN2?\})(?:[ \t]*(?:mm|m)\b)*",
        m => m.Groups["field"].Value + (millimeters ? " mm" : " m"),
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    internal static string CollapseDuplicateLength(string current) =>
        DuplicateTailRegex.Replace(current, m => m.Groups["empty"].Value + " ?? m");

    internal static string RemoveGcCode(string text, string code)
    {
        if (string.IsNullOrWhiteSpace(code) || !text.StartsWith(code, StringComparison.OrdinalIgnoreCase)) return text;
        var tail = text[code.Length..];
        if (tail.StartsWith("\\P", StringComparison.OrdinalIgnoreCase)) return tail[2..];
        if (tail.StartsWith(",", StringComparison.Ordinal)) return tail[1..].TrimStart();
        return text;
    }

    internal static string FormatLength(double length, double factor, int decimals) =>
        (length * factor).ToString("F" + Math.Clamp(decimals, 0, 8), CultureInfo.InvariantCulture);

    internal static string NormalizeSlots(string template, bool hasSecondSource)
    {
        if (!hasSecondSource) return template.Replace(SecondToken, Token, StringComparison.Ordinal);
        var first = template.IndexOf(Token, StringComparison.Ordinal);
        if (first < 0) return template;
        var second = template.IndexOf(Token, first + Token.Length, StringComparison.Ordinal);
        return second < 0 ? template : template[..second] + SecondToken + template[(second + Token.Length)..];
    }

    internal static string Render(LinkData link, double? length, double? secondaryLength = null)
    {
        var value = length.HasValue ? link.Millimeters
            ? Math.Round(length.Value * link.Factor * 1000d, 0, MidpointRounding.AwayFromZero).ToString("F0", CultureInfo.InvariantCulture)
            : FormatLength(length.Value, link.Factor, link.Decimals) : "??";
        if (link.IsGc && length.HasValue && value.Contains('.')) value = value.TrimEnd('0').TrimEnd('.');
        var secondaryValue = secondaryLength.HasValue ? link.Millimeters
            ? Math.Round(secondaryLength.Value * link.Factor * 1000d, 0, MidpointRounding.AwayFromZero).ToString("F0", CultureInfo.InvariantCulture)
            : FormatLength(secondaryLength.Value, link.Factor, link.Decimals) : "??";
        return link.Template.Replace(SecondToken, secondaryValue, StringComparison.Ordinal)
            .Replace(Token, value, StringComparison.Ordinal);
    }

    internal static ObjectId Resolve(Database db, string handle)
    {
        if (string.IsNullOrWhiteSpace(handle) || !long.TryParse(handle, NumberStyles.HexNumber,
                CultureInfo.InvariantCulture, out long value)) return ObjectId.Null;
        try { return db.GetObjectId(false, new Handle(value), 0); }
        catch { return ObjectId.Null; }
    }

    internal static double? GetLength(Transaction tr, Database db, string handle)
    {
        var id = Resolve(db, handle);
        if (id.IsNull || id.IsErased) return null;
        try
        {
            var source = tr.GetObject(id, OpenMode.ForRead, false);
            if (source is AlignedDimension or RotatedDimension)
                return ((Dimension)source).Measurement;
            if (source is Mline mline)
            {
                double total = 0;
                for (int i = 1; i < mline.NumberOfVertices; i++)
                    total += mline.VertexAt(i - 1).DistanceTo(mline.VertexAt(i));
                if (mline.IsClosed && mline.NumberOfVertices > 2)
                    total += mline.VertexAt(mline.NumberOfVertices - 1).DistanceTo(mline.VertexAt(0));
                return total;
            }
            if (source is not Curve curve || !IsSource(curve)) return null;
            return curve.GetDistanceAtParameter(curve.EndParam) - curve.GetDistanceAtParameter(curve.StartParam);
        }
        catch { return null; }
    }

    internal static Point3d GetPosition(Entity e, out bool valid)
    {
        try
        {
            var ext = e.GeometricExtents;
            valid = true;
            return new Point3d((ext.MinPoint.X + ext.MaxPoint.X) / 2,
                (ext.MinPoint.Y + ext.MaxPoint.Y) / 2,
                (ext.MinPoint.Z + ext.MaxPoint.Z) / 2);
        }
        catch
        {
            valid = false;
            return Point3d.Origin;
        }
    }

    internal static string Preview(string source)
    {
        var value = source.Replace("\\P", " | ", StringComparison.OrdinalIgnoreCase)
            .Replace("\r", " ").Replace("\n", " ").Trim();
        return value.Length > 135 ? value[..132] + "..." : value;
    }
}
