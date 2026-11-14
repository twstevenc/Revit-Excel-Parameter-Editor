using System;
using System.Linq;
using System.Reflection;
using System.Windows.Media.Imaging;
using Autodesk.Revit.UI;

namespace RevitParameterEditor
{
    // Revit 啟動時執行：在功能區建立頁籤、面板與按鈕
    public class App : IExternalApplication
    {
        // 想改頁籤、面板或按鈕名稱，改這三行即可
        private const string TabName = "BIM 工具";
        private const string PanelName = "參數";
        private const string FillPanelName = "填值流程";
        private const string CreateButtonText = "建立\n參數";
        private const string ExcelButtonText = "Excel\n參數編輯";

        public Result OnStartup(UIControlledApplication application)
        {
            // 頁籤已存在時（例如其他外掛也用同一個頁籤）會丟出例外，略過即可
            try
            {
                application.CreateRibbonTab(TabName);
            }
            catch (Autodesk.Revit.Exceptions.ArgumentException)
            {
            }

            RibbonPanel panel = application.GetRibbonPanels(TabName)
                                    .FirstOrDefault(p => p.Name == PanelName)
                                ?? application.CreateRibbonPanel(TabName, PanelName);

            string assemblyPath = Assembly.GetExecutingAssembly().Location;

            // 按鈕一：依設定檔建立共用參數
            var createData = new PushButtonData(
                "RevitParameterEditor_CreateParameters",
                CreateButtonText,
                assemblyPath,
                typeof(CreateParametersCommand).FullName)
            {
                ToolTip = "選擇設定檔，勾選要建立的參數與品類，一次建立共用參數並完成綁定。",
                LargeImage = LoadIcon("param32.png"),
                Image = LoadIcon("param16.png")
            };
            panel.AddItem(createData);

            // 模型盤點：把模型概況匯出成 Excel（唯讀）
            var inventoryData = new PushButtonData(
                "RevitParameterEditor_ModelInventory",
                "模型\n盤點",
                assemblyPath,
                typeof(ModelInventoryCommand).FullName)
            {
                ToolTip = "匯出模型概況：樓層、各品類的族群類型與數量、房間清單、參數填寫現況、連結模型。不會修改模型。",
                LargeImage = LoadIcon("inventory32.png"),
                Image = LoadIcon("inventory16.png")
            };
            panel.AddItem(inventoryData);

            // 按鈕二：明細表匯出 / 匯入 Excel
            var buttonData = new PushButtonData(
                "RevitParameterEditor_Open",
                ExcelButtonText,
                assemblyPath,
                typeof(Command).FullName)
            {
                ToolTip = "將明細表匯出到 Excel，修改後再匯入以批次更新元件參數。",
                LargeImage = LoadIcon("icon32.png"),
                Image = LoadIcon("icon16.png")
            };

            panel.AddItem(buttonData);

            // 針對選取的元件手動增刪欄位
            AddButton(panel, assemblyPath, "RevitParameterEditor_ElementFields", "元件\n欄位",
                typeof(ElementFieldsCommand).FullName, "field",
                "先選取元件再按：手動新增欄位與內容、修改內容，或勾選刪除欄位。");

            // 第二個面板：填值流程，按鈕由左到右就是執行順序
            RibbonPanel fillPanel = application.GetRibbonPanels(TabName)
                                        .FirstOrDefault(p => p.Name == FillPanelName)
                                    ?? application.CreateRibbonPanel(TabName, FillPanelName);

            AddButton(fillPanel, assemblyPath, "RevitParameterEditor_RoomExport", "匯出\n房間",
                typeof(RoomExportCommand).FullName, "step1",
                "第 1 步（在建築模型執行）：把房間的範圍與 rm_id 匯出成檔案。不會修改模型。");
            AddButton(fillPanel, assemblyPath, "RevitParameterEditor_RoomFill", "帶入\n房間",
                typeof(RoomFillCommand).FullName, "step2",
                "第 2 步：依元件位置找出所在房間，填入 rm_id 與 fl_id。");
            AddButton(fillPanel, assemblyPath, "RevitParameterEditor_TypeFill", "契約\n填值",
                typeof(TypeFillCommand).FullName, "step3",
                "第 3 步：依對照表（品類＋族群＋類型）填入契約項次、單價、供應商、設備標準等欄位。");
            AddButton(fillPanel, assemblyPath, "RevitParameterEditor_EquipmentId", "設備\n編碼",
                typeof(EquipmentIdCommand).FullName, "step4",
                "第 4 步：替有 eq_std 的元件產生 eq_id（rm_id 前六碼 + eq_std + 三碼流水號）。");
            AddButton(fillPanel, assemblyPath, "RevitParameterEditor_CheckReport", "檢核\n報告",
                typeof(CheckReportCommand).FullName, "step5",
                "第 5 步：依自主檢查表檢核欄位、房間編碼與設備編碼，匯出 Excel 報告。不會修改模型。");

            return Result.Succeeded;
        }

        private static void AddButton(RibbonPanel panel, string assemblyPath, string name, string text,
            string className, string icon, string toolTip)
        {
            var data = new PushButtonData(name, text, assemblyPath, className)
            {
                ToolTip = toolTip,
                LargeImage = LoadIcon(icon + "_32.png"),
                Image = LoadIcon(icon + "_16.png")
            };
            panel.AddItem(data);
        }

        // 讀取內嵌在 dll 裡的圖示（Resources 資料夾）
        private static BitmapImage LoadIcon(string fileName)
        {
            try
            {
                return new BitmapImage(new Uri(
                    $"pack://application:,,,/RevitParameterEditor;component/Resources/{fileName}"));
            }
            catch (Exception)
            {
                return null; // 圖示載入失敗時仍顯示純文字按鈕
            }
        }

        public Result OnShutdown(UIControlledApplication application)
        {
            return Result.Succeeded;
        }
    }
}
