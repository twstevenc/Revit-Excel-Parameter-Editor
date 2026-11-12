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
