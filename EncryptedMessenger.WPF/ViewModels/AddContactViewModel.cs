using System.Net;
using EncryptedMessenger.WPF.Helpers;

namespace EncryptedMessenger.WPF.ViewModels
{
    /// <summary>
    /// ViewModel for the "Add contact manually" dialog.
    /// Validates IP address and port; on success exposes the values so the
    /// window can return them to MainViewModel for sending over the pipe.
    /// </summary>
    public class AddContactViewModel : ObservableObject
    {
        private string _displayName = string.Empty;
        public string DisplayName
        {
            get => _displayName;
            set => SetField(ref _displayName, value);
        }

        private string _ipAddress = string.Empty;
        public string IpAddress
        {
            get => _ipAddress;
            set { SetField(ref _ipAddress, value); Validate(); }
        }

        private string _port = "9876";
        public string Port
        {
            get => _port;
            set { SetField(ref _port, value); Validate(); }
        }

        private string _error = string.Empty;
        public string Error
        {
            get => _error;
            set => SetField(ref _error, value);
        }

        private bool _isValid;
        public bool IsValid
        {
            get => _isValid;
            set => SetField(ref _isValid, value);
        }

        /// <summary>Set to true by the window when the user confirms a valid entry.</summary>
        public bool Confirmed { get; private set; }

        public int ParsedPort { get; private set; }

        public RelayCommand AddCommand { get; }

        public AddContactViewModel()
        {
            AddCommand = new RelayCommand(_ => Confirm(), _ => IsValid);
            Validate();
        }

        private void Validate()
        {
            if (!IPAddress.TryParse(IpAddress.Trim(), out _))
            {
                Error = "Ungültige IP-Adresse (z. B. 192.168.0.42)";
                IsValid = false;
                return;
            }

            if (!int.TryParse(Port.Trim(), out var p) || p < 1 || p > 65535)
            {
                Error = "Port muss zwischen 1 und 65535 liegen";
                IsValid = false;
                return;
            }

            ParsedPort = p;
            Error = string.Empty;
            IsValid = true;
        }

        private void Confirm()
        {
            Validate();
            if (IsValid) Confirmed = true;
        }
    }
}