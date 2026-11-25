using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using CsvHelper;
using CsvHelper.Configuration;
using ExcelDataReader;

public static partial class ConfGen
{
    // ====== 公共：扫描/分组 & 代码片段 ======
    static GenCodeContent ScanInput(string inputDir, string side = "ALL")
    {
        side = NormalizeSide(side);
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        var result = new GenCodeContent { classes = new List<ClassContent>() };

        string[] csvFiles  = Directory.Exists(inputDir) ? Directory.GetFiles(inputDir, "*.csv",  SearchOption.AllDirectories) : Array.Empty<string>();
        string[] xlsxFiles = Directory.Exists(inputDir) ? Directory.GetFiles(inputDir, "*.xlsx", SearchOption.AllDirectories) : Array.Empty<string>();
        string[] xlsFiles  = Directory.Exists(inputDir) ? Directory.GetFiles(inputDir, "*.xls",  SearchOption.AllDirectories) : Array.Empty<string>();

        // ===== CSV：只读首行表头；其它都从 schema.json =====
        foreach (var file in csvFiles)
        {
            // 1) 读首行表头
            var config = new CsvConfiguration(CultureInfo.InvariantCulture)
            {
                HasHeaderRecord = true,
                Delimiter = ",",
                TrimOptions = TrimOptions.None,
                PrepareHeaderForMatch = args => args.Header.Trim(),
                BadDataFound = null
            };

            var headerSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var reader = new StreamReader(file))
            using (var csv = new CsvReader(reader, config))
            {
                if (!csv.Read() || !csv.ReadHeader()) { Warn($"[Warn] CSV {file} missing header"); continue; }
                foreach (var h in csv.HeaderRecord)
                    headerSet.Add(ReadConfEditorUtil.ToCamelLower(h));
            }

            // 2) 读 schema.json（表级 + 列级）
            var sp = SchemaPathFor(file);
            if (!File.Exists(sp))
            {
                Warn($"[Warn] Schema not found for CSV: {file}.schema.json . Skip table.");
                continue; // 强约束：必须有 schema
            }

            var ts = ReadJson<TableSchema>(sp) ?? new TableSchema();
            var tableCs = ResolveCsType(ts.table_properties, "ALL");
            // 表级不匹配 => 整张跳过
            if (!MatchesSide(tableCs, side))
            {
                Log($"[Filter] CSV {Path.GetFileName(file)} table cs={tableCs} side={side} => SKIP");
                continue;
            }

            // 3) 以 schema.columns 的排序（col_sort/声明顺序）为准；列只看“列自身 cs_type”，默认 ALL
            var orderedCols = (ts.columns ?? new()).Select((c, idx) => (c, idx))
                .OrderBy(x => x.c.col_sort ?? x.idx)
                .Select(x => x.c);

            var props = new List<GenCodeProp>();
            var keyPriorities = new List<int?>();

            foreach (var col in orderedCols)
            {
                var nameLower = ReadConfEditorUtil.ToCamelLower(col.col_name ?? "");
                if (!headerSet.Contains(nameLower))
                {
                    Log($"[Skip] CSV {Path.GetFileName(file)} col '{col.col_name}' not in header");
                    continue;
                }

                var colCs = ResolveCsType(col.col_properties, "ALL");     // 仅看列自身（默认 ALL）
                if (!MatchesSide(colCs, side))
                {
                    Log($"[Filter] CSV {Path.GetFileName(file)} col '{col.col_name}' cs={colCs} side={side} => EXCLUDE");
                    continue;
                }

                var csharpType = MapCsvTypeToCSharpType(col.value_type);
                props.Add(new GenCodeProp { name = nameLower, propType = csharpType });
                keyPriorities.Add(col.pk_order.HasValue && col.pk_order.Value > 0 ? col.pk_order : null);
            }

            if (props.Count == 0) { Warn($"[Warn] CSV {file} has no columns after filtering."); continue; }

            string className = ReadConfEditorUtil.ToCamelUpper(Path.GetFileNameWithoutExtension(file));
            result.classes.Add(new ClassContent
            {
                className     = className,
                fileName      = className,
                FilePath      = file,
                classDef      = "",
                props         = props,
                keyPriorities = keyPriorities
            });
        }

        // ===== Excel：每个 sheet 一个类；只读首行表头；其它都从 workbook schema =====
        foreach (var file in xlsxFiles.Concat(xlsFiles))
        {
            // workbook schema
            var sp = SchemaPathFor(file);
            if (!File.Exists(sp))
            {
                Warn($"[Warn] Schema not found for Excel: {file}.schema.json . Skip workbook.");
                continue; // 强约束：必须有 schema
            }
            var wb = ReadJson<WorkbookSchema>(sp) ?? new WorkbookSchema();

            using var stream = File.Open(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = ExcelReaderFactory.CreateReader(stream);

            do
            {
                string sheetName = reader.Name;

                // 1) 找 sheet schema
                var sheetSch = wb.sheets?.FirstOrDefault(s =>
                    s.sheet_name.Equals(sheetName, StringComparison.OrdinalIgnoreCase));
                if (sheetSch == null)
                {
                    Warn($"[Warn] Sheet '{sheetName}' in {file} not found in workbook schema. Skipped.");
                    continue;
                }

                // 2) 读首行表头
                var headerSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (reader.Read())
                {
                    for (int i = 0; i < reader.FieldCount; i++)
                        headerSet.Add(ReadConfEditorUtil.ToCamelLower(reader.GetValue(i)?.ToString()?.Trim() ?? string.Empty));
                }
                if (headerSet.Count == 0)
                {
                    Warn($"[Warn] Excel {file}/{sheetName} missing header.");
                    continue;
                }

                // 3) 先判 Sheet 侧别；不匹配直接整张跳过
                var sheetCs = ResolveCsType(sheetSch.sheet_properties, "ALL");
                if (!MatchesSide(sheetCs, side))
                {
                    Log($"[Filter] XLSX {Path.GetFileName(file)}/{sheetName} sheet cs={sheetCs} side={side} => SKIP");
                    continue;
                }

                // 4) 列只看“列自身 cs_type”，默认 ALL
                var orderedCols = (sheetSch.columns ?? new()).Select((c, idx) => (c, idx))
                    .OrderBy(x => x.c.col_sort ?? x.idx)
                    .Select(x => x.c);

                var props = new List<GenCodeProp>();
                var keyPriorities = new List<int?>();

                foreach (var col in orderedCols)
                {
                    var nameLower = ReadConfEditorUtil.ToCamelLower(col.col_name ?? "");
                    if (!headerSet.Contains(nameLower))
                    {
                        Log($"[Skip] XLSX {Path.GetFileName(file)}/{sheetName} col '{col.col_name}' not in header");
                        continue;
                    }

                    var colCs = ResolveCsType(col.col_properties, "ALL");   // 仅看列自身（默认 ALL）
                    if (!MatchesSide(colCs, side))
                    {
                        Log($"[Filter] XLSX {Path.GetFileName(file)}/{sheetName} col '{col.col_name}' cs={colCs} side={side} => EXCLUDE");
                        continue;
                    }

                    var csharpType = MapCsvTypeToCSharpType(col.value_type);
                    props.Add(new GenCodeProp { name = nameLower, propType = csharpType });
                    keyPriorities.Add(col.pk_order.HasValue && col.pk_order.Value > 0 ? col.pk_order : null);
                }

                if (props.Count == 0) { Warn($"[Warn] Excel {file}/{sheetName} has no columns after filtering."); continue; }

                string className = ReadConfEditorUtil.ToCamelUpper(sheetName);
                result.classes.Add(new ClassContent
                {
                    className     = className,
                    fileName      = className,
                    FilePath      = file,
                    classDef      = "",
                    props         = props,
                    keyPriorities = keyPriorities
                });

            } while (reader.NextResult());
        }

        return result;
    }

    static Dictionary<string, List<(string className, string classFieldName)>> GroupByFolder(GenCodeContent scan)
    {
        var dict = new Dictionary<string, List<(string, string)>>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in scan.classes)
        {
            string folder = Path.GetFileName(Path.GetDirectoryName(c.FilePath)) ?? "";
            string field  = char.ToLower(c.className[0]) + c.className[1..];
            if (!dict.TryGetValue(folder, out var list)) { list = new(); dict[folder] = list; }
            list.Add((c.className, field));
        }
        return dict;
    }
}