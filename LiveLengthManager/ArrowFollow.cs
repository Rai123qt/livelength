using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace LiveLengthManager;

// Keeps MLeader arrowheads on their source after the source is stretched, trimmed or moved.
// Each arrowhead remembers its relative position (0..1) along the source; when the source
// changes and the tip is no longer on it, the tip is moved to the same relative position.
internal static class ArrowFollow
{
    private readonly record struct Anchor(double Fraction, double Offset);
    private static readonly Dictionary<Database, Dictionary<ObjectId, Dictionary<int, Anchor>>> Anchors = new();

    private static string SettingFile => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "LiveLengthManager2027", "arrow-follow.txt");

    private static bool? _enabled;
    internal static bool Enabled
    {
        get
        {
            if (_enabled == null)
            {
                try { _enabled = System.IO.File.ReadAllText(SettingFile).Trim() != "off"; }
                catch { _enabled = true; }
            }
            return _enabled.Value;
        }
        set
        {
            _enabled = value;
            try
            {
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(SettingFile)!);
                System.IO.File.WriteAllText(SettingFile, value ? "on" : "off");
            }
            catch { }
        }
    }

    internal static void Forget(Database db) => Anchors.Remove(db);
    internal static void Clear() => Anchors.Clear();

    /// <summary>Records arrow anchors and, when <paramref name="sourceChanged"/>, moves detached tips.
    /// Returns true when the leader was modified.</summary>
    internal static bool Update(Database db, MLeader leader, Entity? source, bool sourceChanged)
    {
        if (!Anchors.TryGetValue(db, out var notes)) Anchors[db] = notes = new();
        if (source == null || !TryMeasure(source, out var length) || length < 1e-9)
        {
            notes.Remove(leader.ObjectId);
            return false;
        }
        notes.TryGetValue(leader.ObjectId, out var previous);
        var current = new Dictionary<int, Anchor>();
        var moved = false;
        var epsilon = Math.Max(length * 1e-6, 1e-6);
        foreach (int leaderIndex in leader.GetLeaderIndexes())
        foreach (int line in leader.GetLeaderLineIndexes(leaderIndex))
        {
            var tip = leader.GetFirstVertex(line);
            if (!TryProject(source, tip, out var along, out var distance)) continue;
            if (Enabled && sourceChanged && previous != null && previous.TryGetValue(line, out var anchor) &&
                distance > anchor.Offset + epsilon)
            {
                if (!leader.IsWriteEnabled) leader.UpgradeOpen();
                leader.SetFirstVertex(line, PointAt(source, anchor.Fraction * length));
                current[line] = anchor with { Offset = 0 };
                moved = true;
                continue;
            }
            // Only arrows placed on (or right next to) the source are followed.
            if (distance <= Math.Max(length * 0.02, 1e-3))
                current[line] = new Anchor(along / length, distance);
        }
        if (current.Count > 0) notes[leader.ObjectId] = current;
        else notes.Remove(leader.ObjectId);
        return moved;
    }

    private static bool TryMeasure(Entity source, out double length)
    {
        length = 0;
        if (source is Curve curve)
        {
            length = curve.GetDistanceAtParameter(curve.EndParam) - curve.GetDistanceAtParameter(curve.StartParam);
            return true;
        }
        if (source is not Mline mline || mline.NumberOfVertices < 2) return false;
        foreach (var (a, b) in Segments(mline)) length += a.DistanceTo(b);
        return true;
    }

    private static bool TryProject(Entity source, Point3d point, out double along, out double distance)
    {
        along = 0;
        distance = double.MaxValue;
        try
        {
            if (source is Curve curve)
            {
                var closest = curve.GetClosestPointTo(point, false);
                along = curve.GetDistAtPoint(closest) - curve.GetDistanceAtParameter(curve.StartParam);
                distance = closest.DistanceTo(point);
                return true;
            }
            if (source is not Mline mline) return false;
            double walked = 0;
            foreach (var (a, b) in Segments(mline))
            {
                var direction = b - a;
                var segment = direction.Length;
                var t = segment < 1e-12 ? 0 : Math.Clamp((point - a).DotProduct(direction) / (segment * segment), 0, 1);
                var d = point.DistanceTo(a + direction * t);
                if (d < distance) { distance = d; along = walked + segment * t; }
                walked += segment;
            }
            return distance < double.MaxValue;
        }
        catch (System.Exception) { return false; }
    }

    private static Point3d PointAt(Entity source, double along)
    {
        if (source is Curve curve)
        {
            var start = curve.GetDistanceAtParameter(curve.StartParam);
            var end = curve.GetDistanceAtParameter(curve.EndParam);
            return curve.GetPointAtDist(Math.Clamp(start + along, start, end));
        }
        var mline = (Mline)source;
        Point3d last = mline.VertexAt(0);
        foreach (var (a, b) in Segments(mline))
        {
            var segment = a.DistanceTo(b);
            if (along <= segment && segment > 1e-12) return a + (b - a) * (along / segment);
            along -= segment;
            last = b;
        }
        return last;
    }

    private static IEnumerable<(Point3d, Point3d)> Segments(Mline mline)
    {
        for (int i = 1; i < mline.NumberOfVertices; i++)
            yield return (mline.VertexAt(i - 1), mline.VertexAt(i));
        if (mline.IsClosed && mline.NumberOfVertices > 2)
            yield return (mline.VertexAt(mline.NumberOfVertices - 1), mline.VertexAt(0));
    }
}
