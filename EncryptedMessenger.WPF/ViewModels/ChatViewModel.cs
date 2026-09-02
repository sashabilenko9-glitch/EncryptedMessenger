using System.Collections.ObjectModel;
using System.Windows;
using EncryptedMessenger.Core.Database;
using EncryptedMessenger.Core.IPC;
using EncryptedMessenger.Core.Models;
using EncryptedMessenger.WPF.Helpers;
using Microsoft.Extensions.Logging;

namespace EncryptedMessenger.WPF.ViewModels
{
    public class ChatViewModel : ObservableObject
    {
        private readonly PipeClient _pipe;
        private readonly string _ownId;
        private readonly Contact _contact;
        private readonly ILogger _logger;

        // ── Contact info ──────────────────────────────────────────────────
        public string ContactId => _contact.Id;
        public string ContactName => _contact.DisplayName;
        public string ContactIp => _contact.IpAddress;
        public string ContactFirstLetter =>
            string.IsNullOrEmpty(_contact.DisplayName) ? "?"
            : _contact.DisplayName[0].ToString().ToUpper();

        private bool _isContactOnline;
        public bool IsContactOnline
        {
            get => _isContactOnline;
            set
            {
                SetField(ref _isContactOnline, value);
                OnPropertyChanged(nameof(StatusText));
            }
        }

        private bool _isConnecting;
        public bool IsConnecting
        {
            get => _isConnecting;
            set
            {
                SetField(ref _isConnecting, value);
                OnPropertyChanged(nameof(StatusText));
            }
        }

        /// <summary>Status text shown under the contact's name in the chat header.</summary>
        public string StatusText =>
            IsConnecting ? "Verbinde…" :
            IsContactOnline ? "Online" :
                              "Offline";

        // ── Security (key fingerprint) ───────────────────────────────────
        private string _fingerprint = string.Empty;
        /// <summary>SHA-256 fingerprint of the peer's public key from the last handshake, for manual out-of-band verification.</summary>
        public string Fingerprint
        {
            get => _fingerprint;
            private set => SetField(ref _fingerprint, value);
        }

        private bool _keyChanged;
        /// <summary>True when this handshake's key differs from a previously known key for this contact — possible reinstall or MITM.</summary>
        public bool KeyChanged
        {
            get => _keyChanged;
            private set => SetField(ref _keyChanged, value);
        }

        // ── Messages ──────────────────────────────────────────────────────
        public ObservableCollection<MessageViewModel> Messages { get; } = [];

        // ── Input ─────────────────────────────────────────────────────────
        private string _draftText = string.Empty;
        public string DraftText
        {
            get => _draftText;
            set
            {
                SetField(ref _draftText, value);
                OnPropertyChanged(nameof(IsDraftEmpty));
                SendCommand.RaiseCanExecuteChanged();
            }
        }
        public bool IsDraftEmpty => string.IsNullOrWhiteSpace(_draftText);

        // ── State ─────────────────────────────────────────────────────────
        private bool _isSending;
        public bool IsSending
        {
            get => _isSending;
            set => SetField(ref _isSending, value);
        }

        private bool _isLoading = true;
        public bool IsLoading
        {
            get => _isLoading;
            set => SetField(ref _isLoading, value);
        }

        // ── Commands ──────────────────────────────────────────────────────
        public AsyncRelayCommand SendCommand { get; }
        public RelayCommand InsertEmojiCommand { get; }

        /// <summary>Small set of emoji for the quick-insert panel.</summary>
        public string[] Emojis { get; } =
        [
            "😀","😂","😍","😎","😉","😊","🙂","😢","😭","😡",
            "👍","👎","👌","🙏","👏","💪","🔥","✨","🎉","❤️",
            "💔","😴","🤔","🙄","😱","🥳","😇","🤝","✅","❌"
        ];

        // ── Constructor ───────────────────────────────────────────────────
        public ChatViewModel(Contact contact, PipeClient pipe, string ownId, ILoggerFactory loggerFactory)
        {
            _contact = contact;
            _pipe = pipe;
            _ownId = ownId;
            _logger = loggerFactory.CreateLogger<ChatViewModel>();

            SendCommand = new AsyncRelayCommand(
                _ => SendMessageAsync(),
                _ => !IsDraftEmpty && !IsSending);

            InsertEmojiCommand = new RelayCommand(e =>
            {
                if (e is string emoji) DraftText += emoji;
            });

            _ = LoadHistoryAsync();
            _ = ConnectAsync();   // establish the connection as soon as the chat opens
        }

        // ── Connect on open ───────────────────────────────────────────────
        private async Task ConnectAsync()
        {
            IsConnecting = true;
            try
            {
                await _pipe.SendAsync(PipeMessage.Create(PipeMessageType.Connect,
                    new ConnectPayload(_contact.Id)));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Connect request failed for {ContactId}", _contact.Id);
                IsConnecting = false;
            }
        }

        /// <summary>Called by MainViewModel when an online/offline status update arrives.</summary>
        public void SetOnlineStatus(bool online)
        {
            IsConnecting = false;
            IsContactOnline = online;
        }

        /// <summary>Called by MainViewModel when the service reports the peer's key fingerprint after a handshake.</summary>
        public void SetKeyFingerprint(string fingerprint, bool changed)
        {
            Fingerprint = fingerprint;
            KeyChanged = changed;
            if (changed)
                _logger.LogWarning("Key fingerprint for {ContactId} changed: {Fingerprint}", _contact.Id, fingerprint);
        }

        /// <summary>
        /// The service merged a manual contact (manual_ip_port) with the peer's
        /// real UserId. Update the Id and reload history under the correct key.
        /// </summary>
        public void UpdateContactId(string newId)
        {
            if (_contact.Id == newId) return;
            _contact.Id = newId;
            OnPropertyChanged(nameof(ContactId));
            _ = LoadHistoryAsync();
        }

        // ── Send ──────────────────────────────────────────────────────────
        private async Task SendMessageAsync()
        {
            var text = DraftText.Trim();
            if (string.IsNullOrEmpty(text)) return;

            IsSending = true;
            var msgId = Guid.NewGuid().ToString();
            DraftText = string.Empty;

            var vm = new MessageViewModel
            {
                MessageId = msgId,
                Text = text,
                Timestamp = DateTime.UtcNow,
                IsOutgoing = true,
                Status = MessageStatus.Pending
            };
            Messages.Add(vm);

            try
            {
                await _pipe.SendAsync(PipeMessage.Create(PipeMessageType.SendMessage,
                    new SendMessagePayload(_contact.Id, text, msgId)));
                vm.Status = MessageStatus.Sent;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Send failed for message {MessageId} to {ContactId}", msgId, _contact.Id);
                vm.Status = MessageStatus.Failed;
            }
            finally
            {
                IsSending = false;
            }
        }

        // ── Receive ───────────────────────────────────────────────────────
        public void AddIncomingMessage(NewMessagePayload payload)
        {
            Application.Current.Dispatcher.Invoke(() =>
                Messages.Add(new MessageViewModel
                {
                    MessageId = payload.MessageId,
                    Text = payload.PlainText,
                    Timestamp = payload.Timestamp,
                    IsOutgoing = false,
                    Status = MessageStatus.Delivered
                }));

            _ = _pipe.SendAsync(PipeMessage.Create(PipeMessageType.MarkRead,
                new MarkReadPayload(
                    MessageRepository.ConversationId(_ownId, _contact.Id),
                    payload.MessageId)));
        }

        public void MarkDelivered(string messageId)
        {
            var vm = Messages.FirstOrDefault(m => m.MessageId == messageId);
            if (vm != null) vm.Status = MessageStatus.Delivered;
        }

        // ── History ───────────────────────────────────────────────────────
        private async Task LoadHistoryAsync()
        {
            IsLoading = true;
            var convId = MessageRepository.ConversationId(_ownId, _contact.Id);
            try
            {
                await _pipe.SendAsync(PipeMessage.Create(PipeMessageType.GetHistory,
                    new GetHistoryPayload(convId, 0, 50)));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "History request failed for conversation {ConversationId}", convId);
                IsLoading = false;
            }
        }

        public void PopulateHistory(List<Message> messages)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                Messages.Clear();
                foreach (var m in messages)
                    Messages.Add(new MessageViewModel
                    {
                        MessageId = m.MessageId,
                        Text = m.DecryptedContent ?? m.EncryptedContent,
                        Timestamp = m.Timestamp,
                        IsOutgoing = m.IsOutgoing,
                        Status = m.Status
                    });
                IsLoading = false;
            });
        }
    }

    // ── MessageViewModel ──────────────────────────────────────────────────
    public class MessageViewModel : ObservableObject
    {
        public string MessageId { get; init; } = string.Empty;
        public string Text { get; init; } = string.Empty;
        public DateTime Timestamp { get; init; }
        public bool IsOutgoing { get; init; }

        private MessageStatus _status;
        public MessageStatus Status
        {
            get => _status;
            set => SetField(ref _status, value);
        }
    }
}