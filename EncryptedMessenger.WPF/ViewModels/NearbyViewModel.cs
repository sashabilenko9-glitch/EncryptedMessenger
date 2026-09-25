using System.Collections.ObjectModel;
using EncryptedMessenger.Core.IPC;
using EncryptedMessenger.WPF.Helpers;
using Microsoft.Extensions.Logging;

namespace EncryptedMessenger.WPF.ViewModels
{
    /// <summary>
    /// "Find contacts" window: incoming requests, our pending requests, and messenger instances
    /// announcing themselves on the LAN that are not contacts yet. All three lists are owned by
    /// the service; this view model only mirrors the latest RequestList / NearbyList it sent.
    /// </summary>
    public class NearbyViewModel : ObservableObject
    {
        private readonly PipeClient _pipe;
        private readonly ILogger _logger;

        public ObservableCollection<NearbyPeerViewModel> Peers { get; } = [];
        public ObservableCollection<ContactRequestViewModel> IncomingRequests { get; } = [];
        public ObservableCollection<ContactRequestViewModel> OutgoingRequests { get; } = [];

        public bool IsEmpty => Peers.Count == 0;
        public bool HasIncoming => IncomingRequests.Count > 0;
        public bool HasOutgoing => OutgoingRequests.Count > 0;

        /// <summary>Shown as a badge on the sidebar button so a new request isn't missed.</summary>
        public int IncomingCount => IncomingRequests.Count;

        /// <summary>Discovery can be switched off in the settings; then the nearby list stays empty for a reason worth showing.</summary>
        public bool IsDiscoveryEnabled { get; }

        public AsyncRelayCommand AddCommand { get; }
        public AsyncRelayCommand AcceptCommand { get; }
        public AsyncRelayCommand DeclineCommand { get; }
        public AsyncRelayCommand CancelCommand { get; }

        public NearbyViewModel(PipeClient pipe, bool isDiscoveryEnabled, ILoggerFactory loggerFactory)
        {
            _pipe = pipe;
            IsDiscoveryEnabled = isDiscoveryEnabled;
            _logger = loggerFactory.CreateLogger<NearbyViewModel>();

            AddCommand = new AsyncRelayCommand(
                p => SendAsync(PipeMessageType.AddNearbyPeer, p is NearbyPeerViewModel n ? new AddNearbyPeerPayload(n.PeerId) : null),
                p => p is NearbyPeerViewModel { IsCompatible: true });
            AcceptCommand  = RequestCommand(PipeMessageType.AcceptContactRequest);
            DeclineCommand = RequestCommand(PipeMessageType.DeclineContactRequest);
            CancelCommand  = RequestCommand(PipeMessageType.CancelContactRequest);
        }

        // The three request buttons differ only in the message type they send.
        private AsyncRelayCommand RequestCommand(PipeMessageType type)
            => new(p => SendAsync(type, p is ContactRequestViewModel r ? new ContactIdPayload(r.ContactId) : null),
                   p => p is ContactRequestViewModel);

        /// <summary>Called by MainViewModel (on the UI thread) when the service sends a new NearbyList.</summary>
        public void Replace(List<NearbyPeerPayload> peers)
        {
            Peers.Clear();
            foreach (var p in peers) Peers.Add(new NearbyPeerViewModel(p));
            OnPropertyChanged(nameof(IsEmpty));
        }

        /// <summary>Called by MainViewModel (on the UI thread) when the service sends a new RequestList.</summary>
        public void ReplaceRequests(List<ContactRequestPayload> requests)
        {
            IncomingRequests.Clear();
            OutgoingRequests.Clear();
            foreach (var r in requests)
                (r.Incoming ? IncomingRequests : OutgoingRequests).Add(new ContactRequestViewModel(r));
            OnPropertyChanged(nameof(HasIncoming));
            OnPropertyChanged(nameof(HasOutgoing));
            OnPropertyChanged(nameof(IncomingCount));
        }

        /// <summary>Asks the service for the current lists (e.g. when the window opens).</summary>
        public Task RefreshAsync() => SendAsync(PipeMessageType.GetNearby, new { });

        private async Task SendAsync<T>(PipeMessageType type, T? payload)
        {
            if (payload == null) return;
            try { await _pipe.SendAsync(PipeMessage.Create(type, payload)); }
            catch (Exception ex) { _logger.LogWarning(ex, "{Type} request failed", type); }
        }
    }

    public class NearbyPeerViewModel(NearbyPeerPayload peer)
    {
        /// <summary>Same protocol version as this app; otherwise a request couldn't be delivered.</summary>
        public bool IsCompatible { get; } = peer.ProtocolVersion == Core.Network.ProtocolVersions.Current;
        public string IncompatibleText { get; } =
            $"⚠ Andere Version (Protokoll v{peer.ProtocolVersion}, diese App v{Core.Network.ProtocolVersions.Current}) – bitte beide aktualisieren";

        public string PeerId { get; } = peer.PeerId;
        public string DisplayName { get; } = peer.DisplayName;
        public string Address { get; } = $"{peer.IpAddress}:{peer.Port}";
        public string FirstLetter { get; } =
            string.IsNullOrEmpty(peer.DisplayName) ? "?" : peer.DisplayName[0].ToString().ToUpper();
    }

    public class ContactRequestViewModel(ContactRequestPayload request)
    {
        public string ContactId { get; } = request.ContactId;
        public string DisplayName { get; } = request.DisplayName;
        public string IpAddress { get; } = request.IpAddress;
        public string FirstLetter { get; } =
            string.IsNullOrEmpty(request.DisplayName) ? "?" : request.DisplayName[0].ToString().ToUpper();

        /// <summary>The address answered with a different key than the one known for this peer; the connection was refused.</summary>
        public bool KeyRejected { get; } = request.KeyRejected;

        /// <summary>Set when the peer turned out to speak another protocol version; null otherwise.</summary>
        public string? IncompatibleText { get; } = request.IncompatibleVersion is { } v
            ? $"⚠ Andere Version (Protokoll v{v}, diese App v{Core.Network.ProtocolVersions.Current}) – bitte beide aktualisieren"
            : null;

        /// <summary>Line under the name: the code to compare, or why there is none yet.</summary>
        public string CodeText { get; } = request.VerificationCode is { } code
            ? $"Sicherheitscode: {code}"
            : "Sicherheitscode nach der ersten Verbindung";
    }
}
