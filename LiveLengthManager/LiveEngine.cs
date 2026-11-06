using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Runtime;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace LiveLengthManager;

internal static class LiveEngine
{
    private static readonly HashSet<Document> Subscribed = new();
    private static readonly Dictionary<Document, Database> DocumentDatabases = new();
    private static readonly Dictionary<Document, string> DocumentNames = new();
    private static readonly HashSet<Database> Dirty = new();
    private static readonly Dictionary<Database, HashSet<ObjectId>> ArrowDirty = new();
    private static readonly HashSet<Document> HistoryCommands = new();
    private static readonly HashSet<Document> SelectionDirty = new();
    private static readonly HashSet<Database> RedoProtected = new();
    // Incremental sync: objects touched since the last idle pass, and an index of
    // source handle -> linked notes rebuilt by every full scan.
    private static readonly HashSet<Database> FullSync = new();
    private static readonly Dictionary<Database, HashSet<ObjectId>> Changed = new();
    private static readonly Dictionary<Database, Dictionary<string, HashSet<ObjectId>>> Dependents = new();
    private static bool _started;
    private static bool _booted;
    private static bool _syncing;
    private static bool _enabled = true;
    private static DateTime _nextIdle = DateTime.MinValue;
    private static string? _closedDocumentName;
    internal static bool Enabled => _enabled;

    internal static void Start()
    {
        if (_started) return;
        _started = true;
        var docs = AcadApp.DocumentManager;
        docs.DocumentCreated += DocumentCreated;
        docs.DocumentActivated += DocumentActivated;
        docs.DocumentToBeDestroyed += DocumentClosing;
        AcadApp.Idle += Idle;
        foreach (Document doc in docs) Attach(doc);
    }

    internal static void Stop()
    {
        if (!_started) return;
        _started = false;
        // AutoCAD owns these native event sources and is destroying them now.
        // Touching a Document/Database or palette during host teardown can fault.
        Subscribed.Clear();
        DocumentDatabases.Clear();
        DocumentNames.Clear();
        Dirty.Clear();
        ArrowDirty.Clear();
        HistoryCommands.Clear();
        SelectionDirty.Clear();
        RedoProtected.Clear();
        FullSync.Clear();
        Changed.Clear();
        Dependents.Clear();
        ArrowFollow.Clear();
    }

    private static void Attach(Document doc)
    {
        if (!_started) return;
        if (!Subscribed.Add(doc)) return;
        var db = doc.Database;
        DocumentDatabases[doc] = db;
        DocumentNames[doc] = doc.Name;
        db.ObjectModified += DatabaseChanged;
        db.ObjectErased += DatabaseErased;
        db.ObjectAppended += DatabaseAppended;
        doc.CommandWillStart += CommandStarting;
        doc.CommandEnded += CommandFinished;
        doc.CommandCancelled += CommandFinished;
        doc.CommandFailed += CommandFinished;
        doc.ImpliedSelectionChanged += ImpliedSelectionChanged;
        Dirty.Add(db);
        FullSync.Add(db);
    }

    private static void Forget(Document doc)
    {
        if (!Subscribed.Remove(doc)) return;
        if (DocumentDatabases.Remove(doc, out var db))
        {
            Dirty.Remove(db);
            ArrowDirty.Remove(db);
            RedoProtected.Remove(db);
            FullSync.Remove(db);
            Changed.Remove(db);
            Dependents.Remove(db);
            ArrowFollow.Forget(db);
        }
        HistoryCommands.Remove(doc);
        SelectionDirty.Remove(doc);
        if (DocumentNames.Remove(doc, out var name)) _closedDocumentName = name;
        // The Document/Database are being destroyed by AutoCAD. Do not call
        // their native-backed event accessors while that teardown is underway.
    }

    private static void DocumentCreated(object sender, DocumentCollectionEventArgs e)
    {
        try { if (_started && e.Document is { } doc) Attach(doc); }
        catch (System.Exception) { }
    }
    private static void DocumentActivated(object sender, DocumentCollectionEventArgs e)
    {
        if (!_started || e.Document is not { } doc) return;
        // During CLOSE AutoCAD can activate a document whose native Database
        // is not ready (or is already being destroyed). Never read doc.Database
        // or subscribe here. DocumentCreated and Start attach stable documents.
        try
        {
            if (DocumentDatabases.TryGetValue(doc, out var db)) Dirty.Add(db);
            SelectionDirty.Add(doc);
        }
        catch (System.Exception) { }
    }
    private static void DocumentClosing(object sender, DocumentCollectionEventArgs e)
    {
        if (!_started || e.Document is not { } doc) return;
        try { Forget(doc); }
        catch (System.Exception) { }
    }

    private static void ImpliedSelectionChanged(object? sender, EventArgs e)
    {
        // The callback may run during an AutoCAD selection operation. Read the
        // PICKFIRST set later at idle, after the editor has finished changing it.
        if (_started && sender is Document doc && Subscribed.Contains(doc)) SelectionDirty.Add(doc);
    }

    private static void DatabaseChanged(object sender, ObjectEventArgs e)
    {
        try
        {
            if (!_started || _syncing || sender is not Database db || !DocumentDatabases.Values.Contains(db)) return;
            if (DocumentDatabases.Any(pair => pair.Value == db && HistoryCommands.Contains(pair.Key))) return;
            RedoProtected.Remove(db);
            if (e.DBObject is MLeader leader)
            {
                Dirty.Add(db);
                Track(db, leader.ObjectId);
                if (!ArrowDirty.TryGetValue(db, out var notes)) ArrowDirty[db] = notes = new HashSet<ObjectId>();
                if (!leader.ObjectId.IsNull) notes.Add(leader.ObjectId);
            }
            else if (e.DBObject is Curve or Mline or AlignedDimension or RotatedDimension or DBText or MText)
            {
                Dirty.Add(db);
                Track(db, e.DBObject.ObjectId);
            }
        }
        catch (System.Exception) { }
    }
    private static void DatabaseErased(object sender, ObjectErasedEventArgs e)
    {
        try
        {
            if (_started && !_syncing && sender is Database db && DocumentDatabases.Values.Contains(db) &&
                (e.DBObject is Curve || e.DBObject is Mline || e.DBObject is Dimension || e.DBObject is DBText || e.DBObject is MText || e.DBObject is MLeader))
            {
                if (DocumentDatabases.Any(pair => pair.Value == db && HistoryCommands.Contains(pair.Key))) return;
                RedoProtected.Remove(db);
                Dirty.Add(db);
                Track(db, e.DBObject.ObjectId);
            }
        }
        catch (System.Exception) { }
    }
    private static void DatabaseAppended(object sender, ObjectEventArgs e)
    {
        // COPY/paste of a linked note must enter the index so later source edits reach it.
        try
        {
            if (!_started || _syncing || sender is not Database db || !DocumentDatabases.Values.Contains(db) ||
                e.DBObject is not (MLeader or MText or DBText)) return;
            if (DocumentDatabases.Any(pair => pair.Value == db && HistoryCommands.Contains(pair.Key))) return;
            Dirty.Add(db);
            Track(db, e.DBObject.ObjectId);
        }
        catch (System.Exception) { }
    }
    private static void Track(Database db, ObjectId id)
    {
        if (id.IsNull) return;
        if (!Changed.TryGetValue(db, out var ids)) Changed[db] = ids = new HashSet<ObjectId>();
        ids.Add(id);
        // Huge edits (e.g. pasting thousands of objects) are cheaper as one full scan.
        if (ids.Count > 20000)
        {
            Changed.Remove(db);
            FullSync.Add(db);
        }
    }
    private static void InvalidateIndex(Database db)
    {
        // UNDO/REDO can restore notes or links without normal edit events.
        Changed.Remove(db);
        Dependents.Remove(db);
    }
    private static bool IsHistoryCommand(string? name)
    {
        var normalized = (name ?? "").TrimStart('.', '_').ToUpperInvariant();
        return normalized is "U" or "UNDO" or "REDO" or "MREDO";
    }

    private static void CommandStarting(object sender, CommandEventArgs e)
    {
        try
        {
            if (!_started || sender is not Document doc || !IsHistoryCommand(e.GlobalCommandName) ||
                !DocumentDatabases.TryGetValue(doc, out var db)) return;
            HistoryCommands.Add(doc);
            RedoProtected.Add(db);
            Dirty.Remove(db);
            ArrowDirty.Remove(db);
            InvalidateIndex(db);
        }
        catch (System.Exception) { }
    }

    private static void CommandFinished(object sender, CommandEventArgs e)
    {
        try
        {
            if (!_started || _syncing || sender is not Document doc ||
                !DocumentDatabases.TryGetValue(doc, out var db)) return;
            if (HistoryCommands.Remove(doc) || IsHistoryCommand(e.GlobalCommandName))
            {
                RedoProtected.Add(db);
                ArrowDirty.Remove(db);
                InvalidateIndex(db);
                // A palette scan here can invalidate AutoCAD's redo history.
                // Leave the displayed snapshot until the next real edit or
                // the user's explicit Quét lại action.
                Dirty.Remove(db);
                return;
            }
            // Database edit events and explicit LLM operations already mark the
            // drawing dirty. A command alone must not start an Idle transaction:
            // it can add an undo record above the user's ERASE operation.
        }
        catch (System.Exception) { }
    }

    internal static void MarkDirty(Document doc)
    {
        if (!_started || !DocumentDatabases.TryGetValue(doc, out var db)) return;
        Dirty.Add(db);
        FullSync.Add(db);
    }
    internal static void SetEnabled(bool value)
    {
        _enabled = value;
        if (value && AcadApp.DocumentManager.MdiActiveDocument is { } doc) MarkDirty(doc);
    }

    private static bool IsIdle()
    {
        try { return string.IsNullOrWhiteSpace(Convert.ToString(AcadApp.GetSystemVariable("CMDNAMES"))); }
        catch { return false; }
    }

    // An exception escaping an AutoCAD event handler terminates AutoCAD, so nothing may leak out.
    private static void Idle(object? sender, EventArgs e)
    {
        try { IdleCore(); }
        catch (System.Exception ex)
        {
            try { System.Diagnostics.Trace.WriteLine("LiveLength idle: " + ex); } catch { }
        }
    }

    private static void IdleCore()
    {
        var now = DateTime.UtcNow;
        if (!_started || now < _nextIdle || _syncing) return;
        if (!_booted)
        {
            // First idle: AutoCAD's status bar exists and startup work is done.
            _booted = true;
            try { PaletteHost.EnsureTray(); } catch (System.Exception ex) { PluginLog.Write("tray: " + ex.Message); }
            try
            {
                Updater.CleanupOldVersions();
                Updater.CheckInBackground(false);
            }
            catch (System.Exception) { }
        }
        if (Updater.TakePrompt())
        {
            try { UpdateDialog.Open(); }
            catch (System.Exception ex) { PluginLog.Write("update dialog: " + ex.Message); }
        }
        // Idle fires constantly; skip the CMDNAMES query when there is nothing to do.
        if (Dirty.Count == 0 && _closedDocumentName == null &&
            (SelectionDirty.Count == 0 || !PaletteHost.IsVisible)) return;
        if (!IsIdle()) return;
        _nextIdle = now.AddMilliseconds(160);
        Document? doc;
        try { doc = AcadApp.DocumentManager.MdiActiveDocument; }
        catch (System.Exception) { return; }
        if (doc == null)
        {
            if (_closedDocumentName is { } closed)
            {
                _closedDocumentName = null;
                try { PaletteHost.ClearDocument(closed); } catch (System.Exception) { }
            }
            return;
        }
        if (!DocumentDatabases.TryGetValue(doc, out var db)) return;
        _closedDocumentName = null;
        var changed = Dirty.Remove(db);
        var delta = new PaletteDelta { Full = changed };
        if (changed && _enabled && !RedoProtected.Contains(db))
        {
            try
            {
                using (doc.LockDocument())
                {
                    var arrows = RefreshArrowSources(doc, db);
                    // Edits to unrelated geometry no longer touch the palette.
                    delta = SynchronizePending(doc, db);
                    delta.Notes.UnionWith(arrows);
                }
            }
            catch (System.Exception)
            {
                // The document can be temporarily unavailable during editing / switching.
                Dirty.Add(db);
                FullSync.Add(db);
                return;
            }
        }
        // UI refresh is deliberately throttled; editing the DataGridView is never done
        // in ObjectModified/ObjectErased event callbacks.
        if (PaletteHost.IsVisible && !delta.IsEmpty)
        {
            try
            {
                using (doc.LockDocument()) PaletteHost.Refresh(doc, delta);
            }
            catch (System.Exception) { /* palette will refresh on next idle */ }
        }
        if (PaletteHost.IsVisible && SelectionDirty.Remove(doc))
        {
            try { PaletteHost.SelectImplied(doc); }
            catch (System.Exception) { /* Selection may disappear before idle. */ }
        }
    }

    internal static int Synchronize(Document doc) => Synchronize(doc, null);

    private static int Synchronize(Document doc, HashSet<string>? changedHandles)
    {
        if (_syncing) return 0;
        var db = doc.Database;
        _syncing = true;
        var count = 0;
        var index = new Dictionary<string, HashSet<ObjectId>>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var tr = db.TransactionManager.StartOpenCloseTransaction();
            foreach (var id in EnumerateNotes(db, tr))
            {
                try { count += SyncNote(tr, db, id, index, changedHandles); }
                catch (System.Exception) { /* malformed one note must not stop others */ }
            }
            tr.Commit();
            if (DocumentDatabases.Values.Contains(db))
            {
                Dependents[db] = index;
                Changed.Remove(db);
                FullSync.Remove(db);
            }
        }
        finally { _syncing = false; }
        return count;
    }

    // Updates only notes that were edited or depend on an edited source and
    // reports which palette rows need re-reading.
    private static PaletteDelta SynchronizePending(Document doc, Database db)
    {
        var delta = new PaletteDelta();
        Changed.Remove(db, out var ids);
        var handles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (ids != null) foreach (var id in ids) handles.Add(id.Handle.ToString());
        if (FullSync.Contains(db) || !Dependents.TryGetValue(db, out var index))
        {
            Synchronize(doc, handles);
            delta.Full = true;
            return delta;
        }
        if (_syncing || ids == null || ids.Count == 0) return delta;
        _syncing = true;
        try
        {
            using var tr = db.TransactionManager.StartOpenCloseTransaction();
            var targets = new HashSet<ObjectId>();
            foreach (var id in ids)
            {
                // Handle is readable without opening, so erased sources still map to notes.
                if (index.TryGetValue(id.Handle.ToString(), out var notes)) targets.UnionWith(notes);
                try
                {
                    if (tr.GetObject(id, OpenMode.ForRead, true) is not Entity e) continue;
                    if (GcData.IsRegistered(e)) delta.GcSources.Add(e.Handle.ToString());
                    if (!LengthData.IsNote(e) || !LengthData.HasAppData(e)) continue;
                    // Erased notes stay in the delta so their rows are removed.
                    delta.Notes.Add(e.Handle.ToString());
                    if (!id.IsErased) targets.Add(id);
                }
                catch (System.Exception) { }
            }
            foreach (var id in targets)
            {
                if (id.IsNull) continue;
                delta.Notes.Add(id.Handle.ToString());
                if (id.IsErased) continue;
                try { SyncNote(tr, db, id, index, handles); }
                catch (System.Exception) { /* malformed one note must not stop others */ }
            }
            tr.Commit();
        }
        finally { _syncing = false; }
        return delta;
    }

    private static void AddDependent(Dictionary<string, HashSet<ObjectId>> index, string handle, ObjectId note)
    {
        if (string.IsNullOrWhiteSpace(handle) || handle == "0") return;
        if (!index.TryGetValue(handle, out var notes)) index[handle] = notes = new HashSet<ObjectId>();
        notes.Add(note);
    }

    private static int SyncNote(Transaction tr, Database db, ObjectId id,
        Dictionary<string, HashSet<ObjectId>>? index, HashSet<string>? changedHandles)
    {
        if (tr.GetObject(id, OpenMode.ForRead, false) is not Entity note) return 0;
        var link = LengthData.ReadLink(note);
        if (link == null) return 0;
        if (index != null)
        {
            AddDependent(index, link.SourceHandle, id);
            AddDependent(index, link.SecondarySourceHandle, id);
        }
        var current = LengthData.ReadText(note);
        if (current == null) return 0;
        var canonical = LengthData.CollapseDuplicateLength(current);
        bool gcTemplateChanged = false;
        if (link.IsGc)
        {
            canonical = LengthData.RemoveGcCode(canonical, link.GcLabel);
            var cleaned = LengthData.RemoveGcCode(link.Template, link.GcLabel);
            gcTemplateChanged = cleaned != link.Template;
            link.Template = cleaned;
        }
        bool duplicateLength = canonical != current;
        if (!LengthData.HasLengthField(canonical))
        {
            note.UpgradeOpen();
            LengthData.MarkPlain(note);
            return 1;
        }
        var normalizedTemplate = LengthData.NormalizeSlots(
            LengthData.TemplateFor(link.Template, note is DBText, link.Millimeters),
            link.SecondarySourceHandle.Length > 0);
        bool unitsChanged = normalizedTemplate != link.Template;
        if (unitsChanged) link.Template = normalizedTemplate;
        var length = LengthData.GetLength(tr, db, link.SourceHandle);
        var secondLength = link.SecondarySourceHandle.Length > 0
            ? LengthData.GetLength(tr, db, link.SecondarySourceHandle) : null;
        // Do not write a placeholder after a source is erased. That
        // would create another undo record before the user can undo
        // the LINE/DIM deletion; the palette still shows missing source.
        if (!length.HasValue ||
            (link.SecondarySourceHandle.Length > 0 && !secondLength.HasValue)) return 0;
        var arrowMoved = false;
        if (note is MLeader arrowNote)
        {
            var sourceId = LengthData.Resolve(db, link.SourceHandle);
            var source = sourceId.IsNull || sourceId.IsErased ? null : tr.GetObject(sourceId, OpenMode.ForRead, false) as Entity;
            arrowMoved = ArrowFollow.Update(db, arrowNote, source, changedHandles?.Contains(link.SourceHandle) == true);
        }
        var wanted = LengthData.Render(link, length, secondLength);
        bool fixHeight = false;
        if (note is MLeader leader &&
            tr.GetObject(leader.MLeaderStyle, OpenMode.ForRead) is MLeaderStyle style &&
            (style.Name == "LLM-TNR" || style.Name == "LLM-VNROMANS"))
            fixHeight = Math.Abs(leader.TextHeight - 2.0) > 0.001;
        if (current == wanted && !fixHeight && !duplicateLength && !unitsChanged && !gcTemplateChanged) return arrowMoved ? 1 : 0;
        // The note could also have been manually edited since the previous
        // update. Reconstruct its template from CURRENT text to preserve it.
        var reconstructed = LengthData.NormalizeSlots(
            LengthData.TemplateFor(canonical, note is DBText, link.Millimeters),
            link.SecondarySourceHandle.Length > 0);
        bool templateChanged = reconstructed != link.Template && reconstructed.Length <= 3000;
        if (templateChanged) link.Template = reconstructed;
        wanted = LengthData.Render(link, length, secondLength);
        if (current == wanted && !templateChanged && !fixHeight && !unitsChanged && !gcTemplateChanged) return arrowMoved ? 1 : 0;
        if (!note.IsWriteEnabled) note.UpgradeOpen();
        if (templateChanged || unitsChanged || gcTemplateChanged) LengthData.WriteLink(note, link);
        var textChanged = current != wanted && LengthData.WriteText(note, wanted);
        if (fixHeight) ((MLeader)note).TextHeight = 2.0;
        return textChanged || fixHeight || arrowMoved ? 1 : 0;
    }

    internal static List<ObjectId> EnumerateNotes(Database db, Transaction tr)
    {
        var output = new List<ObjectId>();
        var table = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
        foreach (ObjectId btrId in table)
        {
            var btr = (BlockTableRecord)tr.GetObject(btrId, OpenMode.ForRead);
            if (!btr.IsLayout) continue; // Model and paper layouts, not block definitions.
            foreach (ObjectId entityId in btr)
            {
                try
                {
                    var e = tr.GetObject(entityId, OpenMode.ForRead, false) as Entity;
                    if (e != null && LengthData.IsNote(e)) output.Add(entityId);
                }
                catch (System.Exception) { }
            }
        }
        return output;
    }

    internal static List<NoteRow> GetRows(Document doc)
    {
        var rows = new List<NoteRow>();
        using var tr = doc.Database.TransactionManager.StartOpenCloseTransaction();
        var table = (BlockTable)tr.GetObject(doc.Database.BlockTableId, OpenMode.ForRead);
        foreach (ObjectId blockId in table)
        {
            var btr = (BlockTableRecord)tr.GetObject(blockId, OpenMode.ForRead);
            if (!btr.IsLayout) continue;
            var layout = (Layout)tr.GetObject(btr.LayoutId, OpenMode.ForRead);
            foreach (ObjectId entityId in btr)
            {
                try
                {
                    if (tr.GetObject(entityId, OpenMode.ForRead, false) is Entity e &&
                        BuildRow(tr, doc.Database, e, layout.LayoutName) is { } row) rows.Add(row);
                }
                catch (System.Exception) { }
            }
        }
        tr.Commit();
        return SortRows(rows);
    }

    internal static List<NoteRow> SortRows(IEnumerable<NoteRow> rows) =>
        rows.OrderBy(r => r.Layout).ThenBy(r => r.Layer).ThenBy(r => r.Handle).ToList();

    /// <summary>Reads only the given notes. Erased or non-LiveLength handles yield no row.</summary>
    internal static List<NoteRow> GetRowsFor(Document doc, IEnumerable<string> handles)
    {
        var rows = new List<NoteRow>();
        var db = doc.Database;
        using var tr = db.TransactionManager.StartOpenCloseTransaction();
        foreach (var handle in handles)
        {
            try
            {
                var id = LengthData.Resolve(db, handle);
                if (id.IsNull || id.IsErased || tr.GetObject(id, OpenMode.ForRead, false) is not Entity e ||
                    tr.GetObject(e.OwnerId, OpenMode.ForRead) is not BlockTableRecord { IsLayout: true } btr) continue;
                var layout = (Layout)tr.GetObject(btr.LayoutId, OpenMode.ForRead);
                if (BuildRow(tr, db, e, layout.LayoutName) is { } row) rows.Add(row);
            }
            catch (System.Exception) { }
        }
        tr.Commit();
        return rows;
    }

    private static readonly System.Text.RegularExpressions.Regex InlineFont =
        new(@"\\[fF]([^|;\\]+)", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>True when the note uses the VNROMANS style, whose SHX font expects TCVN3 text.</summary>
    internal static bool IsTcvn3(Transaction tr, Entity e)
    {
        try
        {
            if (e is MLeader ml && tr.GetObject(ml.MLeaderStyle, OpenMode.ForRead) is MLeaderStyle s)
                return s.Name.Contains("VNROMANS", StringComparison.OrdinalIgnoreCase);
            var name = e switch { MText m => m.TextStyleName, DBText t => t.TextStyleName, _ => "" };
            return name.Contains("VNROMANS", StringComparison.OrdinalIgnoreCase);
        }
        catch (System.Exception) { return false; }
    }

    private static string StyleLabel(Transaction tr, Entity e, string text)
    {
        var name = "";
        try
        {
            name = e switch
            {
                MLeader ml when tr.GetObject(ml.MLeaderStyle, OpenMode.ForRead) is MLeaderStyle s => s.Name switch
                {
                    "LLM-TNR" => "TNR",
                    "LLM-VNROMANS" => "VNROMANS",
                    var other => other
                },
                MText m => m.TextStyleName,
                DBText t => t.TextStyleName,
                _ => ""
            };
        }
        catch (System.Exception) { }
        // An inline \f override decides the visible font regardless of the style.
        var font = InlineFont.Match(text);
        return font.Success ? $"{name} ({font.Groups[1].Value.Trim()})" : name;
    }

    private static NoteRow? BuildRow(Transaction tr, Database db, Entity e, string layoutName)
    {
        if (!LengthData.IsNote(e)) return null;
        var link = LengthData.ReadLink(e);
        var plain = link == null && LengthData.IsPlainNote(e);
        if (link == null && !plain) return null;
        var tcvn3 = IsTcvn3(tr, e);
        var txt = LengthData.ReadText(e) ?? "";
        if (tcvn3) txt = LiveLengthManager.Tcvn3.ToUnicode(txt);
        var pos = LengthData.GetPosition(e, out bool posOk);
        var row = new NoteRow
        {
            Handle = e.Handle.ToString(),
            Type = e is MLeader ? "MLEADER" : e is MText ? "MTEXT" : "TEXT",
            CurrentText = txt,
            Description = LengthData.Preview(txt),
            Layer = e.Layer,
            Layout = layoutName,
            Style = StyleLabel(tr, e, txt),
            Tcvn3 = tcvn3,
            Position = pos,
            HasPosition = posOk,
            Link = link,
            Linked = link != null
        };
        if (link != null)
        {
            row.SourceHandle = link.SourceHandle;
            row.DrawingLength = LengthData.GetLength(tr, db, link.SourceHandle);
            row.LengthMeters = row.DrawingLength * link.Factor;
            row.SecondaryLengthMeters = link.SecondarySourceHandle.Length > 0
                ? LengthData.GetLength(tr, db, link.SecondarySourceHandle) * link.Factor : null;
            row.State = row.DrawingLength.HasValue &&
                (link.SecondarySourceHandle.Length == 0 || row.SecondaryLengthMeters.HasValue)
                ? "Đã liên kết" : "Mất liên kết";
        }
        else row.State = "Ghi chú thường";
        return row;
    }

    internal static void UpdateGcNotes(Document doc, string sourceHandle, string code, string description)
    {
        _syncing = true;
        try
        {
            using var tr = doc.Database.TransactionManager.StartOpenCloseTransaction();
            var length = LengthData.GetLength(tr, doc.Database, sourceHandle);
            foreach (var id in EnumerateNotes(doc.Database, tr))
            {
                try
                {
                    if (tr.GetObject(id, OpenMode.ForRead, false) is not Entity note ||
                        LengthData.ReadLink(note) is not { IsGc: true } link ||
                        !link.SourceHandle.Equals(sourceHandle, StringComparison.OrdinalIgnoreCase)) continue;
                    link.GcLabel = code;
                    link.Template = LengthData.EnsureUnits(GcData.BuildTemplate(code, description), link.Millimeters);
                    note.UpgradeOpen();
                    LengthData.WriteLink(note, link);
                    var second = link.SecondarySourceHandle.Length > 0
                        ? LengthData.GetLength(tr, doc.Database, link.SecondarySourceHandle) : null;
                    LengthData.WriteText(note, LengthData.Render(link, length, second));
                }
                catch (System.Exception) { }
            }
            tr.Commit();
        }
        finally { _syncing = false; }
        MarkDirty(doc);
    }

    internal static bool EditNote(Document doc, string handle, string? text, bool? millimeters, out string message)
    {
        message = "";
        var id = LengthData.Resolve(doc.Database, handle);
        if (id.IsNull || id.IsErased) { message = "Không tìm thấy ghi chú."; return false; }
        try
        {
            using var tr = doc.Database.TransactionManager.StartOpenCloseTransaction();
            if (tr.GetObject(id, OpenMode.ForRead, false) is not Entity note)
            { message = "Không tìm thấy ghi chú."; return false; }
            var link = LengthData.ReadLink(note);
            if (link == null)
            {
                if (!LengthData.IsPlainNote(note))
                { message = "Ghi chú không thuộc LiveLength."; return false; }
                if (text == null) { message = "Ghi chú thường không có đơn vị chiều dài."; return false; }
                var plainText = text.Replace("\r\n", "\\P").Replace("\n", "\\P").Trim();
                if (plainText.Length == 0) { message = "Nội dung không được để trống."; return false; }
                note.UpgradeOpen();
                LengthData.WriteText(note, plainText);
                tr.Commit();
                MarkDirty(doc);
                message = "Đã cập nhật ghi chú thường.";
                return true;
            }
            if (millimeters.HasValue) link.Millimeters = millimeters.Value;
            if (text != null)
            {
                var cleaned = text.Replace("\r\n", "\\P").Replace("\n", "\\P").Trim();
                if (string.IsNullOrWhiteSpace(cleaned)) { message = "Nội dung không được để trống."; return false; }
                link.Template = LengthData.NormalizeSlots(
                    LengthData.TemplateFor(cleaned, note is DBText, link.Millimeters),
                    link.SecondarySourceHandle.Length > 0);
                if (!LengthData.HasLengthField(cleaned))
                {
                    note.UpgradeOpen();
                    LengthData.MarkPlain(note);
                    LengthData.WriteText(note, cleaned);
                    tr.Commit();
                    MarkDirty(doc);
                    message = "Đã chuyển thành ghi chú thường; không còn tự cập nhật chiều dài.";
                    return true;
                }
            }
            else link.Template = LengthData.NormalizeSlots(
                LengthData.EnsureUnits(link.Template, link.Millimeters),
                link.SecondarySourceHandle.Length > 0);
            if (link.Template.Length > 3000) { message = "Nội dung quá dài."; return false; }
            var length = LengthData.GetLength(tr, doc.Database, link.SourceHandle);
            var secondLength = link.SecondarySourceHandle.Length > 0
                ? LengthData.GetLength(tr, doc.Database, link.SecondarySourceHandle) : null;
            note.UpgradeOpen();
            _syncing = true;
            try
            {
                LengthData.WriteLink(note, link);
                LengthData.WriteText(note, LengthData.Render(link, length, secondLength));
            }
            finally { _syncing = false; }
            tr.Commit();
            MarkDirty(doc);
            message = "Đã cập nhật ghi chú.";
            return true;
        }
        catch (System.Exception ex) { _syncing = false; message = ex.Message; return false; }
    }

    internal static bool BindSecond(Document doc, ObjectId noteId, ObjectId sourceId, out string message)
    {
        message = "";
        try
        {
            using var tr = doc.Database.TransactionManager.StartOpenCloseTransaction();
            if (tr.GetObject(noteId, OpenMode.ForRead, false) is not Entity note ||
                LengthData.ReadLink(note) is not { IsGc: false } link)
            { message = "Hãy chọn ghi chú chiều dài đã liên kết."; return false; }
            if (tr.GetObject(sourceId, OpenMode.ForRead, false) is not Entity source || !LengthData.IsSource(source))
            { message = "Nguồn thứ hai phải là LINE, PLINE, ARC, SPLINE, ELLIPSE, MLINE hoặc DIM tuyến tính."; return false; }
            var current = LengthData.ReadText(note) ?? "";
            link.Template = LengthData.TemplateFor(current, note is DBText, link.Millimeters);
            if (link.Template.Split(LengthData.Token, StringSplitOptions.None).Length < 3 &&
                !link.Template.Contains(LengthData.SecondToken, StringComparison.Ordinal))
            { message = "Ghi chú cần có ít nhất hai dòng L =. Hãy sửa Nội dung trước."; return false; }
            link.SecondarySourceHandle = source.Handle.ToString();
            link.Template = LengthData.NormalizeSlots(link.Template, true);
            var first = LengthData.GetLength(tr, doc.Database, link.SourceHandle);
            var second = LengthData.GetLength(tr, doc.Database, link.SecondarySourceHandle);
            if (!second.HasValue) { message = "Không lấy được chiều dài nguồn thứ hai."; return false; }
            note.UpgradeOpen();
            _syncing = true;
            try
            {
                LengthData.WriteLink(note, link);
                LengthData.WriteText(note, LengthData.Render(link, first, second));
            }
            finally { _syncing = false; }
            tr.Commit();
            MarkDirty(doc);
            message = "Đã gán L thứ hai. Hai chiều dài sẽ cập nhật theo hai nguồn riêng.";
            return true;
        }
        catch (System.Exception ex) { _syncing = false; message = ex.Message; return false; }
    }

    internal static bool Bind(Document doc, ObjectId noteId, ObjectId sourceId, out string result,
        string? gcLabel = null)
    {
        result = "";
        if (noteId.IsNull || sourceId.IsNull) { result = "Không có đối tượng được chọn."; return false; }
        try
        {
            using var tr = doc.Database.TransactionManager.StartOpenCloseTransaction();
            if (tr.GetObject(noteId, OpenMode.ForRead, false) is not Entity note || !LengthData.IsNote(note))
            { result = "Chỉ hỗ trợ TEXT, MTEXT, MLEADER (nội dung MTEXT)."; return false; }
            if (tr.GetObject(sourceId, OpenMode.ForRead, false) is not Entity source || !LengthData.IsSource(source))
            { result = "Chỉ hỗ trợ LINE, PLINE 2D/3D, ARC, SPLINE, ELLIPSE, MLINE hoặc DIM tuyến tính."; return false; }
            var current = LengthData.ReadText(note);
            if (current == null) { result = "Không đọc được nội dung ghi chú."; return false; }
            var old = LengthData.ReadLink(note);
            // When re-linking, preserve original template unless description
            // has been edited by the user since last synchronization.
            var oldExpected = old == null ? null : LengthData.Render(old,
                LengthData.GetLength(tr, doc.Database, old.SourceHandle),
                old.SecondarySourceHandle.Length > 0
                    ? LengthData.GetLength(tr, doc.Database, old.SecondarySourceHandle) : null);
            var template = old != null && current == oldExpected
                ? old.Template : LengthData.NormalizeSlots(
                    LengthData.TemplateFor(current, note is DBText, old?.Millimeters == true),
                    old?.SecondarySourceHandle.Length > 0);
            if (!template.Contains(LengthData.Token, StringComparison.Ordinal))
            { result = "Ghi chú không có L=. App sẽ giữ nguyên nội dung, không tự thêm chiều dài."; return false; }
            if (template.Length > 3000)
            { result = "Ghi chú quá dài (giới hạn 3000 ký tự XData)."; return false; }
            var data = new LinkData
            {
                SourceHandle = source.Handle.ToString(),
                SecondarySourceHandle = old?.SecondarySourceHandle ?? "",
                Factor = old?.Factor ?? LengthData.DefaultFactor,
                Decimals = old?.Decimals ?? LengthData.DefaultDecimals,
                Millimeters = old?.Millimeters == true,
                Template = template,
                IsGc = gcLabel != null,
                GcLabel = gcLabel ?? ""
            };
            var length = LengthData.GetLength(tr, doc.Database, data.SourceHandle);
            if (length == null) { result = "Không lấy được chiều dài LINE/PLINE."; return false; }
            LengthData.EnsureRegApp(doc.Database, tr);
            note.UpgradeOpen();
            _syncing = true;
            try
            {
                LengthData.WriteLink(note, data);
                var second = data.SecondarySourceHandle.Length > 0
                    ? LengthData.GetLength(tr, doc.Database, data.SecondarySourceHandle) : null;
                LengthData.WriteText(note, LengthData.Render(data, length, second));
            }
            finally { _syncing = false; }
            tr.Commit();
            MarkDirty(doc);
            result = "Đã liên kết. Khi sửa PLINE, chiều dài sẽ cập nhật sau thao tác.";
            return true;
        }
        catch (System.Exception ex) { _syncing = false; result = ex.Message; return false; }
    }

    internal static ObjectId FindSourceAtArrow(Document doc, ObjectId noteId)
    {
        using var tr = doc.Database.TransactionManager.StartOpenCloseTransaction();
        if (tr.GetObject(noteId, OpenMode.ForRead, false) is not MLeader leader) return ObjectId.Null;
        var tips = new List<Autodesk.AutoCAD.Geometry.Point3d>();
        foreach (int leaderIndex in leader.GetLeaderIndexes())
            foreach (int lineIndex in leader.GetLeaderLineIndexes(leaderIndex))
                tips.Add(leader.GetFirstVertex(lineIndex));
        if (tips.Count == 0) return ObjectId.Null;
        var space = (BlockTableRecord)tr.GetObject(leader.OwnerId, OpenMode.ForRead);
        using var view = doc.Editor.GetCurrentView();
        double tolerance = Math.Max(view.Width * 0.005, 0.01);
        ObjectId nearest = ObjectId.Null;
        double best = tolerance;
        double second = double.MaxValue;
        bool bestIsDimension = false;
        bool secondIsDimension = false;
        foreach (ObjectId id in space)
        {
            if (id == noteId) continue;
            try
            {
                if (tr.GetObject(id, OpenMode.ForRead, false) is not Entity entity || !LengthData.IsSource(entity)) continue;
                bool isDimension = entity is Dimension;
                double candidate = double.MaxValue;
                foreach (var tip in tips)
                {
                    Autodesk.AutoCAD.Geometry.Point3d closest;
                    if (entity is Curve curve) closest = curve.GetClosestPointTo(tip, false);
                    else if (entity is Mline mline) closest = mline.GetClosestPointTo(tip, false, true);
                    else if (entity is Dimension dimension)
                    {
                        candidate = Math.Min(candidate, DimensionDistance(dimension, tip, tolerance));
                        continue;
                    }
                    else continue;
                    candidate = Math.Min(candidate, closest.DistanceTo(tip));
                }
                var tieMargin = Math.Max(0.001, tolerance * 0.02);
                if (candidate < best - tieMargin ||
                    (candidate < tolerance && Math.Abs(candidate - best) <= tieMargin && isDimension && !bestIsDimension))
                {
                    second = best;
                    secondIsDimension = bestIsDimension;
                    best = candidate;
                    bestIsDimension = isDimension;
                    nearest = id;
                }
                else if (candidate < second)
                {
                    second = candidate;
                    secondIsDimension = isDimension;
                }
            }
            catch (System.Exception) { }
        }
        // Two overlapping paths cannot be identified reliably from arrow position.
        if (second <= tolerance && Math.Abs(second - best) < Math.Max(0.001, tolerance * 0.02) &&
            bestIsDimension == secondIsDimension) return ObjectId.Null;
        return nearest;
    }

    private static double SegmentDistance(Autodesk.AutoCAD.Geometry.Point3d point,
        Autodesk.AutoCAD.Geometry.Point3d start, Autodesk.AutoCAD.Geometry.Point3d end)
    {
        var direction = end - start;
        var lengthSquared = direction.DotProduct(direction);
        if (lengthSquared < 1e-12) return point.DistanceTo(start);
        var fraction = Math.Clamp((point - start).DotProduct(direction) / lengthSquared, 0, 1);
        return point.DistanceTo(start + direction * fraction);
    }

    private static double DimensionDistance(Dimension dim, Autodesk.AutoCAD.Geometry.Point3d tip, double tolerance)
    {
        var ext = dim.GeometricExtents;
        var dx = Math.Max(Math.Max(ext.MinPoint.X - tip.X, 0), tip.X - ext.MaxPoint.X);
        var dy = Math.Max(Math.Max(ext.MinPoint.Y - tip.Y, 0), tip.Y - ext.MaxPoint.Y);
        var dz = Math.Max(Math.Max(ext.MinPoint.Z - tip.Z, 0), tip.Z - ext.MaxPoint.Z);
        if (Math.Sqrt(dx * dx + dy * dy + dz * dz) > tolerance) return double.MaxValue;
        using var parts = new DBObjectCollection();
        try
        {
            dim.Explode(parts); // Returns transient geometry; the DIM in the DWG is unchanged.
            double distance = double.MaxValue;
            foreach (DBObject part in parts)
            {
                if (part is Curve curve)
                {
                    try { distance = Math.Min(distance, curve.GetClosestPointTo(tip, false).DistanceTo(tip)); }
                    catch (System.Exception) { }
                }
            }
            if (distance < double.MaxValue) return distance;
        }
        finally
        {
            foreach (DBObject part in parts) part.Dispose();
        }
        return dim switch
        {
            AlignedDimension aligned => AlignedDimensionDistance(aligned, tip),
            RotatedDimension rotated => RotatedDimensionDistance(rotated, tip),
            _ => double.MaxValue
        };
    }

    private static double AlignedDimensionDistance(AlignedDimension dim, Autodesk.AutoCAD.Geometry.Point3d tip)
    {
        var axis = dim.XLine2Point - dim.XLine1Point;
        var length = axis.Length;
        if (length < 1e-9) return double.MaxValue;
        var unit = axis / length;
        var offset = dim.DimLinePoint - dim.XLine1Point;
        var perpendicular = offset - unit * offset.DotProduct(unit);
        var first = dim.XLine1Point + perpendicular;
        var second = dim.XLine2Point + perpendicular;
        return Math.Min(SegmentDistance(tip, first, second),
            Math.Min(SegmentDistance(tip, dim.XLine1Point, first), SegmentDistance(tip, dim.XLine2Point, second)));
    }

    private static double RotatedDimensionDistance(RotatedDimension dim, Autodesk.AutoCAD.Geometry.Point3d tip)
    {
        var axis = new Autodesk.AutoCAD.Geometry.Vector3d(Math.Cos(dim.Rotation), Math.Sin(dim.Rotation), 0);
        var first = dim.DimLinePoint + axis * (dim.XLine1Point - dim.DimLinePoint).DotProduct(axis);
        var second = dim.DimLinePoint + axis * (dim.XLine2Point - dim.DimLinePoint).DotProduct(axis);
        return Math.Min(SegmentDistance(tip, first, second),
            Math.Min(SegmentDistance(tip, dim.XLine1Point, first), SegmentDistance(tip, dim.XLine2Point, second)));
    }

    private static IEnumerable<string> RefreshArrowSources(Document doc, Database db)
    {
        if (!ArrowDirty.Remove(db, out var modified)) return Array.Empty<string>();
        foreach (var noteId in modified)
        {
            try
            {
                if (noteId.IsNull || noteId.IsErased) continue;
                string? oldHandle;
                bool isGc;
                using (var tr = db.TransactionManager.StartOpenCloseTransaction())
                {
                    var link = tr.GetObject(noteId, OpenMode.ForRead, false) is MLeader note
                        ? LengthData.ReadLink(note) : null;
                    oldHandle = link?.SourceHandle;
                    isGc = link?.IsGc == true;
                    tr.Commit();
                }
                if (oldHandle == null || isGc) continue; // GC follows its registered DIM, not its arrow.
                var newSource = FindSourceAtArrow(doc, noteId);
                if (!newSource.IsNull)
                {
                    using var tr = db.TransactionManager.StartOpenCloseTransaction();
                    var source = (Entity)tr.GetObject(newSource, OpenMode.ForRead, false);
                    var newHandle = source.Handle.ToString();
                    tr.Commit();
                    if (!newHandle.Equals(oldHandle, StringComparison.OrdinalIgnoreCase))
                        Bind(doc, noteId, newSource, out _);
                }
                else if (oldHandle != "0") ClearSource(doc, noteId);
            }
            catch (System.Exception) { }
        }
        return modified.Where(id => !id.IsNull).Select(id => id.Handle.ToString()).ToList();
    }

    private static void ClearSource(Document doc, ObjectId noteId)
    {
        using var tr = doc.Database.TransactionManager.StartOpenCloseTransaction();
        if (tr.GetObject(noteId, OpenMode.ForRead, false) is not MLeader note ||
            LengthData.ReadLink(note) is not { } link) return;
        link.SourceHandle = "0";
        note.UpgradeOpen();
        _syncing = true;
        try
        {
            LengthData.WriteLink(note, link);
            LengthData.WriteText(note, LengthData.Render(link, null));
            tr.Commit();
        }
        finally { _syncing = false; }
    }

    internal static bool Unlink(Document doc, string noteHandle, out string result)
    {
        result = "";
        var id = LengthData.Resolve(doc.Database, noteHandle);
        if (id.IsNull || id.IsErased) { result = "Không tìm được ghi chú."; return false; }
        try
        {
            using var tr = doc.Database.TransactionManager.StartOpenCloseTransaction();
            if (tr.GetObject(id, OpenMode.ForRead, false) is not Entity note || LengthData.ReadLink(note) == null)
            { result = "Ghi chú này chưa liên kết."; return false; }
            note.UpgradeOpen();
            _syncing = true;
            try { LengthData.Unlink(note); }
            finally { _syncing = false; }
            tr.Commit();
            MarkDirty(doc);
            result = "Đã hủy liên kết, giữ nguyên nội dung chú thích.";
            return true;
        }
        catch (System.Exception ex) { _syncing = false; result = ex.Message; return false; }
    }
}
