using System.Windows;
using EncryptedMessenger.WPF.ViewModels;

namespace EncryptedMessenger.WPF.Views
{
    public partial class NearbyView : Window
    {
        public NearbyView(NearbyViewModel viewModel)
        {
            InitializeComponent();
            DataContext = viewModel;
            // Ask for a fresh list every time the window opens.
            Loaded += async (_, _) => await viewModel.RefreshAsync();
        }
    }
}
