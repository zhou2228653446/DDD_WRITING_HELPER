using System.Windows;

namespace 编辑器
{
    public partial class WelcomeDialog : Window
    {
        public bool SkipWelcome => SkipWelcomeCheckBox.IsChecked == true;

        public WelcomeDialog(Window owner)
        {
            InitializeComponent();
            Owner = owner;
        }

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
            Close();
        }
    }
}
