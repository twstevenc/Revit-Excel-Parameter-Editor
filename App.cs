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

            return Result.Succeeded;
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
