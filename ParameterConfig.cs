using System;
using System.Collections.Generic;
using System.Linq;
using OfficeOpenXml;

namespace RevitParameterEditor
{
    // 設定檔中的一列參數定義
    public class ParameterSpec
    {
        public string Name;         // 參數名稱
        public string Description;  // 中文說明（會成為參數的工具提示）
        public bool IsInstance;     // true = 實例參數，false = 類型參數
        public string DataType;     // 文字 / 整數 / 數值 / 是否
        public string Group;        // 性質面板中的群組
        public string GuidText;     // 選填；空白時依參數名稱自動產生固定的 GUID
        public string DefaultValue; // 選填；建立時自動填入尚未填值的元件
    }

    // 設定檔中的一列品類
    public class CategorySpec
    {
        public string Code;   // BuiltInCategory 名稱，例如 OST_MechanicalEquipment
        public string Label;  // 中文說明，只用於顯示
    }

    public class ParameterConfig
    {
        public const string ParameterSheet = "參數";
        public const string CategorySheet = "品類";

        public List<ParameterSpec> Parameters = new List<ParameterSpec>();
        public List<CategorySpec> Categories = new List<CategorySpec>();
        public List<string> Warnings = new List<string>();

        // 讀取設定檔（Excel 開著時也能讀）
        public static ParameterConfig Load(string path)
        {
            System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);

            var config = new ParameterConfig();

            using (var stream = new System.IO.FileStream(path, System.IO.FileMode.Open,
                       System.IO.FileAccess.Read, System.IO.FileShare.ReadWrite))
            using (var package = new ExcelPackage(stream))
            {
                var paramSheet = package.Workbook.Worksheets[ParameterSheet];
                if (paramSheet == null || paramSheet.Dimension == null)
                    throw new Exception($"設定檔中找不到「{ParameterSheet}」工作表。");

                var catSheet = package.Workbook.Worksheets[CategorySheet];
                if (catSheet == null || catSheet.Dimension == null)
                    throw new Exception($"設定檔中找不到「{CategorySheet}」工作表。");

                ReadParameters(paramSheet, config);
                ReadCategories(catSheet, config);
            }

            return config;
        }

        private static void ReadParameters(ExcelWorksheet ws, ParameterConfig config)
        {
            var cols = ReadHeader(ws);
            int cEnabled = Require(cols, "啟用", ws.Name);
            int cName = Require(cols, "參數名稱", ws.Name);
            int cKind = Require(cols, "實例或類型", ws.Name);
            int cDesc = Optional(cols, "中文說明");
            int cType = Optional(cols, "資料型別");
            int cGroup = Optional(cols, "群組");
            int cGuid = Optional(cols, "GUID");
            int cDefault = Optional(cols, "預設值");

            var seen = new HashSet<string>();
            for (int row = 2; row <= ws.Dimension.End.Row; row++)
            {
                string name = Cell(ws, row, cName);
                if (name.Length == 0) continue;
                if (!IsYes(Cell(ws, row, cEnabled))) continue;

                if (!seen.Add(name))
                {
                    config.Warnings.Add($"第 {row} 列：參數「{name}」重複，已略過。");
                    continue;
                }

                string kind = Cell(ws, row, cKind);
                bool isInstance;
                if (kind == "實例" || kind.Equals("Instance", StringComparison.OrdinalIgnoreCase)) isInstance = true;
                else if (kind == "類型" || kind.Equals("Type", StringComparison.OrdinalIgnoreCase)) isInstance = false;
                else
                {
                    config.Warnings.Add($"第 {row} 列：參數「{name}」的「實例或類型」填了「{kind}」，應為「實例」或「類型」，已略過。");
                    continue;
                }

                config.Parameters.Add(new ParameterSpec
                {
                    Name = name,
                    Description = Cell(ws, row, cDesc),
                    IsInstance = isInstance,
                    DataType = Cell(ws, row, cType),
                    Group = Cell(ws, row, cGroup),
                    GuidText = Cell(ws, row, cGuid),
                    DefaultValue = Cell(ws, row, cDefault)
                });
            }
        }

        private static void ReadCategories(ExcelWorksheet ws, ParameterConfig config)
        {
            var cols = ReadHeader(ws);
            int cEnabled = Require(cols, "啟用", ws.Name);
            int cCode = Require(cols, "品類代碼", ws.Name);
            int cLabel = Optional(cols, "中文名稱");

            var seen = new HashSet<string>();
            for (int row = 2; row <= ws.Dimension.End.Row; row++)
            {
                string code = Cell(ws, row, cCode);
                if (code.Length == 0) continue;
                if (!IsYes(Cell(ws, row, cEnabled))) continue;
                if (!seen.Add(code)) continue;

                config.Categories.Add(new CategorySpec { Code = code, Label = Cell(ws, row, cLabel) });
            }
        }

        // 讀第一列表頭，回傳「欄名 -> 欄號」
        private static Dictionary<string, int> ReadHeader(ExcelWorksheet ws)
        {
            var cols = new Dictionary<string, int>();
            for (int col = 1; col <= ws.Dimension.End.Column; col++)
            {
                string header = ws.Cells[1, col].Text.Trim();
                if (header.Length > 0 && !cols.ContainsKey(header)) cols[header] = col;
            }
            return cols;
        }

        private static int Require(Dictionary<string, int> cols, string header, string sheetName)
        {
            if (!cols.TryGetValue(header, out int col))
                throw new Exception($"「{sheetName}」工作表缺少「{header}」欄。");
            return col;
        }

        private static int Optional(Dictionary<string, int> cols, string header)
        {
            return cols.TryGetValue(header, out int col) ? col : 0;
        }

        private static string Cell(ExcelWorksheet ws, int row, int col)
        {
            return col == 0 ? string.Empty : ws.Cells[row, col].Text.Trim();
        }

        private static bool IsYes(string text)
        {
            return new[] { "Y", "YES", "是", "V", "1", "TRUE" }.Contains(text.ToUpperInvariant());
        }
    }
}
