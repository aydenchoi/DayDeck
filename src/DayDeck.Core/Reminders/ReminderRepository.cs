using Microsoft.Data.Sqlite;
using DayDeck.Core.Calendar;
using DayDeck.Core.Data;
using DayDeck.Core.Settings;
using DayDeck.Core.Todo;

namespace DayDeck.Core.Reminders;

/// <summary>
/// 알림 저장/조회. 다음 울릴 시각(next_fire_at)은 <see cref="ReminderRules"/>로 계산해서 저장해 두고,
/// <see cref="ReminderEngine"/>는 그 값만 보고 타이머를 건다.
/// </summary>
public sealed class ReminderRepository
{
    private const string ReminderColumns = """
        r.id, r.item_id, r.offset_minutes, r.absolute_at, r.is_persistent, r.next_fire_at, r.target_at,
        r.snoozed_until, r.last_fired_at, r.dismissed_for
        """;

    private const string SelectColumns = "SELECT " + ReminderColumns + " FROM reminders r";

    // 알림 기준: 할 일은 마감(due_at), 일정은 시작(start_at)과 반복 규칙
    private const string BaseColumns = "i.status = 0 AND i.deleted_at IS NULL, i.kind, i.due_at, i.start_at, i.is_all_day, i.rrule";

    private readonly Database _db;
    private readonly TimeProvider _time;
    private readonly Func<ReminderSettings> _settings;

    public ReminderRepository(Database db, TimeProvider time, Func<ReminderSettings> settings)
    {
        _db = db;
        _time = time;
        _settings = settings;
    }

    /// <summary>알림이 추가/삭제되었거나 다음 울릴 시각이 바뀌었다.</summary>
    public event EventHandler? Changed;

    private DateTime Now => _time.GetLocalNow().DateTime;

    public IReadOnlyList<Reminder> GetForItem(long itemId) =>
        Query("WHERE r.item_id = $item ORDER BY r.absolute_at IS NOT NULL, r.offset_minutes, r.absolute_at, r.id", ("$item", itemId));

    public Reminder? Get(long id) => Query("WHERE r.id = $id", ("$id", id)).FirstOrDefault();

    public long AddRelative(long itemId, int offsetMinutes, bool persistent = false) =>
        Add(new Reminder { ItemId = itemId, OffsetMinutes = offsetMinutes, IsPersistent = persistent });

    public long AddAbsolute(long itemId, DateTime at, bool persistent = false) =>
        Add(new Reminder { ItemId = itemId, AbsoluteAt = TruncateToMinute(at), IsPersistent = persistent });

    /// <summary>할 일에 알림이 하나도 없으면 설정의 기본 알림을 붙인다. 붙였으면 true.</summary>
    public bool AddDefaultsIfNone(long itemId)
    {
        var settings = _settings();
        if (!settings.AutoAddDefault || settings.DefaultOffsets.Length == 0 || GetForItem(itemId).Count > 0)
        {
            return false;
        }

        foreach (var offset in settings.DefaultOffsets.Distinct())
        {
            InsertAndRecompute(new Reminder { ItemId = itemId, OffsetMinutes = offset });
        }
        OnChanged();
        return true;
    }

    public void Remove(long reminderId)
    {
        Execute("DELETE FROM reminders WHERE id = $id", c => c.Parameters.AddWithValue("$id", reminderId));
        OnChanged();
    }

    /// <summary>할 일의 모든 알림을 끈질긴 알림으로 바꾸거나 되돌린다.</summary>
    public void SetPersistent(long itemId, bool persistent)
    {
        Execute("UPDATE reminders SET is_persistent = $p WHERE item_id = $item", c =>
        {
            c.Parameters.AddWithValue("$item", itemId);
            c.Parameters.AddWithValue("$p", persistent ? 1 : 0);
        });
        OnChanged();
    }

    /// <summary>
    /// 모든 알림의 다음 울릴 시각을 다시 계산한다. 할 일이 바뀌었을 때, 시작할 때, 설정이 바뀌었을 때 부른다.
    /// 바뀐 것이 있으면 <see cref="Changed"/>를 발생시킨다.
    /// </summary>
    public void RecomputeAll()
    {
        var now = Now;
        var dateOnlyTime = _settings().GetDateOnlyReminderTime();

        using var connection = _db.Open();
        using var transaction = connection.BeginTransaction();
        var rows = new List<(Reminder Reminder, ReminderBase Base)>();
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT " + ReminderColumns + ", " + BaseColumns + " FROM reminders r JOIN items i ON i.id = r.item_id";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                rows.Add((Read(reader), ReadBase(reader, 10)));
            }
        }

        var changed = 0;
        foreach (var (reminder, b) in rows)
        {
            var (target, allowPast) = ReminderRules.ComputeTarget(reminder, b, dateOnlyTime, now);
            if (ReminderRules.Recompute(reminder, b.IsActive, target, now, allowPast))
            {
                Save(connection, transaction, reminder);
                changed++;
            }
        }
        transaction.Commit();

        if (changed > 0)
        {
            OnChanged();
        }
    }

    /// <summary>지금(또는 그 전에) 울려야 하는 알림. 열린 할 일 것만.</summary>
    public IReadOnlyList<DueReminder> GetDue(DateTime now)
    {
        using var connection = _db.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT r.id, r.item_id, i.title, l.name, i.due_at, r.next_fire_at, r.is_persistent,
                   i.kind, i.is_all_day, r.offset_minutes, r.target_at, r.absolute_at, i.priority
            FROM reminders r
            JOIN items i ON i.id = r.item_id
            LEFT JOIN lists l ON l.id = i.list_id
            WHERE r.next_fire_at IS NOT NULL AND r.next_fire_at <= $now AND i.status = 0 AND i.deleted_at IS NULL
            ORDER BY r.next_fire_at, r.id
            """;
        command.Parameters.AddWithValue("$now", DbTime.Stamp(now));
        using var reader = command.ExecuteReader();
        var due = new List<DueReminder>();
        while (reader.Read())
        {
            var (dueDate, dueTime) = DbTime.ParseDue(reader.IsDBNull(4) ? null : reader.GetString(4));
            var fireAt = DbTime.ParseStamp(reader.GetString(5))!.Value;
            var isEvent = reader.GetInt32(7) == (int)ItemKind.Event;
            if (isEvent && !reader.IsDBNull(10) && reader.IsDBNull(11))
            {
                // 일정은 이번 회차의 시작 시각을 보여 준다 (알림 시각 - 오프셋).
                var start = DbTime.ParseStamp(reader.GetString(10))!.Value.AddMinutes(-(reader.IsDBNull(9) ? 0 : reader.GetInt32(9)));
                dueDate = DateOnly.FromDateTime(start);
                dueTime = reader.GetBoolean(8) ? null : TimeOnly.FromDateTime(start);
            }
            due.Add(new DueReminder(
                reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                dueDate, dueTime, fireAt, reader.GetBoolean(6),
                IsMissed: now - fireAt > ReminderRules.MissedThreshold,
                IsEvent: isEvent,
                Priority: (Priority)reader.GetInt32(12)));
        }
        return due;
    }

    /// <summary>앞으로 가장 먼저 울릴 시각. 없으면 null.</summary>
    public DateTime? GetNextFireTime()
    {
        using var connection = _db.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT min(r.next_fire_at) FROM reminders r JOIN items i ON i.id = r.item_id
            WHERE r.next_fire_at IS NOT NULL AND i.status = 0 AND i.deleted_at IS NULL
            """;
        return DbTime.ParseStamp(command.ExecuteScalar() as string);
    }

    public void MarkFired(long reminderId, DateTime now) => Update(reminderId, r =>
        ReminderRules.MarkFired(r, now, TimeSpan.FromMinutes(_settings().PersistentRepeatMinutes)));

    public void Snooze(long reminderId, DateTime until) => Update(reminderId, r => ReminderRules.Snooze(r, TruncateToMinute(until)));

    public void Dismiss(long reminderId) => Update(reminderId, ReminderRules.Dismiss);

    // ---------- 내부 ----------

    private long Add(Reminder reminder)
    {
        InsertAndRecompute(reminder);
        OnChanged();
        return reminder.Id;
    }

    private void InsertAndRecompute(Reminder reminder)
    {
        using var connection = _db.Open();
        using var transaction = connection.BeginTransaction();
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO reminders(item_id, offset_minutes, absolute_at, is_persistent) VALUES ($item, $offset, $abs, $p)
                RETURNING id
                """;
            command.Parameters.AddWithValue("$item", reminder.ItemId);
            command.Parameters.AddWithValue("$offset", (object?)reminder.OffsetMinutes ?? DBNull.Value);
            command.Parameters.AddWithValue("$abs", reminder.AbsoluteAt is { } a ? DbTime.Stamp(a) : DBNull.Value);
            command.Parameters.AddWithValue("$p", reminder.IsPersistent ? 1 : 0);
            reminder.Id = (long)command.ExecuteScalar()!;
        }

        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT " + BaseColumns + " FROM items i WHERE i.id = $item";
            command.Parameters.AddWithValue("$item", reminder.ItemId);
            using var reader = command.ExecuteReader();
            if (reader.Read())
            {
                var b = ReadBase(reader, 0);
                var (target, allowPast) = ReminderRules.ComputeTarget(reminder, b, _settings().GetDateOnlyReminderTime(), Now);
                ReminderRules.Recompute(reminder, b.IsActive, target, Now, allowPast);
            }
        }
        Save(connection, transaction, reminder);
        transaction.Commit();
    }

    private void Update(long reminderId, Action<Reminder> change)
    {
        var reminder = Get(reminderId);
        if (reminder is null)
        {
            return;
        }

        change(reminder);
        using (var connection = _db.Open())
        using (var transaction = connection.BeginTransaction())
        {
            Save(connection, transaction, reminder);
            transaction.Commit();
        }
        OnChanged();
    }

    private static void Save(SqliteConnection connection, SqliteTransaction transaction, Reminder r)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE reminders SET next_fire_at = $next, target_at = $target, snoozed_until = $snoozed,
                                 last_fired_at = $fired, dismissed_for = $dismissed, is_persistent = $p
            WHERE id = $id
            """;
        command.Parameters.AddWithValue("$id", r.Id);
        command.Parameters.AddWithValue("$next", Stamp(r.NextFireAt));
        command.Parameters.AddWithValue("$target", Stamp(r.TargetAt));
        command.Parameters.AddWithValue("$snoozed", Stamp(r.SnoozedUntil));
        command.Parameters.AddWithValue("$fired", Stamp(r.LastFiredAt));
        command.Parameters.AddWithValue("$dismissed", Stamp(r.DismissedFor));
        command.Parameters.AddWithValue("$p", r.IsPersistent ? 1 : 0);
        command.ExecuteNonQuery();

        static object Stamp(DateTime? value) => value is { } v ? DbTime.Stamp(v) : DBNull.Value;
    }

    private List<Reminder> Query(string where, params (string Name, object Value)[] parameters)
    {
        using var connection = _db.Open();
        using var command = connection.CreateCommand();
        command.CommandText = SelectColumns + "\n" + where;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }
        using var reader = command.ExecuteReader();
        var list = new List<Reminder>();
        while (reader.Read())
        {
            list.Add(Read(reader));
        }
        return list;
    }

    private static ReminderBase ReadBase(SqliteDataReader reader, int offset)
    {
        var active = reader.GetBoolean(offset);
        if (reader.GetInt32(offset + 1) == (int)ItemKind.Event && !reader.IsDBNull(offset + 3))
        {
            var start = EventRepository.ParsePoint(reader.GetString(offset + 3));
            var allDay = reader.GetBoolean(offset + 4);
            var rule = RecurrenceRule.Parse(reader.IsDBNull(offset + 5) ? null : reader.GetString(offset + 5));
            return new ReminderBase(active, DateOnly.FromDateTime(start), allDay ? null : TimeOnly.FromDateTime(start), rule);
        }

        var (dueDate, dueTime) = DbTime.ParseDue(reader.IsDBNull(offset + 2) ? null : reader.GetString(offset + 2));
        return new ReminderBase(active, dueDate, dueTime);
    }

    private static Reminder Read(SqliteDataReader reader) => new()
    {
        Id = reader.GetInt64(0),
        ItemId = reader.GetInt64(1),
        OffsetMinutes = reader.IsDBNull(2) ? null : reader.GetInt32(2),
        AbsoluteAt = Stamp(reader, 3),
        IsPersistent = reader.GetBoolean(4),
        NextFireAt = Stamp(reader, 5),
        TargetAt = Stamp(reader, 6),
        SnoozedUntil = Stamp(reader, 7),
        LastFiredAt = Stamp(reader, 8),
        DismissedFor = Stamp(reader, 9),
    };

    private static DateTime? Stamp(SqliteDataReader reader, int index) =>
        reader.IsDBNull(index) ? null : DbTime.ParseStamp(reader.GetString(index));

    private static DateTime TruncateToMinute(DateTime time) => new(time.Year, time.Month, time.Day, time.Hour, time.Minute, 0, time.Kind);

    private void Execute(string sql, Action<SqliteCommand> bind)
    {
        using var connection = _db.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        bind(command);
        command.ExecuteNonQuery();
    }

    private void OnChanged() => Changed?.Invoke(this, EventArgs.Empty);
}
