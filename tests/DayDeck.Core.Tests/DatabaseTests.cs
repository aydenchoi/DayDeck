using DayDeck.Core.Data;

namespace DayDeck.Core.Tests;

public sealed class DatabaseTests : IDisposable
{
    private readonly TempDirectory _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void Migrate_CreatesSchemaAndSetsLatestVersion()
    {
        var db = new Database(_dir.Combine("sub", "data.db"));

        db.Migrate();

        Assert.Equal(Migrations.Latest, db.GetSchemaVersion());
        using var connection = db.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type IN ('table') ORDER BY name";
        var tables = new List<string>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                tables.Add(reader.GetString(0));
            }
        }

        foreach (var expected in new[] { "lists", "items", "occurrences", "reminders", "tags", "item_tags", "notes_index", "items_fts" })
        {
            Assert.Contains(expected, tables);
        }
    }

    [Fact]
    public void Migrate_IsIdempotent()
    {
        var db = new Database(_dir.Combine("data.db"));

        db.Migrate();
        db.Migrate();

        Assert.Equal(Migrations.Latest, db.GetSchemaVersion());
    }

    [Fact]
    public void Migrate_UsesRollbackJournalSoDataLivesInSingleFile()
    {
        var db = new Database(_dir.Combine("data.db"));
        db.Migrate();

        using var connection = db.Open();
        Assert.Equal("delete", Scalar(connection, "PRAGMA journal_mode;"));
    }

    [Fact]
    public void Migrate_ConvertsExistingWalDatabase()
    {
        var path = _dir.Combine("data.db");
        using (var legacy = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path};Pooling=False"))
        {
            legacy.Open();
            Assert.Equal("wal", Scalar(legacy, "PRAGMA journal_mode = WAL;"));
        }

        var db = new Database(path);
        db.Migrate();

        using var connection = db.Open();
        Assert.Equal("delete", Scalar(connection, "PRAGMA journal_mode;"));
    }

    [Fact]
    public void AfterWrites_NoSidecarFilesRemain()
    {
        var db = new Database(_dir.Combine("data.db"));
        db.Migrate();
        using (var connection = db.Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "INSERT INTO lists(name) VALUES ('업무')";
            command.ExecuteNonQuery();
        }

        // 연결이 열려 있어도 커밋 후에는 data.db 하나만 있어야 복사만으로 옮길 수 있다.
        Assert.Equal(["data.db"], Directory.GetFiles(_dir.Path).Select(Path.GetFileName));
    }

    [Fact]
    public void Migrate_RejectsNewerDatabase()
    {
        var db = new Database(_dir.Combine("data.db"));
        db.Migrate();
        using (var connection = db.Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = $"PRAGMA user_version = {Migrations.Latest + 1};";
            command.ExecuteNonQuery();
        }

        Assert.Throws<InvalidOperationException>(db.Migrate);
    }

    [Fact]
    public void FullTextSearch_TracksItemInsertUpdateDelete()
    {
        var db = new Database(_dir.Combine("data.db"));
        db.Migrate();
        using var connection = db.Open();

        Exec("INSERT INTO items(id, kind, title, notes, created_at, updated_at) VALUES (1, 0, '분기 보고서 제출', NULL, '2026-10-28T09:00', '2026-10-28T09:00')");
        Assert.Equal(1L, Count("보고서"));

        Exec("UPDATE items SET title = '디자인 리뷰' WHERE id = 1");
        Assert.Equal(0L, Count("보고서"));
        Assert.Equal(1L, Count("디자인"));

        Exec("DELETE FROM items WHERE id = 1");
        Assert.Equal(0L, Count("디자인"));

        void Exec(string sql)
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }

        long Count(string term)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT count(*) FROM items_fts WHERE items_fts MATCH $term";
            command.Parameters.AddWithValue("$term", term + "*");
            return (long)command.ExecuteScalar()!;
        }
    }

    [Fact]
    public void ForeignKeys_AreEnforced()
    {
        var db = new Database(_dir.Combine("data.db"));
        db.Migrate();
        using var connection = db.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO reminders(item_id, offset_minutes) VALUES (999, -10)";

        Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => command.ExecuteNonQuery());
    }

    private static string Scalar(Microsoft.Data.Sqlite.SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (string)command.ExecuteScalar()!;
    }
}
