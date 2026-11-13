using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace RevitParameterEditor
{
    // 建立結果，用來產生報告
    public class CreationReport
    {
        public List<string> Created = new List<string>();
        public List<string> Updated = new List<string>();
        public List<string> Unchanged = new List<string>();
        public List<string> Skipped = new List<string>();
        public List<string> CategoryNotes = new List<string>();
        public List<string> Defaults = new List<string>();
        public int CategoryCount;
    }

    // 依設定檔把共用參數建立並綁定到指定品類
    public class SharedParameterCreator
    {
        private const string DefinitionGroupName = "RevitParameterEditor";

        private readonly Document _doc;

        public SharedParameterCreator(Document doc)
        {
            _doc = doc;
        }

        // 必須在交易（Transaction）中呼叫
        public CreationReport Run(ParameterConfig config)
        {
            var report = new CreationReport();
            var app = _doc.Application;

            List<Category> categories = ResolveCategories(config, report);
            report.CategoryCount = categories.Count;
            if (categories.Count == 0)
                throw new Exception("設定檔中沒有任何可用的品類。");

            // 專案中已綁定的參數名稱，用來偵測同名衝突
            var boundNames = new HashSet<string>();
            var iterator = _doc.ParameterBindings.ForwardIterator();
            iterator.Reset();
            while (iterator.MoveNext())
            {
                if (iterator.Key != null) boundNames.Add(iterator.Key.Name);
            }

            // Revit 一定要透過「共用參數檔」建立共用參數。
            // 這裡用一個暫時的檔案，做完再把使用者原本的設定還原。
            string originalFile = app.SharedParametersFilename;
            string tempFile = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "RevitParameterEditor_SharedParameters.txt");
            System.IO.File.WriteAllText(tempFile, string.Empty);

            try
            {
                app.SharedParametersFilename = tempFile;
                DefinitionFile definitionFile = app.OpenSharedParameterFile();
                if (definitionFile == null)
                    throw new Exception("無法開啟暫存的共用參數檔。");

                DefinitionGroup group = definitionFile.Groups.Create(DefinitionGroupName);

                foreach (ParameterSpec spec in config.Parameters)
                {
                    try
                    {
                        ProcessParameter(spec, group, categories, boundNames, report);
                    }
                    catch (Exception ex)
                    {
                        report.Skipped.Add($"{spec.Name}：{ex.Message}");
                    }
                }
            }
            finally
            {
                try
                {
                    app.SharedParametersFilename = originalFile ?? string.Empty;
                }
                catch (Exception)
                {
                    // 原本的共用參數檔已不存在時無法還原，不影響結果
                }
            }

            try
            {
                FillDefaults(config, categories, report);
            }
            catch (Exception ex)
            {
                report.Skipped.Add("填入預設值時發生錯誤：" + ex.Message);
            }

            return report;
        }

        // 設定檔有填「預設值」的參數：把值填進還沒有值的元件，已有值的不會被覆蓋
        private void FillDefaults(ParameterConfig config, List<Category> categories, CreationReport report)
        {
            List<ParameterSpec> specs = config.Parameters
                .Where(p => !string.IsNullOrEmpty(p.DefaultValue))
                .ToList();
            if (specs.Count == 0) return;

            // 讓剛綁定的參數出現在元件上
            _doc.Regenerate();

            List<ElementId> categoryIds = categories.Select(c => c.Id).ToList();
            var categoryFilter = new ElementMulticategoryFilter(categoryIds);

            foreach (ParameterSpec spec in specs)
            {
                Guid guid = ResolveGuid(spec);

                FilteredElementCollector collector = new FilteredElementCollector(_doc).WherePasses(categoryFilter);
                IList<Element> targets = spec.IsInstance
                    ? collector.WhereElementIsNotElementType().ToElements()
                    : collector.WhereElementIsElementType().ToElements();

                int filled = 0;
                int failed = 0;

                foreach (Element element in targets)
                {
                    Parameter parameter = element.get_Parameter(guid);
                    if (parameter == null || parameter.IsReadOnly) continue;

                    try
                    {
                        if (parameter.StorageType == StorageType.String)
                        {
                            if (!string.IsNullOrEmpty(parameter.AsString())) continue;
                            if (parameter.Set(spec.DefaultValue)) filled++;
                        }
                        else
                        {
                            if (parameter.HasValue) continue;
                            if (parameter.SetValueString(spec.DefaultValue)) filled++;
                        }
                    }
                    catch (Exception)
                    {
                        failed++;
                    }
                }

                report.Defaults.Add($"{spec.Name} = {spec.DefaultValue}：填入 {filled} 個元件" +
                                    (failed > 0 ? $"，{failed} 個無法寫入" : string.Empty));
            }
        }

        private void ProcessParameter(ParameterSpec spec, DefinitionGroup group, List<Category> categories,
            HashSet<string> boundNames, CreationReport report)
        {
            Guid guid = ResolveGuid(spec);
            ForgeTypeId groupTypeId = ResolveGroup(spec.Group);
            string kindText = spec.IsInstance ? "實例" : "類型";

            SharedParameterElement existing = SharedParameterElement.Lookup(_doc, guid);

            // 情況一：這個參數已經在專案裡，只補上缺少的品類
            if (existing != null)
            {
                InternalDefinition definition = existing.GetDefinition();
                var binding = _doc.ParameterBindings.get_Item(definition) as ElementBinding;

                if (binding == null)
                {
                    ElementBinding newBinding = CreateBinding(spec.IsInstance, categories);
                    _doc.ParameterBindings.Insert(definition, newBinding, groupTypeId);
                    report.Updated.Add($"{spec.Name}（{kindText}）：已重新綁定到 {categories.Count} 個品類");
                    return;
                }

                bool existingIsInstance = binding is InstanceBinding;
                if (existingIsInstance != spec.IsInstance)
                {
                    report.Skipped.Add($"{spec.Name}：專案中已是{(existingIsInstance ? "實例" : "類型")}參數，" +
                                       $"與設定檔的「{kindText}」不同，未變更");
                    return;
                }

                int added = 0;
                foreach (Category category in categories)
                {
                    if (!binding.Categories.Contains(category))
                    {
                        binding.Categories.Insert(category);
                        added++;
                    }
                }

                if (added == 0)
                {
                    report.Unchanged.Add(spec.Name);
                    return;
                }

                _doc.ParameterBindings.ReInsert(definition, binding, groupTypeId);
                report.Updated.Add($"{spec.Name}（{kindText}）：新增 {added} 個品類");
                return;
            }

            // 情況二：專案裡有同名、但不是這個外掛建立的參數，為避免混淆不處理
            if (boundNames.Contains(spec.Name))
            {
                report.Skipped.Add($"{spec.Name}：專案中已有同名參數（來源不同），未變更");
                return;
            }

            // 情況三：全新建立
            var options = new ExternalDefinitionCreationOptions(spec.Name, ResolveDataType(spec.DataType))
            {
                GUID = guid,
                UserModifiable = true,
                Visible = true
            };
            if (!string.IsNullOrEmpty(spec.Description)) options.Description = spec.Description;

            Definition newDefinition = group.Definitions.Create(options);
            ElementBinding createdBinding = CreateBinding(spec.IsInstance, categories);

            if (!_doc.ParameterBindings.Insert(newDefinition, createdBinding, groupTypeId))
            {
                report.Skipped.Add($"{spec.Name}：Revit 拒絕建立這個參數");
                return;
            }

            boundNames.Add(spec.Name);

            // 實例參數允許在模型群組的各個實例中填不同的值（例如 eq_id）
            if (spec.IsInstance)
            {
                try
                {
                    SharedParameterElement.Lookup(_doc, guid)?.GetDefinition()?.SetAllowVaryBetweenGroups(_doc, true);
                }
                catch (Exception)
                {
                    // 有些資料型別不支援此設定，略過
                }
            }

            report.Created.Add($"{spec.Name}（{kindText}）");
        }

        private ElementBinding CreateBinding(bool isInstance, List<Category> categories)
        {
            var app = _doc.Application;
            CategorySet set = app.Create.NewCategorySet();
            foreach (Category category in categories) set.Insert(category);

            if (isInstance) return app.Create.NewInstanceBinding(set);
            return app.Create.NewTypeBinding(set);
        }

        // 把設定檔的品類代碼轉成專案中的品類
        private List<Category> ResolveCategories(ParameterConfig config, CreationReport report)
        {
            var result = new List<Category>();
            foreach (CategorySpec spec in config.Categories)
            {
                string label = string.IsNullOrEmpty(spec.Label) ? spec.Code : $"{spec.Label}（{spec.Code}）";

                if (!Enum.TryParse(spec.Code, out BuiltInCategory builtIn) ||
                    !Enum.IsDefined(typeof(BuiltInCategory), builtIn))
                {
                    report.CategoryNotes.Add($"{label}：這個版本的 Revit 沒有此品類代碼，已略過");
                    continue;
                }

                Category category = null;
                try
                {
                    category = Category.GetCategory(_doc, builtIn);
                }
                catch (Exception)
                {
                }

                if (category == null)
                {
                    report.CategoryNotes.Add($"{label}：專案中找不到此品類，已略過");
                    continue;
                }

                if (!category.AllowsBoundParameters)
                {
                    report.CategoryNotes.Add($"{label}：此品類不能綁定參數，已略過");
                    continue;
                }

                result.Add(category);
            }
            return result;
        }

        // GUID 空白時由參數名稱計算，確保同一個名稱在每個專案、每台電腦都得到同一個 GUID
        public static Guid ResolveGuid(ParameterSpec spec)
        {
            if (!string.IsNullOrWhiteSpace(spec.GuidText))
            {
                if (Guid.TryParse(spec.GuidText, out Guid parsed)) return parsed;
                throw new Exception($"GUID「{spec.GuidText}」格式不正確");
            }

            using (var md5 = System.Security.Cryptography.MD5.Create())
            {
                byte[] hash = md5.ComputeHash(System.Text.Encoding.UTF8.GetBytes("RevitParameterEditor:" + spec.Name));
                return new Guid(hash);
            }
        }

        private static ForgeTypeId ResolveDataType(string text)
        {
            switch ((text ?? string.Empty).Trim())
            {
                case "":
                case "文字":
                case "Text":
                    return SpecTypeId.String.Text;
                case "整數":
                case "Integer":
                    return SpecTypeId.Int.Integer;
                case "數值":
                case "Number":
                    return SpecTypeId.Number;
                case "是否":
                case "YesNo":
                    return SpecTypeId.Boolean.YesNo;
                default:
                    throw new Exception($"不支援的資料型別「{text}」（可用：文字、整數、數值、是否）");
            }
        }

        private static ForgeTypeId ResolveGroup(string text)
        {
            switch ((text ?? string.Empty).Trim())
            {
                case "":
                case "識別資料":
                    return GroupTypeId.IdentityData;
                case "資料":
                    return GroupTypeId.Data;
                case "文字":
                    return GroupTypeId.Text;
                case "一般":
                    return GroupTypeId.General;
                case "機械":
                    return GroupTypeId.Mechanical;
                case "電氣":
                    return GroupTypeId.Electrical;
                default:
                    throw new Exception($"不支援的群組「{text}」（可用：識別資料、資料、文字、一般、機械、電氣）");
            }
        }
    }
}
