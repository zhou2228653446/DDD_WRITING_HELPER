using System.Windows;

namespace 编辑器
{
    public partial class InputDialog : Window
    {
        public string? InputText { get; private set; }

        public InputDialog(string title, string message, string defaultValue = "")
        {
            InitializeComponent();
            Title = title;
            MessageText.Text = message;
            InputTextBox.Text = defaultValue;
            InputTextBox.Focus();
            InputTextBox.SelectAll();
        }

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            InputText = InputTextBox.Text;
            DialogResult = true;
            Close();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
