using EncryptedMessenger.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace EncryptedMessenger.Core.Database
{
    /// <summary>
    /// All database operations related to <see cref="Contact"/>.
    /// Every call goes through <see cref="AppDbContext.RunAsync{T}"/> so concurrent
    /// access from discovery / pipe / messaging threads is serialised.
    /// </summary>
    public sealed class ContactRepository(AppDbContext db)
    {
        /// <summary>
        /// Applies a discovery announcement to an ALREADY KNOWN peer (discovery never creates
        /// contacts — unknown peers only appear in the in-memory "nearby" list). Discovery
        /// repeats every few seconds, so this only writes when name/address changed or
        /// <see cref="Contact.LastSeen"/> is older than <paramref name="lastSeenResolution"/>.
        ///
        /// The announced name only replaces a placeholder name. A name the contact already has
        /// (typed when adding by IP, or from their request) is kept: UDP announcements aren't
        /// authenticated, so anyone on the LAN could otherwise rename a known contact.
        /// Returns the peer's state (null = unknown) and whether name/address changed.
        /// </summary>
        public Task<(ContactState? State, bool Changed)> ApplyDiscoveryAsync(Contact announced, TimeSpan lastSeenResolution)
            => db.RunAsync(async d =>
            {
                var existing = await d.Contacts.FindAsync(announced.Id);
                if (existing == null) return ((ContactState?)null, false);

                var takeName = existing.DisplayName == Contact.PlaceholderName(existing.Id)
                               && !string.IsNullOrWhiteSpace(announced.DisplayName)
                               && existing.DisplayName != announced.DisplayName;
                var changed = takeName
                              || existing.IpAddress != announced.IpAddress
                              || existing.Port != announced.Port;
                if (!changed && announced.LastSeen - existing.LastSeen < lastSeenResolution)
                    return (existing.State, false);

                if (takeName) existing.DisplayName = announced.DisplayName;
                existing.IpAddress = announced.IpAddress;
                existing.Port = announced.Port;
                existing.LastSeen = announced.LastSeen;
                await d.SaveChangesAsync();
                return (existing.State, changed);
            });

        /// <summary>
        /// Creates the contact if unknown, or fills in its address if it has none yet
        /// (e.g. created from an incoming message while discovery was off). An address that
        /// is already known is left alone — the inbound handshake is not authenticated.
        /// Returns true if a contact was created or its address was filled in.
        /// </summary>
        public Task<bool> EnsureWithAddressAsync(string contactId, string ipAddress, int port)
            => db.RunAsync(async d =>
            {
                var c = await d.Contacts.FindAsync(contactId);
                if (c == null)
                {
                    d.Contacts.Add(new Contact
                    {
                        Id = contactId,
                        DisplayName = Contact.PlaceholderName(contactId),
                        IpAddress = ipAddress,
                        Port = port,
                        LastSeen = DateTime.UtcNow
                    });
                }
                else if (string.IsNullOrEmpty(c.IpAddress))
                {
                    c.IpAddress = ipAddress;
                    c.Port = port;
                }
                else
                {
                    return false;
                }
                await d.SaveChangesAsync();
                return true;
            });

        /// <summary>
        /// Key pinning with trust-on-first-use, as ONE atomic step:
        /// no key on file yet → pin <paramref name="publicKeyXml"/> and trust it (creating a
        /// placeholder contact if the id is unknown); key on file → trust only if identical.
        ///
        /// Atomic because "check, then store" as two separate calls would let two first-time
        /// handshakes for the same id (say, the real peer and an impostor) both see "no key
        /// yet" and both be trusted. Inside one RunAsync the second one sees the first's pin.
        /// </summary>
        public Task<bool> PinOrVerifyKeyAsync(string contactId, string publicKeyXml)
            => db.RunAsync(async d =>
            {
                var c = await d.Contacts.FindAsync(contactId);
                if (c == null)
                {
                    d.Contacts.Add(new Contact
                    {
                        Id = contactId,
                        DisplayName = Contact.PlaceholderName(contactId),
                        PublicKeyXml = publicKeyXml,
                        LastSeen = DateTime.UtcNow
                    });
                    await d.SaveChangesAsync();
                    return true;
                }

                if (string.IsNullOrEmpty(c.PublicKeyXml))
                {
                    c.PublicKeyXml = publicKeyXml;
                    await d.SaveChangesAsync();
                    return true;
                }

                return c.PublicKeyXml == publicKeyXml;
            });

        public Task<List<Contact>> GetAllAsync()
            => db.RunAsync(d => d.Contacts.OrderBy(c => c.DisplayName).ToListAsync());

        /// <summary>The contact list proper: only people the user added or accepted.</summary>
        public Task<List<Contact>> GetAcceptedAsync()
            => db.RunAsync(d => d.Contacts
                                  .Where(c => c.State == ContactState.Accepted)
                                  .OrderBy(c => c.DisplayName)
                                  .ToListAsync());

        /// <summary>Contacts with a pending request in either direction.</summary>
        public Task<List<Contact>> GetRequestsAsync()
            => db.RunAsync(d => d.Contacts
                                  .Where(c => c.State == ContactState.IncomingRequest
                                           || c.State == ContactState.OutgoingRequest)
                                  .OrderBy(c => c.DisplayName)
                                  .ToListAsync());

        // ── Contact-request state machine ─────────────────────────────────
        //
        //   Stranger ──we ask──▶ OutgoingRequest ──they accept──▶ Accepted
        //   Stranger ──they ask─▶ IncomingRequest ──we accept───▶ Accepted
        //   both ask each other  ─────────────────────────────────▶ Accepted
        //   decline / cancel ──▶ back to Stranger
        //
        // Every transition is one atomic compare-and-set inside RunAsync, so two events racing
        // (our click vs. their packet) can't overwrite each other's result.

        /// <summary>
        /// We send a request to <paramref name="target"/> (new peer from "nearby", a manual
        /// contact, or a known stranger). Creates the row if needed and fills in name/address.
        /// Returns the resulting state: OutgoingRequest, or Accepted if they had already asked
        /// us (mutual request), or unchanged if we're already contacts / already asked.
        /// </summary>
        public Task<ContactState> MarkRequestSentAsync(Contact target)
            => db.RunAsync(async d =>
            {
                var c = await d.Contacts.FindAsync(target.Id);
                if (c == null)
                {
                    target.State = ContactState.OutgoingRequest;
                    d.Contacts.Add(target);
                    await d.SaveChangesAsync();
                    return target.State;
                }

                if (!string.IsNullOrEmpty(target.IpAddress))
                {
                    c.IpAddress = target.IpAddress;
                    c.Port = target.Port;
                }
                if (c.DisplayName == Contact.PlaceholderName(c.Id) && !string.IsNullOrWhiteSpace(target.DisplayName))
                    c.DisplayName = target.DisplayName;

                c.State = c.State switch
                {
                    ContactState.Stranger        => ContactState.OutgoingRequest,
                    ContactState.IncomingRequest => ContactState.Accepted,
                    _                            => c.State
                };
                await d.SaveChangesAsync();
                return c.State;
            });

        /// <summary>
        /// <paramref name="contactId"/> sent us a request. Returns (old, new) state:
        /// Stranger → IncomingRequest; OutgoingRequest → Accepted (mutual); otherwise unchanged.
        /// The requester's name replaces a placeholder name.
        /// </summary>
        public Task<(ContactState Old, ContactState New)> MarkRequestReceivedAsync(string contactId, string displayName)
            => db.RunAsync(async d =>
            {
                var c = await d.Contacts.FindAsync(contactId);
                if (c == null)
                {
                    // Normally the handshake already created a Stranger row when pinning the key.
                    c = new Contact { Id = contactId, DisplayName = Contact.PlaceholderName(contactId), LastSeen = DateTime.UtcNow };
                    d.Contacts.Add(c);
                }
                if (c.DisplayName == Contact.PlaceholderName(c.Id) && !string.IsNullOrWhiteSpace(displayName))
                    c.DisplayName = displayName.Trim();

                var old = c.State;
                c.State = old switch
                {
                    ContactState.Stranger        => ContactState.IncomingRequest,
                    ContactState.OutgoingRequest => ContactState.Accepted,
                    _                            => old
                };
                await d.SaveChangesAsync();
                return (old, c.State);
            });

        /// <summary>
        /// Atomic compare-and-set: moves the contact from <paramref name="from"/> to
        /// <paramref name="to"/> only if it is currently in <paramref name="from"/>.
        /// Returns whether the transition happened.
        /// </summary>
        public Task<bool> TransitionAsync(string contactId, ContactState from, ContactState to)
            => db.RunAsync(async d =>
            {
                var c = await d.Contacts.FindAsync(contactId);
                if (c == null || c.State != from) return false;
                c.State = to;
                await d.SaveChangesAsync();
                return true;
            });

        public Task<Contact?> GetByIdAsync(string id)
            => db.RunAsync(async d => await d.Contacts.FindAsync(id));

        public Task<Contact?> GetByIpAsync(string ip)
            => db.RunAsync(d => d.Contacts.FirstOrDefaultAsync(c => c.IpAddress == ip));

        /// <summary>
        /// Re-pins a contact to a different public key (after the user accepted a changed key),
        /// without touching display name / IP / port. Also clears <see cref="Contact.Verified"/>: that confirmation was about the OLD key,
        /// and silently carrying it over would vouch for a key nobody compared.
        /// </summary>
        public Task SetPublicKeyXmlAsync(string contactId, string publicKeyXml)
            => db.RunAsync(async d =>
            {
                var c = await d.Contacts.FindAsync(contactId);
                if (c == null) return;
                if (c.PublicKeyXml != publicKeyXml) c.Verified = false;
                c.PublicKeyXml = publicKeyXml;
                await d.SaveChangesAsync();
            });

        /// <summary>
        /// Marks the contact verified, but only if its pinned key still has the verification
        /// code the user actually compared. Returns false otherwise (no key yet, or the key
        /// changed between showing the code and the click).
        /// </summary>
        public Task<bool> MarkVerifiedAsync(string contactId, string comparedCode, Func<string, string> codeForKey)
            => db.RunAsync(async d =>
            {
                var c = await d.Contacts.FindAsync(contactId);
                if (c == null || string.IsNullOrEmpty(c.PublicKeyXml) || codeForKey(c.PublicKeyXml) != comparedCode)
                    return false;
                c.Verified = true;
                await d.SaveChangesAsync();
                return true;
            });

        public Task UpdateLastSeenAsync(string contactId)
            => db.RunAsync(async d =>
            {
                var c = await d.Contacts.FindAsync(contactId);
                if (c == null) return;
                c.LastSeen = DateTime.UtcNow;
                await d.SaveChangesAsync();
            });

        public Task DeleteAsync(string contactId)
            => db.RunAsync(async d =>
            {
                var c = await d.Contacts.FindAsync(contactId);
                if (c == null) return;
                d.Contacts.Remove(c);
                await d.SaveChangesAsync();
            });
        /// <summary>
        /// Re-links a manually-added contact ("manual_ip_port") to the real
        /// peer UserId discovered during the TCP handshake, so both sides share
        /// the same ConversationId. Returns the id to use afterwards.
        /// </summary>
        public Task<string> MergeManualContactAsync(string oldId, string realUserId)
            => db.RunAsync(async d =>
            {
                if (oldId == realUserId) return realUserId;

                var manual = await d.Contacts.FindAsync(oldId);
                if (manual == null) return realUserId;

                var real = await d.Contacts.FindAsync(realUserId);
                if (real != null)
                {
                    real.IpAddress = manual.IpAddress;
                    real.Port = manual.Port;
                    real.LastSeen = DateTime.UtcNow;
                    // The real contact may be a stub created moments ago when its key was
                    // pinned during this very handshake — keep the name the user typed.
                    if (real.DisplayName == Contact.PlaceholderName(real.Id))
                        real.DisplayName = manual.DisplayName;
                    // Same for the relationship: a Stranger stub takes over what the user
                    // decided for the manual entry.
                    if (real.State == ContactState.Stranger)
                        real.State = manual.State;
                    d.Contacts.Remove(manual);
                    await d.SaveChangesAsync();
                    return realUserId;
                }

                d.Contacts.Add(new Contact
                {
                    Id = realUserId,
                    DisplayName = manual.DisplayName,
                    IpAddress = manual.IpAddress,
                    Port = manual.Port,
                    PublicKeyXml = manual.PublicKeyXml,
                    LastSeen = DateTime.UtcNow,
                    State = manual.State,
                    Verified = manual.Verified
                });
                d.Contacts.Remove(manual);
                await d.SaveChangesAsync();
                return realUserId;
            });
    }
}