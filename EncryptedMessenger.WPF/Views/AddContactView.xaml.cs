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

        // Not bound via Command="...": WPF raises Click BEFORE executing the Command, so the
        // handler would see Confirmed == false on the first click. Run the command here instead.
        private void OnAddClick(object sender, RoutedEventArgs e)
        {
            ViewModel.AddCommand.Execute(null);
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