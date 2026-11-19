using Microsoft.Data.Sqlite;
using DayDeck.Core.Data;

namespace DayDeck.Core.Todo;

/// <summary>
/// 할 일과 목록의 저장/조회. 모든 변경은 즉시 커밋되고 <see cref="Changed"/>를 발생시킨다.
/// 스마트 목록은 SQL로 후보를 좁힌 뒤 <see cref="TaskRules"/>로 최종 판정한다 (화면과 같은 규칙).
/// </summary>
public sealed class TaskRepository
{
    private const string SelectColumns = """
        SELECT i.id, i.kind, i.list_id, i.parent_id, i.title, i.notes, i.priority, i.due_at, i.my_day,
               i.status, i.completed_at, i.sort_order, i.created_at, i.updated_at, i.deleted_at,
               l.name, l.color,
               (SELECT count(*) FROM items c WHERE c.parent_id = i.id AND c.deleted_at IS NULL),
               (SELECT count(*) FROM items c WHERE c.parent_id = i.id AND c.deleted_at IS NULL AND c.status <> 0),
               (SELECT min(r.next_fire_at) FROM reminders r WHERE r.item_id = i.id AND r.next_fire_at IS NOT NULL),
               i.rrule,
               (SELECT group_concat(t.name, char(31)) FROM item_tags it JOIN tags t ON t.id = it.tag_id WHERE it.item_id = i.id)
        FROM items i LEFT JOIN lists l ON l.id = i.list_id
        """;

    private const string TopLevelTodos = "i.deleted_at IS NULL AND i.kind = 0 AND i.parent_id IS NULL";

    private readonly Database _db;
    private readonly TimeProvider _time;

    public TaskRepository(Database db, TimeProvider time)
    {
        _db = db;
        _time = time;
    }

    /// <summary>할 일이나 목록이 바뀌면 발생한다. 호출한 스레드에서 실행된다.</summary>
    public event EventHandler? Changed;

    private DateTime Now => _time.GetLocalNow().DateTime;

    // ---------- 조회 ----------

    public IReadOnlyList<TaskItem> GetToday()
    {
        var now = Now;
        var today = DateOnly.FromDateTime(now);
        var items = Query(
            $"WHERE {TopLevelTodos} AND (i.due_at < $tomorrow OR i.my_day = $today) AND (i.status = 0 OR i.completed_at >= $today)",
            ("$today", DbTime.Date(today)), ("$tomorrow", DbTime.Date(today.AddDays(1))));
        return items.Where(i => TaskRules.IsInToday(i, now)).ToList();
    }

    /// <summary>오늘부터 <paramref name="days"/>일 안에 마감인 미완료 할 일.</summary>
    public IReadOnlyList<TaskItem> GetUpcoming(int days = TaskRules.UpcomingDays)
    {
        var now = Now;
        var today = DateOnly.FromDateTime(now);
        var items = Query(
            $"WHERE {TopLevelTodos} AND i.status = 0 AND i.due_at >= $today AND i.due_at < $end",
            ("$today", DbTime.Date(today)), ("$end", DbTime.Date(today.AddDays(days + 1))));
        return TaskRules.OrderOpen(items.Where(i => TaskRules.IsUpcoming(i, now, days)), now).ToList();
    }

    /// <summary>기한이 지난 미완료 할 일. <paramref name="days"/>를 주면 마감이 최근 그 일수 안(오늘 − days일부터)인 것만.</summary>
    public IReadOnlyList<TaskItem> GetOverdue(int? days = null)
    {
        var now = Now;
        var today = DateOnly.FromDateTime(now);
        var items = Query(
            $"WHERE {TopLevelTodos} AND i.status = 0 AND i.due_at < $tomorrow AND i.due_at >= $from",
            ("$tomorrow", DbTime.Date(today.AddDays(1))), ("$from", days is { } d ? DbTime.Date(today.AddDays(-d)) : ""));
        return TaskRules.OrderOpen(items.Where(i => TaskRules.IsOverdue(i, now)), now).ToList();
    }

    /// <summary>완료(취소 포함)한 할 일, 최근 순. <paramref name="since"/>를 주면 그날부터.</summary>
    public IReadOnlyList<TaskItem> GetCompleted(int limit = 300, DateOnly? since = null) =>
        Query($"WHERE {TopLevelTodos} AND i.status <> 0 AND i.completed_at >= $since ORDER BY i.completed_at DESC, i.id DESC LIMIT $limit",
            ("$limit", limit), ("$since", since is { } d ? DbTime.Date(d) : ""));

    /// <summary>목록의 미완료 전부와 최근 완료 <paramref name="doneLimit"/>개.</summary>
    public IReadOnlyList<TaskItem> GetByList(long listId, int doneLimit = 200)
    {
        var open = Query($"WHERE {TopLevelTodos} AND i.list_id = $list AND i.status = 0", ("$list", listId));
        var done = Query($"WHERE {TopLevelTodos} AND i.list_id = $list AND i.status <> 0 ORDER BY i.completed_at DESC, i.id DESC LIMIT $limit",
            ("$list", listId), ("$limit", doneLimit));
        return [.. open, .. done];
    }

    /// <summary>마감이 날짜 범위(<paramref name="to"/> 포함)에 있는 할 일 (달력 표시용, 완료 포함).</summary>
    public IReadOnlyList<TaskItem> GetDueBetween(DateOnly from, DateOnly to) =>
        Query($"WHERE {TopLevelTodos} AND i.due_at >= $from AND i.due_at < $to ORDER BY i.due_at, i.id",
            ("$from", DbTime.Date(from)), ("$to", DbTime.Date(to.AddDays(1))));

    /// <summary>삭제되지 않은 모든 할 일 (하위 할 일 제외, 내보내기용).</summary>
    public IReadOnlyList<TaskItem> GetAll() => Query($"WHERE {TopLevelTodos} ORDER BY i.created_at");

    public IReadOnlyList<TaskItem> GetSubtasks(long parentId) =>
        Query("WHERE i.deleted_at IS NULL AND i.parent_id = $parent ORDER BY i.sort_order, i.id", ("$parent", parentId));

    public TaskItem? Get(long id) => Query("WHERE i.id = $id", ("$id", id)).FirstOrDefault();

    /// <summary>탐색 메뉴의 개수. 기한 지남은 기간과 관계없이 전부 센다 (잊지 않도록).</summary>
    public TaskCounts GetCounts(int upcomingDays = TaskRules.UpcomingDays)
    {
        var today = GetToday();
        return new TaskCounts(
            TodayOpen: today.Count(i => i.Status == ItemStatus.Open),
            TodayDone: today.Count(i => i.Status != ItemStatus.Open),
            Overdue: GetOverdue().Count,
            Upcoming: GetUpcoming(upcomingDays).Count);
    }

    public IReadOnlyList<TodoList> GetLists()
    {
        using var connection = _db.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT l.id, l.name, l.color, l.sort_order,
                   (SELECT count(*) FROM items i WHERE i.list_id = l.id AND {TopLevelTodos} AND i.status = 0)
            FROM lists l WHERE l.is_archived = 0 ORDER BY l.sort_order, l.id
            """;
        using var reader = command.ExecuteReader();
        var lists = new List<TodoList>();
        while (reader.Read())
        {
            lists.Add(new TodoList
            {
                Id = reader.GetInt64(0),
                Name = reader.GetString(1),
                Color = reader.IsDBNull(2) ? null : reader.GetString(2),
                SortOrder = reader.GetInt32(3),
                OpenCount = reader.GetInt32(4),
            });
        }
        return lists;
    }

    // ---------- 변경 ----------

    public long Add(TaskItem item)
    {
        item.Title = ValidateTitle(item.Title);
        var now = Now;
        using var connection = _db.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO items(kind, list_id, parent_id, title, notes, priority, due_at, my_day, rrule, status, completed_at,
                              sort_order, created_at, updated_at)
            VALUES ($kind, $list, $parent, $title, $notes, $priority, $due, $myDay, $rrule, $status, $completed,
                    (SELECT coalesce(max(sort_order), 0) + 1 FROM items WHERE parent_id IS $parent AND list_id IS $list),
                    $now, $now)
            RETURNING id, sort_order
            """;
        AddEditableParameters(command, item);
        command.Parameters.AddWithValue("$kind", (int)item.Kind);
        command.Parameters.AddWithValue("$parent", (object?)item.ParentId ?? DBNull.Value);
        command.Parameters.AddWithValue("$status", (int)item.Status);
        command.Parameters.AddWithValue("$completed", item.CompletedAt is { } c ? DbTime.Stamp(c) : DBNull.Value);
        command.Parameters.AddWithValue("$now", DbTime.Stamp(now));
        using (var reader = command.ExecuteReader())
        {
            reader.Read();
            item.Id = reader.GetInt64(0);
            item.SortOrder = reader.GetInt32(1);
        }
        item.CreatedAt = item.UpdatedAt = TruncateToSeconds(now);
        if (item.Tags.Count > 0)
        {
            using var transaction = connection.BeginTransaction();
            SaveTags(connection, transaction, item.Id, NormalizeTags(item.Tags));
            transaction.Commit();
        }
        OnChanged();
        return item.Id;
    }

    /// <summary>제목, 메모, 목록, 우선순위, 마감, 오늘 할 일을 저장한다. 상태는 <see cref="SetCompleted"/>로 바꾼다.</summary>
    public void Update(TaskItem item)
    {
        item.Title = ValidateTitle(item.Title);
        var now = Now;
        Execute("""
            UPDATE items SET list_id = $list, title = $title, notes = $notes, priority = $priority,
                             due_at = $due, my_day = $myDay, rrule = $rrule, updated_at = $now
            WHERE id = $id
            """, command =>
        {
            AddEditableParameters(command, item);
            command.Parameters.AddWithValue("$id", item.Id);
            command.Parameters.AddWithValue("$now", DbTime.Stamp(now));
        });
        item.UpdatedAt = TruncateToSeconds(now);
    }

    /// <summary>
    /// 완료/미완료로 바꾼다. 반복 할 일을 완료하면 다음 회차를 새 할 일로 만든다 (Microsoft To Do 방식: 완료 기록은 남음).
    /// 완료를 취소하면, 그 사이 손대지 않은 다음 회차는 지운다.
    /// </summary>
    public void SetCompleted(long id, bool completed) => SetStatus(id, completed ? ItemStatus.Completed : ItemStatus.Open);

    /// <summary>취소: 하지 않기로 한 할 일. 완료 영역에 ⊘로 표시된다. 반복이면 다음 회차는 만든다.</summary>
    public void SetCancelled(long id, bool cancelled) => SetStatus(id, cancelled ? ItemStatus.Cancelled : ItemStatus.Open);

    private void SetStatus(long id, ItemStatus status)
    {
        var now = Now;
        var item = Get(id);
        using (var connection = _db.Open())
        using (var transaction = connection.BeginTransaction())
        {
            Run(connection, transaction, "UPDATE items SET status = $status, completed_at = $completed, updated_at = $now WHERE id = $id",
                ("$id", id), ("$status", (int)status),
                ("$completed", status == ItemStatus.Open ? DBNull.Value : DbTime.Stamp(now)), ("$now", DbTime.Stamp(now)));

            if (item is { IsRecurring: true, ParentId: null })
            {
                var nextId = Scalar(connection, transaction, "SELECT recur_next_id FROM items WHERE id = $id", ("$id", id)) as long?;
                if (status != ItemStatus.Open && item.Status == ItemStatus.Open && nextId is null)
                {
                    if (CreateNextOccurrence(connection, transaction, item, now) is { } created)
                    {
                        Run(connection, transaction, "UPDATE items SET recur_next_id = $next WHERE id = $id", ("$id", id), ("$next", created));
                    }
                }
                else if (status == ItemStatus.Open && nextId is { } next)
                {
                    // 손대지 않은(만든 뒤 수정 없음, 미완료) 다음 회차만 지운다. 이미 고쳤으면 남긴다.
                    Run(connection, transaction, """
                        DELETE FROM items WHERE id = $next AND status = 0 AND deleted_at IS NULL AND updated_at = created_at
                        """, ("$next", next));
                    Run(connection, transaction, "UPDATE items SET recur_next_id = NULL WHERE id = $id", ("$id", id));
                }
            }
            transaction.Commit();
        }
        OnChanged();
    }

    /// <summary>다음 회차를 만든다: 같은 제목·메모·목록·우선순위·태그·상대 알림·하위 할 일(미완료로), 다음 마감.</summary>
    private long? CreateNextOccurrence(SqliteConnection connection, SqliteTransaction transaction, TaskItem item, DateTime now)
    {
        var today = DateOnly.FromDateTime(now);
        if (item.Rule is not { } rule || TaskRules.NextDue(rule, item.DueDate ?? today, today) is not { } next)
        {
            return null;
        }
        var nextRule = rule.Count is { } count ? (count > 1 ? rule with { Count = count - 1 } : null) : rule;
        if (rule.Count is not null && nextRule is null)
        {
            return null;
        }

        var stamp = DbTime.Stamp(now);
        var newId = (long)Scalar(connection, transaction, """
            INSERT INTO items(kind, list_id, title, notes, priority, due_at, rrule, status, sort_order, created_at, updated_at)
            VALUES (0, $list, $title, $notes, $priority, $due, $rrule, 0,
                    (SELECT coalesce(max(sort_order), 0) + 1 FROM items WHERE parent_id IS NULL AND list_id IS $list), $now, $now)
            RETURNING id
            """,
            ("$list", (object?)item.ListId ?? DBNull.Value), ("$title", item.Title), ("$notes", (object?)item.Notes ?? DBNull.Value),
            ("$priority", (int)item.Priority), ("$due", DbTime.Due(next, item.DueTime)!), ("$rrule", nextRule!.ToString()), ("$now", stamp))!;

        Run(connection, transaction, "INSERT INTO item_tags(item_id, tag_id) SELECT $new, tag_id FROM item_tags WHERE item_id = $old",
            ("$new", newId), ("$old", item.Id));
        // 상대 알림만 옮긴다 (특정 시각 알림은 그 회차에만 해당). 시각은 알림 재계산 때 정해진다.
        Run(connection, transaction, """
            INSERT INTO reminders(item_id, offset_minutes, is_persistent)
            SELECT $new, offset_minutes, is_persistent FROM reminders WHERE item_id = $old AND offset_minutes IS NOT NULL
            """, ("$new", newId), ("$old", item.Id));
        Run(connection, transaction, """
            INSERT INTO items(kind, list_id, parent_id, title, priority, status, sort_order, created_at, updated_at)
            SELECT 0, list_id, $new, title, priority, 0, sort_order, $now, $now FROM items
            WHERE parent_id = $old AND deleted_at IS NULL
            """, ("$new", newId), ("$old", item.Id), ("$now", stamp));
        return newId;
    }

    // ---------- 순서 ----------

    /// <summary>
    /// 목록의 미완료 할 일 순서를 바꾼다 (직접 정렬): <paramref name="id"/>를 <paramref name="beforeId"/> 앞으로,
    /// null이면 맨 뒤로. 같은 목록의 미완료 할 일 전체에 1부터 다시 번호를 매긴다.
    /// </summary>
    public void MoveBefore(long id, long? beforeId)
    {
        var item = Get(id) ?? throw new ArgumentException("할 일이 없습니다.", nameof(id));
        var order = Query($"WHERE {TopLevelTodos} AND i.list_id IS $list AND i.status = 0 ORDER BY i.sort_order, i.id",
            ("$list", (object?)item.ListId ?? DBNull.Value)).Select(t => t.Id).ToList();
        order.Remove(id);
        var index = beforeId is { } before && order.IndexOf(before) is >= 0 and var at ? at : order.Count;
        order.Insert(index, id);

        using (var connection = _db.Open())
        using (var transaction = connection.BeginTransaction())
        {
            for (var i = 0; i < order.Count; i++)
            {
                Run(connection, transaction, "UPDATE items SET sort_order = $order WHERE id = $id", ("$id", order[i]), ("$order", i + 1));
            }
            transaction.Commit();
        }
        OnChanged();
    }

    /// <summary>할 일을 다른 목록으로 옮긴다 (드래그). 하위 할 일도 함께.</summary>
    public void MoveToList(long id, long? listId)
    {
        Execute("UPDATE items SET list_id = $list, updated_at = $now WHERE id = $id OR parent_id = $id", command =>
        {
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$list", (object?)listId ?? DBNull.Value);
            command.Parameters.AddWithValue("$now", DbTime.Stamp(Now));
        });
    }

    /// <summary>태그 하나를 더한다 (드래그).</summary>
    public void AddTag(long id, string tag)
    {
        var item = Get(id);
        if (item is not null && !item.Tags.Contains(tag, StringComparer.CurrentCultureIgnoreCase))
        {
            SetTags(id, [.. item.Tags, tag]);
        }
    }

    // ---------- 태그 ----------

    /// <summary>할 일의 태그를 바꾼다. 없는 태그는 만들고, 아무 데도 안 쓰는 태그는 지운다.</summary>
    public void SetTags(long id, IEnumerable<string> tags)
    {
        var names = NormalizeTags(tags);
        using (var connection = _db.Open())
        using (var transaction = connection.BeginTransaction())
        {
            SaveTags(connection, transaction, id, names);
            Run(connection, transaction, "UPDATE items SET updated_at = $now WHERE id = $id", ("$id", id), ("$now", DbTime.Stamp(Now)));
            transaction.Commit();
        }
        OnChanged();
    }

    /// <summary>쓰이고 있는 모든 태그와 미완료 할 일 수.</summary>
    public IReadOnlyList<(string Name, int OpenCount)> GetTags()
    {
        using var connection = _db.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT t.name, (SELECT count(*) FROM item_tags it JOIN items i ON i.id = it.item_id
                            WHERE it.tag_id = t.id AND {TopLevelTodos} AND i.status = 0)
            FROM tags t ORDER BY t.name
            """;
        using var reader = command.ExecuteReader();
        var list = new List<(string, int)>();
        while (reader.Read())
        {
            list.Add((reader.GetString(0), reader.GetInt32(1)));
        }
        return list;
    }

    /// <summary>태그가 붙은 미완료 할 일 전부와 최근 완료 <paramref name="doneLimit"/>개.</summary>
    public IReadOnlyList<TaskItem> GetByTag(string tag, int doneLimit = 200)
    {
        const string hasTag = "EXISTS (SELECT 1 FROM item_tags it JOIN tags t ON t.id = it.tag_id WHERE it.item_id = i.id AND t.name = $tag)";
        var open = Query($"WHERE {TopLevelTodos} AND {hasTag} AND i.status = 0", ("$tag", tag));
        var done = Query($"WHERE {TopLevelTodos} AND {hasTag} AND i.status <> 0 ORDER BY i.completed_at DESC, i.id DESC LIMIT $limit",
            ("$tag", tag), ("$limit", doneLimit));
        return [.. open, .. done];
    }

    public static IReadOnlyList<string> NormalizeTags(IEnumerable<string> tags) =>
        [.. tags.Select(t => t.Trim().TrimStart('@', '#').Trim()).Where(t => t.Length > 0).Distinct(StringComparer.CurrentCultureIgnoreCase)];

    private static void SaveTags(SqliteConnection connection, SqliteTransaction transaction, long id, IReadOnlyList<string> names)
    {
        Run(connection, transaction, "DELETE FROM item_tags WHERE item_id = $id", ("$id", id));
        foreach (var name in names)
        {
            Run(connection, transaction, "INSERT INTO tags(name) VALUES ($name) ON CONFLICT(name) DO NOTHING", ("$name", name));
            Run(connection, transaction, "INSERT OR IGNORE INTO item_tags(item_id, tag_id) SELECT $id, id FROM tags WHERE name = $name",
                ("$id", id), ("$name", name));
        }
        Run(connection, transaction, "DELETE FROM tags WHERE id NOT IN (SELECT tag_id FROM item_tags)");
    }

    private static void Run(SqliteConnection connection, SqliteTransaction transaction, string sql, params (string Name, object Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }
        command.ExecuteNonQuery();
    }

    private static object? Scalar(SqliteConnection connection, SqliteTransaction transaction, string sql, params (string Name, object Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }
        var result = command.ExecuteScalar();
        return result is DBNull ? null : result;
    }

    public void SetMyDay(long id, bool inMyDay)
    {
        var now = Now;
        Execute("UPDATE items SET my_day = $myDay, updated_at = $now WHERE id = $id", command =>
        {
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$myDay", inMyDay ? DbTime.Date(DateOnly.FromDateTime(now)) : DBNull.Value);
            command.Parameters.AddWithValue("$now", DbTime.Stamp(now));
        });
    }

    /// <summary>휴지통으로 보낸다. 하위 할 일도 같은 시각으로 표시해서 <see cref="Restore"/>로 함께 되돌린다.</summary>
    public void Delete(long id)
    {
        Execute("UPDATE items SET deleted_at = $now WHERE (id = $id OR parent_id = $id) AND deleted_at IS NULL", command =>
        {
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$now", DbTime.Stamp(Now));
        });
    }

    public void Restore(long id)
    {
        Execute("""
            UPDATE items SET deleted_at = NULL
            WHERE (id = $id OR parent_id = $id) AND deleted_at = (SELECT deleted_at FROM items WHERE id = $id)
            """, command => command.Parameters.AddWithValue("$id", id));
    }

    public long AddList(string name, string? color = null)
    {
        var trimmed = ValidateListName(name);
        using var connection = _db.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO lists(name, color, sort_order) VALUES ($name, $color, (SELECT coalesce(max(sort_order), 0) + 1 FROM lists))
            RETURNING id
            """;
        command.Parameters.AddWithValue("$name", trimmed);
        command.Parameters.AddWithValue("$color", (object?)color ?? DBNull.Value);
        var id = (long)command.ExecuteScalar()!;
        OnChanged();
        return id;
    }

    public void RenameList(long id, string name)
    {
        var trimmed = ValidateListName(name);
        Execute("UPDATE lists SET name = $name WHERE id = $id", command =>
        {
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$name", trimmed);
        });
    }

    /// <summary>목록 색 (#RRGGBB). null이면 색 없음. 목록의 할 일과 일정 표시에 쓰인다.</summary>
    public void SetListColor(long id, string? color)
    {
        if (color is not null && !System.Text.RegularExpressions.Regex.IsMatch(color, "^#[0-9A-Fa-f]{6}$"))
        {
            throw new ArgumentException("색은 #RRGGBB 형식이어야 합니다.", nameof(color));
        }
        Execute("UPDATE lists SET color = $color WHERE id = $id", command =>
        {
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$color", (object?)color?.ToUpperInvariant() ?? DBNull.Value);
        });
    }

    /// <summary>목록을 지우고 그 안의 할 일은 휴지통으로 보낸다.</summary>
    public void DeleteList(long id)
    {
        using var connection = _db.Open();
        using var transaction = connection.BeginTransaction();
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE items SET deleted_at = $now WHERE list_id = $id AND deleted_at IS NULL;
                DELETE FROM lists WHERE id = $id;
                """;
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$now", DbTime.Stamp(Now));
            command.ExecuteNonQuery();
        }
        transaction.Commit();
        OnChanged();
    }

    // ---------- 내부 ----------

    private List<TaskItem> Query(string whereAndOrder, params (string Name, object Value)[] parameters)
    {
        using var connection = _db.Open();
        using var command = connection.CreateCommand();
        command.CommandText = SelectColumns + "\n" + whereAndOrder;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        using var reader = command.ExecuteReader();
        var items = new List<TaskItem>();
        while (reader.Read())
        {
            var (dueDate, dueTime) = DbTime.ParseDue(reader.IsDBNull(7) ? null : reader.GetString(7));
            items.Add(new TaskItem
            {
                Id = reader.GetInt64(0),
                Kind = (ItemKind)reader.GetInt32(1),
                ListId = reader.IsDBNull(2) ? null : reader.GetInt64(2),
                ParentId = reader.IsDBNull(3) ? null : reader.GetInt64(3),
                Title = reader.GetString(4),
                Notes = reader.IsDBNull(5) ? null : reader.GetString(5),
                Priority = (Priority)reader.GetInt32(6),
                DueDate = dueDate,
                DueTime = dueTime,
                MyDay = DbTime.ParseDate(reader.IsDBNull(8) ? null : reader.GetString(8)),
                Status = (ItemStatus)reader.GetInt32(9),
                CompletedAt = DbTime.ParseStamp(reader.IsDBNull(10) ? null : reader.GetString(10)),
                SortOrder = reader.GetInt32(11),
                CreatedAt = DbTime.ParseStamp(reader.GetString(12))!.Value,
                UpdatedAt = DbTime.ParseStamp(reader.GetString(13))!.Value,
                DeletedAt = DbTime.ParseStamp(reader.IsDBNull(14) ? null : reader.GetString(14)),
                ListName = reader.IsDBNull(15) ? null : reader.GetString(15),
                ListColor = reader.IsDBNull(16) ? null : reader.GetString(16),
                SubtaskCount = reader.GetInt32(17),
                SubtaskDoneCount = reader.GetInt32(18),
                NextReminderAt = DbTime.ParseStamp(reader.IsDBNull(19) ? null : reader.GetString(19)),
                RRule = reader.IsDBNull(20) ? null : reader.GetString(20),
                Tags = reader.IsDBNull(21) ? [] : [.. reader.GetString(21).Split('\u001f').Order(StringComparer.CurrentCulture)],
            });
        }
        return items;
    }

    private void Execute(string sql, Action<SqliteCommand> bind)
    {
        using (var connection = _db.Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = sql;
            bind(command);
            command.ExecuteNonQuery();
        }
        OnChanged();
    }

    private static void AddEditableParameters(SqliteCommand command, TaskItem item)
    {
        command.Parameters.AddWithValue("$list", (object?)item.ListId ?? DBNull.Value);
        command.Parameters.AddWithValue("$title", item.Title);
        command.Parameters.AddWithValue("$notes", string.IsNullOrEmpty(item.Notes) ? DBNull.Value : item.Notes);
        command.Parameters.AddWithValue("$priority", (int)item.Priority);
        command.Parameters.AddWithValue("$due", (object?)DbTime.Due(item.DueDate, item.DueTime) ?? DBNull.Value);
        command.Parameters.AddWithValue("$myDay", item.MyDay is { } d ? DbTime.Date(d) : DBNull.Value);
        command.Parameters.AddWithValue("$rrule", (object?)item.RRule ?? DBNull.Value);
    }

    private static string ValidateTitle(string title)
    {
        var trimmed = title.Trim();
        return trimmed.Length > 0 ? trimmed : throw new ArgumentException("제목이 비어 있습니다.", nameof(title));
    }

    private static string ValidateListName(string name)
    {
        var trimmed = name.Trim();
        return trimmed.Length > 0 ? trimmed : throw new ArgumentException("목록 이름이 비어 있습니다.", nameof(name));
    }

    private static DateTime TruncateToSeconds(DateTime time) => new(time.Ticks - time.Ticks % TimeSpan.TicksPerSecond, time.Kind);

    private void OnChanged() => Changed?.Invoke(this, EventArgs.Empty);
}

public readonly record struct TaskCounts(int TodayOpen, int TodayDone, int Overdue, int Upcoming);
