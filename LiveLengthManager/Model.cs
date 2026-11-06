using Autodesk.AutoCAD.Geometry;
using System.Globalization;

namespace LiveLengthManager;

internal sealed class LinkData
{
    public string SourceHandle = "";
    public string SecondarySourceHandle = "";
    public double Factor = 0.001;
    public int Decimals = 2;
    public bool Millimeters;
    public string Template = "";
    public bool IsGc;
    public string GcLabel = "";
}

internal sealed class GcRow
{
    public string Name = "";
    public string Description = "";
    public string SourceType = "";
    public string Handle = "";
    public double LengthMeters;
    public override string ToString() => Name + (Description.Length > 0 ? " — " + Description : "") + ", L=" +
        LengthMeters.ToString("0.##", CultureInfo.InvariantCulture) + "m";
}

internal sealed class NoteRow
{
    public string Handle = "";
    public string Type = "";
    public string Description = "";
    public string CurrentText = "";
    public string Layer = "";
    public string Layout = "";
    public Point3d Position = Point3d.Origin;
    public bool HasPosition;
    public bool Linked;
    public string SourceHandle = "";
    public double? LengthMeters;
    public double? SecondaryLengthMeters;
    public double? DrawingLength;
    public string State = "";
    public LinkData? Link;
}
