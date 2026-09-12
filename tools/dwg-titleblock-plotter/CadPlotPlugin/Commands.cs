using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.PlottingServices;
using Autodesk.AutoCAD.Runtime;
using AcDbPlotType = Autodesk.AutoCAD.DatabaseServices.PlotType;

namespace CadPlotPlugin;

// Plots title blocks to PDF with PlotEngine inside accoreconsole (headless core).
// C3DF-TESTPLOT  = single-sheet feasibility test (verified: headless produces a correct A3/mono/landscape PDF).
// C3DF-BATCHPLOT = one process, many drawings: each DWG is read as a side database and
//                  every title block whose name contains a keyword is plotted to its own PDF.
public sealed class Commands
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    // Attribute tags for sheet title / sheet number, in priority order (kept in sync with the Python side).
    private static readonly string[] TumingTags = { "SHEET_TITLE", "TITLE", "DWGNAME" };
    private static readonly string[] TuhaoTags = { "SHEET_NO", "SHEETNO", "NUMBER", "DWGNO" };

    [CommandMethod("C3DF-TESTPLOT")]
    public void TestPlot()
    {
        Document doc = Application.DocumentManager.MdiActiveDocument;
        Database db = doc.Database;
        Editor ed = doc.Editor;
        string outPdf = Environment.GetEnvironmentVariable("C3DF_PLOT_OUTPUT")
            ?? Path.Combine(Path.GetTempPath(), "c3df_testplot.pdf");
        string blockContains = Environment.GetEnvironmentVariable("C3DF_PLOT_BLOCK") ?? "TITLE";
        try
        {
            Application.SetSystemVariable("BACKGROUNDPLOT", 0);
            Application.SetSystemVariable("PLOTTRANSPARENCYOVERRIDE", 1); // 1 = do not plot transparency (solid fills)
            ObjectId layoutId = ObjectId.Null;
            Extents3d window = new Extents3d();
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                foreach (var hit in FindFrames(tr, db, new List<string> { blockContains }, new List<string>()))
                {
                    layoutId = hit.LayoutId;
                    window = hit.Window;
                    break;
                }
                tr.Commit();
            }
            if (layoutId.IsNull) { ed.WriteMessage($"\nC3DF-TESTPLOT: no layout contains a title block matching \"{blockContains}\".\n"); return; }
            PlotWindow(db, layoutId, window, outPdf, "A3", "monochrome.ctb", false, false);
            bool ok = File.Exists(outPdf);
            long size = ok ? new FileInfo(outPdf).Length : 0;
            ed.WriteMessage($"\nC3DF-TESTPLOT: {(ok ? "OK" : "NO-FILE")} -> {outPdf} ({size} bytes)\n");
        }
        catch (System.Exception ex)
        {
            ed.WriteMessage($"\nC3DF-TESTPLOT ERROR: {ex.GetType().Name}: {ex.Message}\n");
        }
    }

    [CommandMethod("C3DF-BATCHPLOT")]
    public void BatchPlot()
    {
        string inPath = RequiredEnv("C3DF_PLOT_BATCH_JSON");
        string resPath = RequiredEnv("C3DF_PLOT_RESULT_JSON");
        BatchPlotPayload payload = JsonSerializer.Deserialize<BatchPlotPayload>(File.ReadAllText(inPath), JsonOpts)
            ?? throw new InvalidDataException("Invalid batch plot JSON.");
        var result = new BatchPlotResult();
        Editor ed = Application.DocumentManager.MdiActiveDocument.Editor;

        Application.SetSystemVariable("BACKGROUNDPLOT", 0);
        // 2 = force plotting transparency, 1 = force off (transparent objects print solid). Follows the payload flag; default 1.
        Application.SetSystemVariable("PLOTTRANSPARENCYOVERRIDE", payload.PlotTransparency ? 2 : 1);
        // When > 0, raise the "default" lineweight (hundredths of mm) so thin text that uses the
        // default lineweight gets a visible pen width instead of a faint hairline.
        if (payload.LwDefault > 0) Application.SetSystemVariable("LWDEFAULT", payload.LwDefault);
        // Prefer the style sheet named explicitly by the Python side (acad.ctb / monochrome.ctb / a
        // located file name); fall back to the legacy Monochrome boolean for older callers.
        string ctb = !string.IsNullOrWhiteSpace(payload.Ctb)
            ? payload.Ctb.Trim()
            : (payload.Monochrome ? "monochrome.ctb" : "");
        Directory.CreateDirectory(payload.OutputDir);

        foreach (BatchPlotJob job in payload.Jobs)
        {
            var file = new BatchPlotFile { Id = job.Id };
            result.Files.Add(file);
            Database? previous = HostApplicationServices.WorkingDatabase;
            try
            {
                using var db = new Database(false, true);
                db.ReadDwgFile(job.Path, FileOpenMode.OpenForReadAndAllShare, true, null);
                db.CloseInput(true);
                // Required: the side database must be the working database for PlotEngine to plot its layouts.
                HostApplicationServices.WorkingDatabase = db;
                // Required: side databases do not load xrefs automatically; sheets that depend on xrefs
                // would silently plot blank, so resolve them explicitly.
                PrepareXrefs(db, job, file, ed);
                // Safety net: re-resolve text styles before plotting (the interactive regen step that
                // headless mode skips) so TrueType text does not print faint.
                int restyled = RefreshTextStyles(db);
                if (restyled > 0) ed.WriteMessage($"\nC3DF-BATCHPLOT [{job.Id}]: re-resolved {restyled} text style(s).\n");
                PlotDatabaseTitleBlocks(db, payload, job, file, ctb);
            }
            catch (System.Exception ex)
            {
                file.Error = $"{ex.GetType().Name}: {ex.Message}";
                ed.WriteMessage($"\nC3DF-BATCHPLOT [{job.Id}] ERROR: {file.Error}\n");
            }
            finally
            {
                HostApplicationServices.WorkingDatabase = previous;
            }
        }

        WriteJson(resPath, result);
        int pdfs = result.Files.Sum(f => f.Pdfs.Count);
        ed.WriteMessage($"\nC3DF-BATCHPLOT: {result.Files.Count} files, {pdfs} pdfs.\n");
    }

    /// <summary>
    /// Xref preparation. A side database (ReadDwgFile) does NOT load xrefs automatically: a sheet whose
    /// content lives in xrefs plots as a blank page with no error and a valid-looking PDF.
    /// Two steps: (1) plotting uses a temporary ASCII-named copy, so relative xref paths cannot resolve
    /// from the copy's folder; rebuild them as absolute paths from the original folder (origin_dir),
    /// in memory only. (2) Call ResolveXrefs explicitly and report anything still unresolved in Errors,
    /// so a blank page is never passed off as a finished sheet.
    /// </summary>
    private static int PrepareXrefs(Database db, BatchPlotJob job, BatchPlotFile file, Editor ed)
    {
        var names = new List<string>();
        using (Transaction tr = db.TransactionManager.StartTransaction())
        {
            var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            foreach (ObjectId id in bt)
            {
                var btr = (BlockTableRecord)tr.GetObject(id, OpenMode.ForRead);
                if (!btr.IsFromExternalReference) continue;
                names.Add(btr.Name);
                string stored = btr.PathName ?? string.Empty;
                if (stored.Length == 0 || System.IO.Path.IsPathRooted(stored)) continue;
                if (string.IsNullOrEmpty(job.OriginDir)) continue;
                string full;
                try { full = System.IO.Path.GetFullPath(System.IO.Path.Combine(job.OriginDir, stored)); }
                catch { continue; }
                if (!File.Exists(full)) continue;
                btr.UpgradeOpen();
                btr.PathName = full;
            }
            tr.Commit();
        }
        if (names.Count == 0) return 0;

        db.ResolveXrefs(true, false);

        var unresolved = new List<string>();
        using (Transaction tr = db.TransactionManager.StartTransaction())
        {
            var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            foreach (ObjectId id in bt)
            {
                var btr = (BlockTableRecord)tr.GetObject(id, OpenMode.ForRead);
                if (!btr.IsFromExternalReference) continue;
                if (btr.XrefStatus != XrefStatus.Resolved)
                    unresolved.Add($"{btr.Name}({btr.XrefStatus})");
            }
            tr.Commit();
        }
        foreach (string bad in unresolved)
        {
            string msg = $"Unresolved xref: {bad} -- sheet content is missing; this PDF is not a deliverable.";
            file.Errors.Add(msg);
            ed.WriteMessage($"\nC3DF-BATCHPLOT [{job.Id}] WARN: {msg}\n");
        }
        ed.WriteMessage($"\nC3DF-BATCHPLOT [{job.Id}]: {names.Count} xref(s), {unresolved.Count} unresolved.\n");
        return names.Count;
    }

    private static void PlotDatabaseTitleBlocks(Database db, BatchPlotPayload p, BatchPlotJob job, BatchPlotFile file, string ctb)
    {
        var hits = new List<TitleHit>();
        using (Transaction tr = db.TransactionManager.StartTransaction())
        {
            hits.AddRange(FindFrames(tr, db, p.BlockKeywords, p.LayerKeywords));
            tr.Commit();
        }
        foreach (TitleHit hit in hits)
        {
            string baseName = Sanitize((hit.Tuhao + " " + hit.Tuming).Trim());
            if (baseName.Length == 0) baseName = Sanitize($"{job.Stem}_{hit.LayoutName}");
            string outPdf = UniquePath(Path.Combine(p.OutputDir, baseName + ".pdf"));
            // eLayoutNotCurrent fix: PlotInfoValidator requires the target layout to be current.
            // The side database is already the WorkingDatabase, so switch to the layout that holds
            // the title block (model or a paper layout) unless it is current already. If switching
            // fails, let the plot below throw and record the concrete error.
            try
            {
                if (!string.Equals(LayoutManager.Current.CurrentLayout, hit.LayoutName, StringComparison.Ordinal))
                    LayoutManager.Current.CurrentLayout = hit.LayoutName;
            }
            catch (System.Exception) { }
            System.Exception? lastError = null;
            for (int attempt = 1; attempt <= 2; attempt++)
            {
                try
                {
                    if (File.Exists(outPdf)) File.Delete(outPdf);
                    PlotWindow(db, hit.LayoutId, hit.Window, outPdf, p.Paper, ctb, p.PlotTransparency, p.PrintLineweights);
                    long size = File.Exists(outPdf) ? new FileInfo(outPdf).Length : 0;
                    if (size < 1024) throw new IOException($"PDF missing or too small ({size} bytes).");
                    file.Pdfs.Add(new BatchPlotPdf
                    {
                        Name = Path.GetFileName(outPdf),
                        Path = outPdf,
                        Layout = hit.LayoutName,
                        Ok = true,
                        Size = size,
                    });
                    lastError = null;
                    break;
                }
                catch (System.Exception ex)
                {
                    lastError = ex;
                    if (File.Exists(outPdf)) File.Delete(outPdf);
                    if (attempt < 2) System.Threading.Thread.Sleep(300);
                }
            }
            if (lastError != null)
            {
                file.Errors.Add($"{hit.LayoutName}/{Path.GetFileName(outPdf)}: {lastError.GetType().Name}: {lastError.Message}");
            }
        }
    }

    // Two kinds of frames: (1) block references whose name contains any keyword,
    // (2) closed polylines whose layer name contains any keyword.
    private static IEnumerable<TitleHit> FindFrames(Transaction tr, Database db, List<string> blockKeywords, List<string> layerKeywords)
    {
        var layouts = (DBDictionary)tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead);
        foreach (DBDictionaryEntry entry in layouts)
        {
            var layout = (Layout)tr.GetObject(entry.Value, OpenMode.ForRead);
            var space = (BlockTableRecord)tr.GetObject(layout.BlockTableRecordId, OpenMode.ForRead);
            foreach (ObjectId id in space)
            {
                DBObject obj = tr.GetObject(id, OpenMode.ForRead);
                if (obj is BlockReference br)
                {
                    // Attributes are not required: plain blocks count as frames; attributes only affect PDF naming.
                    if (blockKeywords.Count == 0) continue;
                    if (!MatchesAny(EffectiveName(tr, br), blockKeywords)) continue;
                    var (tuhao, tuming) = ReadTitleName(tr, br);
                    yield return new TitleHit
                    {
                        LayoutId = entry.Value,
                        LayoutName = layout.LayoutName,
                        Window = br.GeometricExtents,
                        Tuhao = tuhao,
                        Tuming = tuming,
                    };
                }
                else if (layerKeywords.Count > 0 && IsClosedPolyline(obj))
                {
                    var ent = (Entity)obj;
                    if (!MatchesAny(ent.Layer, layerKeywords)) continue;
                    Extents3d window;
                    try { window = ent.GeometricExtents; }
                    catch (System.Exception) { continue; } // degenerate polyline without extents; skip
                    yield return new TitleHit
                    {
                        LayoutId = entry.Value,
                        LayoutName = layout.LayoutName,
                        Window = window,
                    };
                }
            }
        }
    }

    private static bool IsClosedPolyline(DBObject obj) => obj switch
    {
        Polyline pl => pl.Closed,
        Polyline2d p2 => p2.Closed,
        Polyline3d p3 => p3.Closed,
        _ => false,
    };

    private static bool MatchesAny(string name, List<string> keywords)
        => keywords.Any(k => name.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0);

    // Pre-plot safety net: re-assign every TrueType text style's font descriptor unchanged. That marks
    // the style as modified and forces accoreconsole to re-resolve the typeface, a step the interactive
    // front end does implicitly and headless plotting skips (symptom: faint text / wrong style).
    // Only forces re-resolution; never changes the designed font. A wrong typeface stored in the DWG
    // must still be fixed upstream at the data level.
    private static int RefreshTextStyles(Database db)
    {
        int touched = 0;
        using Transaction tr = db.TransactionManager.StartTransaction();
        var tst = (TextStyleTable)tr.GetObject(db.TextStyleTableId, OpenMode.ForRead);
        foreach (ObjectId id in tst)
        {
            if (tr.GetObject(id, OpenMode.ForRead) is not TextStyleTableRecord rec) continue;
            Autodesk.AutoCAD.GraphicsInterface.FontDescriptor f;
            try { f = rec.Font; }
            catch { continue; }
            if (string.IsNullOrEmpty(f.TypeFace)) continue; // SHX styles use FileName and are unaffected
            try
            {
                rec.UpgradeOpen();
                rec.Font = new Autodesk.AutoCAD.GraphicsInterface.FontDescriptor(
                    f.TypeFace, f.Bold, f.Italic, f.CharacterSet, f.PitchAndFamily);
                touched++;
            }
            catch { /* one failing style must not block the plot */ }
        }
        tr.Commit();
        return touched;
    }

    private static void PlotWindow(Database db, ObjectId layoutId, Extents3d window, string outPdf, string paper, string ctb, bool plotTransparency, bool printLineweights)
    {
        using Transaction tr = db.TransactionManager.StartTransaction();
        var layout = (Layout)tr.GetObject(layoutId, OpenMode.ForRead);
        Extents3d plotWindow = window;
        if (layout.ModelType)
        {
            // SetPlotWindowArea uses the active model view's DCS, while title-block
            // extents are WCS. Passing WCS directly produces a valid but blank PDF
            // when a model-space frame is far from the origin.
            ObjectId viewId = db.CurrentViewportTableRecordId;
            if (viewId.IsNull)
                throw new InvalidOperationException("Model-space active viewport is unavailable.");
            var view = (ViewportTableRecord)tr.GetObject(viewId, OpenMode.ForRead);
            Matrix3d wcsToDcs = Matrix3d.PlaneToWorld(view.ViewDirection);
            wcsToDcs = Matrix3d.Displacement(view.Target - Point3d.Origin) * wcsToDcs;
            wcsToDcs = Matrix3d.Rotation(
                -view.ViewTwist, view.ViewDirection, view.Target) * wcsToDcs;
            plotWindow = TransformWindowExtents(window, wcsToDcs.Inverse());
        }

        var ps = new PlotSettings(layout.ModelType);
        ps.CopyFrom(layout);
        PlotSettingsValidator psv = PlotSettingsValidator.Current;
        psv.SetPlotConfigurationName(ps, "DWG To PDF.pc3", null);
        psv.RefreshLists(ps);

        string[] matchingMedia = psv.GetCanonicalMediaNameList(ps).Cast<string>()
            .Where(m => m.IndexOf(paper, StringComparison.OrdinalIgnoreCase) >= 0)
            .ToArray();
        string? media = matchingMedia.FirstOrDefault(m =>
                m.IndexOf("full_bleed", StringComparison.OrdinalIgnoreCase) >= 0)
            ?? matchingMedia.FirstOrDefault();
        if (media == null)
            throw new InvalidOperationException($"DWG To PDF.pc3 does not provide requested paper: {paper}");
        psv.SetCanonicalMediaName(ps, media);

        psv.SetPlotWindowArea(ps, new Extents2d(
            plotWindow.MinPoint.X, plotWindow.MinPoint.Y,
            plotWindow.MaxPoint.X, plotWindow.MaxPoint.Y));
        psv.SetPlotType(ps, AcDbPlotType.Window);
        psv.SetUseStandardScale(ps, true);
        psv.SetStdScaleType(ps, StdScaleType.ScaleToFit);
        psv.SetPlotCentered(ps, true);
        psv.SetPlotPaperUnits(ps, PlotPaperUnit.Millimeters);
        // Landscape windows are rotated 90 degrees to fit the portrait media.
        double w = plotWindow.MaxPoint.X - plotWindow.MinPoint.X;
        double h = plotWindow.MaxPoint.Y - plotWindow.MinPoint.Y;
        psv.SetPlotRotation(ps, w >= h ? PlotRotation.Degrees090 : PlotRotation.Degrees000);
        if (!string.IsNullOrEmpty(ctb))
        {
            ps.PlotPlotStyles = true;
            psv.SetCurrentStyleSheet(ps, ctb);
        }
        else
        {
            ps.PlotPlotStyles = false;
        }
        ps.PrintLineweights = printLineweights; // plot object/layer lineweights (cures faint hairline text)
        ps.ScaleLineweights = false;
        ps.PlotTransparency = plotTransparency;

        var pi = new PlotInfo { Layout = layoutId, OverrideSettings = ps };
        // PlotInfoValidator still needs media matching enabled on a side database; disabling it throws
        // eNoMatchingMedia. The canonical media was chosen explicitly above; the validator only confirms it.
        var piv = new PlotInfoValidator { MediaMatchingPolicy = MatchingPolicy.MatchEnabled };
        piv.Validate(pi);

        using (PlotEngine pe = PlotFactory.CreatePublishEngine())
        {
            pe.BeginPlot(null, null);
            pe.BeginDocument(pi, outPdf, null, 1, true, outPdf);
            using (var ppi = new PlotPageInfo())
            {
                pe.BeginPage(ppi, pi, true, null);
                pe.BeginGenerateGraphics(null);
                pe.EndGenerateGraphics(null);
                pe.EndPage(null);
            }
            pe.EndDocument(null);
            pe.EndPlot(null);
        }
        tr.Commit();
    }

    private static Extents3d TransformWindowExtents(Extents3d source, Matrix3d transform)
    {
        Point3d[] points =
        {
            new(source.MinPoint.X, source.MinPoint.Y, 0),
            new(source.MaxPoint.X, source.MinPoint.Y, 0),
            new(source.MaxPoint.X, source.MaxPoint.Y, 0),
            new(source.MinPoint.X, source.MaxPoint.Y, 0),
        };
        var result = new Extents3d(
            points[0].TransformBy(transform), points[0].TransformBy(transform));
        for (int i = 1; i < points.Length; i++)
            result.AddPoint(points[i].TransformBy(transform));
        return result;
    }

    private static (string tuhao, string tuming) ReadTitleName(Transaction tr, BlockReference br)
    {
        var tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (ObjectId id in br.AttributeCollection)
        {
            if (tr.GetObject(id, OpenMode.ForRead) is not AttributeReference att) continue;
            if (!tags.ContainsKey(att.Tag)) tags[att.Tag] = att.TextString ?? string.Empty;
        }
        string tuming = TumingTags.Select(t => tags.TryGetValue(t, out var v) ? v.Trim() : "").FirstOrDefault(v => v.Length > 0) ?? "";
        string tuhao = TuhaoTags.Select(t => tags.TryGetValue(t, out var v) ? v.Trim() : "").FirstOrDefault(v => v.Length > 0) ?? "";
        return (tuhao, tuming);
    }

    private static string EffectiveName(Transaction tr, BlockReference br)
    {
        ObjectId defId = br.IsDynamicBlock ? br.DynamicBlockTableRecord : br.BlockTableRecord;
        return ((BlockTableRecord)tr.GetObject(defId, OpenMode.ForRead)).Name;
    }

    private static string Sanitize(string name)
        => Regex.Replace(name, "[\\\\/:*?\"<>|]", "_").Trim();

    private static string UniquePath(string path)
    {
        if (!File.Exists(path)) return path;
        string dir = Path.GetDirectoryName(path) ?? ".";
        string stem = Path.GetFileNameWithoutExtension(path);
        string ext = Path.GetExtension(path);
        for (int i = 2; ; i++)
        {
            string candidate = Path.Combine(dir, $"{stem}_{i}{ext}");
            if (!File.Exists(candidate)) return candidate;
        }
    }

    private static string RequiredEnv(string name)
    {
        string? value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value)) throw new InvalidOperationException($"Missing environment variable: {name}");
        return value;
    }

    private static void WriteJson<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
        File.WriteAllText(path, JsonSerializer.Serialize(value, JsonOpts));
    }
}

internal sealed class TitleHit
{
    public ObjectId LayoutId { get; set; }
    public string LayoutName { get; set; } = string.Empty;
    public Extents3d Window { get; set; }
    public string Tuhao { get; set; } = string.Empty;
    public string Tuming { get; set; } = string.Empty;
}

public sealed class BatchPlotPayload
{
    public List<string> BlockKeywords { get; set; } = new();
    public List<string> LayerKeywords { get; set; } = new();
    public string OutputDir { get; set; } = string.Empty;
    public string Paper { get; set; } = "A3";
    public bool Monochrome { get; set; } = true;
    public string Ctb { get; set; } = "";   // explicit style sheet name; empty = fall back to Monochrome
    public bool PlotTransparency { get; set; } = false;  // plot transparency; default off (transparent objects print solid)
    public bool PrintLineweights { get; set; } = false;  // plot lineweights; default off (legacy default, thin text prints as hairline)
    public int LwDefault { get; set; } = 0;              // > 0 sets LWDEFAULT (hundredths of mm, e.g. 30 = 0.30 mm); 0 = leave unchanged
    public List<BatchPlotJob> Jobs { get; set; } = new();
}

public sealed class BatchPlotJob
{
    public string Id { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public string Stem { get; set; } = string.Empty;
    /// <summary>Folder of the original DWG; used to rebuild relative xref paths from the temporary copy.</summary>
    public string OriginDir { get; set; } = string.Empty;
}

public sealed class BatchPlotResult
{
    public List<BatchPlotFile> Files { get; set; } = new();
}

public sealed class BatchPlotFile
{
    public string Id { get; set; } = string.Empty;
    public List<BatchPlotPdf> Pdfs { get; set; } = new();
    public List<string> Errors { get; set; } = new();
    public string? Error { get; set; }
}

public sealed class BatchPlotPdf
{
    public string Name { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public string Layout { get; set; } = string.Empty;
    public bool Ok { get; set; }
    public long Size { get; set; }
}
