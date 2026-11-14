using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using OfficeOpenXml;

namespace RevitParameterEditor
{
    // 「模型盤點」按鈕：把模型的概況匯出成一份 Excel（唯讀，不會修改模型）
    [Transaction(TransactionMode.ReadOnly)]
    public class ModelInventoryCommand : IExternalCommand
    {
        private const string Title = "模型盤點";
        private const string NoLevel = "(無樓層)";
        private const double SquareFeetToSquareMeters = 0.09290304;
        private const double FeetToMeters = 0.3048;

        // 找不到 LevelId 時，依序嘗試這些內建參數來判斷元件所在樓層
        private static readonly BuiltInParameter[] LevelParameters =
        {
            BuiltInParameter.FAMILY_LEVEL_PARAM,
            BuiltInParameter.SCHEDULE_LEVEL_PARAM,
            BuiltInParameter.INSTANCE_REFERENCE_LEVEL_PARAM,
            BuiltInParameter.RBS_START_LEVEL_PARAM,
            BuiltInParameter.WALL_BASE_CONSTRAINT,
            BuiltInParameter.STAIRS_BASE_LEVEL_PARAM,
            BuiltInParameter.LEVEL_PARAM
        };

        // 一種族群類型的統計
        private class TypeStat
        {
            public string Category;
            public string Family;
            public string TypeName;
            public string TypeMark;
            public string Description;
            public int Total;
            public Dictionary<string, int> ByLevel = new Dictionary<string, int>();
        }

        // 一個品類的參數填寫統計
        private class CategoryStat
        {
            public int Count;
            public int[] Bound;
            public int[] Filled;
        }

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            Document doc = commandData.Application.ActiveUIDocument?.Document;
            if (doc == null || doc.IsFamilyDocument)
            {
                TaskDialog.Show(Title, "請先開啟一個專案。");
                return Result.Cancelled;
            }

            var saveDialog = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "Excel 檔案 (*.xlsx)|*.xlsx",
                FileName = doc.Title + "_模型盤點.xlsx",
                Title = "儲存模型盤點結果"
            };
            if (saveDialog.ShowDialog() != true) return Result.Cancelled;

            try
            {
                System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);

                // 參數清單取自上次使用的設定檔；讀不到時就略過「參數現況」
                List<string> parameterNames = LoadParameterNames(out string configName);

                int elementCount = Export(doc, commandData.Application.Application, saveDialog.FileName,
                    parameterNames, configName);

                TaskDialog.Show(Title,
                    $"盤點完成，共統計 {elementCount} 個元件。\n檔案已儲存：\n{saveDialog.FileName}");
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                TaskDialog.Show(Title, "發生錯誤：" + ex.Message);
                return Result.Failed;
            }
        }

        private static List<string> LoadParameterNames(out string configName)
        {
            configName = string.Empty;
            try
            {
                List<ConfigEntry> entries = ConfigStore.GetEntries();
                if (entries.Count == 0) return new List<string>();

                string last = ConfigStore.LoadLastUsed();
                ConfigEntry entry = entries.FirstOrDefault(e =>
                                        string.Equals(e.Path, last, StringComparison.OrdinalIgnoreCase))
                                    ?? entries[0];

                ParameterConfig config = ParameterConfig.Load(entry.Path);
                configName = entry.Name;
                return config.Parameters.Select(p => p.Name).ToList();
            }
            catch (Exception)
            {
                return new List<string>();
            }
        }

        private int Export(Document doc, Autodesk.Revit.ApplicationServices.Application app, string filePath,
            List<string> parameterNames, string configName)
        {
            // 樓層
            List<Level> levels = new FilteredElementCollector(doc)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .OrderBy(l => l.Elevation)
                .ToList();

            var levelNames = new Dictionary<long, string>();
            var levelOrder = new Dictionary<string, int>();
            for (int i = 0; i < levels.Count; i++)
            {
                levelNames[levels[i].Id.Value] = levels[i].Name;
                levelOrder[levels[i].Name] = i;
            }
            levelOrder[NoLevel] = int.MaxValue;

            // 走訪所有模型元件
            var typeStats = new Dictionary<string, TypeStat>();
            var categoryStats = new SortedDictionary<string, CategoryStat>();
            var typeCache = new Dictionary<long, string[]>();
            var rooms = new List<Autodesk.Revit.DB.Architecture.Room>();
            int elementCount = 0;

            foreach (Element element in new FilteredElementCollector(doc).WhereElementIsNotElementType())
            {
                Category category = element.Category;
                if (category == null) continue;
                if (category.CategoryType != CategoryType.Model) continue;
                if (element.ViewSpecific) continue;

                // 相機、圖紙、連結檔不是模型元件，不列入
                long builtInCategory = category.Id.Value;
                if (builtInCategory == (long)BuiltInCategory.OST_Cameras ||
                    builtInCategory == (long)BuiltInCategory.OST_Sheets ||
                    builtInCategory == (long)BuiltInCategory.OST_RvtLinks) continue;

                var room = element as Autodesk.Revit.DB.Architecture.Room;
                ElementId typeId = element.GetTypeId();
                bool hasType = typeId != null && typeId != ElementId.InvalidElementId;

                // 只統計有類型的元件，以及房間
                if (!hasType && room == null) continue;

                string categoryName = category.Name;
                string family;
                string typeName;
                string typeMark = string.Empty;
                string description = string.Empty;

                if (room != null)
                {
                    rooms.Add(room);
                    family = "(房間)";
                    typeName = "(房間)";
                }
                else
                {
                    if (!typeCache.TryGetValue(typeId.Value, out string[] info))
                    {
                        var elementType = doc.GetElement(typeId) as ElementType;
                        info = new[]
                        {
                            elementType != null ? elementType.FamilyName : string.Empty,
                            elementType != null ? elementType.Name : string.Empty,
                            GetText(elementType?.get_Parameter(BuiltInParameter.ALL_MODEL_TYPE_MARK)),
                            GetText(elementType?.get_Parameter(BuiltInParameter.ALL_MODEL_DESCRIPTION))
                        };
                        typeCache[typeId.Value] = info;
                    }
                    family = info[0];
                    typeName = info[1];
                    typeMark = info[2];
                    description = info[3];
                }

                string levelName = GetLevelName(element, levelNames);

                string key = categoryName + "\u0001" + family + "\u0001" + typeName;
                if (!typeStats.TryGetValue(key, out TypeStat stat))
                {
                    stat = new TypeStat
                    {
                        Category = categoryName,
                        Family = family,
                        TypeName = typeName,
                        TypeMark = typeMark,
                        Description = description
                    };
                    typeStats[key] = stat;
                }
                stat.Total++;
                stat.ByLevel[levelName] = stat.ByLevel.TryGetValue(levelName, out int n) ? n + 1 : 1;

                if (parameterNames.Count > 0)
                {
                    if (!categoryStats.TryGetValue(categoryName, out CategoryStat cs))
                    {
                        cs = new CategoryStat
                        {
                            Bound = new int[parameterNames.Count],
                            Filled = new int[parameterNames.Count]
                        };
                        categoryStats[categoryName] = cs;
                    }
                    cs.Count++;
                    for (int i = 0; i < parameterNames.Count; i++)
                    {
                        Parameter parameter = element.LookupParameter(parameterNames[i]);
                        if (parameter == null) continue;
                        cs.Bound[i]++;
                        if (!string.IsNullOrWhiteSpace(GetText(parameter))) cs.Filled[i]++;
                    }
                }

                elementCount++;
            }

            List<TypeStat> sortedTypes = typeStats.Values
                .OrderBy(s => s.Category).ThenBy(s => s.Family).ThenBy(s => s.TypeName)
                .ToList();

            using (var package = new ExcelPackage())
            {
                // 1. 概況
                ExcelWorksheet overview = package.Workbook.Worksheets.Add("概況");
                var overviewRows = new List<string[]>
                {
                    new[] { "檔案名稱", doc.Title },
                    new[] { "檔案路徑", doc.PathName },
                    new[] { "Revit 版本", app.VersionName + "（" + app.VersionBuild + "）" },
                    new[] { "是否為工作共用", doc.IsWorkshared ? "是" : "否" },
                    new[] { "盤點時間", DateTime.Now.ToString("yyyy-MM-dd HH:mm") },
                    new[] { "樓層數", levels.Count.ToString() },
                    new[] { "統計的元件數", elementCount.ToString() },
                    new[] { "族群類型數", sortedTypes.Count.ToString() },
                    new[] { "房間數", rooms.Count.ToString() },
                    new[] { "參數現況使用的設定檔", string.IsNullOrEmpty(configName) ? "(無)" : configName }
                };
                overview.Cells[1, 1].Value = "項目";
                overview.Cells[1, 2].Value = "內容";
                for (int i = 0; i < overviewRows.Count; i++)
                {
                    overview.Cells[i + 2, 1].Value = overviewRows[i][0];
                    overview.Cells[i + 2, 2].Value = overviewRows[i][1];
                }
                Finish(overview, 2, new double[] { 26, 90 });

                // 2. 樓層
                ExcelWorksheet levelSheet = package.Workbook.Worksheets.Add("樓層");
                levelSheet.Cells[1, 1].Value = "樓層名稱";
                levelSheet.Cells[1, 2].Value = "高程(m)";
                for (int i = 0; i < levels.Count; i++)
                {
                    levelSheet.Cells[i + 2, 1].Value = levels[i].Name;
                    levelSheet.Cells[i + 2, 2].Value = Math.Round(levels[i].Elevation * FeetToMeters, 3);
                }
                Finish(levelSheet, 2, new double[] { 28, 12 });

                // 3. 類型清單（每種類型一列）
                ExcelWorksheet typeSheet = package.Workbook.Worksheets.Add("類型清單");
                string[] typeHeaders = { "品類", "族群", "類型", "類型標記", "說明", "總數量", "各樓層數量" };
                for (int i = 0; i < typeHeaders.Length; i++) typeSheet.Cells[1, i + 1].Value = typeHeaders[i];
                int row = 2;
                foreach (TypeStat s in sortedTypes)
                {
                    typeSheet.Cells[row, 1].Value = s.Category;
                    typeSheet.Cells[row, 2].Value = s.Family;
                    typeSheet.Cells[row, 3].Value = s.TypeName;
                    typeSheet.Cells[row, 4].Value = s.TypeMark;
                    typeSheet.Cells[row, 5].Value = s.Description;
                    typeSheet.Cells[row, 6].Value = s.Total;
                    typeSheet.Cells[row, 7].Value = string.Join("、",
                        s.ByLevel.OrderBy(p => LevelRank(levelOrder, p.Key))
                            .Select(p => p.Key + "×" + p.Value));
                    row++;
                }
                Finish(typeSheet, typeHeaders.Length, new double[] { 16, 34, 40, 12, 24, 9, 70 });

                // 4. 類型依樓層（每種類型在每個樓層一列，方便篩選單一樓層）
                ExcelWorksheet byLevelSheet = package.Workbook.Worksheets.Add("類型依樓層");
                string[] byLevelHeaders = { "樓層", "品類", "族群", "類型", "數量" };
                for (int i = 0; i < byLevelHeaders.Length; i++) byLevelSheet.Cells[1, i + 1].Value = byLevelHeaders[i];
                row = 2;
                foreach (TypeStat s in sortedTypes)
                {
                    foreach (KeyValuePair<string, int> pair in s.ByLevel.OrderBy(p => LevelRank(levelOrder, p.Key)))
                    {
                        byLevelSheet.Cells[row, 1].Value = pair.Key;
                        byLevelSheet.Cells[row, 2].Value = s.Category;
                        byLevelSheet.Cells[row, 3].Value = s.Family;
                        byLevelSheet.Cells[row, 4].Value = s.TypeName;
                        byLevelSheet.Cells[row, 5].Value = pair.Value;
                        row++;
                    }
                }
                Finish(byLevelSheet, byLevelHeaders.Length, new double[] { 18, 16, 34, 40, 9 });

                // 5. 房間
                ExcelWorksheet roomSheet = package.Workbook.Worksheets.Add("房間");
                string[] roomParameters = { "fl_id", "rm_id", "rm_name", "rm_std", "HouseNB" };
                var roomHeaders = new List<string> { "ElementID", "樓層", "編號", "名稱", "面積(m2)", "是否已放置" };
                roomHeaders.AddRange(roomParameters);
                for (int i = 0; i < roomHeaders.Count; i++) roomSheet.Cells[1, i + 1].Value = roomHeaders[i];
                row = 2;
                foreach (Autodesk.Revit.DB.Architecture.Room room in rooms
                             .OrderBy(r => LevelRank(levelOrder, GetLevelName(r, levelNames)))
                             .ThenBy(r => r.Number))
                {
                    roomSheet.Cells[row, 1].Value = room.Id.ToString();
                    roomSheet.Cells[row, 2].Value = GetLevelName(room, levelNames);
                    roomSheet.Cells[row, 3].Value = room.Number;
                    roomSheet.Cells[row, 4].Value = GetText(room.get_Parameter(BuiltInParameter.ROOM_NAME));
                    roomSheet.Cells[row, 5].Value = Math.Round(room.Area * SquareFeetToSquareMeters, 2);
                    roomSheet.Cells[row, 6].Value = room.Area > 0 ? "是" : "否";
                    for (int i = 0; i < roomParameters.Length; i++)
                    {
                        roomSheet.Cells[row, 7 + i].Value = GetText(room.LookupParameter(roomParameters[i]));
                    }
                    row++;
                }
                Finish(roomSheet, roomHeaders.Count, new double[] { 12, 16, 10, 22, 10, 10, 8, 18, 14, 10, 12 });

                // 6. 參數現況（每個品類一列；儲存格為「已填數/具備此欄位的元件數」，「—」表示該品類沒有此欄位）
                ExcelWorksheet paramSheet = package.Workbook.Worksheets.Add("參數現況");
                paramSheet.Cells[1, 1].Value = "品類";
                paramSheet.Cells[1, 2].Value = "元件數";
                for (int i = 0; i < parameterNames.Count; i++) paramSheet.Cells[1, i + 3].Value = parameterNames[i];
                row = 2;
                foreach (KeyValuePair<string, CategoryStat> pair in categoryStats)
                {
                    paramSheet.Cells[row, 1].Value = pair.Key;
                    paramSheet.Cells[row, 2].Value = pair.Value.Count;
                    for (int i = 0; i < parameterNames.Count; i++)
                    {
                        paramSheet.Cells[row, i + 3].Value = pair.Value.Bound[i] == 0
                            ? "—"
                            : pair.Value.Filled[i] + "/" + pair.Value.Bound[i];
                    }
                    row++;
                }
                var paramWidths = new List<double> { 18, 9 };
                paramWidths.AddRange(parameterNames.Select(p => 13.0));
                Finish(paramSheet, parameterNames.Count + 2, paramWidths.ToArray());

                // 7. 連結模型
                ExcelWorksheet linkSheet = package.Workbook.Worksheets.Add("連結模型");
                linkSheet.Cells[1, 1].Value = "連結名稱";
                linkSheet.Cells[1, 2].Value = "是否已載入";
                linkSheet.Cells[1, 3].Value = "放置次數";
                var linkInstanceCounts = new Dictionary<long, int>();
                foreach (Element instance in new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)))
                {
                    long linkTypeId = instance.GetTypeId().Value;
                    linkInstanceCounts[linkTypeId] =
                        linkInstanceCounts.TryGetValue(linkTypeId, out int c) ? c + 1 : 1;
                }
                row = 2;
                foreach (Element linkType in new FilteredElementCollector(doc).OfClass(typeof(RevitLinkType)))
                {
                    bool loaded = false;
                    try
                    {
                        loaded = RevitLinkType.IsLoaded(doc, linkType.Id);
                    }
                    catch (Exception)
                    {
                    }
                    linkSheet.Cells[row, 1].Value = linkType.Name;
                    linkSheet.Cells[row, 2].Value = loaded ? "是" : "否";
                    linkSheet.Cells[row, 3].Value =
                        linkInstanceCounts.TryGetValue(linkType.Id.Value, out int count) ? count : 0;
                    row++;
                }
                Finish(linkSheet, 3, new double[] { 60, 12, 10 });

                package.SaveAs(new System.IO.FileInfo(filePath));
            }

            return elementCount;
        }

        private static int LevelRank(Dictionary<string, int> levelOrder, string levelName)
        {
            return levelOrder.TryGetValue(levelName, out int rank) ? rank : int.MaxValue;
        }

        private static string GetLevelName(Element element, Dictionary<long, string> levelNames)
        {
            ElementId levelId = element.LevelId;

            if (levelId == null || levelId == ElementId.InvalidElementId)
            {
                foreach (BuiltInParameter builtIn in LevelParameters)
                {
                    Parameter parameter = element.get_Parameter(builtIn);
                    if (parameter == null || parameter.StorageType != StorageType.ElementId) continue;

                    ElementId value = parameter.AsElementId();
                    if (value != null && value != ElementId.InvalidElementId)
                    {
                        levelId = value;
                        break;
                    }
                }
            }

            if (levelId == null || levelId == ElementId.InvalidElementId) return NoLevel;
            return levelNames.TryGetValue(levelId.Value, out string name) ? name : NoLevel;
        }

        private static string GetText(Parameter parameter)
        {
            if (parameter == null) return string.Empty;
            return parameter.AsValueString() ?? parameter.AsString() ?? string.Empty;
        }

        // 表頭粗體、凍結首列、設定欄寬
        private static void Finish(ExcelWorksheet sheet, int columnCount, double[] widths)
        {
            if (columnCount < 1) return;
            sheet.Cells[1, 1, 1, columnCount].Style.Font.Bold = true;
            sheet.View.FreezePanes(2, 1);
            for (int i = 0; i < columnCount; i++)
            {
                sheet.Column(i + 1).Width = i < widths.Length ? widths[i] : 14;
            }
        }
    }
}
