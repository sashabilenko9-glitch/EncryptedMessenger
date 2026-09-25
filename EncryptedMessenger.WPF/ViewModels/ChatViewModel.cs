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
        public string ConversationId => MessageRepository.ConversationId(_ownId, _contact.Id);
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
            private set { SetField(ref _fingerprint, value); OnPropertyChanged(nameof(HasTrustedKey)); }
        }

        /// <summary>A handshake with the pinned key happened, so "🔒 Verschlüsselt" is actually true.</summary>
        public bool HasTrustedKey => !string.IsNullOrEmpty(Fingerprint);

        private bool _keyChanged;
        /// <summary>
        /// True while the peer presents a key that differs from the pinned one: the service
        /// refused the connection (reinstall or MITM). Sending is blocked until the user
        /// accepts the new key or the peer shows up again with the pinned one.
        /// </summary>
        public bool KeyChanged
        {
            get => _keyChanged;
            private set => SetField(ref _keyChanged, value);
        }

        private string _pendingFingerprint = string.Empty;
        /// <summary>Fingerprint of the refused NEW key — what the user must compare with the contact before accepting.</summary>
        public string PendingFingerprint
        {
            get => _pendingFingerprint;
            private set => SetField(ref _pendingFingerprint, value);
        }

        // Peer id the refused key belongs to. Usually ContactId; differs when this chat is a
        // manual contact whose address was answered by a peer with a different real id.
        private string _pendingKeyContactId = string.Empty;

        private string _verificationCode = string.Empty;
        /// <summary>40-digit code (20 digits per public key); the contact sees the same one unless someone is in between.</summary>
        public string VerificationCode
        {
            get => _verificationCode;
            private set { SetField(ref _verificationCode, value); OnPropertyChanged(nameof(CanVerify)); }
        }

        private bool _isVerified;
        /// <summary>The user confirmed the code matches (for the currently pinned key).</summary>
        public bool IsVerified
        {
            get => _isVerified;
            private set { SetField(ref _isVerified, value); OnPropertyChanged(nameof(CanVerify)); }
        }

        /// <summary>Show the "Code prüfen" button: there is a code and it hasn't been confirmed yet.</summary>
        public bool CanVerify => !IsVerified && !string.IsNullOrEmpty(VerificationCode);

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
        public AsyncRelayCommand AcceptKeyCommand { get; }
        public AsyncRelayCommand VerifyCommand { get; }
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
                _ => !IsDraftEmpty && !IsSending && !KeyChanged);

            AcceptKeyCommand = new AsyncRelayCommand(_ => AcceptNewKeyAsync(), _ => KeyChanged);
            VerifyCommand = new AsyncRelayCommand(_ => VerifyCodeAsync(), _ => CanVerify);
            _isVerified = contact.Verified;

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
        /// <param name="peerId">Id the fingerprint belongs to (may differ from ContactId for a manual contact).</param>
        public void SetKeyFingerprint(string peerId, string fingerprint, bool changed, string? verificationCode = null)
        {
            // A different key means a different code; the old confirmation no longer applies
            // (the service resets Verified in the database when a new key is pinned).
            if (changed) { VerificationCode = string.Empty; IsVerified = false; }
            else if (verificationCode != null) VerificationCode = verificationCode;

            if (changed)
            {
                // Keep Fingerprint = the trusted key; the new one goes to PendingFingerprint.
                PendingFingerprint = fingerprint;
                _pendingKeyContactId = peerId;
                KeyChanged = true;
                IsConnecting = false;
                _logger.LogWarning("Connection to {ContactId} refused: key changed, new fingerprint {Fingerprint}", peerId, fingerprint);
            }
            else
            {
                Fingerprint = fingerprint;
                PendingFingerprint = string.Empty;
                KeyChanged = false;
            }
            SendCommand.RaiseCanExecuteChanged();
        }

        private string? _protocolWarning;
        /// <summary>Set when the contact's app speaks another protocol version; shown in the header.</summary>
        public string? ProtocolWarning
        {
            get => _protocolWarning;
            private set => SetField(ref _protocolWarning, value);
        }

        /// <summary>Called by MainViewModel when the service refused this contact for a protocol version mismatch.</summary>
        public void SetIncompatible(int peerVersion)
        {
            IsConnecting = false;
            ProtocolWarning = $"⚠ Kontakt nutzt Protokoll v{peerVersion}, diese App v{Core.Network.ProtocolVersions.Current} – bitte beide aktualisieren";
        }

        /// <summary>Called by MainViewModel when the contact list arrives with this contact's Verified flag.</summary>
        public void SetVerified(bool verified) => IsVerified = verified;

        /// <summary>
        /// Asks the user to compare the code with the contact over another channel. Only an
        /// explicit "Ja" marks it verified, and the code shown is sent along, so the service
        /// refuses it if the key (and thus the code) changed in the meantime.
        /// </summary>
        private async Task VerifyCodeAsync()
        {
            var code = VerificationCode;
            var answer = MessageBox.Show(
                $"Vergleichen Sie diesen Sicherheitscode mit {ContactName} – am Telefon oder persönlich, " +
                "nicht über diesen Chat:\n\n" +
                $"      {code}\n\n" +
                "Sieht Ihr Kontakt auf seinem Gerät genau denselben Code?",
                "Sicherheitscode prüfen",
                MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes) return;

            try
            {
                await _pipe.SendAsync(PipeMessage.Create(PipeMessageType.MarkVerified,
                    new MarkVerifiedPayload(_contact.Id, code)));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Mark-verified request failed for {ContactId}", _contact.Id);
            }
        }

        /// <summary>
        /// Re-pins the contact to the new key — after an explicit confirmation, because this is
        /// exactly the click a man-in-the-middle needs the user to make. The service only accepts
        /// if its pending key still has the fingerprint shown here, then reconnects this chat.
        /// </summary>
        private async Task AcceptNewKeyAsync()
        {
            var answer = MessageBox.Show(
                "Neuen Schlüssel nur akzeptieren, wenn Sie den Fingerabdruck über einen anderen Kanal " +
                "(Telefon, persönlich) mit dem Kontakt verglichen haben und er exakt übereinstimmt:\n\n" +
                PendingFingerprint + "\n\n" +
                "Stimmt er nicht überein, könnte jemand die Verbindung abfangen.",
                "Neuen Schlüssel akzeptieren?",
                MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes) return;

            try
            {
                IsConnecting = true;
                await _pipe.SendAsync(PipeMessage.Create(PipeMessageType.AcceptPeerKey,
                    new AcceptPeerKeyPayload(_pendingKeyContactId, PendingFingerprint, _contact.Id)));
            }
            catch (Exception ex)
            {
                IsConnecting = false;
                _logger.LogWarning(ex, "Accept-key request failed for {ContactId}", _pendingKeyContactId);
            }
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
                // An ack (or a SendFailed) may already have arrived while we awaited — don't downgrade it.
                if (vm.Status == MessageStatus.Pending)
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
            if (vm != null && vm.Status is MessageStatus.Pending or MessageStatus.Sent)
                vm.Status = MessageStatus.Delivered;
        }

        /// <summary>The peer's user has read our message (ReadAck).</summary>
        public void MarkReadByPeer(string messageId)
        {
            var vm = Messages.FirstOrDefault(m => m.MessageId == messageId);
            if (vm != null) vm.Status = MessageStatus.Read;
        }

        /// <summary>The service could not deliver our message (no connection, handshake failed, …).</summary>
        public void MarkFailed(string messageId)
        {
            var vm = Messages.FirstOrDefault(m => m.MessageId == messageId);
            if (vm != null && vm.Status is MessageStatus.Pending or MessageStatus.Sent)
                vm.Status = MessageStatus.Failed;
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

            // Messages that arrived while this chat was closed are read now that it is open.
            var convId = MessageRepository.ConversationId(_ownId, _contact.Id);
            // Incoming + Delivered = not read yet. (ReadAckPending/Read are already read;
            // the service retries owed receipts on its own.)
            foreach (var m in messages.Where(m => !m.IsOutgoing && m.Status == MessageStatus.Delivered))
                _ = _pipe.SendAsync(PipeMessage.Create(PipeMessageType.MarkRead,
                    new MarkReadPayload(convId, m.MessageId)));
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