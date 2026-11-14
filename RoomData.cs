using System;
using System.Collections.Generic;
using System.Text.Json;

namespace RevitParameterEditor
{
    // 一個房間的範圍與編碼（長度單位為英尺，與 Revit 內部座標相同）
    public class RoomRecord
    {
        public long Id { get; set; }
        public string Number { get; set; }
        public string Name { get; set; }
        public string Level { get; set; }
        public string RmId { get; set; }
        public string RmName { get; set; }
        public string RmStd { get; set; }
        public string FlId { get; set; }
        public double ZMin { get; set; }
        public double ZMax { get; set; }
        public double MinX { get; set; }
        public double MinY { get; set; }
        public double MaxX { get; set; }
        public double MaxY { get; set; }

        // 邊界迴圈；每個迴圈是一串 [x, y]
        public List<List<double[]>> Loops { get; set; }

        // 判斷點是否在房間內（奇偶規則，內部的孔洞會被排除）
        public bool Contains(double x, double y, double z)
        {
            if (z < ZMin || z >= ZMax) return false;
            if (x < MinX || x > MaxX || y < MinY || y > MaxY) return false;
            if (Loops == null) return false;

            bool inside = false;
            foreach (List<double[]> loop in Loops)
            {
                int count = loop.Count;
                for (int i = 0, j = count - 1; i < count; j = i++)
                {
                    double xi = loop[i][0], yi = loop[i][1];
                    double xj = loop[j][0], yj = loop[j][1];
                    if ((yi > y) != (yj > y) && x < (xj - xi) * (y - yi) / (yj - yi) + xi)
                    {
                        inside = !inside;
                    }
                }
            }
            return inside;
        }
    }

    // 從建築模型匯出的房間資料檔
    public class RoomFile
    {
        public string SourceModel { get; set; }
        public string ExportedAt { get; set; }
        public List<RoomRecord> Rooms { get; set; }

        public static void Save(RoomFile file, string path)
        {
            string json = JsonSerializer.Serialize(file);
            System.IO.File.WriteAllText(path, json);
        }

        public static RoomFile Load(string path)
        {
            string json = System.IO.File.ReadAllText(path);
            RoomFile file = JsonSerializer.Deserialize<RoomFile>(json);
            if (file == null || file.Rooms == null) throw new Exception("房間資料檔的內容無法讀取。");
            return file;
        }

        // 找出包含這個點的房間
        public List<RoomRecord> Find(double x, double y, double z)
        {
            var result = new List<RoomRecord>();
            foreach (RoomRecord room in Rooms)
            {
                if (room.Contains(x, y, z)) result.Add(room);
            }
            return result;
        }
    }
}
