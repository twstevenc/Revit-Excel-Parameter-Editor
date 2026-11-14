using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using OfficeOpenXml;

namespace RevitParameterEditor
{
    // 「契約填值」按鈕：依對照表（品類＋族群＋類型）把契約欄位批次填入元件
    [Transaction(TransactionMode.Manual)]
    public class TypeFillCommand : IExternalCommand
    {
        private const string Title = "契約填值";
        private const string SheetName = "填值對照";
        private const string PrefixHeader = "rm_id開頭";
        private static readonly string[] KeyHeaders = { "品類", "族群", "類型" };

        // 對照表的一列
        private class MappingRow
        {
            public int RowNumber;
            public string Prefix;
            public Dictionary<string, string> Values = new Dictionary<string, string>();
            public int Used;
        }

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            Document doc = commandData.Application.ActiveUIDocument?.Document;
            if (doc == null || doc.IsFamilyDocument)
            {
                TaskDialog.Show(Title, "請先開啟一個專案。");
                return Result.Cancelled;
            }

            try
            {
                var openDialog = new Microsoft.Win32.OpenFileDialog
                {
                    Filter = "Excel 檔案 (*.xlsx)|*.xlsx",
                    Title = "選擇填值對照表"
                };
                if (openDialog.ShowDialog() != true) return Result.Cancelled;

                Dictionary<string, List<MappingRow>> mapping = LoadMapping(openDialog.FileName,
                    out List<string> valueHeaders, out int rowCount);

                bool? viewOnly = FillSupport.AskScope(Title,
                    $"對照表共 {rowCount} 列，會填入 {valueHeaders.Count} 個欄位。要處理哪些元件？");
                if (viewOnly == null) return Result.Cancelled;

                bool? overwrite = FillSupport.AskOverwrite(Title);
                if (overwrite == null) return Result.Cancelled;

                List<Element> targets = FillSupport.CollectModelElements(doc, viewOnly.Value);

                var typeCache = new Dictionary<long, string>();
                int matchedElements = 0, writtenValues = 0, keptValues = 0, failedValues = 0;
                var missingParameters = new SortedSet<string>();

                using (var transaction = new Transaction(doc, "依對照表填入契約欄位"))
                {
                    transaction.Start();

                    foreach (Element element in targets)
                    {
                        long typeId = element.GetTypeId().Value;
                        if (!typeCache.TryGetValue(typeId, out string typeKey))
                        {
                            var elementType = doc.GetElement(element.GetTypeId()) as ElementType;
                            typeKey = elementType == null
                                ? string.Empty
                                : MakeKey(element.Category.Name, elementType.FamilyName, elementType.Name);
                            typeCache[typeId] = typeKey;
                        }

                        if (typeKey.Length == 0) continue;
                        if (!mapping.TryGetValue(typeKey, out List<MappingRow> rows)) continue;

                        string roomId = FillSupport.GetText(element, "rm_id") ?? string.Empty;

                        // 有指定 rm_id 開頭的列優先；元件的 rm_id 必須符合
                        MappingRow row = rows.FirstOrDefault(r => r.Prefix.Length > 0 && roomId.StartsWith(r.Prefix))
                                         ?? rows.FirstOrDefault(r => r.Prefix.Length == 0);
                        if (row == null) continue;

                        row.Used++;
                        matchedElements++;

                        foreach (KeyValuePair<string, string> pair in row.Values)
                        {
                            switch (FillSupport.SetText(element, pair.Key, pair.Value, overwrite.Value))
                            {
                                case WriteResult.Written: writtenValues++; break;
                                case WriteResult.KeptExisting: keptValues++; break;
                                case WriteResult.NoParameter: missingParameters.Add(pair.Key); break;
                                case WriteResult.Unchanged: break;
                                default: failedValues++; break;
                            }
                        }
                    }

                    transaction.Commit();
                }

                List<MappingRow> allRows = mapping.Values.SelectMany(r => r).OrderBy(r => r.RowNumber).ToList();
                List<MappingRow> unused = allRows.Where(r => r.Used == 0).ToList();

                var details = new List<string>
                {
                    $"範圍內的元件：{targets.Count}",
                    $"對應到對照表的元件：{matchedElements}",
                    $"寫入的欄位值：{writtenValues}",
                    $"已有值而未覆蓋：{keptValues}",
                    $"寫入失敗或唯讀：{failedValues}",
                    $"對照表中沒有對應到任何元件的列：{unused.Count}"
                };
                if (missingParameters.Count > 0)
                {
                    details.Add("模型中沒有這些欄位（已略過）：" + string.Join("、", missingParameters));
                }
                if (unused.Count > 0)
                {
                    details.Add(string.Empty);
                    details.Add("沒有用到的列（Excel 列號）：" +
                                string.Join("、", unused.Take(60).Select(r => r.RowNumber.ToString())));
                }

                FillSupport.ShowResult(Title,
                    $"已填入 {matchedElements} 個元件，共 {writtenValues} 個欄位值",
                    "按一次 Ctrl+Z 可以全部復原。展開可看明細。" +
                    (matchedElements == 0
                        ? "\n\n沒有任何元件對應到。請確認已先執行「帶入房間」，而且品類、族群、類型的名稱和模型一致。"
                        : string.Empty),
                    details);
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                TaskDialog.Show(Title, "發生錯誤：" + ex.Message);
                return Result.Failed;
            }
        }

        private static string MakeKey(string category, string family, string type)
        {
            return (category ?? string.Empty).Trim() + "\u0001" +
                   (family ?? string.Empty).Trim() + "\u0001" +
                   (type ?? string.Empty).Trim();
        }

        private static Dictionary<string, List<MappingRow>> LoadMapping(string path,
            out List<string> valueHeaders, out int rowCount)
        {
            System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);

            var mapping = new Dictionary<string, List<MappingRow>>();
            valueHeaders = new List<string>();
            rowCount = 0;

            using (var stream = new System.IO.FileStream(path, System.IO.FileMode.Open,
                       System.IO.FileAccess.Read, System.IO.FileShare.ReadWrite))
            using (var package = new ExcelPackage(stream))
            {
                ExcelWorksheet sheet = package.Workbook.Worksheets[SheetName];
                if (sheet == null || sheet.Dimension == null)
                    throw new Exception($"對照表中找不到「{SheetName}」工作表。");

                var columns = new Dictionary<string, int>();
                for (int col = 1; col <= sheet.Dimension.End.Column; col++)
                {
                    string header = sheet.Cells[1, col].Text.Trim();
                    if (header.Length > 0 && !columns.ContainsKey(header)) columns[header] = col;
                }

                foreach (string key in KeyHeaders)
                {
                    if (!columns.ContainsKey(key)) throw new Exception($"對照表缺少「{key}」欄。");
                }

                // 欄名以括號開頭的是說明用的欄，不會寫入模型
                var valueColumns = new List<KeyValuePair<string, int>>();
                foreach (KeyValuePair<string, int> pair in columns)
                {
                    if (KeyHeaders.Contains(pair.Key) || pair.Key == PrefixHeader) continue;
                    if (pair.Key.StartsWith("(") || pair.Key.StartsWith("（")) continue;
                    valueColumns.Add(pair);
                    valueHeaders.Add(pair.Key);
                }

                for (int row = 2; row <= sheet.Dimension.End.Row; row++)
                {
                    string category = sheet.Cells[row, columns["品類"]].Text.Trim();
                    string type = sheet.Cells[row, columns["類型"]].Text.Trim();
                    if (category.Length == 0 || type.Length == 0) continue;

                    string family = sheet.Cells[row, columns["族群"]].Text.Trim();
                    var mappingRow = new MappingRow
                    {
                        RowNumber = row,
                        Prefix = columns.ContainsKey(PrefixHeader)
                            ? sheet.Cells[row, columns[PrefixHeader]].Text.Trim()
                            : string.Empty
                    };

                    foreach (KeyValuePair<string, int> pair in valueColumns)
                    {
                        string value = sheet.Cells[row, pair.Value].Text.Trim();
                        if (value.Length > 0) mappingRow.Values[pair.Key] = value;
                    }
                    if (mappingRow.Values.Count == 0) continue;

                    string key = MakeKey(category, family, type);
                    if (!mapping.TryGetValue(key, out List<MappingRow> list))
                    {
                        list = new List<MappingRow>();
                        mapping[key] = list;
                    }
                    list.Add(mappingRow);
                    rowCount++;
                }
            }

            if (rowCount == 0) throw new Exception("對照表中沒有可用的資料列。");
            return mapping;
        }
    }
}
