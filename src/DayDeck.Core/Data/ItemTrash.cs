using DayDeck.Core.Todo;

namespace DayDeck.Core.Data;

/// <summary>휴지통에 있는 할 일 또는 일정.</summary>
public sealed record TrashedItem(long Id, ItemKind Kind, string Title, DateTime DeletedAt, string? ListName, int SubtaskCount);

/// <summary>
/// 지운 할 일/일정 (설계 §5.9 휴지통). 삭제는 deleted_at 표시이고, 30일이 지나면 영구 삭제한다.
/// 메모 휴지통은 파일 기반이라 <see cref="Notes.NoteStore"/>가 따로 관리한다.
/// </summary>
public sealed class ItemTrash
{
    public static readonly TimeSpan Retention = TimeSpan.FromDays(30);

    private readonly Database _db;
    private readonly TimeProvider _time;

    public ItemTrash(Database db, TimeProvider time)
    {
        _db = db;
        _time = time;
    }

    public event EventHandler? Changed;

    public IReadOnlyList<TrashedItem> GetItems()
    {
        using var connection = _db.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT i.id, i.kind, i.title, i.deleted_at, l.name,
                   (SELECT count(*) FROM items c WHERE c.parent_id = i.id AND c.deleted_at = i.deleted_at)
            FROM items i LEFT JOIN lists l ON l.id = i.list_id
            WHERE i.deleted_at IS NOT NULL
              AND (i.parent_id IS NULL OR (SELECT p.deleted_at FROM items p WHERE p.id = i.parent_id) IS NOT i.deleted_at)
            ORDER BY i.deleted_at DESC, i.id DESC
            """;
        using var reader = command.ExecuteReader();
        var items = new List<TrashedItem>();
        while (reader.Read())
        {
            items.Add(new TrashedItem(reader.GetInt64(0), (ItemKind)reader.GetInt32(1), reader.GetString(2),
                DbTime.ParseStamp(reader.GetString(3))!.Value, reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetInt32(5)));
        }
        return items;
    }

    /// <summary>되돌린다 (같이 지운 하위 할 일 포함).</summary>
    public void Restore(long id)
    {
        Execute("""
            UPDATE items SET deleted_at = NULL
            WHERE (id = $id OR parent_id = $id) AND deleted_at = (SELECT deleted_at FROM items WHERE id = $id)
            """, id);
    }

    /// <summary>영구 삭제 (하위 할 일, 알림, 회차 기록도 함께).</summary>
    public void DeleteForever(long id) => Execute("DELETE FROM items WHERE (id = $id OR parent_id = $id) AND deleted_at IS NOT NULL", id);

    /// <summary>보관 기간이 지난 항목을 영구 삭제한다. 지운 개수.</summary>
    public int Purge()
    {
        var cutoff = DbTime.Stamp(_time.GetLocalNow().DateTime - Retention);
        using var connection = _db.Open();
        using var command = connection.CreateCommand();
        // 하위 할 일은 외래 키로 함께 지워져 changes()에 잡히지 않으므로 먼저 센다.
        command.CommandText = "SELECT count(*) FROM items WHERE deleted_at IS NOT NULL AND deleted_at < $cutoff";
        command.Parameters.AddWithValue("$cutoff", cutoff);
        var removed = Convert.ToInt32(command.ExecuteScalar());
        command.CommandText = "DELETE FROM items WHERE deleted_at IS NOT NULL AND deleted_at < $cutoff";
        command.ExecuteNonQuery();
        if (removed > 0)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
        return removed;
    }

    public void EmptyAll()
    {
        using (var connection = _db.Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "DELETE FROM items WHERE deleted_at IS NOT NULL";
            command.ExecuteNonQuery();
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Execute(string sql, long id)
    {
        using (var connection = _db.Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = sql;
            command.Parameters.AddWithValue("$id", id);
            command.ExecuteNonQuery();
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
