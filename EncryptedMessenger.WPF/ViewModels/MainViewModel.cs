using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using EncryptedMessenger.Core.IPC;
using EncryptedMessenger.Core.Models;
using EncryptedMessenger.WPF.Helpers;
using Microsoft.Extensions.Logging;

namespace EncryptedMessenger.WPF.ViewModels
{
    public class MainViewModel : ObservableObject, IDisposable
    {
        private readonly PipeClient _pipe;
        private readonly AppSettings _settings;
        private readonly ILoggerFactory _loggerFactory;
        private readonly ILogger _logger;

        // ── Contacts ──────────────────────────────────────────────────────
        public ObservableCollection<ContactViewModel> Contacts { get; } = [];

        private readonly ICollectionView _filteredContacts;
        public ICollectionView FilteredContacts => _filteredContacts;

        private string _searchText = string.Empty;
        public string SearchText
        {
            get => _searchText;
            set
            {
                SetField(ref _searchText, value);
                OnPropertyChanged(nameof(IsSearchEmpty));
                _filteredContacts.Refresh();
            }
        }
        public bool IsSearchEmpty => string.IsNullOrEmpty(_searchText);

        // ── Selection / active chat ────────────────────────────────────────
        private ContactViewModel? _selectedContact;
        public ContactViewModel? SelectedContact
        {
            get => _selectedContact;
            set { SetField(ref _selectedContact, value); if (value != null) OpenChat(value); }
        }

        private ChatViewModel? _activeChat;
        public ChatViewModel? ActiveChat
        {
            get => _activeChat;
            set => SetField(ref _activeChat, value);
        }

        // ── Status / identity ──────────────────────────────────────────────
        private bool _isServiceConnected;
        public bool IsServiceConnected
        {
            get => _isServiceConnected;
            set => SetField(ref _isServiceConnected, value);
        }

        public string OwnDisplayName => _settings.DisplayName;
        public string OwnFirstLetter =>
            string.IsNullOrEmpty(_settings.DisplayName) ? "?" : _settings.DisplayName[0].ToString().ToUpper();

        // ── Commands ──────────────────────────────────────────────────────
        public AsyncRelayCommand RefreshContactsCommand { get; }
        public RelayCommand OpenSettingsCommand { get; }
        public RelayCommand AddContactCommand { get; }

        // ── Constructor ───────────────────────────────────────────────────
        public MainViewModel(AppSettings settings, PipeClient pipeClient, ILoggerFactory loggerFactory)
        {
            _settings = settings;
            _pipe = pipeClient;
            _loggerFactory = loggerFactory;
            _logger = loggerFactory.CreateLogger<MainViewModel>();

            _filteredContacts = CollectionViewSource.GetDefaultView(Contacts);
            _filteredContacts.Filter = obj =>
                obj is ContactViewModel c &&
                (string.IsNullOrEmpty(_searchText) ||
                 c.DisplayName.Contains(_searchText, StringComparison.OrdinalIgnoreCase) ||
                 c.IpAddress.Contains(_searchText, StringComparison.OrdinalIgnoreCase));

            _pipe.MessageReceived += OnPipeMessage;
            _pipe.ConnectionChanged += (_, connected) =>
            {
                _logger.LogInformation("Service connection state changed: {Connected}", connected);
                Application.Current.Dispatcher.Invoke(() => IsServiceConnected = connected);
                if (connected) _ = RequestContactsAsync();
            };

            RefreshContactsCommand = new AsyncRelayCommand(_ => RequestContactsAsync());
            OpenSettingsCommand = new RelayCommand(_ => OpenSettings());
            AddContactCommand = new RelayCommand(_ => OpenAddContact());

            _ = _pipe.StartAsync();
        }

        // ── IPC ───────────────────────────────────────────────────────────
        private async Task RequestContactsAsync()
            => await _pipe.SendAsync(PipeMessage.Create(PipeMessageType.GetContacts, new { }));

        private void OnPipeMessage(object? _, PipeMessage msg)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                switch (msg.Type)
                {
                    case PipeMessageType.ContactList:
                        var contacts = msg.Deserialize<List<Contact>>();
                        Contacts.Clear();
                        foreach (var c in contacts) Contacts.Add(new ContactViewModel(c));
                        break;

                    case PipeMessageType.ContactOnline:
                        var on = msg.Deserialize<ContactStatusPayload>();
                        UpdateContactStatus(on.ContactId, true);
                        break;

                    case PipeMessageType.ContactOffline:
                        var off = msg.Deserialize<ContactStatusPayload>();
                        UpdateContactStatus(off.ContactId, false);
                        break;

                    case PipeMessageType.ContactIdChanged:
                        var chg = msg.Deserialize<ContactIdChangedPayload>();
                        // Update the open chat so it keys messages under the real id.
                        if (ActiveChat?.ContactId == chg.OldId)
                            ActiveChat.UpdateContactId(chg.NewId);
                        break;

                    case PipeMessageType.NewIncomingMessage:
                        var newMsg = msg.Deserialize<NewMessagePayload>();
                        if (ActiveChat?.ContactId == newMsg.SenderId)
                            ActiveChat.AddIncomingMessage(newMsg);
                        else
                            IncrementBadge(newMsg.SenderId, newMsg.PlainText);
                        break;

                    case PipeMessageType.DeliveryAck:
                        var ackId = msg.Deserialize<string>();
                        ActiveChat?.MarkDelivered(ackId);
                        break;

                    case PipeMessageType.MessageHistory:
                        var hist = msg.Deserialize<List<Message>>();
                        ActiveChat?.PopulateHistory(hist);
                        break;
                }
            });
        }

        // ── Helpers ───────────────────────────────────────────────────────
        private void UpdateContactStatus(string contactId, bool online)
        {
            var vm = Contacts.FirstOrDefault(c => c.Id == contactId);
            if (vm != null) vm.IsOnline = online;

            // Also update the open chat header's status text (connecting / online / offline)
            if (ActiveChat?.ContactId == contactId)
                ActiveChat.SetOnlineStatus(online);
        }

        private void IncrementBadge(string contactId, string preview)
        {
            var vm = Contacts.FirstOrDefault(c => c.Id == contactId);
            if (vm != null)
            {
                vm.UnreadCount++;
                vm.LastMessagePreview = preview;
            }
        }

        private void OpenChat(ContactViewModel contact)
        {
            contact.UnreadCount = 0;
            ActiveChat = new ChatViewModel(contact.Contact, _pipe, _settings.UserId, _loggerFactory);
        }

        private void OpenSettings()
        {
            var win = new Views.SettingsView();
            win.Show();
        }

        private async void OpenAddContact()
        {
            var dlg = new Views.AddContactView
            {
                Owner = Application.Current.MainWindow
            };

            if (dlg.ShowDialog() == true && dlg.ViewModel.Confirmed)
            {
                var vm = dlg.ViewModel;
                await _pipe.SendAsync(PipeMessage.Create(PipeMessageType.AddContact,
                    new AddContactPayload(vm.DisplayName.Trim(), vm.IpAddress.Trim(), vm.ParsedPort)));
            }
        }

        public void Dispose()
        {
            _pipe.MessageReceived -= OnPipeMessage;
            _pipe.Dispose();
        }
    }

    // ── ContactViewModel ──────────────────────────────────────────────────
    public class ContactViewModel(Contact contact) : ObservableObject
    {
        public Contact Contact { get; } = contact;

        public string Id => Contact.Id;
        public string DisplayName => Contact.DisplayName;
        public string IpAddress => Contact.IpAddress;
        public DateTime LastSeen => Contact.LastSeen;

        public string FirstLetter =>
            string.IsNullOrEmpty(Contact.DisplayName) ? "?"
            : Contact.DisplayName[0].ToString().ToUpper();

        private bool _isOnline;
        public bool IsOnline
        {
            get => _isOnline;
            set => SetField(ref _isOnline, value);
        }

        private int _unreadCount;
        public int UnreadCount
        {
            get => _unreadCount;
            set { SetField(ref _unreadCount, value); OnPropertyChanged(nameof(HasUnread)); }
        }
        public bool HasUnread => UnreadCount > 0;

        private string _lastMessagePreview = string.Empty;
        public string LastMessagePreview
        {
            get => _lastMessagePreview;
            set => SetField(ref _lastMessagePreview, value);
        }
    }
}