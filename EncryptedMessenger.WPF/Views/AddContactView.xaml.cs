using System.Windows;
using EncryptedMessenger.WPF.ViewModels;

namespace EncryptedMessenger.WPF.Views
{
    public partial class AddContactView : Window
    {
        public AddContactViewModel ViewModel { get; }

        public AddContactView()
        {
            InitializeComponent();
            ViewModel = new AddContactViewModel();
            DataContext = ViewModel;
        }

        private void OnAddClick(object sender, RoutedEventArgs e)
        {
            if (ViewModel.Confirmed)
            {
                DialogResult = true;
                Close();
            }
        }

        private void OnCancelClick(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}