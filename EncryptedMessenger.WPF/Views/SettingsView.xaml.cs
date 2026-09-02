using System.Windows;
using EncryptedMessenger.WPF.ViewModels;

namespace EncryptedMessenger.WPF.Views
{
    public partial class SettingsView : Window
    {
        public SettingsView()
        {
            InitializeComponent();
            DataContext = new SettingsViewModel(App.Settings);
        }
    }
}
