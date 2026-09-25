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
        public Task<Contact> UpsertAsync(Contact contact)
            => db.RunAsync(async d =>
            {
                var existing = await d.Contacts.FindAsync(contact.Id);
                if (existing == null)
                {
                    d.Contacts.Add(contact);
                }
                else
                {
                    existing.DisplayName = contact.DisplayName;
                    existing.IpAddress = contact.IpAddress;
                    existing.Port = contact.Port;
                    existing.LastSeen = contact.LastSeen;
                    if (contact.PublicKeyXml != null)
                        existing.PublicKeyXml = contact.PublicKeyXml;
                }
                await d.SaveChangesAsync();
                return existing ?? contact;
            });

        /// <summary>
        /// Applies a discovery announcement to an ALREADY KNOWN peer (discovery never creates
        /// contacts — unknown peers only appear in the in-memory "nearby" list). Discovery
        /// repeats every few seconds, so this only writes when name/address changed or
        /// <see cref="Contact.LastSeen"/> is older than <paramref name="lastSeenResolution"/>.
        /// Returns the peer's state (null = unknown) and whether name/address changed.
        /// </summary>
        public Task<(ContactState? State, bool Changed)> ApplyDiscoveryAsync(Contact announced, TimeSpan lastSeenResolution)
            => db.RunAsync(async d =>
            {
                var existing = await d.Contacts.FindAsync(announced.Id);
                if (existing == null) return ((ContactState?)null, false);

                var changed = existing.DisplayName != announced.DisplayName
                              || existing.IpAddress != announced.IpAddress
                              || existing.Port != announced.Port;
                if (!changed && announced.LastSeen - existing.LastSeen < lastSeenResolution)
                    return (existing.State, false);

                existing.DisplayName = announced.DisplayName;
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

        /// <summary>
        /// Makes <paramref name="contact"/> an accepted contact: creates it, or — if the peer is
        /// already known (a Stranger row from a pinned key) — fills in name/address and promotes it.
        /// </summary>
        public Task AddAcceptedAsync(Contact contact)
            => db.RunAsync(async d =>
            {
                var existing = await d.Contacts.FindAsync(contact.Id);
                if (existing == null)
                {
                    contact.State = ContactState.Accepted;
                    d.Contacts.Add(contact);
                }
                else
                {
                    existing.DisplayName = contact.DisplayName;
                    existing.IpAddress = contact.IpAddress;
                    existing.Port = contact.Port;
                    existing.LastSeen = contact.LastSeen;
                    existing.State = ContactState.Accepted;
                }
                await d.SaveChangesAsync();
            });

        /// <summary>Promotes a known peer to an accepted contact. Returns false if unknown or already accepted.</summary>
        public Task<bool> AcceptAsync(string contactId)
            => db.RunAsync(async d =>
            {
                var c = await d.Contacts.FindAsync(contactId);
                if (c == null || c.State == ContactState.Accepted) return false;
                c.State = ContactState.Accepted;
                await d.SaveChangesAsync();
                return true;
            });

        public Task<Contact?> GetByIdAsync(string id)
            => db.RunAsync(async d => await d.Contacts.FindAsync(id));

        public Task<Contact?> GetByIpAsync(string ip)
            => db.RunAsync(d => d.Contacts.FirstOrDefaultAsync(c => c.IpAddress == ip));

        /// <summary>
        /// Updates just the stored public key for a contact, without touching
        /// display name / IP / port (unlike <see cref="UpsertAsync"/>, which would
        /// overwrite them with whatever partial <see cref="Contact"/> is passed in).
        /// </summary>
        public Task SetPublicKeyXmlAsync(string contactId, string publicKeyXml)
            => db.RunAsync(async d =>
            {
                var c = await d.Contacts.FindAsync(contactId);
                if (c == null) return;
                c.PublicKeyXml = publicKeyXml;
                await d.SaveChangesAsync();
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