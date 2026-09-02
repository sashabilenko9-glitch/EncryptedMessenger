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

        public Task<List<Contact>> GetAllAsync()
            => db.RunAsync(d => d.Contacts.OrderBy(c => c.DisplayName).ToListAsync());

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
                    LastSeen = DateTime.UtcNow
                });
                d.Contacts.Remove(manual);
                await d.SaveChangesAsync();
                return realUserId;
            });
    }
}