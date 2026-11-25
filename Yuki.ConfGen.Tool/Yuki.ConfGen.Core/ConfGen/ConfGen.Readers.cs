using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using CsvHelper;
using CsvHelper.Configuration;
using ExcelDataReader;

public static partial class ConfGen
{
    // ================= 读取 CSV / Excel 数据（仅跳过表头） =================
    static void ReadCsvFile(string filePath, Type elemType, FieldInfo[] subFields,
        List<string> headerNames, List<object> confList)
    {
        var config = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            HasHeaderRecord = true,
            Delimiter = ",",
            TrimOptions = TrimOptions.None,
            PrepareHeaderForMatch = args => args.Header.Trim(),
            BadDataFound = null
        };

        using var reader = new StreamReader(filePath);
        using var csv = new CsvReader(reader, config);

        if (!csv.Read() || !csv.ReadHeader()) { Warn($"[Warn] CSV {filePath} missing header"); return; }
        headerNames.AddRange(csv.HeaderRecord.Select(ReadConfEditorUtil.ToCamelLower));

        while (csv.Read())
        {
            var rowObj = Activator.CreateInstance(elemType)!;
            for (int i = 0; i < subFields.Length; i++)
            {
                try
                {
                    string name = subFields[i].Name;
                    int idx = headerNames.IndexOf(name);
                    if (idx < 0) continue;

                    string value = csv.GetField(idx);
                    object val = ConvertValue(value, subFields[i].FieldType);
                    subFields[i].SetValue(rowObj, val);
                }
                catch (Exception ex)
                {
                    Err($"[Error] CSV parse error: {ex.Message}");
                }
            }
            confList.Add(rowObj);
        }
    }

    static void ReadExcelFile(string filePath, Type elemType, FieldInfo[] subFields, object data,
                                      string currentClassName, Type confDataType)
    {
        using var stream = File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = ExcelReaderFactory.CreateReader(stream);

        do
        {
            string sheetName = reader.Name;
            if (ReadConfEditorUtil.ToCamelUpper(sheetName) != currentClassName)
                continue;

            var field = confDataType.GetField(currentClassName,
                BindingFlags.IgnoreCase | BindingFlags.Public | BindingFlags.Instance);
            if (field == null)
            {
                Warn($"[Warn] ConfData field not found: {currentClassName}");
                continue;
            }

            var headerNames = new List<string>();
            bool gotHeader = false;
            List<object> confList = new();

            while (reader.Read())
            {
                if (!gotHeader)
                {
                    for (int i = 0; i < reader.FieldCount; i++)
                        headerNames.Add(ReadConfEditorUtil.ToCamelLower(reader.GetValue(i)?.ToString()?.Trim() ?? string.Empty));
                    gotHeader = true;
                    continue;
                }

                var rowObj = Activator.CreateInstance(elemType)!;
                for (int i = 0; i < subFields.Length; i++)
                {
                    try
                    {
                        string name = subFields[i].Name;
                        int idx = headerNames.IndexOf(name);
                        if (idx < 0) continue;

                        string? value = reader.IsDBNull(idx) ? null : reader.GetValue(idx)?.ToString();
                        object val = ConvertValue(value, subFields[i].FieldType);
                        subFields[i].SetValue(rowObj, val);
                    }
                    catch (Exception ex)
                    {
                        Err($"[Error] Excel parse error: {ex.Message}");
                    }
                }
                confList.Add(rowObj);
            }

            var arr = Array.CreateInstance(elemType, confList.Count);
            for (int i = 0; i < confList.Count; i++) arr.SetValue(confList[i], i);
            field.SetValue(data, arr);

        } while (reader.NextResult());
    }
}