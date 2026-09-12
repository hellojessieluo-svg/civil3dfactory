using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;

namespace CadBatchPlugin;

public sealed class Commands
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    [CommandMethod("C3DF-EXPORTATTRS")]
    public void ExportAttributes()
    {
        Document doc = Application.DocumentManager.MdiActiveDocument;
        string output = RequiredEnvironment("C3DF_ATTR_OUTPUT_JSON");
        var result = new ExportResult();

        using (Transaction tr = doc.Database.TransactionManager.StartTransaction())
        {
            var layouts = (DBDictionary)tr.GetObject(doc.Database.LayoutDictionaryId, OpenMode.ForRead);
            foreach (DBDictionaryEntry entry in layouts)
            {
                var layout = (Layout)tr.GetObject(entry.Value, OpenMode.ForRead);
                var space = (BlockTableRecord)tr.GetObject(layout.BlockTableRecordId, OpenMode.ForRead);
                string spaceName = layout.ModelType ? "ModelSpace" : "PaperSpace";

                foreach (ObjectId entityId in space)
                {
                    if (tr.GetObject(entityId, OpenMode.ForRead, false) is not BlockReference block ||
                        block.AttributeCollection.Count == 0)
                    {
                        continue;
                    }

                    var blockData = new ExportBlock
                    {
                        Space = spaceName,
                        Layout = layout.LayoutName,
                        BlockName = EffectiveName(tr, block),
                        BlockHandle = block.Handle.ToString(),
                    };

                    var tagCounts = new Dictionary<string, int>(StringComparer.Ordinal);
                    foreach (ObjectId attributeId in block.AttributeCollection)
                    {
                        if (tr.GetObject(attributeId, OpenMode.ForRead, false) is not AttributeReference attribute)
                        {
                            continue;
                        }
                        int tagIndex = tagCounts.TryGetValue(attribute.Tag, out int current) ? current : 0;
                        tagCounts[attribute.Tag] = tagIndex + 1;
                        blockData.Attributes.Add(new ExportAttribute
                        {
                            Tag = attribute.Tag,
                            TagIndex = tagIndex,
                            AttributeHandle = attribute.Handle.ToString(),
                            Value = attribute.TextString,
                        });
                    }
                    if (blockData.Attributes.Count > 0)
                    {
                        result.Blocks.Add(blockData);
                    }
                }
            }
            tr.Commit();
        }

        WriteJson(output, result);
        doc.Editor.WriteMessage($"\nC3DF-EXPORTATTRS: {result.Blocks.Count} blocks exported.\n");
    }

    [CommandMethod("C3DF-UPDATEATTRS")]
    public void UpdateAttributes()
    {
        Document doc = Application.DocumentManager.MdiActiveDocument;
        string input = RequiredEnvironment("C3DF_ATTR_UPDATE_JSON");
        string reportPath = RequiredEnvironment("C3DF_ATTR_REPORT_JSON");
        UpdatePayload payload = JsonSerializer.Deserialize<UpdatePayload>(File.ReadAllText(input), JsonOptions)
            ?? throw new InvalidDataException("Invalid update JSON.");
        var report = new UpdateReport();

        using (Transaction tr = doc.Database.TransactionManager.StartTransaction())
        {
            foreach (UpdateBlock requestedBlock in payload.Blocks)
            {
                BlockReference? block = FindBlock(doc.Database, tr, requestedBlock.BlockHandle, report);
                if (block is null)
                {
                    continue;
                }
                string actualName = EffectiveName(tr, block);
                if (!string.IsNullOrEmpty(requestedBlock.BlockName) &&
                    !string.Equals(actualName, requestedBlock.BlockName, StringComparison.OrdinalIgnoreCase))
                {
                    report.Issues.Add(new UpdateIssue
                    {
                        BlockHandle = requestedBlock.BlockHandle,
                        Status = "block_name_mismatch",
                        Detail = $"expected={requestedBlock.BlockName}; actual={actualName}",
                    });
                    continue;
                }

                var attributes = block.AttributeCollection
                    .Cast<ObjectId>()
                    .Select(id => tr.GetObject(id, OpenMode.ForRead, false))
                    .OfType<AttributeReference>()
                    .ToList();

                foreach (UpdateAttribute requested in requestedBlock.Attributes)
                {
                    AttributeReference? attribute = FindAttribute(attributes, requested);
                    if (attribute is null)
                    {
                        report.Issues.Add(new UpdateIssue
                        {
                            BlockHandle = requestedBlock.BlockHandle,
                            Tag = requested.Tag,
                            Status = "attribute_not_found",
                        });
                        continue;
                    }
                    string currentValue = attribute.TextString ?? string.Empty;
                    if (currentValue != requested.OriginalValue && !payload.Force)
                    {
                        report.Issues.Add(new UpdateIssue
                        {
                            BlockHandle = requestedBlock.BlockHandle,
                            Tag = requested.Tag,
                            Status = "conflict_skipped",
                            Detail = $"exported={requested.OriginalValue}; current={currentValue}; requested={requested.Value}",
                        });
                        continue;
                    }
                    attribute.UpgradeOpen();
                    attribute.TextString = requested.Value;
                    report.Changed++;
                }
            }
            tr.Commit();
        }

        WriteJson(reportPath, report);
        doc.Editor.WriteMessage($"\nC3DF-UPDATEATTRS: {report.Changed} attributes updated.\n");
    }

    [CommandMethod("C3DF-BATCHEXPORTATTRS")]
    public void BatchExportAttributes()
    {
        string input = RequiredEnvironment("C3DF_ATTR_BATCH_JSON");
        string output = RequiredEnvironment("C3DF_ATTR_OUTPUT_JSON");
        BatchExportPayload payload = JsonSerializer.Deserialize<BatchExportPayload>(File.ReadAllText(input), JsonOptions)
            ?? throw new InvalidDataException("Invalid batch export JSON.");
        var result = new BatchExportResult();

        foreach (BatchExportJob job in payload.Jobs)
        {
            var item = new BatchExportFile { Id = job.Id };
            result.Files.Add(item);
            try
            {
                using var database = new Database(false, true);
                database.ReadDwgFile(job.Path, FileOpenMode.OpenForReadAndAllShare, true, string.Empty);
                database.CloseInput(true);
                item.Blocks = ScanDatabase(database, payload.BlockNameContains);
            }
            catch (System.Exception ex)
            {
                item.Error = ex.Message;
            }
        }

        WriteJson(output, result);
        Application.DocumentManager.MdiActiveDocument.Editor.WriteMessage(
            $"\nC3DF-BATCHEXPORTATTRS: {result.Files.Count} files processed.\n");
    }

    [CommandMethod("C3DF-BATCHUPDATEATTRS")]
    public void BatchUpdateAttributes()
    {
        string input = RequiredEnvironment("C3DF_ATTR_BATCH_JSON");
        string output = RequiredEnvironment("C3DF_ATTR_REPORT_JSON");
        BatchUpdatePayload payload = JsonSerializer.Deserialize<BatchUpdatePayload>(File.ReadAllText(input), JsonOptions)
            ?? throw new InvalidDataException("Invalid batch update JSON.");
        var result = new BatchUpdateResult();

        foreach (BatchUpdateJob job in payload.Jobs)
        {
            var item = new BatchUpdateFile { Id = job.Id };
            result.Files.Add(item);
            try
            {
                using var database = new Database(false, true);
                database.ReadDwgFile(job.InputDwg, FileOpenMode.OpenForReadAndWriteNoShare, true, string.Empty);
                database.CloseInput(true);
                UpdateReport update = ApplyUpdates(database, new UpdatePayload
                {
                    Force = job.Force,
                    Blocks = job.Blocks,
                });
                item.Changed = update.Changed;
                item.Issues = update.Issues;
                if (item.Changed > 0)
                {
                    database.SaveAs(job.OutputDwg, DwgVersion.Current);
                }
            }
            catch (System.Exception ex)
            {
                item.Error = ex.Message;
            }
        }

        WriteJson(output, result);
        Application.DocumentManager.MdiActiveDocument.Editor.WriteMessage(
            $"\nC3DF-BATCHUPDATEATTRS: {result.Files.Count} files processed.\n");
    }

    private static List<ExportBlock> ScanDatabase(Database database, string blockNameContains)
    {
        var blocks = new List<ExportBlock>();
        using Transaction tr = database.TransactionManager.StartTransaction();
        var layouts = (DBDictionary)tr.GetObject(database.LayoutDictionaryId, OpenMode.ForRead);
        foreach (DBDictionaryEntry entry in layouts)
        {
            var layout = (Layout)tr.GetObject(entry.Value, OpenMode.ForRead);
            var space = (BlockTableRecord)tr.GetObject(layout.BlockTableRecordId, OpenMode.ForRead);
            string spaceName = layout.ModelType ? "ModelSpace" : "PaperSpace";
            foreach (ObjectId entityId in space)
            {
                if (tr.GetObject(entityId, OpenMode.ForRead, false) is not BlockReference block ||
                    block.AttributeCollection.Count == 0)
                {
                    continue;
                }
                string blockName = EffectiveName(tr, block);
                if (!string.IsNullOrEmpty(blockNameContains) &&
                    blockName.IndexOf(blockNameContains, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }
                var blockData = new ExportBlock
                {
                    Space = spaceName,
                    Layout = layout.LayoutName,
                    BlockName = blockName,
                    BlockHandle = block.Handle.ToString(),
                };
                var tagCounts = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (ObjectId attributeId in block.AttributeCollection)
                {
                    if (tr.GetObject(attributeId, OpenMode.ForRead, false) is not AttributeReference attribute)
                    {
                        continue;
                    }
                    int tagIndex = tagCounts.TryGetValue(attribute.Tag, out int current) ? current : 0;
                    tagCounts[attribute.Tag] = tagIndex + 1;
                    blockData.Attributes.Add(new ExportAttribute
                    {
                        Tag = attribute.Tag,
                        TagIndex = tagIndex,
                        AttributeHandle = attribute.Handle.ToString(),
                        Value = attribute.TextString,
                    });
                }
                if (blockData.Attributes.Count > 0)
                {
                    blocks.Add(blockData);
                }
            }
        }
        tr.Commit();
        return blocks;
    }

    private static UpdateReport ApplyUpdates(Database database, UpdatePayload payload)
    {
        var report = new UpdateReport();
        using Transaction tr = database.TransactionManager.StartTransaction();
        foreach (UpdateBlock requestedBlock in payload.Blocks)
        {
            BlockReference? block = FindBlock(database, tr, requestedBlock.BlockHandle, report);
            if (block is null)
            {
                continue;
            }
            string actualName = EffectiveName(tr, block);
            if (!string.IsNullOrEmpty(requestedBlock.BlockName) &&
                !string.Equals(actualName, requestedBlock.BlockName, StringComparison.OrdinalIgnoreCase))
            {
                report.Issues.Add(new UpdateIssue
                {
                    BlockHandle = requestedBlock.BlockHandle,
                    Status = "block_name_mismatch",
                    Detail = $"expected={requestedBlock.BlockName}; actual={actualName}",
                });
                continue;
            }
            var attributes = block.AttributeCollection
                .Cast<ObjectId>()
                .Select(id => tr.GetObject(id, OpenMode.ForRead, false))
                .OfType<AttributeReference>()
                .ToList();
            foreach (UpdateAttribute requested in requestedBlock.Attributes)
            {
                AttributeReference? attribute = FindAttribute(attributes, requested);
                if (attribute is null)
                {
                    report.Issues.Add(new UpdateIssue
                    {
                        BlockHandle = requestedBlock.BlockHandle,
                        Tag = requested.Tag,
                        Status = "attribute_not_found",
                    });
                    continue;
                }
                string currentValue = attribute.TextString ?? string.Empty;
                if (currentValue == requested.Value)
                {
                    continue;
                }
                if (currentValue != requested.OriginalValue && !payload.Force)
                {
                    report.Issues.Add(new UpdateIssue
                    {
                        BlockHandle = requestedBlock.BlockHandle,
                        Tag = requested.Tag,
                        Status = "conflict_skipped",
                        Detail = $"exported={requested.OriginalValue}; current={currentValue}; requested={requested.Value}",
                    });
                    continue;
                }
                attribute.UpgradeOpen();
                attribute.TextString = requested.Value;
                report.Changed++;
            }
        }
        tr.Commit();
        return report;
    }

    private static BlockReference? FindBlock(Database database, Transaction tr, string handleText, UpdateReport report)
    {
        try
        {
            long value = long.Parse(handleText, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            ObjectId id = database.GetObjectId(false, new Handle(value), 0);
            return tr.GetObject(id, OpenMode.ForRead, false) as BlockReference;
        }
        catch (System.Exception ex)
        {
            report.Issues.Add(new UpdateIssue
            {
                BlockHandle = handleText,
                Status = "block_not_found",
                Detail = ex.Message,
            });
            return null;
        }
    }

    private static AttributeReference? FindAttribute(List<AttributeReference> attributes, UpdateAttribute requested)
    {
        if (!string.IsNullOrWhiteSpace(requested.AttributeHandle))
        {
            AttributeReference? byHandle = attributes.FirstOrDefault(
                a => string.Equals(a.Handle.ToString(), requested.AttributeHandle, StringComparison.OrdinalIgnoreCase));
            if (byHandle is not null)
            {
                return byHandle;
            }
        }
        return attributes.Where(a => a.Tag == requested.Tag).Skip(requested.TagIndex).FirstOrDefault();
    }

    private static string EffectiveName(Transaction tr, BlockReference block)
    {
        ObjectId definitionId = block.IsDynamicBlock ? block.DynamicBlockTableRecord : block.BlockTableRecord;
        return ((BlockTableRecord)tr.GetObject(definitionId, OpenMode.ForRead)).Name;
    }

    private static string RequiredEnvironment(string name)
    {
        string? value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"Missing environment variable: {name}");
        }
        return value;
    }

    private static void WriteJson<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
        File.WriteAllText(path, JsonSerializer.Serialize(value, JsonOptions));
    }
}

public sealed class ExportResult
{
    public List<ExportBlock> Blocks { get; set; } = new();
}

public sealed class ExportBlock
{
    public string Space { get; set; } = string.Empty;
    public string Layout { get; set; } = string.Empty;
    public string BlockName { get; set; } = string.Empty;
    public string BlockHandle { get; set; } = string.Empty;
    public List<ExportAttribute> Attributes { get; set; } = new();
}

public sealed class ExportAttribute
{
    public string Tag { get; set; } = string.Empty;
    public int TagIndex { get; set; }
    public string AttributeHandle { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
}

public sealed class UpdatePayload
{
    public bool Force { get; set; }
    public List<UpdateBlock> Blocks { get; set; } = new();
}

public sealed class UpdateBlock
{
    public string BlockHandle { get; set; } = string.Empty;
    public string BlockName { get; set; } = string.Empty;
    public List<UpdateAttribute> Attributes { get; set; } = new();
}

public sealed class UpdateAttribute
{
    public string Tag { get; set; } = string.Empty;
    public int TagIndex { get; set; }
    public string AttributeHandle { get; set; } = string.Empty;
    public string OriginalValue { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
}

public sealed class UpdateReport
{
    public int Changed { get; set; }
    public List<UpdateIssue> Issues { get; set; } = new();
}

public sealed class UpdateIssue
{
    public string BlockHandle { get; set; } = string.Empty;
    public string? Tag { get; set; }
    public string Status { get; set; } = string.Empty;
    public string? Detail { get; set; }
}

public sealed class BatchExportPayload
{
    public List<BatchExportJob> Jobs { get; set; } = new();
    // Case-insensitive substring of the block name that marks a title block (e.g. C3DF-TITLEBLOCK-A3).
    public string BlockNameContains { get; set; } = "TITLE";
}

public sealed class BatchExportJob
{
    public string Id { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
}

public sealed class BatchExportResult
{
    public List<BatchExportFile> Files { get; set; } = new();
}

public sealed class BatchExportFile
{
    public string Id { get; set; } = string.Empty;
    public List<ExportBlock> Blocks { get; set; } = new();
    public string? Error { get; set; }
}

public sealed class BatchUpdatePayload
{
    public List<BatchUpdateJob> Jobs { get; set; } = new();
}

public sealed class BatchUpdateJob
{
    public string Id { get; set; } = string.Empty;
    public string InputDwg { get; set; } = string.Empty;
    public string OutputDwg { get; set; } = string.Empty;
    public bool Force { get; set; }
    public List<UpdateBlock> Blocks { get; set; } = new();
}

public sealed class BatchUpdateResult
{
    public List<BatchUpdateFile> Files { get; set; } = new();
}

public sealed class BatchUpdateFile
{
    public string Id { get; set; } = string.Empty;
    public int Changed { get; set; }
    public List<UpdateIssue> Issues { get; set; } = new();
    public string? Error { get; set; }
}
