using EncryptedMessenger.Core.Models;
using EncryptedMessenger.WPF.Helpers;

namespace EncryptedMessenger.WPF.ViewModels
{
    public class SettingsViewModel : ObservableObject
    {
        private readonly AppSettings _settings;

        public string DisplayName
        {
            get => _settings.DisplayName;
            set { _settings.DisplayName = value; OnPropertyChanged(); }
        }

        public int TcpPort
        {
            get => _settings.TcpPort;
            set { _settings.TcpPort = value; OnPropertyChanged(); }
        }

        public int UdpPort
        {
            get => _settings.UdpPort;
            set { _settings.UdpPort = value; OnPropertyChanged(); }
        }

        public bool EnableDiscovery
        {
            get => _settings.Discovery;
            set { _settings.Discovery = value; OnPropertyChanged(); }
        }

        private string _statusMessage = string.Empty;
        public string StatusMessage
        {
            get => _statusMessage;
            set => SetField(ref _statusMessage, value);
        }

        public RelayCommand SaveCommand { get; }

        public SettingsViewModel(AppSettings settings)
        {
            _settings   = settings;
            SaveCommand = new RelayCommand(_ => Save());
        }

        private void Save()
        {
            _settings.Save();
            StatusMessage = "✓  Gespeichert – Neustart erforderlich";
        }
    }
}
