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
        private const string PanelName = "Excel";
        private const string ButtonText = "Excel\n參數編輯";

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

            var buttonData = new PushButtonData(
                "RevitParameterEditor_Open",
                ButtonText,
                Assembly.GetExecutingAssembly().Location,
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
