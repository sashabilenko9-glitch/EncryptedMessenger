using System.Collections.ObjectModel;
using EncryptedMessenger.Core.IPC;
using EncryptedMessenger.WPF.Helpers;
using Microsoft.Extensions.Logging;

namespace EncryptedMessenger.WPF.ViewModels
{
    /// <summary>
    /// "Nearby" window: messenger instances announcing themselves on the LAN that are not
    /// contacts yet. The list is owned by the service (it hears the UDP announcements and
    /// expires silent peers); this view model only mirrors the latest NearbyList it sent.
    /// </summary>
    public class NearbyViewModel : ObservableObject
    {
        private readonly PipeClient _pipe;
        private readonly ILogger _logger;

        public ObservableCollection<NearbyPeerViewModel> Peers { get; } = [];

        public bool IsEmpty => Peers.Count == 0;

        /// <summary>Discovery can be switched off in the settings; then this list stays empty for a reason worth showing.</summary>
        public bool IsDiscoveryEnabled { get; }

        public AsyncRelayCommand AddCommand { get; }

        public NearbyViewModel(PipeClient pipe, bool isDiscoveryEnabled, ILoggerFactory loggerFactory)
        {
            _pipe = pipe;
            IsDiscoveryEnabled = isDiscoveryEnabled;
            _logger = loggerFactory.CreateLogger<NearbyViewModel>();
            AddCommand = new AsyncRelayCommand(p => AddAsync(p as NearbyPeerViewModel), p => p is NearbyPeerViewModel);
        }

        /// <summary>Called by MainViewModel (on the UI thread) when the service sends a new NearbyList.</summary>
        public void Replace(List<NearbyPeerPayload> peers)
        {
            Peers.Clear();
            foreach (var p in peers) Peers.Add(new NearbyPeerViewModel(p));
            OnPropertyChanged(nameof(IsEmpty));
        }

        /// <summary>Asks the service for the current list (e.g. when the window opens).</summary>
        public async Task RefreshAsync()
        {
            try { await _pipe.SendAsync(PipeMessage.Create(PipeMessageType.GetNearby, new { })); }
            catch (Exception ex) { _logger.LogWarning(ex, "Nearby list request failed"); }
        }

        private async Task AddAsync(NearbyPeerViewModel? peer)
        {
            if (peer == null) return;
            try
            {
                await _pipe.SendAsync(PipeMessage.Create(PipeMessageType.AddNearbyPeer,
                    new AddNearbyPeerPayload(peer.PeerId)));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Adding nearby peer {PeerId} failed", peer.PeerId);
            }
        }
    }

    public class NearbyPeerViewModel(NearbyPeerPayload peer)
    {
        public string PeerId { get; } = peer.PeerId;
        public string DisplayName { get; } = peer.DisplayName;
        public string Address { get; } = $"{peer.IpAddress}:{peer.Port}";
        public string FirstLetter { get; } =
            string.IsNullOrEmpty(peer.DisplayName) ? "?" : peer.DisplayName[0].ToString().ToUpper();
    }
}
