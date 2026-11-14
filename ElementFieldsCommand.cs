using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace RevitParameterEditor
{
    // 「元件欄位」按鈕：針對選取的元件，手動新增欄位與內容，或刪除欄位
    [Transaction(TransactionMode.Manual)]
    public class ElementFieldsCommand : IExternalCommand
    {
        private const string Title = "元件欄位";

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIDocument uiDocument = commandData.Application.ActiveUIDocument;
            Document doc = uiDocument?.Document;
            if (doc == null || doc.IsFamilyDocument)
            {
                TaskDialog.Show(Title, "請先開啟一個專案。");
                return Result.Cancelled;
            }

            try
            {
                List<Element> selected = uiDocument.Selection.GetElementIds()
                    .Select(id => doc.GetElement(id))
                    .Where(e => e != null && e.Category != null)
                    .ToList();

                if (selected.Count == 0)
                {
                    TaskDialog.Show(Title, "請先在模型中選取一個或多個元件，再按這個按鈕。");
                    return Result.Cancelled;
                }

                if (selected.Select(e => e.Category.Id.Value).Distinct().Count() > 1)
                {
                    TaskDialog.Show(Title, "選取的元件屬於不同品類。請一次只選同一個品類的元件。");
                    return Result.Cancelled;
                }

                Category category = selected[0].Category;
                if (!category.AllowsBoundParameters)
                {
                    TaskDialog.Show(Title, "「" + category.Name + "」這個品類不能加入自訂欄位。");
                    return Result.Cancelled;
                }

                string status = string.Empty;
                bool changed = false;

                // 每做完一次新增或刪除，就重新開啟視窗顯示最新的狀態，直到使用者按關閉
                while (true)
                {
                    List<CheckItem> fields = ListFields(doc, category, selected[0]);
                    string header = selected.Count == 1
                        ? "已選取 1 個元件：" + category.Name + "　" + selected[0].Name
                        : "已選取 " + selected.Count + " 個元件：" + category.Name;

                    var view = new ElementFieldsView(header, category.Name, fields, status);
                    if (view.ShowDialog() != true || view.Choice == FieldChoice.None) break;

                    if (view.Choice == FieldChoice.Add)
                    {
                        status = AddField(doc, category, selected, view.FieldName, view.FieldValue,
                            view.GroupName, view.IsInstance);
                        changed = true;
                    }
                    else if (view.Choice == FieldChoice.Delete)
                    {
                        string result = DeleteFields(doc, category, view.SelectedFields, view.DeleteEverywhere);
                        if (result != null)
                        {
                            status = result;
                            changed = true;
                        }
                        else
                        {
                            status = "已取消刪除。";
                        }
                    }
                }

                return changed ? Result.Succeeded : Result.Cancelled;
            }
            catch (Exception ex)
            {
                TaskDialog.Show(Title, "發生錯誤：" + ex.Message);
                return Result.Failed;
            }
        }

        // 列出綁定到這個品類的專案參數（Revit 內建的欄位不能刪除，所以不列）
        private static List<CheckItem> ListFields(Document doc, Category category, Element sample)
        {
            var items = new List<CheckItem>();
            Element sampleType = doc.GetElement(sample.GetTypeId());

            DefinitionBindingMapIterator iterator = doc.ParameterBindings.ForwardIterator();
            iterator.Reset();
            while (iterator.MoveNext())
            {
                Definition definition = iterator.Key;
                var binding = iterator.Current as ElementBinding;
                if (definition == null || binding == null) continue;
                if (!binding.Categories.Contains(category)) continue;

                bool isInstance = binding is InstanceBinding;
                Element owner = isInstance ? sample : sampleType;
                string value = owner == null ? string.Empty : FillSupport.GetText(owner, definition.Name) ?? string.Empty;

                items.Add(new CheckItem
                {
                    Title = definition.Name,
                    Subtitle = (isInstance ? "實例" : "類型") + "　" +
                               (value.Length > 0 ? "內容：" + value : "（空白）"),
                    Tag = value,
                    IsChecked = false
                });
            }

            return items.OrderBy(i => i.Title, StringComparer.OrdinalIgnoreCase).ToList();
        }

        // 依名稱找專案參數
        private static Definition FindDefinition(Document doc, string name, out ElementBinding binding)
        {
            binding = null;
            DefinitionBindingMapIterator iterator = doc.ParameterBindings.ForwardIterator();
            iterator.Reset();
            while (iterator.MoveNext())
            {
                Definition definition = iterator.Key;
                if (definition == null || definition.Name != name) continue;
                binding = iterator.Current as ElementBinding;
                if (binding != null) return definition;
            }
            return null;
        }

        private static string AddField(Document doc, Category category, List<Element> selected,
            string name, string value, string groupName, bool isInstance)
        {
            string action;
            int written = 0;

            using (var transaction = new Transaction(doc, "新增或更新欄位：" + name))
            {
                transaction.Start();

                Definition existing = FindDefinition(doc, name, out ElementBinding binding);
                if (existing != null)
                {
                    // 欄位已存在：沿用它原本的實例或類型設定，必要時把這個品類加進去
                    isInstance = binding is InstanceBinding;
                    if (binding.Categories.Contains(category))
                    {
                        action = "已更新「" + name + "」的內容";
                    }
                    else
                    {
                        binding.Categories.Insert(category);
                        doc.ParameterBindings.ReInsert(existing, binding);
                        action = "已把現有的欄位「" + name + "」加到「" + category.Name + "」";
                    }
                }
                else
                {
                    var config = new ParameterConfig();
                    config.Parameters.Add(new ParameterSpec
                    {
                        Name = name,
                        Description = string.Empty,
                        IsInstance = isInstance,
                        DataType = "文字",
                        Group = groupName,
                        GuidText = string.Empty,
                        DefaultValue = string.Empty
                    });
                    config.Categories.Add(new CategorySpec
                    {
                        Code = category.BuiltInCategory.ToString(),
                        Label = category.Name
                    });

                    CreationReport report = new SharedParameterCreator(doc).Run(config);
                    if (report.Created.Count == 0)
                    {
                        transaction.RollBack();
                        string reason = report.Skipped.Concat(report.CategoryNotes).FirstOrDefault() ?? "原因不明";
                        return "無法建立欄位「" + name + "」：" + reason;
                    }
                    action = "已建立欄位「" + name + "」";
                }

                doc.Regenerate();

                if (value.Length > 0)
                {
                    IEnumerable<Element> targets = selected;
                    if (!isInstance)
                    {
                        // 類型欄位：寫到選取元件的類型上，同一個類型只寫一次
                        targets = selected
                            .Select(e => e.GetTypeId())
                            .Where(id => id != null && id != ElementId.InvalidElementId)
                            .GroupBy(id => id.Value)
                            .Select(g => doc.GetElement(g.First()))
                            .Where(e => e != null);
                    }

                    foreach (Element target in targets)
                    {
                        WriteResult result = FillSupport.SetText(target, name, value, true);
                        if (result == WriteResult.Written || result == WriteResult.Unchanged) written++;
                    }
                }

                transaction.Commit();
            }

            if (value.Length == 0) return action + "（未填內容）。";
            return action + "，並填入 " + written + " 個" + (isInstance ? "元件" : "類型") + "。";
        }

        // 回傳 null 表示使用者取消
        private static string DeleteFields(Document doc, Category category, List<string> names, bool everywhere)
        {
            var confirm = new TaskDialog(Title)
            {
                TitleAutoPrefix = false,
                MainInstruction = everywhere
                    ? "確定要從整個專案刪除這 " + names.Count + " 個欄位嗎？"
                    : "確定要從「" + category.Name + "」移除這 " + names.Count + " 個欄位嗎？",
                MainContent = string.Join("、", names) + "\n\n" +
                              (everywhere
                                  ? "所有品類的元件都會失去這些欄位，已經填的內容也會一併消失。"
                                  : "「" + category.Name + "」的所有元件都會失去這些欄位，已經填的內容也會一併消失。" +
                                    "其他品類不受影響。") +
                              "\n\n執行後可以用 Ctrl+Z 復原。",
                CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No,
                DefaultButton = TaskDialogResult.No
            };
            if (confirm.Show() != TaskDialogResult.Yes) return null;

            int removed = 0;
            var failed = new List<string>();

            using (var transaction = new Transaction(doc, "刪除欄位"))
            {
                transaction.Start();

                foreach (string name in names)
                {
                    try
                    {
                        Definition definition = FindDefinition(doc, name, out ElementBinding binding);
                        if (definition == null)
                        {
                            failed.Add(name);
                            continue;
                        }

                        if (everywhere)
                        {
                            // 連同參數本身一起刪除；刪不掉時至少解除綁定
                            bool deleted = false;
                            var internalDefinition = definition as InternalDefinition;
                            if (internalDefinition != null)
                            {
                                try
                                {
                                    doc.Delete(internalDefinition.Id);
                                    deleted = true;
                                }
                                catch (Exception)
                                {
                                }
                            }
                            if (!deleted) doc.ParameterBindings.Remove(definition);
                        }
                        else
                        {
                            binding.Categories.Erase(category);
                            if (binding.Categories.IsEmpty)
                            {
                                doc.ParameterBindings.Remove(definition);
                            }
                            else
                            {
                                doc.ParameterBindings.ReInsert(definition, binding);
                            }
                        }
                        removed++;
                    }
                    catch (Exception)
                    {
                        failed.Add(name);
                    }
                }

                transaction.Commit();
            }

            return "已刪除 " + removed + " 個欄位。" +
                   (failed.Count > 0 ? "無法刪除：" + string.Join("、", failed) : string.Empty);
        }
    }
}
