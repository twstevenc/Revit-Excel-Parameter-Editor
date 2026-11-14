using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace RevitParameterEditor
{
    public enum WriteResult
    {
        NoParameter,   // 元件沒有這個欄位
        ReadOnly,      // 欄位唯讀
        KeptExisting,  // 已有值，依設定不覆蓋
        Unchanged,     // 新舊值相同
        Written,       // 已寫入
        Failed         // 寫入失敗
    }

    // 填值功能共用的工具
    public static class FillSupport
    {
        public const double MetersToFeet = 1.0 / 0.3048;

        // rm_id 是否屬於公區：樓層碼後面帶 Z（GZ304BZ001ES、GZ3B2Z001SR）。
        // 元件跨多個房間（以分號分隔）時，只要有一間是公區就算公區，例如同時面對走廊的住戶大門。
        public static bool IsPublicRoom(string roomText)
        {
            foreach (string part in (roomText ?? string.Empty).Split(';'))
            {
                string roomId = part.Trim();
                if (roomId.Length > 6 && (roomId[6] == 'Z' || roomId[5] == 'Z')) return true;
            }
            return false;
        }

        // 讀取欄位文字；元件沒有這個欄位時回傳 null
        public static string GetText(Element element, string parameterName)
        {
            Parameter parameter = element.LookupParameter(parameterName);
            if (parameter == null) return null;
            return parameter.AsString() ?? parameter.AsValueString() ?? string.Empty;
        }

        public static WriteResult SetText(Element element, string parameterName, string value, bool overwrite)
        {
            Parameter parameter = element.LookupParameter(parameterName);
            if (parameter == null) return WriteResult.NoParameter;
            if (parameter.IsReadOnly) return WriteResult.ReadOnly;

            string current = parameter.AsString() ?? parameter.AsValueString() ?? string.Empty;
            if (current == value) return WriteResult.Unchanged;
            if (!overwrite && current.Trim().Length > 0) return WriteResult.KeptExisting;

            try
            {
                bool ok = parameter.StorageType == StorageType.String
                    ? parameter.Set(value)
                    : parameter.SetValueString(value);
                return ok ? WriteResult.Written : WriteResult.Failed;
            }
            catch (Exception)
            {
                return WriteResult.Failed;
            }
        }

        // 模型中的實體元件（不含類型、視圖專用元件、房間）
        public static List<Element> CollectModelElements(Document doc, bool activeViewOnly)
        {
            FilteredElementCollector collector = activeViewOnly && doc.ActiveView != null
                ? new FilteredElementCollector(doc, doc.ActiveView.Id)
                : new FilteredElementCollector(doc);

            var result = new List<Element>();
            foreach (Element element in collector.WhereElementIsNotElementType())
            {
                Category category = element.Category;
                if (category == null) continue;
                if (category.CategoryType != CategoryType.Model) continue;
                if (element.ViewSpecific) continue;
                if (element is SpatialElement) continue;

                // 相機、圖紙、連結檔不是模型元件
                long builtInCategory = category.Id.Value;
                if (builtInCategory == (long)BuiltInCategory.OST_Cameras ||
                    builtInCategory == (long)BuiltInCategory.OST_Sheets ||
                    builtInCategory == (long)BuiltInCategory.OST_RvtLinks) continue;

                ElementId typeId = element.GetTypeId();
                if (typeId == null || typeId == ElementId.InvalidElementId) continue;

                result.Add(element);
            }
            return result;
        }

        // 詢問處理範圍。回傳 null 表示取消；true 表示只處理目前視圖
        public static bool? AskScope(string title, string instruction)
        {
            var dialog = new TaskDialog(title)
            {
                TitleAutoPrefix = false,
                MainInstruction = instruction,
                CommonButtons = TaskDialogCommonButtons.Cancel
            };
            dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "只處理目前視圖中的元件",
                "先切到要處理的樓層平面或 3D 視圖，再執行。");
            dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "處理整個模型");

            TaskDialogResult result = dialog.Show();
            if (result == TaskDialogResult.CommandLink1) return true;
            if (result == TaskDialogResult.CommandLink2) return false;
            return null;
        }

        // 詢問是否覆蓋現有值。回傳 null 表示取消；true 表示覆蓋
        public static bool? AskOverwrite(string title)
        {
            var dialog = new TaskDialog(title)
            {
                TitleAutoPrefix = false,
                MainInstruction = "欄位已經有值時要怎麼處理？",
                CommonButtons = TaskDialogCommonButtons.Cancel
            };
            dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "只填空白的欄位",
                "已經有值的欄位保持不變。");
            dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "全部重新填入",
                "以這次計算的結果覆蓋現有的值。");

            TaskDialogResult result = dialog.Show();
            if (result == TaskDialogResult.CommandLink1) return false;
            if (result == TaskDialogResult.CommandLink2) return true;
            return null;
        }

        // 結果視窗：摘要加上可展開的明細
        public static void ShowResult(string title, string instruction, string content, IEnumerable<string> details)
        {
            var dialog = new TaskDialog(title)
            {
                TitleAutoPrefix = false,
                MainInstruction = instruction,
                MainContent = content
            };
            List<string> lines = details == null ? new List<string>() : details.ToList();
            if (lines.Count > 0) dialog.ExpandedContent = string.Join("\n", lines);
            dialog.Show();
        }
    }
}
