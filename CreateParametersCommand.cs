using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace RevitParameterEditor
{
    // 「建立參數」按鈕：選設定檔、勾選參數與品類，建立共用參數並綁定
    [Transaction(TransactionMode.Manual)]
    public class CreateParametersCommand : IExternalCommand
    {
        private const string Title = "建立參數";

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            Document doc = commandData.Application.ActiveUIDocument?.Document;
            if (doc == null)
            {
                TaskDialog.Show(Title, "請先開啟一個專案。");
                return Result.Cancelled;
            }
            if (doc.IsFamilyDocument)
            {
                TaskDialog.Show(Title, "這個功能只能在專案中使用，不能在族群編輯器中使用。");
                return Result.Cancelled;
            }

            try
            {
                var view = new CreateParametersView(doc);
                if (view.ShowDialog() != true || view.SelectedConfig == null)
                    return Result.Cancelled;

                CreationReport report;
                using (var transaction = new Transaction(doc, "建立共用參數"))
                {
                    transaction.Start();
                    report = new SharedParameterCreator(doc).Run(view.SelectedConfig);
                    transaction.Commit();
                }

                ShowReport(report);
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                TaskDialog.Show(Title, "發生錯誤：" + ex.Message);
                return Result.Failed;
            }
        }

        private static void ShowReport(CreationReport report)
        {
            var lines = new List<string>();
            AddSection(lines, "新建立", report.Created);
            AddSection(lines, "已更新", report.Updated);
            AddSection(lines, "已存在、無須變更", report.Unchanged);
            AddSection(lines, "已填入預設值（只填空白的元件）", report.Defaults);
            AddSection(lines, "未處理", report.Skipped);
            AddSection(lines, "品類說明", report.CategoryNotes);

            var dialog = new TaskDialog(Title)
            {
                TitleAutoPrefix = false,
                MainInstruction =
                    $"新建立 {report.Created.Count} 個、更新 {report.Updated.Count} 個、" +
                    $"無須變更 {report.Unchanged.Count} 個、未處理 {report.Skipped.Count} 個",
                MainContent =
                    $"已綁定到 {report.CategoryCount} 個品類。按一次 Ctrl+Z 可以全部復原。" +
                    (report.Defaults.Count > 0 ? "\n" + string.Join("\n", report.Defaults) : string.Empty) +
                    (report.Skipped.Count > 0 || report.CategoryNotes.Count > 0
                        ? "\n有項目未處理，請展開詳細資料查看原因。"
                        : string.Empty),
                ExpandedContent = string.Join("\n", lines)
            };
            dialog.Show();
        }

        private static void AddSection(List<string> lines, string heading, List<string> items)
        {
            if (items.Count == 0) return;
            if (lines.Count > 0) lines.Add(string.Empty);
            lines.Add(heading + "：");
            lines.AddRange(items.Select(i => "  " + i));
        }
    }
}
