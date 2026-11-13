using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;

namespace RevitParameterEditor
{
    // 清單中的一列（可勾選）
    public class CheckItem : INotifyPropertyChanged
    {
        private bool _isChecked;

        public string Title { get; set; }
        public string Subtitle { get; set; }
        public object Tag { get; set; }

        public bool IsChecked
        {
            get { return _isChecked; }
            set
            {
                if (_isChecked == value) return;
                _isChecked = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked)));
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;
    }

    // 「建立參數」視窗：選設定檔、勾選要建立的參數與品類
    public partial class CreateParametersView : Window
    {
        private readonly Autodesk.Revit.DB.Document _doc;
        private List<CheckItem> _parameterItems = new List<CheckItem>();
        private List<CheckItem> _categoryItems = new List<CheckItem>();
        private string _baseStatus = string.Empty;

        // 按下「建立參數」後，這裡是使用者勾選的內容
        public ParameterConfig SelectedConfig { get; private set; }

        public CreateParametersView(Autodesk.Revit.DB.Document doc)
        {
            InitializeComponent();
            _doc = doc;
            ReloadConfigList(ConfigStore.LoadLastUsed());
        }

        // 重新整理下拉選單，並選取指定路徑（找不到就選第一個）
        private void ReloadConfigList(string pathToSelect)
        {
            List<ConfigEntry> entries = ConfigStore.GetEntries();
            ConfigComboBox.ItemsSource = entries;

            if (entries.Count == 0)
            {
                ShowLoadError("找不到任何設定檔。請按「瀏覽」選擇一個設定檔（.xlsx）。\n預設位置：" +
                              ConfigStore.BuiltInFolder);
                return;
            }

            ConfigEntry target = entries.FirstOrDefault(e =>
                                     string.Equals(e.Path, pathToSelect, StringComparison.OrdinalIgnoreCase))
                                 ?? entries[0];
            ConfigComboBox.SelectedItem = target;
        }

        private void ConfigComboBox_SelectionChanged(object sender,
            System.Windows.Controls.SelectionChangedEventArgs e)
        {
            var entry = ConfigComboBox.SelectedItem as ConfigEntry;
            if (entry == null) return;
            LoadConfig(entry.Path);
        }

        private void BrowseButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "Excel 檔案 (*.xlsx)|*.xlsx",
                Title = "選擇參數設定檔"
            };
            if (dialog.ShowDialog(this) != true) return;

            ConfigStore.AddExtra(dialog.FileName);
            ReloadConfigList(dialog.FileName);
        }

        // 讀取設定檔並填入兩個清單
        private void LoadConfig(string path)
        {
            ConfigPathText.Text = path;

            ParameterConfig config;
            try
            {
                config = ParameterConfig.Load(path);
            }
            catch (Exception ex)
            {
                ShowLoadError("無法讀取設定檔：" + ex.Message);
                return;
            }

            _parameterItems = config.Parameters.Select(p => new CheckItem
            {
                Title = p.Name,
                Subtitle = BuildParameterSubtitle(p),
                Tag = p,
                IsChecked = true
            }).ToList();

            _categoryItems = config.Categories.Select(c => new CheckItem
            {
                Title = string.IsNullOrEmpty(c.Label) ? c.Code : c.Label,
                Subtitle = string.IsNullOrEmpty(c.Label) ? string.Empty : c.Code,
                Tag = c,
                IsChecked = true
            }).ToList();

            foreach (CheckItem item in _parameterItems.Concat(_categoryItems))
            {
                item.PropertyChanged += (s, args) => UpdateCounts();
            }

            ParameterListBox.ItemsSource = _parameterItems;
            CategoryListBox.ItemsSource = _categoryItems;

            _baseStatus = config.Warnings.Count > 0
                ? "設定檔有 " + config.Warnings.Count + " 個警告：" + config.Warnings[0]
                : "已存在的參數不會重複建立，只會補上缺少的品類。有預設值的參數只會填入空白的元件。";

            UpdateCounts();
        }

        private string BuildParameterSubtitle(ParameterSpec spec)
        {
            var parts = new List<string>();
            if (!string.IsNullOrEmpty(spec.Description)) parts.Add(spec.Description);
            parts.Add(spec.IsInstance ? "實例" : "類型");
            if (!string.IsNullOrEmpty(spec.DefaultValue)) parts.Add("預設值：" + spec.DefaultValue);
            if (ExistsInProject(spec)) parts.Add("專案中已存在");
            return string.Join("　", parts);
        }

        private bool ExistsInProject(ParameterSpec spec)
        {
            try
            {
                Guid guid = SharedParameterCreator.ResolveGuid(spec);
                return Autodesk.Revit.DB.SharedParameterElement.Lookup(_doc, guid) != null;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private void ShowLoadError(string message)
        {
            _parameterItems = new List<CheckItem>();
            _categoryItems = new List<CheckItem>();
            ParameterListBox.ItemsSource = _parameterItems;
            CategoryListBox.ItemsSource = _categoryItems;
            ParamCountText.Text = "參數";
            CategoryCountText.Text = "品類";
            StatusText.Text = message;
            CreateButton.IsEnabled = false;
        }

        private void UpdateCounts()
        {
            int paramChecked = _parameterItems.Count(i => i.IsChecked);
            int catChecked = _categoryItems.Count(i => i.IsChecked);

            ParamCountText.Text = "參數（已選 " + paramChecked + " / " + _parameterItems.Count + "）";
            CategoryCountText.Text = "品類（已選 " + catChecked + " / " + _categoryItems.Count + "）";

            CreateButton.IsEnabled = paramChecked > 0 && catChecked > 0;
            StatusText.Text = CreateButton.IsEnabled
                ? _baseStatus
                : "請至少勾選一個參數和一個品類。";
        }

        private static void SetAll(List<CheckItem> items, bool value)
        {
            foreach (CheckItem item in items) item.IsChecked = value;
        }

        private void ParamAll_Click(object sender, RoutedEventArgs e)
        {
            SetAll(_parameterItems, true);
        }

        private void ParamNone_Click(object sender, RoutedEventArgs e)
        {
            SetAll(_parameterItems, false);
        }

        private void CategoryAll_Click(object sender, RoutedEventArgs e)
        {
            SetAll(_categoryItems, true);
        }

        private void CategoryNone_Click(object sender, RoutedEventArgs e)
        {
            SetAll(_categoryItems, false);
        }

        private void CreateButton_Click(object sender, RoutedEventArgs e)
        {
            var selected = new ParameterConfig();
            selected.Parameters = _parameterItems.Where(i => i.IsChecked).Select(i => (ParameterSpec)i.Tag).ToList();
            selected.Categories = _categoryItems.Where(i => i.IsChecked).Select(i => (CategorySpec)i.Tag).ToList();

            if (selected.Parameters.Count == 0 || selected.Categories.Count == 0) return;

            var entry = ConfigComboBox.SelectedItem as ConfigEntry;
            if (entry != null) ConfigStore.SaveLastUsed(entry.Path);

            SelectedConfig = selected;
            DialogResult = true;
        }
    }
}
