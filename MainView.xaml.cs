using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Microsoft.Win32;
using OfficeOpenXml; // Biblioteca EPPlus
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;

namespace RevitParameterEditor
{
    public partial class MainView : Window
    {
        private readonly UIApplication _uiapp;
        private readonly Document _doc;

        public MainView(UIApplication uiapp)
        {
            InitializeComponent();
            _uiapp = uiapp;
            _doc = uiapp.ActiveUIDocument.Document;

            // EPPlus 4.5.3.3 (LGPL) 不需要設定 LicenseContext；
            // .NET 8 需註冊 code page 編碼，否則 EPPlus 可能找不到 1252 編碼
            System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);

            LoadSchedules();
        }

        // Carrega todas as tabelas do projeto no ComboBox
        private void LoadSchedules()
        {
            var schedules = new FilteredElementCollector(_doc)
                .OfClass(typeof(ViewSchedule))
                .Cast<ViewSchedule>()
                .Where(s => !s.IsTemplate && s.Definition.GetFieldCount() > 0)
                .OrderBy(s => s.Name)
                .ToList();

            ScheduleComboBox.ItemsSource = schedules;
            ScheduleComboBox.DisplayMemberPath = "Name";
        }

        // Lógica do botão de EXPORTAR
        private void ExportButton_Click(object sender, RoutedEventArgs e)
        {
            if (ScheduleComboBox.SelectedItem == null)
            {
                StatusTextBlock.Text = "Erro: Nenhuma tabela selecionada.";
                TaskDialog.Show("Erro", "Por favor, selecione uma tabela da lista.");
                return;
            }

            var schedule = ScheduleComboBox.SelectedItem as ViewSchedule;

            var saveFileDialog = new SaveFileDialog
            {
                Filter = "Arquivo Excel (*.xlsx)|*.xlsx",
                FileName = $"{schedule.Name}.xlsx",
                Title = "Salvar Tabela"
            };

            if (saveFileDialog.ShowDialog() == true)
            {
                StatusTextBlock.Text = "Exportando dados...";
                try
                {
                    ExportSchedule(schedule, saveFileDialog.FileName);
                    StatusTextBlock.Text = "Exportação concluída com sucesso!";
                    TaskDialog.Show("Sucesso", $"A tabela '{schedule.Name}' foi exportada.");
                }
                catch (Exception ex)
                {
                    StatusTextBlock.Text = "Erro na exportação.";
                    TaskDialog.Show("Erro de Exportação", ex.Message);
                }
            }
        }

        // Lógica do botão de IMPORTAR
        private void ImportButton_Click(object sender, RoutedEventArgs e)
        {
            var openFileDialog = new OpenFileDialog
            {
                Filter = "Arquivo Excel (*.xlsx)|*.xlsx",
                Title = "Selecionar Arquivo para Importação"
            };

            if (openFileDialog.ShowDialog() == true)
            {
                StatusTextBlock.Text = "Importando e atualizando...";
                try
                {
                    int updatedCount = ImportParameters(openFileDialog.FileName);
                    StatusTextBlock.Text = $"Importação concluída. {updatedCount} elementos atualizados.";
                    TaskDialog.Show("Sucesso", $"{updatedCount} elementos foram atualizados no projeto Revit.");
                }
                catch (Exception ex)
                {
                    StatusTextBlock.Text = "Erro na importação.";
                    TaskDialog.Show("Erro de Importação", $"Ocorreu um erro: {ex.Message}");
                }
            }
        }

        // 找參數：先找實例參數，找不到再找類型參數（僅供匯出顯示）
        private Parameter FindParameter(Element elem, string name)
        {
            var p = elem.LookupParameter(name);
            if (p != null) return p;

            var typeId = elem.GetTypeId();
            if (typeId == null || typeId == ElementId.InvalidElementId) return null;
            return _doc.GetElement(typeId)?.LookupParameter(name);
        }

        private static string GetValueText(Parameter p)
        {
            if (p == null) return null;
            return p.AsValueString() ?? p.AsString();
        }

        // 匯出（Revit API 只能在主執行緒呼叫，所以不使用 Task.Run）
        private void ExportSchedule(ViewSchedule schedule, string filePath)
        {
            using (var package = new ExcelPackage())
            {
                var worksheet = package.Workbook.Worksheets.Add("Dados da Tabela");
                var collector = new FilteredElementCollector(_doc, schedule.Id);

                // 表頭：A 欄是 ElementID，其後是明細表實際包含的欄位
                var definition = schedule.Definition;
                var fieldNames = new List<string>();
                for (int i = 0; i < definition.GetFieldCount(); i++)
                {
                    fieldNames.Add(definition.GetField(i).GetName());
                }

                worksheet.Cells[1, 1].Value = "ElementID";
                for (int i = 0; i < fieldNames.Count; i++)
                {
                    worksheet.Cells[1, i + 2].Value = fieldNames[i];
                }

                // 資料
                int row = 2;
                foreach (Element elem in collector)
                {
                    worksheet.Cells[row, 1].Value = elem.Id.ToString();
                    for (int col = 0; col < fieldNames.Count; col++)
                    {
                        worksheet.Cells[row, col + 2].Value = GetValueText(FindParameter(elem, fieldNames[col]));
                    }
                    row++;
                }

                worksheet.Cells.AutoFitColumns();
                package.SaveAs(new FileInfo(filePath));
            }
        }

        // Método para importar
        private int ImportParameters(string filePath)
        {
            int updatedCount = 0;
            var fileInfo = new FileInfo(filePath);

            using (var package = new ExcelPackage(fileInfo))
            {
                var worksheet = package.Workbook.Worksheets.FirstOrDefault();
                if (worksheet == null) throw new Exception("Nenhuma planilha encontrada no arquivo.");

                var headers = new List<string>();
                for (int col = 1; col <= worksheet.Dimension.End.Column; col++)
                {
                    headers.Add(worksheet.Cells[1, col].Text);
                }

                int elementIdCol = headers.IndexOf("ElementID") + 1;
                if (elementIdCol == 0) throw new Exception("A coluna 'ElementID' não foi encontrada. Ela é essencial para a importação.");

                using (var trans = new Transaction(_doc, "Atualizar Parâmetros via Excel"))
                {
                    trans.Start();

                    for (int row = 2; row <= worksheet.Dimension.End.Row; row++)
                    {
                        var idText = worksheet.Cells[row, elementIdCol].Text;
                        if (!long.TryParse(idText, out long idAsLong)) continue;
                        
                        var elementId = new ElementId(idAsLong);
                        var element = _doc.GetElement(elementId);

                        if (element != null)
                        {
                            bool elementUpdated = false;
                            for (int col = 1; col <= headers.Count; col++)
                            {
                                if (col == elementIdCol) continue; // Pula a coluna do ID

                                var paramName = headers[col - 1];
                                var parameter = element.LookupParameter(paramName);

                                if (parameter != null && !parameter.IsReadOnly)
                                {
                                    var newValue = worksheet.Cells[row, col].Text;
                                    // Compara o valor antigo com o novo para evitar transações desnecessárias
                                    string oldValue = GetValueText(parameter) ?? string.Empty;
                                    if (oldValue != newValue)
                                    {
                                        // 文字參數用 Set，數值等其他型別用 SetValueString（會依專案單位解析）
                                        try
                                        {
                                            bool ok = parameter.StorageType == StorageType.String
                                                ? parameter.Set(newValue)
                                                : parameter.SetValueString(newValue);
                                            if (ok) elementUpdated = true;
                                        }
                                        catch (Exception)
                                        {
                                            // 這個參數無法用文字寫入（例如 ElementId 型別），略過
                                        }
                                    }
                                }
                            }
                            if (elementUpdated) updatedCount++;
                        }
                    }
                    trans.Commit();
                }
            }
            return updatedCount;
        }
    }
}
