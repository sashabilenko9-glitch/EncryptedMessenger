using System.Collections.ObjectModel;
using System.Windows;
using EncryptedMessenger.Core.Database;
using EncryptedMessenger.Core.IPC;
using EncryptedMessenger.Core.Models;
using EncryptedMessenger.WPF.Helpers;

namespace EncryptedMessenger.WPF.ViewModels
{
    public class ChatViewModel : ObservableObject
    {
        private readonly PipeClient _pipe;
        private readonly string _ownId;
        private readonly Contact _contact;

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

        /// <summary>Текст статуса под именем контакта в шапке чата.</summary>
        public string StatusText =>
            IsConnecting ? "Verbinde…" :
            IsContactOnline ? "Online" :
                              "Offline";

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

        /// <summary>Небольшой набор эмодзи для панели быстрого ввода.</summary>
        public string[] Emojis { get; } =
        [
            "😀","😂","😍","😎","😉","😊","🙂","😢","😭","😡",
            "👍","👎","👌","🙏","👏","💪","🔥","✨","🎉","❤️",
            "💔","😴","🤔","🙄","😱","🥳","😇","🤝","✅","❌"
        ];

        // ── Constructor ───────────────────────────────────────────────────
        public ChatViewModel(Contact contact, PipeClient pipe, string ownId)
        {
            _contact = contact;
            _pipe = pipe;
            _ownId = ownId;

            SendCommand = new AsyncRelayCommand(
                _ => SendMessageAsync(),
                _ => !IsDraftEmpty && !IsSending);

            InsertEmojiCommand = new RelayCommand(e =>
            {
                if (e is string emoji) DraftText += emoji;
            });

            _ = LoadHistoryAsync();
            _ = ConnectAsync();   // поднять соединение сразу при открытии чата
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
            catch
            {
                IsConnecting = false;
            }
        }

        /// <summary>Вызывается из MainViewModel, когда пришёл статус online/offline.</summary>
        public void SetOnlineStatus(bool online)
        {
            IsConnecting = false;
            IsContactOnline = online;
        }

        /// <summary>
        /// Сервис «срастил» ручной контакт (manual_ip_port) с реальным UserId
        /// собеседника. Обновляем Id и перезагружаем историю под правильным ключом.
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
            catch
            {
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
            catch
            {
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