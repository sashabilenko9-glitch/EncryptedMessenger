using System.Windows;

namespace EncryptedMessenger.WPF.Views
{
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
            DataContext = App.MainVM;
        }
    }
}
