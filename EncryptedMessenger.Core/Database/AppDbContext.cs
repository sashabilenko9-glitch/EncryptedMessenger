using EncryptedMessenger.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace EncryptedMessenger.Core.Database
{
    /// <summary>
    /// EF Core context backed by a local SQLite file.
    /// Path is set via <see cref="AppSettings.DatabaseFileName"/> (relative → portable).
    ///
    /// THREAD-SAFETY: a DbContext is NOT thread-safe. Discovery, pipe handlers and
    /// the message pipeline all touch the DB from different threads concurrently,
    /// which throws "A second operation was started on this context instance".
    /// All repository calls therefore go through <see cref="RunAsync{T}"/>, which
    /// serialises access with a single semaphore so operations run one at a time.
    /// </summary>
    public sealed class AppDbContext : DbContext
    {
        private readonly string _dbPath;
        private readonly SemaphoreSlim _gate = new(1, 1);

        public DbSet<Message> Messages { get; set; } = null!;
        public DbSet<Contact> Contacts { get; set; } = null!;

        public AppDbContext(string dbPath = AppSettings.DatabaseFileName)
        {
            _dbPath = dbPath;
            // Ensure directory exists so SQLite can create the file
            var dir = Path.GetDirectoryName(dbPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        }

        protected override void OnConfiguring(DbContextOptionsBuilder options)
        {
            options.UseSqlite($"Data Source={_dbPath}");

#if DEBUG
            options.EnableSensitiveDataLogging();
            options.EnableDetailedErrors();
#endif
        }

        protected override void OnModelCreating(ModelBuilder model)
        {
            // Messages
            model.Entity<Message>(e =>
            {
                e.HasKey(m => m.Id);
                e.HasIndex(m => m.ConversationId);
                e.HasIndex(m => m.Timestamp);
                e.Property(m => m.Status).HasConversion<int>();
            });

            // Contacts
            model.Entity<Contact>(e =>
            {
                e.HasKey(c => c.Id);
                e.HasIndex(c => c.IpAddress);
            });
        }

        /// <summary>
        /// Creates the database and applies all pending migrations.
        /// Call once at application startup.
        /// </summary>
        public void EnsureCreated() => Database.EnsureCreated();

        // ── Serialised access ─────────────────────────────────────────────

        /// <summary>
        /// Runs a DB operation under an exclusive lock so no two operations
        /// execute on this context at the same time. Use for every query/write.
        /// </summary>
        public async Task<T> RunAsync<T>(Func<AppDbContext, Task<T>> work)
        {
            await _gate.WaitAsync();
            try { return await work(this); }
            finally { _gate.Release(); }
        }

        /// <summary>Void-returning overload of <see cref="RunAsync{T}"/>.</summary>
        public async Task RunAsync(Func<AppDbContext, Task> work)
        {
            await _gate.WaitAsync();
            try { await work(this); }
            finally { _gate.Release(); }
        }

        public override void Dispose()
        {
            _gate.Dispose();
            base.Dispose();
        }

        public override async ValueTask DisposeAsync()
        {
            _gate.Dispose();
            await base.DisposeAsync();
        }
    }
}