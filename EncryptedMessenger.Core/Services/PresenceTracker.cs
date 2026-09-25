namespace EncryptedMessenger.Core.Services
{
    /// <summary>
    /// In-memory online/offline bookkeeping for contacts.
    ///
    /// A contact is online while it has at least one live TCP connection (inbound or
    /// outbound) OR a discovery announcement was heard within <see cref="DiscoveryTimeout"/>.
    /// The timeout is what turns a peer that vanished without closing its connections
    /// (Wi-Fi drop, sleep, crash) back to offline.
    ///
    /// <see cref="TryChangeReported"/> remembers what the UI was last told, so callers
    /// broadcast only real transitions instead of re-announcing "online" every 5 seconds.
    /// </summary>
    public sealed class PresenceTracker(TimeSpan discoveryTimeout)
    {
        public TimeSpan DiscoveryTimeout { get; } = discoveryTimeout;

        private readonly object _lock = new();
        private readonly Dictionary<string, int> _connections = [];
        private readonly Dictionary<string, DateTime> _lastHeard = [];
        private readonly Dictionary<string, bool> _reported = [];

        public void ConnectionOpened(string contactId)
        {
            lock (_lock) _connections[contactId] = _connections.GetValueOrDefault(contactId) + 1;
        }

        public void ConnectionClosed(string contactId)
        {
            lock (_lock)
            {
                var count = _connections.GetValueOrDefault(contactId) - 1;
                if (count > 0) _connections[contactId] = count;
                else _connections.Remove(contactId);
            }
        }

        public void Heard(string contactId)
        {
            lock (_lock) _lastHeard[contactId] = DateTime.UtcNow;
        }

        public bool IsOnline(string contactId)
        {
            lock (_lock) return IsOnlineLocked(contactId);
        }

        /// <summary>
        /// Returns true (and records the new state) if the contact's current presence differs
        /// from what was last reported to the UI — i.e. the caller should broadcast it.
        /// </summary>
        public bool TryChangeReported(string contactId, out bool isOnline)
        {
            lock (_lock)
            {
                isOnline = IsOnlineLocked(contactId);
                if (_reported.TryGetValue(contactId, out var last) && last == isOnline) return false;
                _reported[contactId] = isOnline;
                return true;
            }
        }

        /// <summary>Records a state that was broadcast directly (e.g. after an explicit connect attempt).</summary>
        public void SetReported(string contactId, bool isOnline)
        {
            lock (_lock) _reported[contactId] = isOnline;
        }

        /// <summary>All contacts whose presence is being tracked (for the periodic timeout sweep).</summary>
        public List<string> KnownContacts()
        {
            lock (_lock) return [.. _reported.Keys.Union(_lastHeard.Keys).Union(_connections.Keys)];
        }

        private bool IsOnlineLocked(string contactId)
            => _connections.ContainsKey(contactId)
               || (_lastHeard.TryGetValue(contactId, out var heard) && DateTime.UtcNow - heard < DiscoveryTimeout);
    }
}
