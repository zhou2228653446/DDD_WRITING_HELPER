using System;
using System.Windows;
using Microsoft.Win32;

namespace 编辑器
{
    public partial class PathSettingsWindow : Window
    {
        private readonly PathsConfig _original;

        public PathsConfig Result { get; private set; } = null!;

        public PathSettingsWindow(Window owner, PathsConfig current)
        {
            InitializeComponent();
            Owner = owner;
            _original = current;

            ProjectsPathTextBox.Text = !string.IsNullOrWhiteSpace(current.ProjectsDirectory) ? current.ProjectsDirectory : GetDefaultProjectsDir();
            ConfigPathTextBox.Text = !string.IsNullOrWhiteSpace(current.ConfigDirectory) ? current.ConfigDirectory : GetDefaultConfigDir();
        }

        private static string GetDefaultProjectsDir() =>
            System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "TdxClaw", "Projects");

        private static string GetDefaultConfigDir() =>
            System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TdxClaw");

        private void BrowseProjects_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFolderDialog
            {
                Title = "选择项目文件存放目录",
                InitialDirectory = ProjectsPathTextBox.Text
            };

            if (dialog.ShowDialog(this) == true)
            {
                ProjectsPathTextBox.Text = dialog.FolderName;
            }
        }

        private void BrowseConfig_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFolderDialog
            {
                Title = "选择配置文件存放目录",
                InitialDirectory = ConfigPathTextBox.Text
            };

            if (dialog.ShowDialog(this) == true)
            {
                ConfigPathTextBox.Text = dialog.FolderName;
            }
        }

        private void ResetDefaults_Click(object sender, RoutedEventArgs e)
        {
            ProjectsPathTextBox.Text = GetDefaultProjectsDir();
            ConfigPathTextBox.Text = GetDefaultConfigDir();
        }

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            var projectsDir = ProjectsPathTextBox.Text.Trim();
            var configDir = ConfigPathTextBox.Text.Trim();

            if (string.IsNullOrWhiteSpace(projectsDir) || string.IsNullOrWhiteSpace(configDir))
            {
                MessageBox.Show("目录路径不能为空", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            Result = new PathsConfig
            {
                ProjectsDirectory = projectsDir,
                ConfigDirectory = configDir
            };

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
