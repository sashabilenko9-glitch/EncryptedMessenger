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

        public Task<Message?> GetByMessageIdAsync(string messageId)
            => db.RunAsync(d => d.Messages.FirstOrDefaultAsync(m => m.MessageId == messageId));

        /// <summary>Returns all messages for a conversation, oldest first.</summary>
        public Task<List<Message>> GetByConversationAsync(string conversationId, int skip = 0, int take = 50)
            => db.RunAsync(d => d.Messages
                                  .Where(m => m.ConversationId == conversationId)
                                  .OrderBy(m => m.Timestamp)
                                  .Skip(skip)
                                  .Take(take)
                                  .ToListAsync());

        /// <summary>Returns the most recent message per conversation (for contact list previews).</summary>
        public Task<Dictionary<string, Message>> GetLatestPerConversationAsync()
            => db.RunAsync(d => d.Messages
                                  .GroupBy(m => m.ConversationId)
                                  .Select(g => g.OrderByDescending(m => m.Timestamp).First())
                                  .ToDictionaryAsync(m => m.ConversationId));

        public Task<int> CountUnreadAsync(string conversationId)
            => db.RunAsync(d => d.Messages
                                  .CountAsync(m => m.ConversationId == conversationId
                                                && !m.IsOutgoing
                                                && m.Status != MessageStatus.Read));

        // ── Helper (no DB access – stays synchronous and static) ──────────

        /// <summary>Builds the shared conversation ID from two participant IDs.</summary>
        public static string ConversationId(string idA, string idB)
            => string.Compare(idA, idB, StringComparison.Ordinal) < 0
                ? $"{idA}_{idB}"
                : $"{idB}_{idA}";
    }
}