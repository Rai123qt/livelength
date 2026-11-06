using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using System.Text.RegularExpressions;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Application;

[assembly: ExtensionApplication(typeof(LiveLengthManager.Plugin))]
[assembly: CommandClass(typeof(LiveLengthManager.Commands))]

namespace LiveLengthManager;

public sealed class Plugin : IExtensionApplication
{
    public void Initialize()
    {
        LiveEngine.Start();
    }
    public void Terminate()
    {
        LiveEngine.Stop();
    }
}

public sealed class Commands
{
    // Palette operations queue a normal AutoCAD command. This makes GetEntity,
    // zoom, undo and DB writes execute in a valid document command context.
    internal static string PendingHandle = "";
    internal static string PendingDocName = "";
    internal static string PendingGcHandle = "";
    internal static string PendingGcDocName = "";
    internal static readonly Queue<(string DocName, string Handle, string OldName, string NewName, string Description)>
        PendingGcEdits = new();
    internal static readonly Queue<(string DocName, string Handle, string? Text, bool? Millimeters)>
        PendingNoteEdits = new();

    [CommandMethod("LLMEDITNOTE", CommandFlags.Modal)]
    public void EditNoteFromPalette()
    {
        var doc = AcadApp.DocumentManager.MdiActiveDocument;
        if (doc == null || PendingNoteEdits.Count == 0) return;
        var edit = PendingNoteEdits.Dequeue();
        if (edit.DocName != doc.Name) return;
        var edited = LiveEngine.EditNote(doc, edit.Handle, edit.Text, edit.Millimeters, out var message);
        if (!edited) doc.Editor.WriteMessage("\nLiveLength: " + message);
        else if (edit.Text != null && LengthData.HasLengthField(edit.Text))
        {
            var id = LengthData.Resolve(doc.Database, edit.Handle);
            using var tr = doc.Database.TransactionManager.StartTransaction();
            var shouldLink = !id.IsNull && tr.GetObject(id, OpenMode.ForRead, false) is MLeader leader &&
                LengthData.ReadLink(leader) == null && LengthData.IsPlainNote(leader);
            tr.Commit();
            if (shouldLink) LinkAtArrow(doc, id);
        }
        try { PaletteHost.Refresh(doc); } catch (System.Exception) { }
    }

    internal static bool PendingStyleVnromans;

    // Switches a note between TNR (Unicode) and VNROMANS (TCVN3), converting its text and
    // removing inline font overrides that would otherwise hide the new style's font.
    [CommandMethod("LLMSTYLE", CommandFlags.Modal)]
    public void ChangeNoteStyle()
    {
        var doc = AcadApp.DocumentManager.MdiActiveDocument;
        if (doc == null) return;
        var noteId = GetPendingNote(doc);
        if (noteId.IsNull) return;
        var toVn = PendingStyleVnromans;
        var target = toVn ? "VNROMANS (TCVN3)" : "TNR (Unicode)";
        var lossy = 0;
        try
        {
            var db = doc.Database;
            using var tr = db.TransactionManager.StartTransaction();
            if (tr.GetObject(noteId, OpenMode.ForRead, false) is not Entity note) return;
            if (LiveEngine.IsTcvn3(tr, note) == toVn)
            {
                doc.Editor.WriteMessage($"\nGhi chú đã dùng kiểu {target}.");
                return;
            }
            string Convert(string text)
            {
                text = Tcvn3.StripFontOverrides(text);
                if (!toVn) return Tcvn3.ToUnicode(text);
                var result = Tcvn3.FromUnicode(text, out var lost);
                lossy += lost;
                return result;
            }
            var textStyle = EnsureTextStyle(db, tr, toVn ? 1 : 0);
            var leaderStyle = EnsureMLeaderStyle(db, tr, toVn ? 1 : 0, textStyle);
            note.UpgradeOpen();
            var text = Convert(LengthData.ReadText(note) ?? "");
            var textLossy = lossy;
            switch (note)
            {
                case MLeader leader:
                    leader.MLeaderStyle = leaderStyle;
                    leader.TextStyleId = textStyle;
                    break;
                case MText mtext: mtext.TextStyleId = textStyle; break;
                case DBText dbText: dbText.TextStyleId = textStyle; break;
            }
            LengthData.WriteText(note, text);
            if (LengthData.ReadLink(note) is { IsGc: false } link)
            {
                link.Template = Convert(link.Template);
                LengthData.WriteLink(note, link);
                lossy = textLossy; // the template repeats the visible text; report it once
            }
            tr.Commit();
        }
        catch (System.Exception ex)
        {
            doc.Editor.WriteMessage("\nKhông đổi được kiểu chữ: " + ex.Message);
            return;
        }
        LiveEngine.MarkDirty(doc);
        doc.Editor.WriteMessage($"\nĐã đổi ghi chú sang kiểu {target}." +
            (lossy > 0 ? $" TCVN3 không có chữ hoa có dấu: {lossy} ký tự đã chuyển thành chữ thường." : ""));
        try { PaletteHost.Refresh(doc); } catch (System.Exception) { }
    }

    [CommandMethod("LLMGR", CommandFlags.Modal)]
    public void ShowManager()
    {
        PaletteHost.Show();
        if (PaletteHost.Collapsed) PaletteHost.Collapsed = false;
    }

    [CommandMethod("LLMTOGGLE", CommandFlags.Modal)]
    public void ToggleManager() => PaletteHost.Toggle();

    [CommandMethod("LLMMTEXT2LEADER", CommandFlags.Modal)]
    public void CreateLeaderFromMText()
    {
        var doc = AcadApp.DocumentManager.MdiActiveDocument;
        if (doc == null) return;
        var options = new PromptEntityOptions("\nChọn MText để sao chép nội dung sang MLeader: ");
        options.SetRejectMessage("\nHãy chọn MText.");
        options.AddAllowedClass(typeof(MText), true);
        var picked = doc.Editor.GetEntity(options);
        if (picked.Status != PromptStatus.OK) return;
        string content;
        string layer;
        ObjectId textStyleId;
        double textHeight;
        double textWidth;
        Autodesk.AutoCAD.Colors.Color textColor;
        ObjectId leaderStyleId;
        try
        {
            using var tr = doc.Database.TransactionManager.StartTransaction();
            var original = (MText)tr.GetObject(picked.ObjectId, OpenMode.ForRead);
            content = original.Contents;
            layer = original.Layer;
            textStyleId = original.TextStyleId;
            textHeight = original.TextHeight > 0 ? original.TextHeight : 2.0;
            textWidth = original.Width;
            textColor = original.Color;
            var defaultTextStyle = EnsureTextStyle(doc.Database, tr, 0);
            leaderStyleId = EnsureMLeaderStyle(doc.Database, tr, 0, defaultTextStyle);
            tr.Commit();
        }
        catch (System.Exception ex)
        {
            doc.Editor.WriteMessage("\nKhông đọc được MText: " + ex.Message);
            return;
        }
        if (string.IsNullOrWhiteSpace(content))
        {
            doc.Editor.WriteMessage("\nMText không có nội dung.");
            return;
        }
        var created = CreatePlacedMLeader(doc, leaderStyleId, textStyleId, layer,
            textHeight, textWidth, textColor, content, "\nChọn điểm mũi tên MLeader: ");
        if (created.IsNull) return;
        if (LengthData.HasLengthField(content)) LinkAtArrow(doc, created, promptIfMissing: true);
        else MarkPlainNote(doc, created);
        doc.Editor.WriteMessage("\nĐã tạo MLeader từ nội dung MText. MText gốc vẫn được giữ lại.");
        try { PaletteHost.Refresh(doc); } catch (System.Exception) { }
    }

    [CommandMethod("LLMSECOND", CommandFlags.Modal)]
    public void LinkSecondLength()
    {
        var doc = AcadApp.DocumentManager.MdiActiveDocument;
        if (doc == null) return;
        var note = GetPendingNote(doc);
        if (note.IsNull) note = PickNote(doc.Editor);
        if (note.IsNull) return;
        var source = PickSource(doc.Editor);
        if (source.IsNull) return;
        LiveEngine.BindSecond(doc, note, source, out var message);
        doc.Editor.WriteMessage("\n" + message);
        try { PaletteHost.Refresh(doc); } catch (System.Exception) { }
    }

    [CommandMethod("LLMCREATE", CommandFlags.Modal)]
    public void CreateNew() => CreateNewCore(true);

    [CommandMethod("LLMPLAIN", CommandFlags.Modal)]
    public void CreatePlain() => CreateNewCore(false);

    private static void CreateNewCore(bool linkLength)
    {
        var doc = AcadApp.DocumentManager.MdiActiveDocument;
        if (doc == null) return;
        var options = new PromptKeywordOptions("\nChọn kiểu chữ [TNR/VNROMANS] <TNR>: ") { AllowNone = true };
        options.Keywords.Add("TNR");
        options.Keywords.Add("VNROMANS");
        options.Keywords.Default = "TNR";
        var choice = doc.Editor.GetKeywords(options);
        if (choice.Status != PromptStatus.OK && choice.Status != PromptStatus.None) return;
        var selection = choice.StringResult == "VNROMANS" ? 1 : 0;
        var db = doc.Database;
        var previousStyle = db.MLeaderstyle;
        var spaceId = db.CurrentSpaceId;
        ObjectId chosenStyle;
        var existing = new HashSet<ObjectId>();
        try
        {
            using var tr = db.TransactionManager.StartTransaction();
            var tnrText = EnsureTextStyle(db, tr, 0);
            var shxText = EnsureTextStyle(db, tr, 1);
            var tnrLeader = EnsureMLeaderStyle(db, tr, 0, tnrText);
            var shxLeader = EnsureMLeaderStyle(db, tr, 1, shxText);
            chosenStyle = selection == 1 ? shxLeader : tnrLeader;
            var space = (BlockTableRecord)tr.GetObject(spaceId, OpenMode.ForRead);
            foreach (ObjectId id in space) existing.Add(id);
            tr.Commit();
        }
        catch (System.Exception ex) { doc.Editor.WriteMessage("\nKhông tạo được style: " + ex.Message); return; }
        try
        {
            db.MLeaderstyle = chosenStyle;
            doc.Editor.WriteMessage(linkLength
                ? "\nVẽ MLeader và nhập nội dung ngay trong AutoCAD. Có thể gõ L = ở bất kỳ dòng nào."
                : "\nVẽ MLeader và nhập ghi chú thường. Không tự thêm L.");
            doc.Editor.Command("_.MLEADER");
        }
        catch (System.Exception ex) { doc.Editor.WriteMessage("\nLệnh MLEADER không hoàn tất: " + ex.Message); }
        finally { try { db.MLeaderstyle = previousStyle; } catch (System.Exception) { } }
        ObjectId created = ObjectId.Null;
        using (var tr = db.TransactionManager.StartTransaction())
        {
            var space = (BlockTableRecord)tr.GetObject(spaceId, OpenMode.ForRead);
            foreach (ObjectId id in space)
                if (!existing.Contains(id) && tr.GetObject(id, OpenMode.ForRead, false) is MLeader leader &&
                    leader.ContentType == ContentType.MTextContent) created = id;
            tr.Commit();
        }
        if (created.IsNull) return;
        bool hasLengthField = false;
        if (linkLength)
        {
            using var tr = db.TransactionManager.StartTransaction();
            if (tr.GetObject(created, OpenMode.ForRead, false) is Entity note)
                hasLengthField = LengthData.HasLengthField(LengthData.ReadText(note) ?? "");
            tr.Commit();
        }
        if (hasLengthField) LinkAtArrow(doc, created);
        else MarkPlainNote(doc, created);
    }

    private static void MarkPlainNote(Document doc, ObjectId noteId)
    {
        using var tr = doc.Database.TransactionManager.StartTransaction();
        LengthData.EnsureRegApp(doc.Database, tr);
        if (tr.GetObject(noteId, OpenMode.ForWrite, false) is Entity note)
            LengthData.MarkPlain(note);
        tr.Commit();
        LiveEngine.MarkDirty(doc);
        try { PaletteHost.Refresh(doc); } catch (System.Exception) { }
    }

    private static ObjectId EnsureTextStyle(Database db, Transaction tr, int selection)
    {
        var name = selection == 1 ? "L-text-VNROMANS" : "L-text-TNR";
        var table = (TextStyleTable)tr.GetObject(db.TextStyleTableId, OpenMode.ForRead);
        if (table.Has(name)) return table[name];
        table.UpgradeOpen();
        var style = new TextStyleTableRecord { Name = name };
        var id = table.Add(style);
        tr.AddNewlyCreatedDBObject(style, true);
        style.TextSize = 2.0;
        style.XScale = 0.8;
        style.ObliquingAngle = 0;
        style.Annotative = AnnotativeStates.True;
        if (selection == 1) style.FileName = "VNROMANS.SHX";
        else style.Font = new Autodesk.AutoCAD.GraphicsInterface.FontDescriptor("Times New Roman", false, false, 0, 0);
        return id;
    }

    private static ObjectId EnsureMLeaderStyle(Database db, Transaction tr, int selection, ObjectId textStyleId)
    {
        var name = selection == 1 ? "LLM-VNROMANS" : "LLM-TNR";
        var styles = (DBDictionary)tr.GetObject(db.MLeaderStyleDictionaryId, OpenMode.ForRead);
        var green = Autodesk.AutoCAD.Colors.Color.FromColorIndex(Autodesk.AutoCAD.Colors.ColorMethod.ByAci, 3);
        var lineTypes = (LinetypeTable)tr.GetObject(db.LinetypeTableId, OpenMode.ForRead);
        ObjectId id;
        MLeaderStyle style;
        if (styles.Contains(name))
        {
            id = styles.GetAt(name);
            style = (MLeaderStyle)tr.GetObject(id, OpenMode.ForWrite);
        }
        else
        {
            style = new MLeaderStyle();
            try { id = style.PostMLeaderStyleToDb(db, name); }
            catch (System.Exception ex) { throw new InvalidOperationException("PostMLeaderStyleToDb: " + ex.Message, ex); }
            tr.AddNewlyCreatedDBObject(style, true);
        }
        style.ContentType = ContentType.MTextContent;
        style.Annotative = AnnotativeStates.True;
        style.LeaderLineType = LeaderType.StraightLeader;
        style.LeaderLineColor = green;
        style.LeaderLineTypeId = lineTypes["ByBlock"];
        style.LeaderLineWeight = LineWeight.ByBlock;
        style.ArrowSize = 2.0;
        style.BreakSize = 0.5;
        style.MaxLeaderSegmentsPoints = 3;
        style.EnableLanding = true;
        style.EnableDogleg = true;
        style.DoglegLength = 0.36;
        style.LandingGap = 1.0;
        style.TextStyleId = textStyleId;
        style.TextColor = green;
        style.TextHeight = 2.0;
        style.TextAngleType = TextAngleType.HorizontalAngle;
        style.TextAttachmentDirection = TextAttachmentDirection.AttachmentHorizontal;
        style.EnableFrameText = true;
        style.TextAlignAlwaysLeft = false;
        style.TextAttachmentType = TextAttachmentType.AttachmentMiddle;
        style.SetTextAttachmentType(TextAttachmentType.AttachmentMiddle, LeaderDirectionType.LeftLeader);
        style.SetTextAttachmentType(TextAttachmentType.AttachmentMiddle, LeaderDirectionType.RightLeader);
        style.ExtendLeaderToText = false;
        using var defaultText = new MText { TextStyleId = textStyleId, TextHeight = 2.0, Color = green };
        style.DefaultMText = defaultText;
        return id;
    }

    [CommandMethod("LLMLINK", CommandFlags.Modal)]
    public void LinkNew()
    {
        var doc = AcadApp.DocumentManager.MdiActiveDocument;
        if (doc == null) return;
        var note = PickNote(doc.Editor);
        if (note.IsNull) return;
        using (var tr = doc.Database.TransactionManager.StartTransaction())
        {
            if (tr.GetObject(note, OpenMode.ForRead, false) is MLeader)
            {
                tr.Commit();
                LinkAtArrow(doc, note);
                return;
            }
            tr.Commit();
        }
        var source = PickSource(doc.Editor);
        if (source.IsNull) return;
        Link(doc, note, source);
    }

    [CommandMethod("LLMCOPY", CommandFlags.Modal)]
    public void CopyLinkedNote()
    {
        var doc = AcadApp.DocumentManager.MdiActiveDocument;
        if (doc == null) return;
        var originalId = PickNote(doc.Editor);
        if (originalId.IsNull) return;
        string template;
        bool millimeters;
        ObjectId styleId;
        ObjectId textStyleId;
        string layer;
        double textHeight;
        double textWidth;
        Autodesk.AutoCAD.Colors.Color textColor;
        using (var tr = doc.Database.TransactionManager.StartTransaction())
        {
            if (tr.GetObject(originalId, OpenMode.ForRead, false) is not MLeader original ||
                LengthData.ReadLink(original) is not { } link)
            {
                doc.Editor.WriteMessage("\nHãy chọn MLeader đã liên kết bởi LiveLength.");
                return;
            }
            millimeters = link.Millimeters;
            template = LengthData.TemplateFor(link.Template, false, millimeters);
            styleId = original.MLeaderStyle;
            layer = original.Layer;
            using var originalText = original.MText;
            if (originalText == null) return;
            textStyleId = originalText.TextStyleId;
            textHeight = original.TextHeight;
            textWidth = originalText.Width;
            textColor = originalText.Color;
            tr.Commit();
        }
        var separator = template.IndexOf("\\P", StringComparison.OrdinalIgnoreCase);
        var oldTitle = separator >= 0 ? template[..separator] : template;
        if (oldTitle.TrimStart().StartsWith("Line ", StringComparison.OrdinalIgnoreCase))
        {
            var titleOptions = new PromptStringOptions("\nTên line mới (Enter để giữ nguyên): ") { AllowSpaces = true };
            var title = doc.Editor.GetString(titleOptions);
            if (title.Status == PromptStatus.Cancel || title.Status == PromptStatus.Error) return;
            if (title.Status == PromptStatus.OK && !string.IsNullOrWhiteSpace(title.StringResult))
                template = title.StringResult + (separator >= 0 ? template[separator..] : "");
        }
        var created = CreatePlacedMLeader(doc, styleId, textStyleId, layer,
            textHeight, textWidth, textColor, template, "\nChọn điểm mũi tên trên đường: ");
        if (created.IsNull) return;
        LinkAtArrow(doc, created);
        if (millimeters)
        {
            using var tr = doc.Database.TransactionManager.StartTransaction();
            var handle = ((Entity)tr.GetObject(created, OpenMode.ForRead)).Handle.ToString();
            tr.Commit();
            LiveEngine.EditNote(doc, handle, null, true, out _);
        }
    }

    private static ObjectId CreatePlacedMLeader(Document doc, ObjectId styleId,
        ObjectId textStyleId, string layer, double textHeight, double textWidth,
        Autodesk.AutoCAD.Colors.Color textColor, string template, string arrowPrompt)
    {
        var arrow = doc.Editor.GetPoint(arrowPrompt);
        if (arrow.Status != PromptStatus.OK) return ObjectId.Null;
        double viewWidth;
        using (var view = doc.Editor.GetCurrentView()) viewWidth = view.Width;
        double annotationScale = 1;
        try
        {
            annotationScale = Convert.ToDouble(AcadApp.GetSystemVariable("CANNOSCALEVALUE"),
                System.Globalization.CultureInfo.InvariantCulture);
        }
        catch { /* Use drawing units when the annotation scale is unavailable. */ }
        if (annotationScale <= 0 || !double.IsFinite(annotationScale)) annotationScale = 1;
        var arrowPoint = arrow.Value;
        Point3d pickedPoint;
        var preview = new MLeaderPlacementJig(arrowPoint, textHeight, template,
            viewWidth, annotationScale);
        var result = doc.Editor.Drag(preview);
        if (result.Status != PromptStatus.OK) return ObjectId.Null;
        pickedPoint = preview.Point;
        var (notePoint, landingLength, direction) = PlaceLeader(
            arrowPoint, pickedPoint, textHeight);
        ObjectId created;
        try
        {
            using var tr = doc.Database.TransactionManager.StartTransaction();
            var space = (BlockTableRecord)tr.GetObject(doc.Database.CurrentSpaceId, OpenMode.ForWrite);
            var note = new MLeader();
            note.SetDatabaseDefaults();
            created = space.AppendEntity(note);
            tr.AddNewlyCreatedDBObject(note, true);
            note.Layer = layer;
            note.MLeaderStyle = styleId;
            note.ContentType = ContentType.MTextContent;
            using var mtext = new MText
            {
                Contents = template.Replace(LengthData.Token, "??", StringComparison.Ordinal),
                TextStyleId = textStyleId,
                TextHeight = textHeight,
                Width = textWidth,
                Color = textColor,
                Location = notePoint
            };
            note.MText = mtext;
            note.SetTextAttachmentType(TextAttachmentType.AttachmentMiddle, LeaderDirectionType.LeftLeader);
            note.SetTextAttachmentType(TextAttachmentType.AttachmentMiddle, LeaderDirectionType.RightLeader);
            int leaderIndex = note.AddLeader();
            int lineIndex = note.AddLeaderLine(leaderIndex);
            note.AddFirstVertex(lineIndex, arrowPoint);
            note.AddLastVertex(lineIndex, notePoint);
            note.SetDogleg(leaderIndex, new Vector3d(direction * landingLength, 0, 0));
            note.TextLocation = notePoint;
            note.SetLastVertex(lineIndex, notePoint);
            note.SetDoglegLength(leaderIndex, landingLength);
            tr.Commit();
        }
        catch (System.Exception ex)
        {
            doc.Editor.WriteMessage("\nKhông tạo được MLeader: " + ex.Message);
            return ObjectId.Null;
        }
        return created;
    }

    private static (Point3d Note, double LandingLength, double Direction)
        PlaceLeader(Point3d arrowPoint, Point3d pickedPoint, double textHeight)
    {
        var direction = pickedPoint.X < arrowPoint.X ? -1.0 : 1.0;
        return (pickedPoint, 0.01, direction);
    }

    private sealed class MLeaderPlacementJig : DrawJig
    {
        private readonly Point3d _arrow;
        private readonly double _height;
        private readonly double _previewHeight;
        private readonly string[] _lines;
        private readonly int _longestLine;
        internal Point3d Point { get; private set; }

        internal MLeaderPlacementJig(Point3d arrow, double height,
            string content, double viewWidth, double annotationScale)
        {
            _arrow = arrow;
            Point = arrow;
            _height = Math.Max(height, 0.1);
            // Annotative text height is measured on paper; its model-space height
            // is divided by CANNOSCALEVALUE (for example 1:100 => 0.01).
            _previewHeight = Math.Min(_height / annotationScale, viewWidth * 0.012) * 0.45;
            var visible = content.Replace(LengthData.Token, "??", StringComparison.Ordinal)
                .Replace(LengthData.SecondToken, "??", StringComparison.Ordinal);
            var plain = visible.Replace("\\P", "\n", StringComparison.OrdinalIgnoreCase);
            plain = Regex.Replace(plain, @"\\[A-Za-z][^;\n]*;", "");
            plain = plain.Replace("{", "").Replace("}", "");
            _lines = plain.Split('\n');
            _longestLine = _lines.Max(line => line.Length);
        }

        protected override SamplerStatus Sampler(JigPrompts prompts)
        {
            var options = new JigPromptPointOptions("\nChọn vị trí ghi chú: ")
            {
                BasePoint = _arrow,
                UseBasePoint = true,
                UserInputControls = UserInputControls.Accept3dCoordinates |
                    UserInputControls.GovernedByOrthoMode | UserInputControls.GovernedByUCSDetect
            };
            var result = prompts.AcquirePoint(options);
            if (result.Status != PromptStatus.OK) return SamplerStatus.Cancel;
            if (result.Value == Point) return SamplerStatus.NoChange;
            Point = result.Value;
            return SamplerStatus.OK;
        }

        protected override bool WorldDraw(Autodesk.AutoCAD.GraphicsInterface.WorldDraw draw)
        {
            var (notePoint, _, direction) = PlaceLeader(_arrow, Point, _height);
            draw.SubEntityTraits.Color = 8;
            using (var path = new Point3dCollection { _arrow, notePoint })
                draw.Geometry.Polyline(path, Vector3d.ZAxis, IntPtr.Zero);
            var boxWidth = Math.Max(_previewHeight * 8,
                _longestLine * _previewHeight * 0.65);
            var boxHeight = Math.Max(_previewHeight * 1.5, _lines.Length * _previewHeight * 1.35);
            var left = direction > 0 ? notePoint.X : notePoint.X - boxWidth;
            var top = notePoint.Y + boxHeight * 0.5;
            using (var frame = new Point3dCollection
            {
                new(left, top, notePoint.Z), new(left + boxWidth, top, notePoint.Z),
                new(left + boxWidth, top - boxHeight, notePoint.Z),
                new(left, top - boxHeight, notePoint.Z), new(left, top, notePoint.Z)
            }) draw.Geometry.Polyline(frame, Vector3d.ZAxis, IntPtr.Zero);
            for (int i = 0; i < _lines.Length; i++)
            {
                if (_lines[i].Length == 0) continue;
                draw.Geometry.Text(new Point3d(left + _previewHeight * 0.2,
                        top - _previewHeight * (1.1 + i * 1.35), notePoint.Z),
                    Vector3d.ZAxis, Vector3d.XAxis, _previewHeight, 1, 0, _lines[i]);
            }
            return true;
        }

    }

    [CommandMethod("LLMGCADD", CommandFlags.Modal)]
    public void RegisterGcDimension()
    {
        var doc = AcadApp.DocumentManager.MdiActiveDocument;
        if (doc == null) return;
        var options = new PromptEntityOptions("\nChọn DIM / LINE / PLINE / ARC / SPLINE / ELLIPSE / MLINE cần đặt mã GC: ");
        options.SetRejectMessage("\nChỉ hỗ trợ DIM tuyến tính, LINE, PLINE, ARC, SPLINE, ELLIPSE, MLINE.");
        options.AddAllowedClass(typeof(AlignedDimension), true);
        options.AddAllowedClass(typeof(RotatedDimension), true);
        options.AddAllowedClass(typeof(Line), true);
        options.AddAllowedClass(typeof(Polyline), true);
        options.AddAllowedClass(typeof(Polyline2d), true);
        options.AddAllowedClass(typeof(Polyline3d), true);
        options.AddAllowedClass(typeof(Arc), true);
        options.AddAllowedClass(typeof(Spline), true);
        options.AddAllowedClass(typeof(Ellipse), true);
        options.AddAllowedClass(typeof(Mline), true);
        var pick = doc.Editor.GetEntity(options);
        if (pick.Status != PromptStatus.OK) return;
        var suggested = GcData.NextName(GcData.GetRows(doc));
        var name = doc.Editor.GetString(new PromptStringOptions($"\nNhập mã GC <{suggested}>: "));
        if (name.Status == PromptStatus.Cancel || name.Status == PromptStatus.Error) return;
        var code = name.Status == PromptStatus.OK && !string.IsNullOrWhiteSpace(name.StringResult)
            ? name.StringResult : suggested;
        var description = doc.Editor.GetString(new PromptStringOptions("\nMô tả vị trí (có thể để trống, ví dụ: Vị trí xuống đèn): ")
        {
            AllowSpaces = true
        });
        if (description.Status == PromptStatus.Cancel || description.Status == PromptStatus.Error) return;
        var descriptionText = description.Status == PromptStatus.OK ? description.StringResult : "";
        if (GcData.Register(doc, pick.ObjectId, code, descriptionText, out var message))
        {
            using var tr = doc.Database.TransactionManager.StartTransaction();
            var source = (Entity)tr.GetObject(pick.ObjectId, OpenMode.ForRead);
            var handle = source.Handle.ToString();
            tr.Commit();
            try { LiveEngine.UpdateGcNotes(doc, handle, code.Trim().ToUpperInvariant(), descriptionText); }
            catch (System.Exception ex) { doc.Editor.WriteMessage("\nKhông cập nhật được ghi chú GC cũ: " + ex.Message); }
        }
        doc.Editor.WriteMessage("\n" + message);
        try { PaletteHost.Refresh(doc); } catch (System.Exception) { }
    }

    [CommandMethod("LLMGCEDIT", CommandFlags.Modal)]
    public void EditGcDescription()
    {
        var doc = AcadApp.DocumentManager.MdiActiveDocument;
        if (doc == null || PendingGcEdits.Count == 0) return;
        var edit = PendingGcEdits.Dequeue();
        if (edit.DocName != doc.Name) return;
        var sourceId = LengthData.Resolve(doc.Database, edit.Handle);
        if (sourceId.IsNull) return;
        if (GcData.Update(doc, sourceId, edit.OldName, edit.NewName, edit.Description, out var message))
        {
            try { LiveEngine.UpdateGcNotes(doc, edit.Handle, edit.NewName.Trim().ToUpperInvariant(), edit.Description); }
            catch (System.Exception ex) { doc.Editor.WriteMessage("\nKhông cập nhật được ghi chú GC cũ: " + ex.Message); }
        }
        doc.Editor.WriteMessage("\n" + message);
        try { PaletteHost.Refresh(doc); } catch (System.Exception) { }
    }

    [CommandMethod("LLMGCLOCATE", CommandFlags.Modal)]
    public void LocateGcSource()
    {
        var doc = AcadApp.DocumentManager.MdiActiveDocument;
        if (doc == null || PendingGcDocName != doc.Name) return;
        PendingDocName = doc.Name;
        ZoomHandle(doc, PendingGcHandle, true);
    }

    [CommandMethod("LLMGCCREATE", CommandFlags.Modal)]
    public void CreateGcNote()
    {
        var doc = AcadApp.DocumentManager.MdiActiveDocument;
        if (doc == null) return;
        var rows = GcData.GetRows(doc);
        if (rows.Count == 0)
        {
            doc.Editor.WriteMessage("\nChưa có GC nào. Hãy dùng LLMGCADD để đăng ký DIM / LINE / PLINE / ARC / SPLINE / ELLIPSE / MLINE trước.");
            return;
        }
        var chosen = PendingGcDocName == doc.Name
            ? rows.FirstOrDefault(r => r.Handle.Equals(PendingGcHandle, StringComparison.OrdinalIgnoreCase)) : null;
        if (chosen == null)
        {
            for (int i = 0; i < rows.Count; i++) doc.Editor.WriteMessage($"\n{i + 1}. {rows[i]}");
            var pick = doc.Editor.GetInteger(new PromptIntegerOptions("\nChọn số GC: ")
            {
                LowerLimit = 1, UpperLimit = rows.Count
            });
            if (pick.Status != PromptStatus.OK) return;
            chosen = rows[pick.Value - 1];
        }
        var sourceId = LengthData.Resolve(doc.Database, chosen.Handle);
        if (sourceId.IsNull || sourceId.IsErased)
        {
            doc.Editor.WriteMessage("\nNguồn của mã GC này không còn trong bản vẽ.");
            return;
        }
        ObjectId styleId;
        ObjectId textStyleId;
        string layer;
        try
        {
            using var tr = doc.Database.TransactionManager.StartTransaction();
            textStyleId = EnsureTextStyle(doc.Database, tr, 0);
            styleId = EnsureMLeaderStyle(doc.Database, tr, 0, textStyleId);
            layer = ((LayerTableRecord)tr.GetObject(doc.Database.Clayer, OpenMode.ForRead)).Name;
            tr.Commit();
        }
        catch (System.Exception ex)
        {
            doc.Editor.WriteMessage("\nKhông tạo được style GC: " + ex.Message);
            return;
        }
        var green = Autodesk.AutoCAD.Colors.Color.FromColorIndex(Autodesk.AutoCAD.Colors.ColorMethod.ByAci, 3);
        var template = GcData.BuildTemplate(chosen.Name, chosen.Description);
        var noteId = CreatePlacedMLeader(doc, styleId, textStyleId, layer, 2.0, 0,
            green, template, "\nChọn điểm mũi tên GC (không cần chạm DIM): ");
        if (noteId.IsNull) return;
        if (LiveEngine.Bind(doc, noteId, sourceId, out var result, chosen.Name))
        {
            doc.Editor.WriteMessage("\n" + chosen.Name + " đã liên kết với nguồn đã đăng ký; mũi tên có thể chỉ vào vị trí bất kỳ.");
            try { PaletteHost.Refresh(doc); } catch (System.Exception) { }
        }
        else doc.Editor.WriteMessage("\nKhông gắn được GC: " + result);
    }

    [CommandMethod("LLMAUTOLINK", CommandFlags.Modal)]
    public void LinkFromArrow()
    {
        var doc = AcadApp.DocumentManager.MdiActiveDocument;
        if (doc == null) return;
        var note = PickNote(doc.Editor);
        if (!note.IsNull) LinkAtArrow(doc, note);
    }

    private static void LinkAtArrow(Document doc, ObjectId note, bool promptIfMissing = false)
    {
        using (var tr = doc.Database.TransactionManager.StartTransaction())
        {
            if (tr.GetObject(note, OpenMode.ForRead, false) is MLeader leader &&
                LengthData.ReadLink(leader)?.IsGc == true)
            {
                doc.Editor.WriteMessage("\nGhi chú GC theo DIM đã đăng ký; mũi tên có thể chỉ vào vị trí bất kỳ.");
                return;
            }
            tr.Commit();
        }
        ObjectId source;
        try { source = LiveEngine.FindSourceAtArrow(doc, note); }
        catch (System.Exception ex)
        {
            doc.Editor.WriteMessage("\nKhông đọc được đầu mũi tên: " + ex.Message);
            return;
        }
        if (source.IsNull && promptIfMissing)
        {
            doc.Editor.WriteMessage("\nKhông tìm thấy nguồn tại mũi tên. Chọn đường nguồn để L= tự cập nhật.");
            source = PickSource(doc.Editor);
        }
        if (source.IsNull)
        {
            doc.Editor.WriteMessage("\nKhông tìm thấy một LINE / PLINE / ARC / SPLINE / ELLIPSE / MLINE / DIM rõ ràng tại đầu mũi tên. Hãy bật OSNAP vào đường.");
            return;
        }
        Link(doc, note, source);
    }

    [CommandMethod("LLMRELINK", CommandFlags.Modal)]
    public void RelinkSelected()
    {
        var doc = AcadApp.DocumentManager.MdiActiveDocument;
        if (doc == null) return;
        var note = GetPendingNote(doc);
        if (note.IsNull)
        {
            doc.Editor.WriteMessage("\nKhông có chú thích được chọn. Hãy chọn dòng trong Palette.");
            return;
        }
        using (var tr = doc.Database.TransactionManager.StartTransaction())
        {
            if (tr.GetObject(note, OpenMode.ForRead, false) is MLeader)
            {
                tr.Commit();
                LinkAtArrow(doc, note);
                return;
            }
            tr.Commit();
        }
        var source = PickSource(doc.Editor);
        if (source.IsNull) return;
        Link(doc, note, source);
    }

    private static void Link(Document doc, ObjectId note, ObjectId source)
    {
        if (LiveEngine.Bind(doc, note, source, out string result))
        {
            doc.Editor.WriteMessage("\n" + result);
            try { PaletteHost.Refresh(doc); }
            catch (System.Exception) { }
        }
        else doc.Editor.WriteMessage("\nLiveLength: " + result);
    }

    [CommandMethod("LLMZOOMNOTE", CommandFlags.Modal)]
    public void ZoomNote()
    {
        var doc = AcadApp.DocumentManager.MdiActiveDocument;
        if (doc == null) return;
        ZoomHandle(doc, PendingHandle);
    }

    [CommandMethod("LLMZOOMSOURCE", CommandFlags.Modal)]
    public void ZoomSource()
    {
        var doc = AcadApp.DocumentManager.MdiActiveDocument;
        if (doc == null) return;
        var note = GetPendingNote(doc);
        if (note.IsNull) return;
        string? sourceHandle = null;
        using (var tr = doc.Database.TransactionManager.StartTransaction())
        {
            if (tr.GetObject(note, OpenMode.ForRead, false) is Entity ent)
                sourceHandle = LengthData.ReadLink(ent)?.SourceHandle;
            tr.Commit();
        }
        if (sourceHandle == null)
        {
            doc.Editor.WriteMessage("\nChú thích chưa có đường nguồn.");
            return;
        }
        ZoomHandle(doc, sourceHandle);
    }

    [CommandMethod("LLMFIND", CommandFlags.Modal)]
    public void FindSourceFromNote()
    {
        var doc = AcadApp.DocumentManager.MdiActiveDocument;
        if (doc == null) return;
        var options = new PromptEntityOptions("\nChọn MLeader LiveLength để dò vị trí nguồn: ");
        options.SetRejectMessage("\nHãy chọn MLeader.");
        options.AddAllowedClass(typeof(MLeader), true);
        var picked = doc.Editor.GetEntity(options);
        if (picked.Status != PromptStatus.OK) return;
        string? sourceHandle;
        using (var tr = doc.Database.TransactionManager.StartTransaction())
        {
            sourceHandle = tr.GetObject(picked.ObjectId, OpenMode.ForRead, false) is MLeader leader
                ? LengthData.ReadLink(leader)?.SourceHandle : null;
            tr.Commit();
        }
        if (string.IsNullOrWhiteSpace(sourceHandle))
        {
            doc.Editor.WriteMessage("\nMLeader này chưa có liên kết LiveLength.");
            return;
        }
        PendingDocName = doc.Name;
        ZoomHandle(doc, sourceHandle, true);
    }

    [CommandMethod("LLMUNLINK", CommandFlags.Modal)]
    public void UnlinkSelected()
    {
        var doc = AcadApp.DocumentManager.MdiActiveDocument;
        if (doc == null || GetPendingNote(doc).IsNull) return;
        if (LiveEngine.Unlink(doc, PendingHandle, out string result))
        {
            doc.Editor.WriteMessage("\n" + result);
            try { PaletteHost.Refresh(doc); }
            catch (System.Exception) { }
        }
        else doc.Editor.WriteMessage("\n" + result);
    }

    [CommandMethod("LLMSYNC", CommandFlags.Modal)]
    public void SyncNow()
    {
        var doc = AcadApp.DocumentManager.MdiActiveDocument;
        if (doc == null) return;
        var count = LiveEngine.Synchronize(doc);
        doc.Editor.WriteMessage($"\nLiveLength: đã cập nhật {count} chú thích.");
        try { PaletteHost.Refresh(doc); }
        catch (System.Exception) { }
    }

    [CommandMethod("LLMON", CommandFlags.Modal)]
    public void Enable()
    {
        LiveEngine.SetEnabled(true);
        AcadApp.DocumentManager.MdiActiveDocument?.Editor.WriteMessage("\nLiveLength: đã bật tự động cập nhật.");
    }

    [CommandMethod("LLOFF", CommandFlags.Modal)]
    public void Disable()
    {
        LiveEngine.SetEnabled(false);
        AcadApp.DocumentManager.MdiActiveDocument?.Editor.WriteMessage("\nLiveLength: đã tắt tự động cập nhật.");
    }

    private static ObjectId GetPendingNote(Document doc)
    {
        if (PendingDocName != doc.Name || string.IsNullOrEmpty(PendingHandle)) return ObjectId.Null;
        var id = LengthData.Resolve(doc.Database, PendingHandle);
        if (id.IsNull || id.IsErased) return ObjectId.Null;
        return id;
    }

    private static ObjectId PickNote(Editor ed)
    {
        var opts = new PromptEntityOptions("\nChọn MTEXT / MLEADER / TEXT cần gắn: ");
        opts.SetRejectMessage("\nChỉ hỗ trợ MTEXT, MLEADER MTEXT, TEXT.");
        opts.AddAllowedClass(typeof(MText), true);
        opts.AddAllowedClass(typeof(MLeader), true);
        opts.AddAllowedClass(typeof(DBText), true);
        var res = ed.GetEntity(opts);
        if (res.Status != PromptStatus.OK) return ObjectId.Null;
        return res.ObjectId;
    }

    private static ObjectId PickSource(Editor ed)
    {
        var opts = new PromptEntityOptions("\nChọn LINE / PLINE / ARC / SPLINE / ELLIPSE / MLINE / DIM làm nguồn chiều dài: ");
        opts.SetRejectMessage("\nChỉ hỗ trợ LINE, PLINE 2D/3D, ARC, SPLINE, ELLIPSE, MLINE, DIM tuyến tính.");
        opts.AddAllowedClass(typeof(Line), true);
        opts.AddAllowedClass(typeof(Polyline), true);
        opts.AddAllowedClass(typeof(Polyline2d), true);
        opts.AddAllowedClass(typeof(Polyline3d), true);
        opts.AddAllowedClass(typeof(Arc), true);
        opts.AddAllowedClass(typeof(Spline), true);
        opts.AddAllowedClass(typeof(Ellipse), true);
        opts.AddAllowedClass(typeof(Mline), true);
        opts.AddAllowedClass(typeof(AlignedDimension), true);
        opts.AddAllowedClass(typeof(RotatedDimension), true);
        var res = ed.GetEntity(opts);
        return res.Status == PromptStatus.OK ? res.ObjectId : ObjectId.Null;
    }

    private static void ZoomHandle(Document doc, string handle, bool showLocator = false)
    {
        if (PendingDocName != doc.Name || string.IsNullOrWhiteSpace(handle))
        {
            doc.Editor.WriteMessage("\nHãy chọn đối tượng trong bảng LiveLength trước.");
            return;
        }
        var db = doc.Database;
        var id = LengthData.Resolve(db, handle);
        if (id.IsNull || id.IsErased)
        {
            doc.Editor.WriteMessage("\nKhông tìm thấy đối tượng: có thể đã bị xóa.");
            return;
        }
        Extents3d ext;
        string? layoutName = null;
        using (var tr = db.TransactionManager.StartTransaction())
        {
            if (tr.GetObject(id, OpenMode.ForRead, false) is not Entity obj) return;
            try { ext = obj.GeometricExtents; }
            catch
            {
                doc.Editor.WriteMessage("\nKhông đọc được vùng bao đối tượng.");
                return;
            }
            if (tr.GetObject(obj.OwnerId, OpenMode.ForRead) is BlockTableRecord btr && btr.IsLayout)
            {
                var layout = (Layout)tr.GetObject(btr.LayoutId, OpenMode.ForRead);
                layoutName = layout.LayoutName;
            }
            tr.Commit();
        }
        try
        {
            if (layoutName != null && LayoutManager.Current.CurrentLayout != layoutName)
                LayoutManager.Current.CurrentLayout = layoutName;
            using var view = doc.Editor.GetCurrentView();
            var center = new Point3d((ext.MinPoint.X + ext.MaxPoint.X) * .5,
                (ext.MinPoint.Y + ext.MaxPoint.Y) * .5,
                (ext.MinPoint.Z + ext.MaxPoint.Z) * .5);
            // Convert WCS extents to display coordinates, including target and twist.
            var dcsToWcs = Matrix3d.Rotation(-view.ViewTwist, view.ViewDirection, view.Target) *
                Matrix3d.Displacement(view.Target - Point3d.Origin) *
                Matrix3d.PlaneToWorld(view.ViewDirection);
            var wcsToDcs = dcsToWcs.Inverse();
            var corners = new[]
            {
                new Point3d(ext.MinPoint.X, ext.MinPoint.Y, ext.MinPoint.Z),
                new Point3d(ext.MinPoint.X, ext.MaxPoint.Y, ext.MinPoint.Z),
                new Point3d(ext.MaxPoint.X, ext.MinPoint.Y, ext.MinPoint.Z),
                new Point3d(ext.MaxPoint.X, ext.MaxPoint.Y, ext.MinPoint.Z),
                new Point3d(ext.MinPoint.X, ext.MinPoint.Y, ext.MaxPoint.Z),
                new Point3d(ext.MaxPoint.X, ext.MaxPoint.Y, ext.MaxPoint.Z)
            }.Select(p => p.TransformBy(wcsToDcs)).ToArray();
            var minX = corners.Min(p => p.X);
            var maxX = corners.Max(p => p.X);
            var minY = corners.Min(p => p.Y);
            var maxY = corners.Max(p => p.Y);
            var width = Math.Max((maxX - minX) * 2.5, 10);
            var height = Math.Max((maxY - minY) * 2.5, 10);
            var aspect = Math.Max(view.Width / Math.Max(view.Height, 0.001), 0.1);
            if (width / height < aspect) width = height * aspect;
            else height = width / aspect;
            view.CenterPoint = new Point2d((minX + maxX) * .5, (minY + maxY) * .5);
            view.Width = width;
            view.Height = height;
            doc.Editor.SetCurrentView(view);
            doc.Editor.SetImpliedSelection(new[] { id });
            if (showLocator)
            {
                var sourceSize = Math.Max(ext.MaxPoint.X - ext.MinPoint.X, ext.MaxPoint.Y - ext.MinPoint.Y);
                var padding = Math.Max(sourceSize * 0.12, Math.Max(view.Width, view.Height) * 0.02);
                var z = center.Z;
                var a = new Point3d(ext.MinPoint.X - padding, ext.MinPoint.Y - padding, z);
                var b = new Point3d(ext.MaxPoint.X + padding, ext.MinPoint.Y - padding, z);
                var c = new Point3d(ext.MaxPoint.X + padding, ext.MaxPoint.Y + padding, z);
                var d = new Point3d(ext.MinPoint.X - padding, ext.MaxPoint.Y + padding, z);
                doc.Editor.DrawVector(a, b, 1, true);
                doc.Editor.DrawVector(b, c, 1, true);
                doc.Editor.DrawVector(c, d, 1, true);
                doc.Editor.DrawVector(d, a, 1, true);
                doc.Editor.WriteMessage("\nĐã dò vị trí nguồn GC; khung đỏ là nét tạm trên màn hình, không lưu vào DWG.");
            }
        }
        catch (System.Exception ex)
        {
            doc.Editor.WriteMessage("\nKhông thể Zoom: " + ex.Message);
        }
    }
}
