using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace Civil3DFactory
{
    /// <summary>
    /// Minimal xlsx / csv writer: only the built-in .NET zip, no third-party dependency.
    /// Cells use inlineStr, no sharedStrings/styles; opens fine in both Excel and openpyxl.
    /// </summary>
    public static class Excel
    {
        /// <summary>Writes according to format (xlsx|csv|both) and returns the paths actually produced.
        /// Normalisation (case/whitespace) happens here; unknown values throw. Previously both branches could miss,
        /// writing no file yet reporting ok=true (closed 2026-08-20; callers no longer need their own guards).</summary>
        public static List<string> Write(string dir, string baseName, string[] headers,
                                         List<object[]> rows, string format)
        {
            format = string.IsNullOrWhiteSpace(format) ? "xlsx" : format.Trim().ToLowerInvariant();
            if (format != "xlsx" && format != "csv" && format != "both")
                throw new InvalidOperationException("excel_format accepts only xlsx|csv|both, got '" + format + "'.");
            var made = new List<string>();
            bool wantCsv = format == "both" || format == "csv";
            bool wantXlsx = format == "both" || format == "xlsx";

            if (wantCsv)
            {
                string p = Path.Combine(dir, baseName + ".csv");
                WriteCsv(p, headers, rows);
                made.Add(p);
            }
            if (wantXlsx)
            {
                string p = Path.Combine(dir, baseName + ".xlsx");
                WriteXlsx(p, headers, rows);
                made.Add(p);
            }
            return made;
        }

        static void WriteCsv(string path, string[] headers, List<object[]> rows)
        {
            var sb = new StringBuilder();
            sb.Append(string.Join(",", headers)).Append("\r\n");
            foreach (var r in rows)
            {
                for (int c = 0; c < r.Length; c++)
                {
                    if (c > 0) sb.Append(',');
                    sb.Append(Field(r[c]));
                }
                sb.Append("\r\n");
            }
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true)); // BOM so Excel decodes non-ASCII text correctly
        }

        static string Field(object o)
        {
            if (o == null) return "";
            if (o is double) return ((double)o).ToString("0.####", CultureInfo.InvariantCulture);
            if (o is int) return ((int)o).ToString(CultureInfo.InvariantCulture);
            string s = o.ToString();
            if (s.IndexOf(',') >= 0 || s.IndexOf('"') >= 0 || s.IndexOf('\n') >= 0)
                s = "\"" + s.Replace("\"", "\"\"") + "\"";
            return s;
        }

        static void WriteXlsx(string path, string[] headers, List<object[]> rows)
        {
            if (File.Exists(path)) File.Delete(path);
            using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                Add(zip, "[Content_Types].xml", ContentTypes);
                Add(zip, "_rels/.rels", RootRels);
                Add(zip, "xl/workbook.xml", Workbook);
                Add(zip, "xl/_rels/workbook.xml.rels", WorkbookRels);
                Add(zip, "xl/styles.xml", Styles);
                Add(zip, "xl/worksheets/sheet1.xml", Sheet(headers, rows));
            }
        }

        static void Add(ZipArchive zip, string name, string content)
        {
            var e = zip.CreateEntry(name, CompressionLevel.Optimal);
            using (var w = new StreamWriter(e.Open(), new UTF8Encoding(false))) w.Write(content);
        }

        const string ContentTypes =
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
            "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
            "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
            "<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>" +
            "<Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/>" +
            "<Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>" +
            "</Types>";

        const string RootRels =
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
            "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/>" +
            "</Relationships>";

        const string Workbook =
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" " +
            "xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">" +
            "<sheets><sheet name=\"Data\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>";

        const string WorkbookRels =
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
            "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/>" +
            "<Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/>" +
            "</Relationships>";

        const string Styles =
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
            "<styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">" +
            "<fonts count=\"2\"><font><sz val=\"11\"/><name val=\"Calibri\"/></font>" +
            "<font><b/><sz val=\"11\"/><color rgb=\"FFFFFFFF\"/><name val=\"Calibri\"/></font></fonts>" +
            "<fills count=\"3\"><fill><patternFill patternType=\"none\"/></fill>" +
            "<fill><patternFill patternType=\"gray125\"/></fill>" +
            "<fill><patternFill patternType=\"solid\"><fgColor rgb=\"FF1F4E78\"/><bgColor indexed=\"64\"/></patternFill></fill></fills>" +
            "<borders count=\"2\"><border/><border><bottom style=\"thin\"><color rgb=\"FFD9E2F3\"/></bottom></border></borders>" +
            "<cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs>" +
            "<cellXfs count=\"3\">" +
            "<xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/>" +
            "<xf numFmtId=\"0\" fontId=\"1\" fillId=\"2\" borderId=\"1\" xfId=\"0\" applyFont=\"1\" applyFill=\"1\" applyBorder=\"1\" applyAlignment=\"1\"><alignment horizontal=\"center\" vertical=\"center\"/></xf>" +
            "<xf numFmtId=\"4\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyNumberFormat=\"1\"/>" +
            "</cellXfs><cellStyles count=\"1\"><cellStyle name=\"Normal\" xfId=\"0\" builtinId=\"0\"/></cellStyles>" +
            "</styleSheet>";

        static string Sheet(string[] headers, List<object[]> rows)
        {
            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            sb.Append("<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">");
            sb.Append("<sheetViews><sheetView workbookViewId=\"0\"><pane ySplit=\"1\" topLeftCell=\"A2\" activePane=\"bottomLeft\" state=\"frozen\"/></sheetView></sheetViews>");
            sb.Append("<cols><col min=\"1\" max=\"1\" width=\"8\" customWidth=\"1\"/>");
            sb.Append("<col min=\"2\" max=\"2\" width=\"18\" customWidth=\"1\"/>");
            sb.Append("<col min=\"3\" max=\"6\" width=\"20\" customWidth=\"1\"/></cols><sheetData>");

            sb.Append("<row r=\"1\" ht=\"22\" customHeight=\"1\">");
            for (int c = 0; c < headers.Length; c++) sb.Append(Str(Col(c) + "1", headers[c], 1));
            sb.Append("</row>");

            int ri = 2;
            foreach (var row in rows)
            {
                sb.Append("<row r=\"").Append(ri).Append("\">");
                for (int c = 0; c < row.Length; c++)
                {
                    string cref = Col(c) + ri.ToString(CultureInfo.InvariantCulture);
                    object v = row[c];
                    if (v == null) continue;                      // empty cells are simply omitted
                    if (v is double)
                        sb.Append("<c r=\"").Append(cref).Append("\" s=\"")
                          .Append(c >= 2 ? "2" : "0").Append("\"><v>")
                          .Append(((double)v).ToString("0.####", CultureInfo.InvariantCulture))
                          .Append("</v></c>");
                    else if (v is int)
                        sb.Append("<c r=\"").Append(cref).Append("\"><v>")
                          .Append(((int)v).ToString(CultureInfo.InvariantCulture))
                          .Append("</v></c>");
                    else sb.Append(Str(cref, v.ToString()));
                }
                sb.Append("</row>");
                ri++;
            }
            string last = (rows.Count + 1).ToString(CultureInfo.InvariantCulture);
            sb.Append("</sheetData><autoFilter ref=\"A1:")
              .Append(Col(headers.Length - 1)).Append(last).Append("\"/></worksheet>");
            return sb.ToString();
        }

        /// <summary>Multi-sheet xlsx: one workbook, several sheets. Sheet names are sanitised automatically (strip []:*?/\, cut to 31 chars, de-duplicate).</summary>
        public static string WriteWorkbook(string path,
            List<(string Name, string[] Headers, List<object[]> Rows)> sheets)
        {
            if (sheets == null || sheets.Count == 0)
                throw new InvalidOperationException("No worksheet to write.");
            if (File.Exists(path)) File.Delete(path);

            List<string> names = CleanSheetNames(sheets);

            var ct = new StringBuilder();
            ct.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>")
              .Append("<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">")
              .Append("<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>")
              .Append("<Default Extension=\"xml\" ContentType=\"application/xml\"/>")
              .Append("<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>")
              .Append("<Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/>");
            var wb = new StringBuilder();
            wb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>")
              .Append("<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" ")
              .Append("xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets>");
            var rels = new StringBuilder();
            rels.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>")
                .Append("<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">");

            for (int i = 0; i < sheets.Count; i++)
            {
                int n = i + 1;
                ct.Append("<Override PartName=\"/xl/worksheets/sheet").Append(n)
                  .Append(".xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>");
                wb.Append("<sheet name=\"").Append(Esc(names[i]).Replace("\"", "&quot;"))
                  .Append("\" sheetId=\"").Append(n).Append("\" r:id=\"rId").Append(n).Append("\"/>");
                rels.Append("<Relationship Id=\"rId").Append(n)
                    .Append("\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet")
                    .Append(n).Append(".xml\"/>");
            }
            ct.Append("</Types>");
            wb.Append("</sheets></workbook>");
            rels.Append("<Relationship Id=\"rId").Append(sheets.Count + 1)
                .Append("\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/>")
                .Append("</Relationships>");

            using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                Add(zip, "[Content_Types].xml", ct.ToString());
                Add(zip, "_rels/.rels", RootRels);
                Add(zip, "xl/workbook.xml", wb.ToString());
                Add(zip, "xl/_rels/workbook.xml.rels", rels.ToString());
                Add(zip, "xl/styles.xml", Styles);
                for (int i = 0; i < sheets.Count; i++)
                    Add(zip, "xl/worksheets/sheet" + (i + 1) + ".xml", Sheet(sheets[i].Headers, sheets[i].Rows));
            }
            return path;
        }

        static List<string> CleanSheetNames(List<(string Name, string[] Headers, List<object[]> Rows)> sheets)
        {
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var result = new List<string>();
            foreach (var s in sheets)
            {
                string n = string.IsNullOrWhiteSpace(s.Name) ? "Sheet" : s.Name.Trim();
                foreach (char bad in "[]:*?/\\") n = n.Replace(bad, '_');
                if (n.Length > 31) n = n.Substring(0, 31);
                if (n.Length == 0) n = "Sheet";
                string baseName = n;
                int k = 2;
                while (!used.Add(n))
                {
                    string suffix = "-" + k++;
                    n = (baseName.Length + suffix.Length > 31
                        ? baseName.Substring(0, 31 - suffix.Length) : baseName) + suffix;
                }
                result.Add(n);
            }
            return result;
        }

        static string Str(string cref, string text)
        {
            return Str(cref, text, 0);
        }

        static string Str(string cref, string text, int style)
        {
            return "<c r=\"" + cref + "\" s=\"" + style.ToString(CultureInfo.InvariantCulture) +
                   "\" t=\"inlineStr\"><is><t xml:space=\"preserve\">" +
                   Esc(text) + "</t></is></c>";
        }

        static string Esc(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
        }

        static string Col(int idx)
        {
            string s = ""; idx++;
            while (idx > 0) { int m = (idx - 1) % 26; s = (char)('A' + m) + s; idx = (idx - 1) / 26; }
            return s;
        }
    }
}
