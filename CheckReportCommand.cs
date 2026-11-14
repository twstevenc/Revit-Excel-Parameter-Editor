using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using OfficeOpenXml;

namespace RevitParameterEditor
{
    // 「檢核報告」按鈕：依自主檢查表檢核模型，匯出 Excel（唯讀，不會修改模型）
    [Transaction(TransactionMode.ReadOnly)]
    public class CheckReportCommand : IExternalCommand
    {
        private const string Title = "檢核報告";
        private const int PrefixLength = 6;

        // rm_id：三碼大廈碼 + 兩碼樓層碼 + 一碼棟別 + 可有可無的 Z + 三碼流水號 + 房間代碼
        private static readonly Regex RoomIdPattern =
            new Regex(@"^[A-Z0-9]{3}[A-Z0-9]{2}[A-Z]Z?\d{3}[A-Z0-9_]+$");
        private static readonly Regex CodePattern = new Regex(@"^[A-Za-z0-9_]+$");
        private static readonly Regex AsciiPattern = new Regex(@"^[\x20-\x7E]+$");

        // 有契約項次時，這些欄位也應該要有值
        private static readonly string[] ContractFields = { "part", "unit", "price" };

        private class Issue
        {
            public string Check;
            public string Section;
            public string ElementId;
            public string Category;
            public string TypeName;
            public string Value;
            public string Note;
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
                // 要檢查的欄位清單：取自上次使用的設定檔
                List<string> parameterNames = LoadParameterNames(out string configName);
                if (parameterNames.Count == 0)
                {
                    TaskDialog.Show(Title, "讀不到參數設定檔，無法得知要檢查哪些欄位。請先開啟一次「建立參數」確認設定檔。");
                    return Result.Cancelled;
                }

                bool? viewOnly = FillSupport.AskScope(Title,
                    $"將依設定檔「{configName}」的 {parameterNames.Count} 個欄位檢核。要檢查哪些元件？");
                if (viewOnly == null) return Result.Cancelled;

                var saveDialog = new Microsoft.Win32.SaveFileDialog
                {
                    Filter = "Excel 檔案 (*.xlsx)|*.xlsx",
                    FileName = doc.Title + "_檢核報告.xlsx",
                    Title = "儲存檢核報告"
                };
                if (saveDialog.ShowDialog() != true) return Result.Cancelled;

                var issues = new List<Issue>();
                var sectionCounts = new SortedDictionary<string, int>(StringComparer.Ordinal);
                var typeNames = new Dictionary<long, string>();

                List<Element> modelElements = FillSupport.CollectModelElements(doc, viewOnly.Value);
                var equipmentIds = new Dictionary<string, List<Element>>();
                var missingByCategory = new SortedDictionary<string, int>(StringComparer.Ordinal);

                foreach (Element element in modelElements)
                {
                    string roomId = (FillSupport.GetText(element, "rm_id") ?? string.Empty).Trim();
                    string section = SectionOf(roomId);
                    sectionCounts[section] = sectionCounts.TryGetValue(section, out int n) ? n + 1 : 1;

                    // 欄位是否齊全
                    List<string> missing = parameterNames.Where(p => element.LookupParameter(p) == null).ToList();
                    if (missing.Count > 0)
                    {
                        // 欄位不齊全通常是整個品類一起缺，所以依品類彙總，不逐一列出元件
                        string missingText = missing.Count == parameterNames.Count
                            ? "完全沒有這些欄位"
                            : "缺：" + string.Join("、", missing.Take(10)) + (missing.Count > 10 ? "…" : string.Empty);
                        string missingKey = element.Category.Name + "\u0001" + missing.Count + "\u0001" + missingText;
                        missingByCategory[missingKey] =
                            missingByCategory.TryGetValue(missingKey, out int m) ? m + 1 : 1;
                        if (missing.Count == parameterNames.Count) continue;
                    }

                    // 是否有房間編碼
                    if (roomId.Length == 0)
                    {
                        Add(issues, doc, typeNames, element, section, "沒有 rm_id", string.Empty, string.Empty);
                    }
                    else
                    {
                        foreach (string part in roomId.Split(';'))
                        {
                            if (!RoomIdPattern.IsMatch(part.Trim()))
                            {
                                Add(issues, doc, typeNames, element, section, "rm_id 格式不符", roomId, string.Empty);
                                break;
                            }
                        }
                    }

                    // 樓層碼是否和房間編碼一致
                    string floorId = (FillSupport.GetText(element, "fl_id") ?? string.Empty).Trim();
                    if (roomId.Length >= PrefixLength)
                    {
                        if (floorId.Length == 0)
                        {
                            Add(issues, doc, typeNames, element, section, "沒有 fl_id", string.Empty, string.Empty);
                        }
                        else if (!FloorMatches(floorId, roomId.Substring(3, 2)))
                        {
                            Add(issues, doc, typeNames, element, section, "fl_id 與 rm_id 的樓層不一致",
                                floorId, "rm_id：" + roomId);
                        }
                    }

                    // 設備標準與設備編碼
                    string standard = (FillSupport.GetText(element, "eq_std") ?? string.Empty).Trim();
                    string equipmentId = (FillSupport.GetText(element, "eq_id") ?? string.Empty).Trim();

                    if (standard.Length > 0 && !CodePattern.IsMatch(standard))
                    {
                        Add(issues, doc, typeNames, element, section, "eq_std 不是英數代碼", standard, string.Empty);
                    }
                    // 住戶內的元件不編 eq_id，所以只檢查公區和還沒有 rm_id 的元件
                    bool needsEquipmentId = roomId.Length < PrefixLength || FillSupport.IsPublicRoom(roomId);
                    if (standard.Length > 0 && equipmentId.Length == 0 && needsEquipmentId)
                    {
                        Add(issues, doc, typeNames, element, section, "有 eq_std 但沒有 eq_id", standard, string.Empty);
                    }
                    if (equipmentId.Length > 0)
                    {
                        if (!AsciiPattern.IsMatch(equipmentId))
                        {
                            Add(issues, doc, typeNames, element, section, "eq_id 含有非英數字元", equipmentId, string.Empty);
                        }
                        else if (roomId.Length >= PrefixLength && standard.Length > 0 &&
                                 !Regex.IsMatch(equipmentId,
                                     "^" + Regex.Escape(roomId.Substring(0, PrefixLength) + standard) + @"\d{3}$"))
                        {
                            Add(issues, doc, typeNames, element, section, "eq_id 格式不符", equipmentId,
                                "應為 rm_id 前六碼 + eq_std + 三碼流水號");
                        }

                        if (!equipmentIds.TryGetValue(equipmentId, out List<Element> sameId))
                        {
                            sameId = new List<Element>();
                            equipmentIds[equipmentId] = sameId;
                        }
                        sameId.Add(element);
                    }

                    // 契約欄位是否成套
                    string partId = (FillSupport.GetText(element, "part_id") ?? string.Empty).Trim();
                    if (partId.Length > 0)
                    {
                        List<string> empty = ContractFields
                            .Where(f => (FillSupport.GetText(element, f) ?? string.Empty).Trim().Length == 0)
                            .ToList();
                        if (empty.Count > 0)
                        {
                            Add(issues, doc, typeNames, element, section, "有 part_id 但契約欄位不完整",
                                partId, "缺：" + string.Join("、", empty));
                        }
                    }
                }

                // 欄位不齊全：每個品類一列
                foreach (KeyValuePair<string, int> pair in missingByCategory)
                {
                    string[] parts = pair.Key.Split('\u0001');
                    issues.Add(new Issue
                    {
                        Check = "欄位不齊全",
                        Section = "(整個品類)",
                        ElementId = string.Empty,
                        Category = parts[0],
                        TypeName = $"共 {pair.Value} 個元件",
                        Value = $"缺 {parts[1]} 個欄位",
                        Note = parts[2]
                    });
                }

                // eq_id 重複
                foreach (KeyValuePair<string, List<Element>> pair in equipmentIds)
                {
                    if (pair.Value.Count < 2) continue;
                    foreach (Element element in pair.Value)
                    {
                        string roomId = (FillSupport.GetText(element, "rm_id") ?? string.Empty).Trim();
                        Add(issues, doc, typeNames, element, SectionOf(roomId), "eq_id 重複", pair.Key,
                            $"共 {pair.Value.Count} 個元件使用");
                    }
                }

                // 房間
                int roomCount = CheckRooms(doc, viewOnly.Value, issues);

                Write(saveDialog.FileName, doc, configName, parameterNames.Count, viewOnly.Value,
                    modelElements.Count, roomCount, sectionCounts, issues);

                int checkCount = issues.Select(i => i.Check).Distinct().Count();
                FillSupport.ShowResult(Title,
                    issues.Count == 0
                        ? "檢核完成，沒有發現問題"
                        : $"檢核完成，共 {issues.Count} 筆待處理，分屬 {checkCount} 種問題",
                    "報告已儲存：\n" + saveDialog.FileName,
                    issues.GroupBy(i => i.Check).OrderByDescending(g => g.Count())
                        .Select(g => $"{g.Key}：{g.Count()}"));
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                TaskDialog.Show(Title, "發生錯誤：" + ex.Message);
                return Result.Failed;
            }
        }

        // 檢查房間：未放置、沒有編碼、格式、重複、房間標準與代碼是否一致
        private static int CheckRooms(Document doc, bool viewOnly, List<Issue> issues)
        {
            FilteredElementCollector collector = viewOnly && doc.ActiveView != null
                ? new FilteredElementCollector(doc, doc.ActiveView.Id)
                : new FilteredElementCollector(doc);

            var rooms = new List<Autodesk.Revit.DB.Architecture.Room>();
            foreach (Element element in collector.OfCategory(BuiltInCategory.OST_Rooms).WhereElementIsNotElementType())
            {
                var room = element as Autodesk.Revit.DB.Architecture.Room;
                if (room != null) rooms.Add(room);
            }

            var byRoomId = new Dictionary<string, List<Autodesk.Revit.DB.Architecture.Room>>();

            foreach (Autodesk.Revit.DB.Architecture.Room room in rooms)
            {
                string roomId = (FillSupport.GetText(room, "rm_id") ?? string.Empty).Trim();
                string roomStandard = (FillSupport.GetText(room, "rm_std") ?? string.Empty).Trim();
                string floorId = (FillSupport.GetText(room, "fl_id") ?? string.Empty).Trim();
                string levelName = room.Level != null ? room.Level.Name : string.Empty;
                string section = roomId.Length >= PrefixLength ? roomId.Substring(0, PrefixLength) : "(房間未編碼)";
                string label = room.Number + " " + (room.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString() ?? string.Empty);

                if (room.Area <= 0)
                {
                    AddRoom(issues, room, section, label, "房間未放置", roomId, "已不在模型中，但仍留在明細表，應刪除");
                    continue;
                }
                if (roomId.Length == 0)
                {
                    AddRoom(issues, room, section, label, "房間沒有 rm_id", string.Empty, "樓層：" + levelName);
                    continue;
                }
                if (!RoomIdPattern.IsMatch(roomId))
                {
                    AddRoom(issues, room, section, label, "rm_id 格式不符", roomId, string.Empty);
                }
                if (roomStandard.Length == 0)
                {
                    AddRoom(issues, room, section, label, "房間沒有 rm_std", roomId, string.Empty);
                }
                else if (!roomId.EndsWith(roomStandard))
                {
                    AddRoom(issues, room, section, label, "rm_std 與 rm_id 的房間代碼不一致", roomStandard,
                        "rm_id：" + roomId);
                }
                if (floorId.Length == 0)
                {
                    AddRoom(issues, room, section, label, "沒有 fl_id", roomId, string.Empty);
                }
                else if (roomId.Length >= PrefixLength && !FloorMatches(floorId, roomId.Substring(3, 2)))
                {
                    AddRoom(issues, room, section, label, "fl_id 與 rm_id 的樓層不一致", floorId, "rm_id：" + roomId);
                }

                if (!byRoomId.TryGetValue(roomId, out List<Autodesk.Revit.DB.Architecture.Room> same))
                {
                    same = new List<Autodesk.Revit.DB.Architecture.Room>();
                    byRoomId[roomId] = same;
                }
                same.Add(room);
            }

            foreach (KeyValuePair<string, List<Autodesk.Revit.DB.Architecture.Room>> pair in byRoomId)
            {
                if (pair.Value.Count < 2) continue;
                foreach (Autodesk.Revit.DB.Architecture.Room room in pair.Value)
                {
                    string label = room.Number + " " +
                                   (room.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString() ?? string.Empty);
                    AddRoom(issues, room, pair.Key.Length >= PrefixLength ? pair.Key.Substring(0, PrefixLength) : pair.Key,
                        label, "房間 rm_id 重複", pair.Key,
                        $"共 {pair.Value.Count} 個房間使用；樓層：" + (room.Level != null ? room.Level.Name : string.Empty));
                }
            }

            return rooms.Count;
        }

        // fl_id（F04、B1F、R1F…）和 rm_id 第 4、5 碼（04、B1、R1…）是否指同一層
        private static bool FloorMatches(string floorId, string roomFloorCode)
        {
            string a = floorId.ToUpperInvariant();
            string b = roomFloorCode.ToUpperInvariant();
            if (a.StartsWith("F") && a.Length == 3) return a.Substring(1) == b;      // F04 ↔ 04
            if (a.EndsWith("F") && a.Length == 3) return a.Substring(0, 2) == b;     // B1F ↔ B1、R1F ↔ R1
            return a == b;
        }

        private static string SectionOf(string roomId)
        {
            return roomId.Length >= PrefixLength ? roomId.Substring(0, PrefixLength) : "(沒有 rm_id)";
        }

        private static void Add(List<Issue> issues, Document doc, Dictionary<long, string> typeNames,
            Element element, string section, string check, string value, string note)
        {
            long typeId = element.GetTypeId().Value;
            if (!typeNames.TryGetValue(typeId, out string typeName))
            {
                var elementType = doc.GetElement(element.GetTypeId()) as ElementType;
                typeName = elementType == null ? string.Empty : elementType.FamilyName + "：" + elementType.Name;
                typeNames[typeId] = typeName;
            }

            issues.Add(new Issue
            {
                Check = check,
                Section = section,
                ElementId = element.Id.ToString(),
                Category = element.Category.Name,
                TypeName = typeName,
                Value = value,
                Note = note
            });
        }

        private static void AddRoom(List<Issue> issues, Element room, string section, string label,
            string check, string value, string note)
        {
            issues.Add(new Issue
            {
                Check = check,
                Section = section,
                ElementId = room.Id.ToString(),
                Category = "房間",
                TypeName = label,
                Value = value,
                Note = note
            });
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
                configName = entry.Name;
                return ParameterConfig.Load(entry.Path).Parameters.Select(p => p.Name).ToList();
            }
            catch (Exception)
            {
                return new List<string>();
            }
        }

        private static void Write(string path, Document doc, string configName, int parameterCount, bool viewOnly,
            int elementCount, int roomCount, SortedDictionary<string, int> sectionCounts, List<Issue> issues)
        {
            System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);

            using (var package = new ExcelPackage())
            {
                // 摘要
                ExcelWorksheet summary = package.Workbook.Worksheets.Add("摘要");
                var info = new List<string[]>
                {
                    new[] { "模型", doc.Title },
                    new[] { "檢核時間", DateTime.Now.ToString("yyyy-MM-dd HH:mm") },
                    new[] { "檢核範圍", viewOnly ? "目前視圖" : "整個模型" },
                    new[] { "欄位清單", configName + "（" + parameterCount + " 個欄位）" },
                    new[] { "檢查的元件數", elementCount.ToString() },
                    new[] { "檢查的房間數", roomCount.ToString() },
                    new[] { "待處理筆數", issues.Count.ToString() }
                };
                for (int i = 0; i < info.Count; i++)
                {
                    summary.Cells[i + 1, 1].Value = info[i][0];
                    summary.Cells[i + 1, 2].Value = info[i][1];
                    summary.Cells[i + 1, 1].Style.Font.Bold = true;
                }

                int row = info.Count + 3;
                summary.Cells[row, 1].Value = "檢核項目";
                summary.Cells[row, 2].Value = "筆數";
                summary.Cells[row, 1, row, 2].Style.Font.Bold = true;
                row++;
                foreach (IGrouping<string, Issue> group in issues.GroupBy(i => i.Check).OrderByDescending(g => g.Count()))
                {
                    summary.Cells[row, 1].Value = group.Key;
                    summary.Cells[row, 2].Value = group.Count();
                    row++;
                }
                if (issues.Count == 0)
                {
                    summary.Cells[row, 1].Value = "沒有發現問題";
                }
                summary.Column(1).Width = 36;
                summary.Column(2).Width = 48;

                // 依區段統計：區段是 rm_id 的前六碼（大廈碼＋樓層碼＋棟別）
                ExcelWorksheet bySection = package.Workbook.Worksheets.Add("依區段統計");
                List<string> checks = issues.Select(i => i.Check).Distinct().OrderBy(c => c).ToList();
                bySection.Cells[1, 1].Value = "區段";
                bySection.Cells[1, 2].Value = "元件數";
                bySection.Cells[1, 3].Value = "待處理筆數";
                for (int i = 0; i < checks.Count; i++) bySection.Cells[1, i + 4].Value = checks[i];
                bySection.Cells[1, 1, 1, checks.Count + 3].Style.Font.Bold = true;

                List<string> sections = sectionCounts.Keys.Union(issues.Select(i => i.Section)).Distinct()
                    .OrderBy(s => s, StringComparer.Ordinal).ToList();
                row = 2;
                foreach (string section in sections)
                {
                    List<Issue> inSection = issues.Where(i => i.Section == section).ToList();
                    bySection.Cells[row, 1].Value = section;
                    bySection.Cells[row, 2].Value = sectionCounts.TryGetValue(section, out int count) ? count : 0;
                    bySection.Cells[row, 3].Value = inSection.Count;
                    for (int i = 0; i < checks.Count; i++)
                    {
                        int value = inSection.Count(x => x.Check == checks[i]);
                        if (value > 0) bySection.Cells[row, i + 4].Value = value;
                    }
                    row++;
                }
                bySection.View.FreezePanes(2, 2);
                bySection.Column(1).Width = 18;
                for (int i = 2; i <= checks.Count + 3; i++) bySection.Column(i).Width = 16;

                // 明細
                ExcelWorksheet detail = package.Workbook.Worksheets.Add("明細");
                string[] headers = { "檢核項目", "區段", "ElementID", "品類", "類型或房間", "目前的值", "說明" };
                for (int i = 0; i < headers.Length; i++) detail.Cells[1, i + 1].Value = headers[i];
                detail.Cells[1, 1, 1, headers.Length].Style.Font.Bold = true;
                row = 2;
                foreach (Issue issue in issues.OrderBy(i => i.Section, StringComparer.Ordinal).ThenBy(i => i.Check))
                {
                    detail.Cells[row, 1].Value = issue.Check;
                    detail.Cells[row, 2].Value = issue.Section;
                    detail.Cells[row, 3].Value = issue.ElementId;
                    detail.Cells[row, 4].Value = issue.Category;
                    detail.Cells[row, 5].Value = issue.TypeName;
                    detail.Cells[row, 6].Value = issue.Value;
                    detail.Cells[row, 7].Value = issue.Note;
                    row++;
                }
                detail.View.FreezePanes(2, 1);
                double[] widths = { 30, 14, 12, 14, 44, 30, 50 };
                for (int i = 0; i < widths.Length; i++) detail.Column(i + 1).Width = widths[i];

                package.SaveAs(new System.IO.FileInfo(path));
            }
        }
    }
}
