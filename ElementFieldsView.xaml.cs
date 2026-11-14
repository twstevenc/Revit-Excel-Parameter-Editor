using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace RevitParameterEditor
{
    public enum FieldChoice
    {
        None,
        Add,
        Delete
    }

    // 「元件欄位」視窗：只收集使用者想做的事，實際修改模型由指令處理
    public partial class ElementFieldsView : Window
    {
        private readonly List<CheckItem> _fields;

        public FieldChoice Choice { get; private set; }
        public string FieldName { get; private set; }
        public string FieldValue { get; private set; }
        public string GroupName { get; private set; }
        public bool IsInstance { get; private set; }
        public List<string> SelectedFields { get; private set; }
        public bool DeleteEverywhere { get; private set; }

        // fields：Title 是欄位名稱，Subtitle 是顯示用文字，Tag 是目前的值
        public ElementFieldsView(string header, string categoryName, List<CheckItem> fields, string status)
        {
            InitializeComponent();

            _fields = fields;
            Choice = FieldChoice.None;
            SelectedFields = new List<string>();

            HeaderText.Text = header;
            AddNoteText.Text = "新欄位會加到「" + categoryName + "」這個品類的所有元件上，內容只填入目前選取的元件。" +
                               "輸入已存在的欄位名稱，就是修改它的內容。";
            ExistingHeaderText.Text = "「" + categoryName + "」現有的自訂欄位（" + fields.Count + " 個）";
            RemoveFromCategoryText.Text = "只從「" + categoryName + "」移除（其他品類保留）";
            StatusText.Text = status ?? string.Empty;

            GroupCombo.ItemsSource = new[] { "識別資料", "資料", "文字", "一般" };
            GroupCombo.SelectedIndex = 0;

            FieldListBox.ItemsSource = _fields;
        }

        // 點選現有欄位時，把名稱和內容帶到上方，方便直接修改
        private void FieldListBox_SelectionChanged(object sender,
            System.Windows.Controls.SelectionChangedEventArgs e)
        {
            var item = FieldListBox.SelectedItem as CheckItem;
            if (item == null) return;
            NameBox.Text = item.Title;
            ValueBox.Text = item.Tag as string ?? string.Empty;
        }

        private void AddButton_Click(object sender, RoutedEventArgs e)
        {
            string name = (NameBox.Text ?? string.Empty).Trim();
            if (name.Length == 0)
            {
                StatusText.Text = "請輸入欄位名稱。";
                return;
            }

            FieldName = name;
            FieldValue = (ValueBox.Text ?? string.Empty).Trim();
            GroupName = GroupCombo.SelectedItem as string ?? "識別資料";
            IsInstance = InstanceRadio.IsChecked == true;
            Choice = FieldChoice.Add;
            DialogResult = true;
        }

        private void DeleteButton_Click(object sender, RoutedEventArgs e)
        {
            List<string> selected = _fields.Where(f => f.IsChecked).Select(f => f.Title).ToList();
            if (selected.Count == 0)
            {
                StatusText.Text = "請先勾選要刪除的欄位。";
                return;
            }

            SelectedFields = selected;
            DeleteEverywhere = DeleteEverywhereRadio.IsChecked == true;
            Choice = FieldChoice.Delete;
            DialogResult = true;
        }
    }
}
