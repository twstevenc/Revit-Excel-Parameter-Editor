using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace RevitParameterEditor
{
    // 「帶入房間」按鈕：依元件位置找出所在房間，填入 rm_id 與 fl_id
    [Transaction(TransactionMode.Manual)]
    public class RoomFillCommand : IExternalCommand
    {
        private const string Title = "帶入房間";
        private const string RoomIdParameter = "rm_id";
        private const string FloorIdParameter = "fl_id";

        private const double DoorProbeMeters = 0.40;     // 門窗：往兩側各探 40 公分
        private const double WallClearMeters = 0.10;     // 牆與線狀元件：離開表面再探 10 公分
        private const double AreaElementMeters = 1.5;    // 平面尺寸大於此值時視為面狀元件，取多個點

        // 元件本身不在任何房間內時（例如裝在陽台外緣的格柵、嵌在牆裡的蓋板），往四周找最近的房間
        private static readonly double[] NearbyMeters = { 0.3, 0.6 };

        // 這些品類不做鄰近判定：柱、梁、牆本來就可能不屬於任何房間
        private static readonly long[] NoNearbyCategories =
        {
            (long)BuiltInCategory.OST_StructuralColumns,
            (long)BuiltInCategory.OST_Columns,
            (long)BuiltInCategory.OST_StructuralFraming,
            (long)BuiltInCategory.OST_StructuralFoundation,
            (long)BuiltInCategory.OST_Walls,
            (long)BuiltInCategory.OST_CurtainWallPanels,
            (long)BuiltInCategory.OST_CurtainWallMullions
        };

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
                var openDialog = new Microsoft.Win32.OpenFileDialog
                {
                    Filter = "房間資料檔 (*.json)|*.json",
                    Title = "選擇從建築模型匯出的房間資料檔"
                };
                if (openDialog.ShowDialog() != true) return Result.Cancelled;

                RoomFile roomFile = RoomFile.Load(openDialog.FileName);

                bool? viewOnly = FillSupport.AskScope(Title,
                    $"房間資料來自「{roomFile.SourceModel}」，共 {roomFile.Rooms.Count} 個房間。要處理哪些元件？");
                if (viewOnly == null) return Result.Cancelled;

                bool? overwrite = FillSupport.AskOverwrite(Title);
                if (overwrite == null) return Result.Cancelled;

                List<Element> targets = FillSupport.CollectModelElements(doc, viewOnly.Value)
                    .Where(e => e.LookupParameter(RoomIdParameter) != null)
                    .ToList();

                if (targets.Count == 0)
                {
                    TaskDialog.Show(Title, "範圍內沒有帶 rm_id 欄位的元件。請先用「建立參數」建立欄位。");
                    return Result.Cancelled;
                }

                int written = 0, unchanged = 0, kept = 0, failed = 0;
                int multiRoom = 0, uncodedRoom = 0, noRoom = 0, byNearby = 0;
                var noRoomByCategory = new SortedDictionary<string, int>();

                using (var transaction = new Transaction(doc, "帶入房間編碼"))
                {
                    transaction.Start();

                    foreach (Element element in targets)
                    {
                        var roomIds = new SortedSet<string>(StringComparer.Ordinal);
                        string floorId = null;
                        bool inUncodedRoom = false;

                        foreach (XYZ probe in GetProbes(element))
                        {
                            foreach (RoomRecord room in roomFile.Find(probe.X, probe.Y, probe.Z))
                            {
                                if (string.IsNullOrEmpty(room.RmId))
                                {
                                    inUncodedRoom = true;
                                    continue;
                                }
                                roomIds.Add(room.RmId);
                                if (floorId == null && !string.IsNullOrEmpty(room.FlId)) floorId = room.FlId;
                            }
                        }

                        // 本身不在房間內：往四周找最近的一個房間
                        if (roomIds.Count == 0 && !inUncodedRoom)
                        {
                            RoomRecord nearby = FindNearbyRoom(element, roomFile);
                            if (nearby != null)
                            {
                                roomIds.Add(nearby.RmId);
                                if (!string.IsNullOrEmpty(nearby.FlId)) floorId = nearby.FlId;
                                byNearby++;
                            }
                        }

                        if (roomIds.Count == 0)
                        {
                            if (inUncodedRoom)
                            {
                                uncodedRoom++;
                            }
                            else
                            {
                                noRoom++;
                                string categoryName = element.Category.Name;
                                noRoomByCategory[categoryName] =
                                    noRoomByCategory.TryGetValue(categoryName, out int n) ? n + 1 : 1;
                            }
                            continue;
                        }

                        if (roomIds.Count > 1) multiRoom++;

                        // 跨多個房間時用半形分號串接
                        WriteResult result = FillSupport.SetText(element, RoomIdParameter,
                            string.Join(";", roomIds), overwrite.Value);
                        if (floorId != null)
                        {
                            FillSupport.SetText(element, FloorIdParameter, floorId, overwrite.Value);
                        }

                        switch (result)
                        {
                            case WriteResult.Written: written++; break;
                            case WriteResult.Unchanged: unchanged++; break;
                            case WriteResult.KeptExisting: kept++; break;
                            default: failed++; break;
                        }
                    }

                    transaction.Commit();
                }

                var details = new List<string>
                {
                    $"處理的元件：{targets.Count}",
                    $"已寫入 rm_id：{written}",
                    $"原本的值就相同：{unchanged}",
                    $"已有值而未覆蓋：{kept}",
                    $"寫入失敗或唯讀：{failed}",
                    $"其中跨多個房間的元件：{multiRoom}",
                    $"其中以鄰近位置判定的元件：{byNearby}",
                    $"所在房間尚未編 rm_id：{uncodedRoom}",
                    $"找不到所在房間：{noRoom}"
                };
                if (noRoomByCategory.Count > 0)
                {
                    details.Add(string.Empty);
                    details.Add("找不到房間的元件（依品類）：");
                    details.AddRange(noRoomByCategory.Select(p => $"  {p.Key}：{p.Value}"));
                }

                string warning = string.Empty;
                if (written + unchanged + kept + uncodedRoom == 0)
                {
                    warning = "\n\n沒有任何元件落在房間內。這個模型的座標原點可能和建築模型不同，" +
                              "或是選錯了房間資料檔。";
                }

                FillSupport.ShowResult(Title,
                    $"已填入 {written} 個元件；找不到房間 {noRoom} 個",
                    "按一次 Ctrl+Z 可以全部復原。展開可看明細。" + warning, details);
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                TaskDialog.Show(Title, "發生錯誤：" + ex.Message);
                return Result.Failed;
            }
        }

        // 以元件中心為圓心，在 30 公分、60 公分處各取八個方向，回傳命中次數最多的已編碼房間
        private static RoomRecord FindNearbyRoom(Element element, RoomFile roomFile)
        {
            if (NoNearbyCategories.Contains(element.Category.Id.Value)) return null;

            BoundingBoxXYZ box = element.get_BoundingBox(null);
            if (box == null) return null;

            double x = (box.Min.X + box.Max.X) / 2;
            double y = (box.Min.Y + box.Max.Y) / 2;
            double z = (box.Min.Z + box.Max.Z) / 2;

            foreach (double meters in NearbyMeters)
            {
                double distance = meters * FillSupport.MetersToFeet;
                var hits = new Dictionary<string, int>();
                var records = new Dictionary<string, RoomRecord>();

                for (int i = 0; i < 8; i++)
                {
                    double angle = Math.PI / 4 * i;
                    foreach (RoomRecord room in roomFile.Find(
                                 x + Math.Cos(angle) * distance, y + Math.Sin(angle) * distance, z))
                    {
                        if (string.IsNullOrEmpty(room.RmId)) continue;
                        hits[room.RmId] = hits.TryGetValue(room.RmId, out int count) ? count + 1 : 1;
                        records[room.RmId] = room;
                    }
                }

                if (hits.Count > 0)
                {
                    string best = hits.OrderByDescending(p => p.Value)
                        .ThenBy(p => p.Key, StringComparer.Ordinal)
                        .First().Key;
                    return records[best];
                }
            }
            return null;
        }

        // 取得用來判斷所在房間的探測點
        private static List<XYZ> GetProbes(Element element)
        {
            var probes = new List<XYZ>();

            BoundingBoxXYZ box = element.get_BoundingBox(null);
            if (box == null) return probes;

            double z = (box.Min.Z + box.Max.Z) / 2;
            var center = new XYZ((box.Min.X + box.Max.X) / 2, (box.Min.Y + box.Max.Y) / 2, z);

            long categoryId = element.Category.Id.Value;
            bool isOpening = categoryId == (long)BuiltInCategory.OST_Doors ||
                             categoryId == (long)BuiltInCategory.OST_Windows;

            // 門窗：沿著面向，往內外兩側各探一點，所以會得到兩個房間
            var instance = element as FamilyInstance;
            if (isOpening && instance != null)
            {
                XYZ origin = (instance.Location as LocationPoint)?.Point ?? center;
                XYZ facing = null;
                try
                {
                    facing = instance.FacingOrientation;
                }
                catch (Exception)
                {
                }

                if (facing != null && facing.GetLength() > 1e-6)
                {
                    XYZ direction = new XYZ(facing.X, facing.Y, 0);
                    if (direction.GetLength() > 1e-6)
                    {
                        direction = direction.Normalize();
                        double distance = DoorProbeMeters * FillSupport.MetersToFeet;
                        probes.Add(new XYZ(origin.X + direction.X * distance, origin.Y + direction.Y * distance, z));
                        probes.Add(new XYZ(origin.X - direction.X * distance, origin.Y - direction.Y * distance, z));
                        return probes;
                    }
                }
                probes.Add(new XYZ(origin.X, origin.Y, z));
                return probes;
            }

            // 牆與其他線狀元件：沿線取三處，各往兩側探
            var locationCurve = element.Location as LocationCurve;
            if (locationCurve != null && locationCurve.Curve != null && locationCurve.Curve.IsBound)
            {
                Curve curve = locationCurve.Curve;
                var wall = element as Wall;
                double halfWidth = wall != null ? wall.Width / 2 : 0;
                double distance = halfWidth + WallClearMeters * FillSupport.MetersToFeet;

                foreach (double t in new[] { 0.1, 0.5, 0.9 })
                {
                    XYZ point = curve.Evaluate(t, true);
                    XYZ tangent = curve.ComputeDerivatives(t, true).BasisX;
                    var normal = new XYZ(-tangent.Y, tangent.X, 0);

                    if (wall == null) probes.Add(new XYZ(point.X, point.Y, z));

                    if (normal.GetLength() > 1e-6)
                    {
                        normal = normal.Normalize();
                        probes.Add(new XYZ(point.X + normal.X * distance, point.Y + normal.Y * distance, z));
                        probes.Add(new XYZ(point.X - normal.X * distance, point.Y - normal.Y * distance, z));
                    }
                }
                return probes;
            }

            // 面狀元件（樓板、天花板等）：在範圍內取 3×3 個點
            double sizeX = box.Max.X - box.Min.X;
            double sizeY = box.Max.Y - box.Min.Y;
            double threshold = AreaElementMeters * FillSupport.MetersToFeet;
            if (sizeX > threshold || sizeY > threshold)
            {
                foreach (double fx in new[] { 0.15, 0.5, 0.85 })
                {
                    foreach (double fy in new[] { 0.15, 0.5, 0.85 })
                    {
                        probes.Add(new XYZ(box.Min.X + sizeX * fx, box.Min.Y + sizeY * fy, z));
                    }
                }
                return probes;
            }

            // 其他：用放置點，沒有就用中心點
            XYZ located = (element.Location as LocationPoint)?.Point;
            probes.Add(located != null ? new XYZ(located.X, located.Y, z) : center);
            if (located != null) probes.Add(center);
            return probes;
        }
    }
}
