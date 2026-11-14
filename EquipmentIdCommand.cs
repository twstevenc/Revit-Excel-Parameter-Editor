using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace RevitParameterEditor
{
    // 「設備編碼」按鈕：依「rm_id 前六碼（大廈碼＋樓層碼＋棟別）＋ eq_std ＋ 三碼流水號」產生 eq_id
    [Transaction(TransactionMode.Manual)]
    public class EquipmentIdCommand : IExternalCommand
    {
        private const string Title = "設備編碼";
        private const int PrefixLength = 6;   // 例如 GZ304B、GZ3B1Z
        private const int SerialDigits = 3;

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
                bool? viewOnly = FillSupport.AskScope(Title,
                    "會替有 eq_std 和 rm_id 的元件產生 eq_id。要處理哪些元件？");
                if (viewOnly == null) return Result.Cancelled;

                bool? overwrite = FillSupport.AskOverwrite(Title);
                if (overwrite == null) return Result.Cancelled;

                bool? publicOnly = AskZone();
                if (publicOnly == null) return Result.Cancelled;

                // 編碼登記簿：記錄每個模型用掉哪些流水號，避免不同模型編出相同的 eq_id
                string registryPath = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "RevitParameterEditor", "eq-id-registry.json");
                Dictionary<string, Dictionary<string, List<int>>> registry = LoadRegistry(registryPath);
                string modelName = doc.Title;

                // 依「前綴＋設備標準」分組
                var groups = new SortedDictionary<string, List<Element>>(StringComparer.Ordinal);
                int noRoom = 0;
                var inUnit = new List<Element>();   // 住戶內的元件：選擇只編公區時不編碼，並清掉舊的 eq_id

                // 處理整個模型時，這個模型先前登記的號碼全部重新計算
                if (!viewOnly.Value)
                {
                    foreach (Dictionary<string, List<int>> byModelAll in registry.Values)
                    {
                        byModelAll.Remove(modelName);
                    }
                }

                foreach (Element element in FillSupport.CollectModelElements(doc, viewOnly.Value))
                {
                    string standard = (FillSupport.GetText(element, "eq_std") ?? string.Empty).Trim();
                    if (standard.Length == 0) continue;
                    if (element.LookupParameter("eq_id") == null) continue;

                    string roomText = FillSupport.GetText(element, "rm_id") ?? string.Empty;
                    string roomId = roomText.Split(';')[0].Trim();
                    if (roomId.Length < PrefixLength)
                    {
                        noRoom++;
                        continue;
                    }

                    if (publicOnly.Value && !FillSupport.IsPublicRoom(roomText))
                    {
                        inUnit.Add(element);
                        continue;
                    }

                    string groupKey = roomId.Substring(0, PrefixLength) + standard;
                    if (!groups.TryGetValue(groupKey, out List<Element> list))
                    {
                        list = new List<Element>();
                        groups[groupKey] = list;
                    }
                    list.Add(element);
                }

                int written = 0, kept = 0, failed = 0, cleared = 0;

                using (var transaction = new Transaction(doc, "產生設備編碼"))
                {
                    transaction.Start();

                    // 住戶內的元件不編碼：把先前編的 eq_id 清掉
                    foreach (Element element in inUnit)
                    {
                        string current = (FillSupport.GetText(element, "eq_id") ?? string.Empty).Trim();
                        if (current.Length == 0) continue;
                        if (FillSupport.SetText(element, "eq_id", string.Empty, true) == WriteResult.Written) cleared++;
                    }

                    foreach (KeyValuePair<string, List<Element>> group in groups)
                    {
                        string groupKey = group.Key;
                        var pattern = new Regex("^" + Regex.Escape(groupKey) + @"(\d+)$");

                        if (!registry.TryGetValue(groupKey, out Dictionary<string, List<int>> byModel))
                        {
                            byModel = new Dictionary<string, List<int>>();
                            registry[groupKey] = byModel;
                        }

                        // 其他模型已經用掉的號碼
                        var taken = new HashSet<int>();
                        foreach (KeyValuePair<string, List<int>> pair in byModel)
                        {
                            if (pair.Key == modelName) continue;
                            foreach (int number in pair.Value) taken.Add(number);
                        }

                        List<Element> ordered = group.Value.OrderBy(e => e.Id.Value).ToList();
                        var mine = new List<int>();
                        var pending = new List<Element>();

                        foreach (Element element in ordered)
                        {
                            string current = (FillSupport.GetText(element, "eq_id") ?? string.Empty).Trim();
                            Match match = pattern.Match(current);

                            // 不覆蓋時，保留已經符合格式、而且沒有和別人重複的編號
                            if (!overwrite.Value && current.Length > 0)
                            {
                                if (match.Success && int.TryParse(match.Groups[1].Value, out int existing) &&
                                    taken.Add(existing))
                                {
                                    mine.Add(existing);
                                }
                                kept++;
                                continue;
                            }
                            pending.Add(element);
                        }

                        int next = 1;
                        foreach (Element element in pending)
                        {
                            while (taken.Contains(next)) next++;
                            string value = groupKey + next.ToString().PadLeft(SerialDigits, '0');

                            WriteResult result = FillSupport.SetText(element, "eq_id", value, true);
                            if (result == WriteResult.Written || result == WriteResult.Unchanged)
                            {
                                taken.Add(next);
                                mine.Add(next);
                                written++;
                            }
                            else
                            {
                                failed++;
                            }
                        }

                        // 只處理目前視圖時，保留這個模型先前登記、但這次沒有處理到的號碼
                        if (viewOnly.Value && byModel.TryGetValue(modelName, out List<int> previous))
                        {
                            foreach (int number in previous)
                            {
                                if (!mine.Contains(number) && !overwrite.Value) mine.Add(number);
                            }
                        }
                        byModel[modelName] = mine.Distinct().OrderBy(n => n).ToList();
                    }

                    transaction.Commit();
                }

                SaveRegistry(registryPath, registry);

                FillSupport.ShowResult(Title,
                    $"已編碼 {written} 個元件，共 {groups.Count} 組",
                    "按一次 Ctrl+Z 可以復原模型中的值。展開可看明細。",
                    new[]
                    {
                        $"已寫入 eq_id：{written}",
                        $"已有值而保留：{kept}",
                        $"寫入失敗或唯讀：{failed}",
                        $"有 eq_std 但沒有 rm_id（無法編碼）：{noRoom}",
                        publicOnly.Value
                            ? $"住戶內的元件（不編碼）：{inUnit.Count}，其中清除舊 eq_id：{cleared}"
                            : "範圍：公區和住戶內都編碼",
                        string.Empty,
                        "編碼格式：rm_id 前六碼 + eq_std + 三碼流水號",
                        "編碼登記簿：" + registryPath,
                        "登記簿存在這台電腦上，用來避免不同模型編出相同的 eq_id。"
                    });
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                TaskDialog.Show(Title, "發生錯誤：" + ex.Message);
                return Result.Failed;
            }
        }

        // 詢問編碼範圍。回傳 null 表示取消；true 表示只編公區
        private static bool? AskZone()
        {
            var dialog = new TaskDialog(Title)
            {
                TitleAutoPrefix = false,
                MainInstruction = "住戶內的元件要不要編 eq_id？",
                MainContent = "依 rm_id 判斷：樓層碼後面帶 Z 的是公區（例如 GZ304BZ001ES、GZ3B2Z001SR），其餘是住戶（例如 GZ304B001L）。",
                CommonButtons = TaskDialogCommonButtons.Cancel
            };
            dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "只編公區",
                "住戶內的元件不編碼，先前編過的 eq_id 會清掉。eq_std 和 fn_std 不會變動。");
            dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "公區和住戶內都編");

            TaskDialogResult result = dialog.Show();
            if (result == TaskDialogResult.CommandLink1) return true;
            if (result == TaskDialogResult.CommandLink2) return false;
            return null;
        }

        private static Dictionary<string, Dictionary<string, List<int>>> LoadRegistry(string path)
        {
            try
            {
                if (System.IO.File.Exists(path))
                {
                    var loaded = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, List<int>>>>(
                        System.IO.File.ReadAllText(path));
                    if (loaded != null) return loaded;
                }
            }
            catch (Exception)
            {
            }
            return new Dictionary<string, Dictionary<string, List<int>>>();
        }

        private static void SaveRegistry(string path, Dictionary<string, Dictionary<string, List<int>>> registry)
        {
            try
            {
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path) ?? string.Empty);
                System.IO.File.WriteAllText(path, JsonSerializer.Serialize(registry));
            }
            catch (Exception)
            {
                // 登記簿寫不進去不影響模型中的結果
            }
        }
    }
}
