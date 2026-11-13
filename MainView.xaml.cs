using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Microsoft.Win32;
using OfficeOpenXml; // EPPlus 套件
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;

namespace RevitParameterEditor
{
    public partial class MainView : Window
    {
        private readonly UIApplication _uiapp;
        private readonly Document _doc;

        public MainView(UIApplication uiapp)
        {
            InitializeComponent();
            _uiapp = uiapp;
            _doc = uiapp.ActiveUIDocument.Document;

            // EPPlus 4.5.3.3 (LGPL) 不需要設定 LicenseContext；
            // .NET 8 需註冊 code page 編碼，否則 EPPlus 可能找不到 1252 編碼
            System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);

            LoadSchedules();
            LoadConfigs();
        }

        // 把可用的參數設定檔載入下拉選單，預設選上次用過的
        private void LoadConfigs()
        {
            List<ConfigEntry> entries = ConfigStore.GetEntries();
            ConfigComboBox.ItemsSource = entries;
            if (entries.Count == 0) return;

            string last = ConfigStore.LoadLastUsed();
            ConfigComboBox.SelectedItem =
                entries.FirstOrDefault(e => string.Equals(e.Path, last, StringComparison.OrdinalIgnoreCase))
                ?? entries[0];
        }

        // 「匯出 A」按鈕：依設定檔匯出，欄位順序與設定檔相同
        private void ExportByConfigButton_Click(object sender, RoutedEventArgs e)
        {
            var entry = ConfigComboBox.SelectedItem as ConfigEntry;
            if (entry == null)
            {
                TaskDialog.Show("錯誤", "找不到參數設定檔。請先用「建立參數」功能確認設定檔存在。");
                return;
            }

            ParameterConfig config;
            try
            {
                config = ParameterConfig.Load(entry.Path);
            }
            catch (Exception ex)
            {
                TaskDialog.Show("錯誤", "無法讀取設定檔：" + ex.Message);
                return;
            }

            bool viewOnly = ViewScopeRadio.IsChecked == true;

            var saveFileDialog = new SaveFileDialog
            {
                Filter = "Excel 檔案 (*.xlsx)|*.xlsx",
                FileName = $"{entry.Name}_{(viewOnly ? "目前視圖" : "整個專案")}.xlsx",
                Title = "儲存匯出檔"
            };
            if (saveFileDialog.ShowDialog() != true) return;

            StatusTextBlock.Text = "匯出中...";
            try
            {
                int count = ExportByConfig(config, viewOnly, saveFileDialog.FileName);
                StatusTextBlock.Text = $"匯出完成，共 {count} 個元件。";
                TaskDialog.Show("完成",
                    $"已匯出 {count} 個元件、{config.Parameters.Count} 個參數欄位。\n" +
                    "修改後存檔並關閉 Excel，再用下方的「匯入」寫回 Revit。");
            }
            catch (Exception ex)
            {
                StatusTextBlock.Text = "匯出失敗。";
                TaskDialog.Show("匯出錯誤", ex.Message);
            }
        }

        // 依設定檔匯出：列 = 設定檔品類中的元件；欄 = 設定檔參數（照設定檔順序）
        private int ExportByConfig(ParameterConfig config, bool viewOnly, string filePath)
        {
            var categories = new List<BuiltInCategory>();
            foreach (CategorySpec spec in config.Categories)
            {
                if (Enum.TryParse(spec.Code, out BuiltInCategory builtIn) &&
                    Enum.IsDefined(typeof(BuiltInCategory), builtIn))
                {
                    categories.Add(builtIn);
                }
            }
            if (categories.Count == 0) throw new Exception("設定檔中沒有可用的品類。");
            if (config.Parameters.Count == 0) throw new Exception("設定檔中沒有啟用的參數。");

            FilteredElementCollector collector;
            if (viewOnly)
            {
                View activeView = _doc.ActiveView;
                if (activeView == null) throw new Exception("目前沒有作用中的視圖。");
                collector = new FilteredElementCollector(_doc, activeView.Id);
            }
            else
            {
                collector = new FilteredElementCollector(_doc);
            }

            List<Element> elements = collector
                .WherePasses(new ElementMulticategoryFilter(categories))
                .WhereElementIsNotElementType()
                .Where(el => el.Category != null)
                .OrderBy(el => el.Category.Name)
                .ThenBy(el => el.Id.Value)
                .ToList();

            if (elements.Count == 0) throw new Exception("範圍內沒有符合設定檔品類的元件。");

            // 前四欄是參考資訊（欄名加括號，匯入時會自動略過），其後是設定檔的參數
            string[] infoHeaders = { "(品類)", "(族群)", "(類型)", "(樓層)" };
            int firstParamCol = 2 + infoHeaders.Length;

            using (var package = new ExcelPackage())
            {
                var worksheet = package.Workbook.Worksheets.Add("參數");

                // 第 1 列：欄名（匯入時靠它對應參數）；第 2 列：中文說明（匯入時略過）
                worksheet.Cells[1, 1].Value = "ElementID";
                worksheet.Cells[2, 1].Value = "說明";
                for (int i = 0; i < infoHeaders.Length; i++)
                {
                    worksheet.Cells[1, i + 2].Value = infoHeaders[i];
                }
                for (int i = 0; i < config.Parameters.Count; i++)
                {
                    worksheet.Cells[1, firstParamCol + i].Value = config.Parameters[i].Name;
                    worksheet.Cells[2, firstParamCol + i].Value = config.Parameters[i].Description;
                }

                int row = 3;
                foreach (Element elem in elements)
                {
                    var elementType = _doc.GetElement(elem.GetTypeId()) as ElementType;
                    var level = _doc.GetElement(elem.LevelId) as Level;

                    worksheet.Cells[row, 1].Value = elem.Id.ToString();
                    worksheet.Cells[row, 2].Value = elem.Category.Name;
                    worksheet.Cells[row, 3].Value = elementType != null ? elementType.FamilyName : string.Empty;
                    worksheet.Cells[row, 4].Value = elementType != null ? elementType.Name : elem.Name;
                    worksheet.Cells[row, 5].Value = level != null ? level.Name : string.Empty;

                    for (int i = 0; i < config.Parameters.Count; i++)
                    {
                        worksheet.Cells[row, firstParamCol + i].Value =
                            GetValueText(elem.LookupParameter(config.Parameters[i].Name));
                    }
                    row++;
                }

                int lastCol = firstParamCol + config.Parameters.Count - 1;
                worksheet.Cells[1, 1, 1, lastCol].Style.Font.Bold = true;
                worksheet.Cells[2, 1, 2, lastCol].Style.Font.Color.SetColor(System.Drawing.Color.Gray);
                worksheet.View.FreezePanes(3, firstParamCol);
                for (int col = 1; col <= lastCol; col++)
                {
                    worksheet.Column(col).Width = col < firstParamCol ? 16 : 20;
                }

                package.SaveAs(new FileInfo(filePath));
            }

            return elements.Count;
        }

        // 把專案中所有明細表載入下拉選單
        private void LoadSchedules()
        {
            var schedules = new FilteredElementCollector(_doc)
                .OfClass(typeof(ViewSchedule))
                .Cast<ViewSchedule>()
                .Where(s => !s.IsTemplate && s.Definition.GetFieldCount() > 0)
                .OrderBy(s => s.Name)
                .ToList();

            ScheduleComboBox.ItemsSource = schedules;
            ScheduleComboBox.DisplayMemberPath = "Name";
        }

        // 「匯出」按鈕
        private void ExportButton_Click(object sender, RoutedEventArgs e)
        {
            if (ScheduleComboBox.SelectedItem == null)
            {
                StatusTextBlock.Text = "錯誤：尚未選擇明細表。";
                TaskDialog.Show("錯誤", "請先從清單中選擇一張明細表。");
                return;
            }

            var schedule = ScheduleComboBox.SelectedItem as ViewSchedule;

            var saveFileDialog = new SaveFileDialog
            {
                Filter = "Excel 檔案 (*.xlsx)|*.xlsx",
                FileName = $"{schedule.Name}.xlsx",
                Title = "儲存明細表"
            };

            if (saveFileDialog.ShowDialog() == true)
            {
                StatusTextBlock.Text = "匯出中...";
                try
                {
                    ExportSchedule(schedule, saveFileDialog.FileName);
                    StatusTextBlock.Text = "匯出完成。";
                    TaskDialog.Show("完成", $"明細表「{schedule.Name}」已匯出。");
                }
                catch (Exception ex)
                {
                    StatusTextBlock.Text = "匯出失敗。";
                    TaskDialog.Show("匯出錯誤", ex.Message);
                }
            }
        }

        // 「匯入」按鈕
        private void ImportButton_Click(object sender, RoutedEventArgs e)
        {
            var openFileDialog = new OpenFileDialog
            {
                Filter = "Excel 檔案 (*.xlsx)|*.xlsx",
                Title = "選擇要匯入的檔案"
            };

            if (openFileDialog.ShowDialog() == true)
            {
                StatusTextBlock.Text = "匯入並更新中...";
                try
                {
                    int updatedCount = ImportParameters(openFileDialog.FileName);
                    StatusTextBlock.Text = $"匯入完成，已更新 {updatedCount} 個元件。";
                    TaskDialog.Show("完成", $"已更新 Revit 專案中的 {updatedCount} 個元件。");
                }
                catch (Exception ex)
                {
                    StatusTextBlock.Text = "匯入失敗。";
                    TaskDialog.Show("匯入錯誤", $"發生錯誤：{ex.Message}");
                }
            }
        }

        // 找參數：先找實例參數，找不到再找類型參數（僅供匯出顯示）
        private Parameter FindParameter(Element elem, string name)
        {
            var p = elem.LookupParameter(name);
            if (p != null) return p;

            var typeId = elem.GetTypeId();
            if (typeId == null || typeId == ElementId.InvalidElementId) return null;
            return _doc.GetElement(typeId)?.LookupParameter(name);
        }

        private static string GetValueText(Parameter p)
        {
            if (p == null) return null;
            return p.AsValueString() ?? p.AsString();
        }

        // 匯出（Revit API 只能在主執行緒呼叫，所以不使用 Task.Run）
        private void ExportSchedule(ViewSchedule schedule, string filePath)
        {
            using (var package = new ExcelPackage())
            {
                var worksheet = package.Workbook.Worksheets.Add("明細表資料");
                var collector = new FilteredElementCollector(_doc, schedule.Id);

                // 表頭：A 欄是 ElementID，其後是明細表實際包含的欄位
                var definition = schedule.Definition;
                var fieldNames = new List<string>();
                for (int i = 0; i < definition.GetFieldCount(); i++)
                {
                    fieldNames.Add(definition.GetField(i).GetName());
                }

                worksheet.Cells[1, 1].Value = "ElementID";
                for (int i = 0; i < fieldNames.Count; i++)
                {
                    worksheet.Cells[1, i + 2].Value = fieldNames[i];
                }

                // 資料
                int row = 2;
                foreach (Element elem in collector)
                {
                    worksheet.Cells[row, 1].Value = elem.Id.ToString();
                    for (int col = 0; col < fieldNames.Count; col++)
                    {
                        worksheet.Cells[row, col + 2].Value = GetValueText(FindParameter(elem, fieldNames[col]));
                    }
                    row++;
                }

                worksheet.Cells.AutoFitColumns();
                package.SaveAs(new FileInfo(filePath));
            }
        }

        // 匯入
        private int ImportParameters(string filePath)
        {
            int updatedCount = 0;
            var fileInfo = new FileInfo(filePath);

            using (var package = new ExcelPackage(fileInfo))
            {
                var worksheet = package.Workbook.Worksheets.FirstOrDefault();
                if (worksheet == null) throw new Exception("檔案中找不到任何工作表。");

                var headers = new List<string>();
                for (int col = 1; col <= worksheet.Dimension.End.Column; col++)
                {
                    headers.Add(worksheet.Cells[1, col].Text);
                }

                int elementIdCol = headers.IndexOf("ElementID") + 1;
                if (elementIdCol == 0) throw new Exception("找不到「ElementID」欄，匯入必須有這一欄。");

                using (var trans = new Transaction(_doc, "從 Excel 更新參數"))
                {
                    trans.Start();

                    for (int row = 2; row <= worksheet.Dimension.End.Row; row++)
                    {
                        var idText = worksheet.Cells[row, elementIdCol].Text;
                        if (!long.TryParse(idText, out long idAsLong)) continue;
                        
                        var elementId = new ElementId(idAsLong);
                        var element = _doc.GetElement(elementId);

                        if (element != null)
                        {
                            bool elementUpdated = false;
                            for (int col = 1; col <= headers.Count; col++)
                            {
                                if (col == elementIdCol) continue; // 略過 ID 欄

                                var paramName = headers[col - 1];
                                var parameter = element.LookupParameter(paramName);

                                if (parameter != null && !parameter.IsReadOnly)
                                {
                                    var newValue = worksheet.Cells[row, col].Text;
                                    // 新舊值相同就不寫入
                                    string oldValue = GetValueText(parameter) ?? string.Empty;
                                    if (oldValue != newValue)
                                    {
                                        // 文字參數用 Set，數值等其他型別用 SetValueString（會依專案單位解析）
                                        try
                                        {
                                            bool ok = parameter.StorageType == StorageType.String
                                                ? parameter.Set(newValue)
                                                : parameter.SetValueString(newValue);
                                            if (ok) elementUpdated = true;
                                        }
                                        catch (Exception)
                                        {
                                            // 這個參數無法用文字寫入（例如 ElementId 型別），略過
                                        }
                                    }
                                }
                            }
                            if (elementUpdated) updatedCount++;
                        }
                    }
                    trans.Commit();
                }
            }
            return updatedCount;
        }
    }
}
