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

// accoreconsole（无界面内核）里用 PlotEngine 把图框出成 PDF。
// C3DF-TESTPLOT  = 可行性单张测试（已验证：无界面能出正确 A3/黑白/横向 PDF）。
// C3DF-BATCHPLOT = 一次进程批量：逐张读侧数据库、每个含关键字的图框出一张 PDF。
public sealed class Commands
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    // 图名 / 图号 属性标签优先级（与 Python 端一致）
    private static readonly string[] TumingTags = { "02图名", "图名", "DWGNAME", "TITLE" };
    private static readonly string[] TuhaoTags = { "03图号", "图号", "DWGNO", "NUMBER" };

    [CommandMethod("C3DF-TESTPLOT")]
    public void TestPlot()
    {
        Document doc = Application.DocumentManager.MdiActiveDocument;
        Database db = doc.Database;
        Editor ed = doc.Editor;
        string outPdf = Environment.GetEnvironmentVariable("C3DF_PLOT_OUTPUT")
            ?? Path.Combine(Path.GetTempPath(), "c3df_testplot.pdf");
        string blockContains = Environment.GetEnvironmentVariable("C3DF_PLOT_BLOCK") ?? "图框";
        try
        {
            Application.SetSystemVariable("BACKGROUNDPLOT", 0);
            Application.SetSystemVariable("PLOTTRANSPARENCYOVERRIDE", 1); // 1=不打印透明度（实心）
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
            if (layoutId.IsNull) { ed.WriteMessage($"\nC3DF-TESTPLOT: 未找到含“{blockContains}”的图框布局。\n"); return; }
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
        // 2=强制打印透明度，1=强制不打印（透明对象出实心）。跟随 payload 开关，默认 1。
        Application.SetSystemVariable("PLOTTRANSPARENCYOVERRIDE", payload.PlotTransparency ? 2 : 1);
        // >0 时抬高“默认”线宽（百分之毫米），让走默认线宽的细字有可见笔宽（治 hairline 淡显）
        if (payload.LwDefault > 0) Application.SetSystemVariable("LWDEFAULT", payload.LwDefault);
        // 优先用 Python 端明确指定的样式表名（acad.ctb / monochrome.ctb / 定位到的原件名）；
        // 未指定时回退到旧的 Monochrome 布尔字段，保持对旧调用方的兼容。
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
                // 关键：把侧数据库设为当前工作库，PlotEngine 才能出它的布局
                HostApplicationServices.WorkingDatabase = db;
                // 关键：侧库不会自动加载外参，图面靠外参的图纸会静默出白纸——必须显式解析
                PrepareXrefs(db, job, file, ed);
                // 兜底：出图前重解析文字样式（无头缺失的“交互式 regen”那步），避免 TTF 发浅
                int restyled = RefreshTextStyles(db);
                if (restyled > 0) ed.WriteMessage($"\nC3DF-BATCHPLOT [{job.Id}]: 重解析文字样式 {restyled} 个。\n");
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
    /// 外参预处理。侧库（ReadDwgFile）**不会**自动加载 xref：图面内容全靠外参的图纸
    /// 会静默出白纸——不报错、PDF 照样生成（2026-08-27 项目B 1103 实测，12 张全是空图框）。
    /// 两步：①打印用的是临时英文副本，相对外参路径在副本目录下必然落空，
    /// 所以先按原图目录 origin_dir 把相对路径还原成绝对路径（只改内存侧库，不落盘）；
    /// ②显式 ResolveXrefs，再把没解析成功的报进 Errors，绝不让白纸冒充成品。
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
            string msg = $"外参未解析：{bad} —— 图面会缺内容，这张 PDF 不能当成品。";
            file.Errors.Add(msg);
            ed.WriteMessage($"\nC3DF-BATCHPLOT [{job.Id}] WARN: {msg}\n");
        }
        ed.WriteMessage($"\nC3DF-BATCHPLOT [{job.Id}]: 外参 {names.Count} 个，未解析 {unresolved.Count} 个。\n");
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
            // eLayoutNotCurrent 修复：PlotInfoValidator 要求目标布局必须是当前布局。
            // 侧库已设为 WorkingDatabase，出图前把图框所在的布局（模型或某个布局）切为当前；
            // 已是当前则不动。切换失败就交给下面 Plot 抛出并记录具体错误。
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

    // 两类图框：①块名含任一关键字的属性块 ②图层名含任一关键字的闭合多段线。
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
                    // 不要求属性块：普通块也算图框，属性只影响 PDF 命名
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
                    catch (System.Exception) { continue; } // 退化多段线无包围盒，跳过
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

    // 出图前兜底：把每个 TTF 文字样式的字体描述符原样重设一遍，标记样式已改，
    // 触发 accoreconsole 无头出图对 TTF typeface 的重解析——这一步交互式前台会隐含做，
    // 无头不做，导致“字发浅/样式不对”（根因见 踩坑.md 与 _memory feedback-dwg-plot-acc-font-datalevel）。
    // 只强制重解析、不改设计字体；typeface 本身填错（如 -黑体 实为 SimSun）的仍需上游字体工具在数据层修。
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
            if (string.IsNullOrEmpty(f.TypeFace)) continue; // SHX 走 FileName，不受此坑影响
            try
            {
                rec.UpgradeOpen();
                rec.Font = new Autodesk.AutoCAD.GraphicsInterface.FontDescriptor(
                    f.TypeFace, f.Bold, f.Italic, f.CharacterSet, f.PitchAndFamily);
                touched++;
            }
            catch { /* 单个样式失败不影响出图 */ }
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
        // 横图转 90 度贴合竖纸
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
        ps.PrintLineweights = printLineweights; // 打印对象/图层线宽（治 hairline 细字淡显）
        ps.ScaleLineweights = false;
        ps.PlotTransparency = plotTransparency;

        var pi = new PlotInfo { Layout = layoutId, OverrideSettings = ps };
        // PlotInfoValidator 在侧数据库上仍需开启介质匹配；关闭会抛
        // eNoMatchingMedia。前面已显式选定 canonical media，这里只让验证器完成匹配。
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
        var tags = new Dictionary<string, string>(StringComparer.Ordinal);
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
    public string Ctb { get; set; } = "";   // 明确的样式表名；空 = 回退到 Monochrome
    public bool PlotTransparency { get; set; } = false;  // 打印透明度；默认关（透明对象出实心）
    public bool PrintLineweights { get; set; } = false;  // 打印线宽；默认关（历史默认，细字打成 hairline）
    public int LwDefault { get; set; } = 0;              // >0 时设 LWDEFAULT（百分之毫米，如 30=0.30mm）；0=不动
    public List<BatchPlotJob> Jobs { get; set; } = new();
}

public sealed class BatchPlotJob
{
    public string Id { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public string Stem { get; set; } = string.Empty;
    /// <summary>原图所在目录：临时英文副本里还原相对外参路径要用它。</summary>
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
