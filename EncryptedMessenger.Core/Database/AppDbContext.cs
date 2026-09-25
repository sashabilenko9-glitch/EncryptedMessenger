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
                e.Property(c => c.State).HasConversion<int>();
            });
        }

        /// <summary>
        /// Creates the database if it doesn't exist, then upgrades an older schema in place.
        /// Call once at application startup.
        ///
        /// EnsureCreated() only builds a MISSING database — on an existing file it does nothing,
        /// so columns added to the model later would be absent and every query touching them
        /// would fail. <see cref="UpgradeSchema"/> adds such columns by hand. (EF migrations
        /// automate exactly this; they'd be the next step if the schema keeps growing.)
        /// </summary>
        public void EnsureCreated()
        {
            Database.EnsureCreated();
            UpgradeSchema();
        }

        /// <summary>
        /// Each entry: table, column, and the SQL type/default to add it with. Adding is
        /// idempotent — a column that already exists (new DB, or already upgraded) is skipped.
        /// </summary>
        private static readonly (string Table, string Column, string Definition)[] AddedColumns =
        [
            // DEFAULT 1 = ContactState.Accepted: everyone who was a contact before the
            // request/accept flow existed stays a contact.
            ("Contacts", "State",    "INTEGER NOT NULL DEFAULT 1"),
            ("Contacts", "Verified", "INTEGER NOT NULL DEFAULT 0"),
        ];

        private void UpgradeSchema()
        {
            var connection = Database.GetDbConnection();
            var openedHere = connection.State != System.Data.ConnectionState.Open;
            if (openedHere) connection.Open();
            try
            {
                foreach (var (table, column, definition) in AddedColumns)
                {
                    if (ColumnExists(connection, table, column)) continue;
                    using var alter = connection.CreateCommand();
                    // Names come from the constant list above, never from user input.
                    alter.CommandText = $"ALTER TABLE \"{table}\" ADD COLUMN \"{column}\" {definition}";
                    alter.ExecuteNonQuery();
                }
            }
            finally
            {
                if (openedHere) connection.Close();
            }
        }

        private static bool ColumnExists(System.Data.Common.DbConnection connection, string table, string column)
        {
            using var cmd = connection.CreateCommand();
            // pragma_table_info lists a table's columns, one row each; "name" is the column name.
            cmd.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = $column";
            var p = cmd.CreateParameter();
            p.ParameterName = "$column";
            p.Value = column;
            cmd.Parameters.Add(p);
            return Convert.ToInt64(cmd.ExecuteScalar()) > 0;
        }

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