using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace RevitParameterEditor
{
    // 下拉選單中的一個設定檔
    public class ConfigEntry
    {
        public string Name { get; set; }
        public string Path { get; set; }
    }

    // 管理可用的設定檔清單，並記住上次使用的那一個
    public static class ConfigStore
    {
        // 外掛內附的設定檔：dll 旁邊的 Configs 資料夾
        public static string BuiltInFolder
        {
            get
            {
                string dllFolder = System.IO.Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)
                                   ?? string.Empty;
                return System.IO.Path.Combine(dllFolder, "Configs");
            }
        }

        // 使用者資料：%AppData%\RevitParameterEditor
        private static string UserFolder
        {
            get
            {
                return System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "RevitParameterEditor");
            }
        }

        private static string ExtraListFile
        {
            get { return System.IO.Path.Combine(UserFolder, "extra-configs.txt"); }
        }

        private static string LastUsedFile
        {
            get { return System.IO.Path.Combine(UserFolder, "last-config.txt"); }
        }

        // 內附的設定檔，加上使用者用「瀏覽」加入過的檔案
        public static List<ConfigEntry> GetEntries()
        {
            var entries = new List<ConfigEntry>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                if (System.IO.Directory.Exists(BuiltInFolder))
                {
                    foreach (string file in System.IO.Directory.GetFiles(BuiltInFolder, "*.xlsx").OrderBy(f => f))
                    {
                        string fileName = System.IO.Path.GetFileName(file);
                        if (fileName.StartsWith("~$")) continue; // Excel 的暫存鎖定檔
                        if (seen.Add(file))
                        {
                            entries.Add(new ConfigEntry
                            {
                                Name = System.IO.Path.GetFileNameWithoutExtension(file),
                                Path = file
                            });
                        }
                    }
                }
            }
            catch (Exception)
            {
            }

            foreach (string file in ReadLines(ExtraListFile))
            {
                if (!System.IO.File.Exists(file)) continue;
                if (seen.Add(file))
                {
                    entries.Add(new ConfigEntry
                    {
                        Name = System.IO.Path.GetFileNameWithoutExtension(file) + "（自訂）",
                        Path = file
                    });
                }
            }

            return entries;
        }

        public static void AddExtra(string path)
        {
            List<string> lines = ReadLines(ExtraListFile);
            if (lines.Any(l => string.Equals(l, path, StringComparison.OrdinalIgnoreCase))) return;
            lines.Add(path);
            WriteLines(ExtraListFile, lines);
        }

        public static string LoadLastUsed()
        {
            return ReadLines(LastUsedFile).FirstOrDefault();
        }

        public static void SaveLastUsed(string path)
        {
            WriteLines(LastUsedFile, new List<string> { path });
        }

        private static List<string> ReadLines(string file)
        {
            try
            {
                if (!System.IO.File.Exists(file)) return new List<string>();
                return System.IO.File.ReadAllLines(file)
                    .Select(l => l.Trim())
                    .Where(l => l.Length > 0)
                    .ToList();
            }
            catch (Exception)
            {
                return new List<string>();
            }
        }

        private static void WriteLines(string file, List<string> lines)
        {
            try
            {
                System.IO.Directory.CreateDirectory(UserFolder);
                System.IO.File.WriteAllLines(file, lines);
            }
            catch (Exception)
            {
                // 記不住設定不影響主要功能
            }
        }
    }
}
