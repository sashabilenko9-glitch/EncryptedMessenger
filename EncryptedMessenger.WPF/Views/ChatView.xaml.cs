using System.Collections.Specialized;
using System.Windows.Controls;
using EncryptedMessenger.WPF.ViewModels;

namespace EncryptedMessenger.WPF.Views
{
    public partial class ChatView : UserControl
    {
        private ChatViewModel? _currentVm;

        public ChatView()
        {
            InitializeComponent();
            DataContextChanged += OnDataContextChanged;
        }

        private void OnDataContextChanged(object sender, System.Windows.DependencyPropertyChangedEventArgs e)
        {
            // Unsubscribe from old VM
            if (_currentVm != null)
                _currentVm.Messages.CollectionChanged -= OnMessagesChanged;

            _currentVm = e.NewValue as ChatViewModel;

            // Subscribe to new VM
            if (_currentVm != null)
            {
                _currentVm.Messages.CollectionChanged += OnMessagesChanged;
                // Focus the text input
                Dispatcher.InvokeAsync(() => MessageInput.Focus(),
                    System.Windows.Threading.DispatcherPriority.Input);
                ScrollToBottom();
            }
        }

        private void OnMessagesChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.Action == NotifyCollectionChangedAction.Add)
                ScrollToBottom();
        }

        private void ScrollToBottom()
        {
            Dispatcher.InvokeAsync(
                () => MessageScroll.ScrollToEnd(),
                System.Windows.Threading.DispatcherPriority.Background);
        }

        // ── Emoji picker ───────────────────────────────────────────────────

        private void OnEmojiButtonClick(object sender, System.Windows.RoutedEventArgs e)
        {
            EmojiPopup.IsOpen = !EmojiPopup.IsOpen;
        }

        private void OnEmojiPicked(object sender, System.Windows.RoutedEventArgs e)
        {
            // Command already inserted the emoji; close popup and refocus input.
            EmojiPopup.IsOpen = false;
            MessageInput.Focus();
            MessageInput.CaretIndex = MessageInput.Text.Length;
        }
    }
}