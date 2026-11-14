using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace RevitParameterEditor
{
    // 「匯出房間」按鈕：在有房間的模型（建築模型）執行，把房間範圍與編碼存成檔案
    [Transaction(TransactionMode.ReadOnly)]
    public class RoomExportCommand : IExternalCommand
    {
        private const string Title = "匯出房間";

        // 房間的垂直範圍：樓層線往下 25 公分，到上一層樓層線往下 25 公分
        private const double BelowLevelMeters = 0.25;
        private const double DefaultStoreyMeters = 3.4;

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
                List<Level> levels = new FilteredElementCollector(doc)
                    .OfClass(typeof(Level)).Cast<Level>().ToList();

                var records = new List<RoomRecord>();
                int unplaced = 0;
                int noBoundary = 0;
                int noCode = 0;

                foreach (Element element in new FilteredElementCollector(doc)
                             .OfCategory(BuiltInCategory.OST_Rooms)
                             .WhereElementIsNotElementType())
                {
                    var room = element as Autodesk.Revit.DB.Architecture.Room;
                    if (room == null) continue;
                    if (room.Area <= 0)
                    {
                        unplaced++;
                        continue;
                    }

                    Level level = room.Level;
                    if (level == null) continue;

                    var loops = new List<List<double[]>>();
                    double minX = double.MaxValue, minY = double.MaxValue;
                    double maxX = double.MinValue, maxY = double.MinValue;

                    IList<IList<BoundarySegment>> boundaries =
                        room.GetBoundarySegments(new SpatialElementBoundaryOptions());
                    if (boundaries != null)
                    {
                        foreach (IList<BoundarySegment> boundary in boundaries)
                        {
                            var loop = new List<double[]>();
                            foreach (BoundarySegment segment in boundary)
                            {
                                IList<XYZ> points = segment.GetCurve().Tessellate();
                                // 每段的最後一點就是下一段的第一點，所以略過
                                for (int i = 0; i < points.Count - 1; i++)
                                {
                                    loop.Add(new[] { points[i].X, points[i].Y });
                                    minX = Math.Min(minX, points[i].X);
                                    minY = Math.Min(minY, points[i].Y);
                                    maxX = Math.Max(maxX, points[i].X);
                                    maxY = Math.Max(maxY, points[i].Y);
                                }
                            }
                            if (loop.Count >= 3) loops.Add(loop);
                        }
                    }

                    if (loops.Count == 0)
                    {
                        noBoundary++;
                        continue;
                    }

                    string rmId = FillSupport.GetText(room, "rm_id") ?? string.Empty;
                    if (rmId.Trim().Length == 0) noCode++;

                    records.Add(new RoomRecord
                    {
                        Id = room.Id.Value,
                        Number = room.Number,
                        Name = room.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString() ?? string.Empty,
                        Level = level.Name,
                        RmId = rmId.Trim(),
                        RmName = (FillSupport.GetText(room, "rm_name") ?? string.Empty).Trim(),
                        RmStd = (FillSupport.GetText(room, "rm_std") ?? string.Empty).Trim(),
                        FlId = (FillSupport.GetText(room, "fl_id") ?? string.Empty).Trim(),
                        ZMin = level.Elevation - BelowLevelMeters * FillSupport.MetersToFeet,
                        ZMax = NextLevelElevation(level, levels) - BelowLevelMeters * FillSupport.MetersToFeet,
                        MinX = minX,
                        MinY = minY,
                        MaxX = maxX,
                        MaxY = maxY,
                        Loops = loops
                    });
                }

                if (records.Count == 0)
                {
                    TaskDialog.Show(Title, "這個模型沒有已放置的房間。請在建築模型中執行。");
                    return Result.Cancelled;
                }

                var saveDialog = new Microsoft.Win32.SaveFileDialog
                {
                    Filter = "房間資料檔 (*.json)|*.json",
                    FileName = doc.Title + "_房間資料.json",
                    Title = "儲存房間資料"
                };
                if (saveDialog.ShowDialog() != true) return Result.Cancelled;

                RoomFile.Save(new RoomFile
                {
                    SourceModel = doc.Title,
                    ExportedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm"),
                    Rooms = records
                }, saveDialog.FileName);

                int coded = records.Count - noCode;
                FillSupport.ShowResult(Title,
                    $"已匯出 {records.Count} 個房間，其中 {coded} 個有 rm_id",
                    "檔案已儲存：\n" + saveDialog.FileName +
                    "\n\n接著在要填值的模型按「帶入房間」，並選擇這個檔案。",
                    new[]
                    {
                        $"有 rm_id 的房間：{coded}",
                        $"尚未編 rm_id 的房間：{noCode}（這些房間裡的元件不會被填入）",
                        $"未放置而略過的房間：{unplaced}",
                        $"沒有邊界而略過的房間：{noBoundary}"
                    });
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                TaskDialog.Show(Title, "發生錯誤：" + ex.Message);
                return Result.Failed;
            }
        }

        // 同一棟（樓層名稱「-」之前的字相同）上一個樓層的高程；找不到時用預設樓高
        private static double NextLevelElevation(Level level, List<Level> levels)
        {
            string prefix = BuildingPrefix(level.Name);
            double best = double.MaxValue;
            foreach (Level other in levels)
            {
                if (other.Elevation <= level.Elevation + 0.01) continue;
                if (BuildingPrefix(other.Name) != prefix) continue;
                if (other.Elevation < best) best = other.Elevation;
            }
            if (best == double.MaxValue)
                return level.Elevation + DefaultStoreyMeters * FillSupport.MetersToFeet;
            return best;
        }

        private static string BuildingPrefix(string levelName)
        {
            int index = levelName.IndexOf('-');
            return index > 0 ? levelName.Substring(0, index) : string.Empty;
        }
    }
}
