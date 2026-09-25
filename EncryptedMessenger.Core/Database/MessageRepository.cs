using EncryptedMessenger.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace EncryptedMessenger.Core.Database
{
    /// <summary>
    /// All database operations related to <see cref="Message"/>.
    /// Every call goes through <see cref="AppDbContext.RunAsync{T}"/> so concurrent
    /// access from discovery / pipe / messaging threads is serialised.
    /// </summary>
    public sealed class MessageRepository(AppDbContext db)
    {
        // ── Write ─────────────────────────────────────────────────────────

        public Task<Message> SaveAsync(Message message)
            => db.RunAsync(async d =>
            {
                if (message.Id == 0)
                    d.Messages.Add(message);
                else
                    d.Messages.Update(message);

                await d.SaveChangesAsync();
                return message;
            });

        public Task UpdateStatusAsync(string messageId, MessageStatus status)
            => db.RunAsync(async d =>
            {
                var msg = await d.Messages.FirstOrDefaultAsync(m => m.MessageId == messageId);
                if (msg == null) return;
                msg.Status = status;
                await d.SaveChangesAsync();
            });

        // ── Read ──────────────────────────────────────────────────────────

        /// <summary>Incoming messages from <paramref name="senderId"/> whose read receipt is still unsent, oldest first.</summary>
        public Task<List<Message>> GetPendingReadAcksAsync(string senderId)
            => db.RunAsync(d => d.Messages
                                  .Where(m => !m.IsOutgoing
                                           && m.SenderId == senderId
                                           && m.Status == MessageStatus.ReadAckPending)
                                  .OrderBy(m => m.Timestamp)
                                  .ToListAsync());

        public Task<Message?> GetByMessageIdAsync(string messageId)
            => db.RunAsync(d => d.Messages.FirstOrDefaultAsync(m => m.MessageId == messageId));

        /// <summary>
        /// Returns the <paramref name="take"/> most recent messages of a conversation
        /// (skipping the newest <paramref name="skip"/>, for paging further back),
        /// ordered oldest first for display.
        /// </summary>
        public Task<List<Message>> GetByConversationAsync(string conversationId, int skip = 0, int take = 50)
            => db.RunAsync(async d =>
            {
                var newestFirst = await d.Messages
                                         .Where(m => m.ConversationId == conversationId)
                                         .OrderByDescending(m => m.Timestamp)
                                         .ThenByDescending(m => m.Id)
                                         .Skip(skip)
                                         .Take(take)
                                         .ToListAsync();
                newestFirst.Reverse();
                return newestFirst;
            });

        // ── Helper (no DB access – stays synchronous and static) ──────────

        /// <summary>Builds the shared conversation ID from two participant IDs.</summary>
        public static string ConversationId(string idA, string idB)
            => string.Compare(idA, idB, StringComparison.Ordinal) < 0
                ? $"{idA}_{idB}"
                : $"{idB}_{idA}";
    }
}