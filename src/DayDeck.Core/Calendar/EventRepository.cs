using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using DayDeck.Core.Data;
using DayDeck.Core.Todo;

namespace DayDeck.Core.Calendar;

/// <summary>
/// 달력 일정 저장/조회. 반복 일정은 원본 하나만 저장하고 조회할 때 회차를 펼친다.
/// 특정 회차만 바꾸거나 지우거나 완료한 기록은 occurrences 테이블에 둔다.
/// </summary>
public sealed class EventRepository
{
    private const string SelectColumns = """
        SELECT i.id, i.list_id, i.title, i.notes, i.location, i.color, i.is_all_day, i.start_at, i.end_at, i.rrule,
               i.status, i.completed_at, i.created_at, i.updated_at, i.deleted_at, l.name, l.color
        FROM items i LEFT JOIN lists l ON l.id = i.list_id
        """;

    private readonly Database _db;
    private readonly TimeProvider _time;

    public EventRepository(Database db, TimeProvider time)
    {
        _db = db;
        _time = time;
    }

    public event EventHandler? Changed;

    private DateTime Now => _time.GetLocalNow().DateTime;

    // ---------- 조회 ----------

    /// <summary>삭제되지 않은 모든 일정 (내보내기용).</summary>
    public IReadOnlyList<CalendarEvent> GetAll() => Query("WHERE i.kind = 1 AND i.deleted_at IS NULL ORDER BY i.start_at");

    public CalendarEvent? Get(long id) => Query("WHERE i.id = $id AND i.kind = 1", ("$id", id)).FirstOrDefault();

    /// <summary>날짜 범위(<paramref name="to"/> 포함)에 걸치는 모든 회차. 시작 시각 순.</summary>
    public IReadOnlyList<EventOccurrence> GetOccurrences(DateOnly from, DateOnly to)
    {
        var fromKey = DbTime.Date(from);
        var toExclusive = to.AddDays(1);
        var events = Query("""
            WHERE i.kind = 1 AND i.deleted_at IS NULL AND i.start_at < $to
              AND (i.rrule IS NOT NULL OR coalesce(i.end_at, i.start_at) >= $from)
            """, ("$from", fromKey), ("$to", DbTime.Date(toExclusive)));

        var overrides = LoadOverrides(events.Where(e => e.IsRecurring).Select(e => e.Id).ToList());
        var result = new List<EventOccurrence>();
        foreach (var e in events)
        {
            if (!e.IsRecurring)
            {
                var occurrence = new EventOccurrence(e, e.Start, e.End, e.Start, e.Title, e.Status != ItemStatus.Open, false);
                if (occurrence.LastDay >= from && occurrence.FirstDay <= to)
                {
                    result.Add(occurrence);
                }
                continue;
            }

            // 회차를 옮긴 경우를 위해 앞뒤로 조금 넓게 펼친 뒤 최종 시각으로 거른다.
            var rule = e.Rule!;
            var expandTo = toExclusive.AddDays(7).ToDateTime(TimeOnly.MinValue);
            foreach (var start in rule.Expand(e.Start, expandTo))
            {
                var end = start + e.Duration;
                if (DateOnly.FromDateTime(end) < from.AddDays(-7))
                {
                    continue;
                }

                var occurrence = new EventOccurrence(e, start, end, start, e.Title, false, false);
                if (overrides.TryGetValue((e.Id, start), out var o))
                {
                    if (o.Status == ItemStatus.Cancelled)
                    {
                        continue;
                    }
                    occurrence = occurrence with
                    {
                        Start = o.Start ?? start,
                        End = o.End ?? end,
                        Title = o.Title ?? e.Title,
                        IsCompleted = o.Status == ItemStatus.Completed,
                        IsException = o.Start is not null || o.End is not null || o.Title is not null,
                    };
                }

                if (occurrence.LastDay >= from && occurrence.FirstDay <= to)
                {
                    result.Add(occurrence);
                }
            }
        }

        return [.. result.OrderBy(o => o.IsAllDay ? 0 : 1).ThenBy(o => o.Start).ThenBy(o => o.Title, StringComparer.Ordinal)];
    }

    // ---------- 변경 ----------

    public long Add(CalendarEvent e)
    {
        Validate(e);
        var now = Now;
        using var connection = _db.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO items(kind, list_id, title, notes, location, color, is_all_day, start_at, end_at, rrule, status,
                              created_at, updated_at)
            VALUES (1, $list, $title, $notes, $location, $color, $allDay, $start, $end, $rrule, 0, $now, $now)
            RETURNING id
            """;
        Bind(command, e);
        command.Parameters.AddWithValue("$now", DbTime.Stamp(now));
        e.Id = (long)command.ExecuteScalar()!;
        e.CreatedAt = e.UpdatedAt = now;
        OnChanged();
        return e.Id;
    }

    /// <summary>원본(반복이면 전체 회차)을 바꾼다.</summary>
    public void Update(CalendarEvent e)
    {
        Validate(e);
        Execute("""
            UPDATE items SET list_id = $list, title = $title, notes = $notes, location = $location, color = $color,
                             is_all_day = $allDay, start_at = $start, end_at = $end, rrule = $rrule, updated_at = $now
            WHERE id = $id AND kind = 1
            """, command =>
        {
            Bind(command, e);
            command.Parameters.AddWithValue("$id", e.Id);
            command.Parameters.AddWithValue("$now", DbTime.Stamp(Now));
        });
    }

    public void SetCompleted(long id, bool completed)
    {
        var now = DbTime.Stamp(Now);
        Execute("UPDATE items SET status = $status, completed_at = $completed, updated_at = $now WHERE id = $id", command =>
        {
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$status", completed ? 1 : 0);
            command.Parameters.AddWithValue("$completed", completed ? now : DBNull.Value);
            command.Parameters.AddWithValue("$now", now);
        });
    }

    public void Delete(long id) => Execute("UPDATE items SET deleted_at = $now WHERE id = $id", command =>
    {
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$now", DbTime.Stamp(Now));
    });

    public void Restore(long id) => Execute("UPDATE items SET deleted_at = NULL WHERE id = $id",
        command => command.Parameters.AddWithValue("$id", id));

    /// <summary>반복 일정의 한 회차를 완료/미완료로 표시한다.</summary>
    public void SetOccurrenceCompleted(long id, DateTime originalStart, bool completed) =>
        UpsertOverride(id, originalStart, o => o with { Status = completed ? ItemStatus.Completed : null });

    /// <summary>반복 일정의 한 회차만 옮기거나 제목을 바꾼다 ("이 일정만").</summary>
    public void UpdateOccurrence(long id, DateTime originalStart, DateTime start, DateTime end, string? title = null) =>
        UpsertOverride(id, originalStart, o => o with { Start = start, End = end, Title = string.IsNullOrWhiteSpace(title) ? o.Title : title.Trim() });

    /// <summary>반복 일정의 한 회차만 지운다.</summary>
    public void DeleteOccurrence(long id, DateTime originalStart) =>
        UpsertOverride(id, originalStart, o => o with { Status = ItemStatus.Cancelled });

    /// <summary>
    /// "이후 모두" 변경: 원래 반복을 이 회차 전날까지로 끝내고, 이 회차부터 <paramref name="updated"/> 내용으로 새 반복을 만든다.
    /// 새 일정의 id를 돌려준다. 첫 회차를 고르면 그냥 전체를 바꾼다.
    /// </summary>
    public long UpdateFollowing(long id, DateTime originalStart, CalendarEvent updated)
    {
        var original = Get(id) ?? throw new InvalidOperationException($"일정 {id} 없음");
        var originalRRule = original.RRule;
        var rule = original.Rule;
        if (rule is null || originalStart <= original.Start)
        {
            updated.Id = id;
            Update(updated);
            return id;
        }

        var before = rule.Expand(original.Start, originalStart).Count();
        var ended = rule with { Until = DateOnly.FromDateTime(originalStart).AddDays(-1), Count = null };
        if (rule.Count is not null)
        {
            ended = rule with { Count = before, Until = null };
        }
        original.RRule = ended.ToString();
        Update(original);

        var newRule = RecurrenceRule.Parse(updated.RRule);
        if (newRule?.Count is { } count && rule.Count is not null && RecurrenceRule.Parse(updated.RRule) == RecurrenceRule.Parse(originalRRule))
        {
            updated.RRule = (newRule with { Count = Math.Max(1, count - before) }).ToString();
        }
        updated.Id = 0;
        return Add(updated);
    }

    /// <summary>"이후 모두" 삭제: 이 회차 전날까지로 반복을 끝낸다.</summary>
    public void DeleteFollowing(long id, DateTime originalStart)
    {
        var original = Get(id);
        if (original?.Rule is not { } rule)
        {
            return;
        }
        if (originalStart <= original.Start)
        {
            Delete(id);
            return;
        }

        var before = rule.Expand(original.Start, originalStart).Count();
        original.RRule = (rule.Count is not null
            ? rule with { Count = before, Until = null }
            : rule with { Until = DateOnly.FromDateTime(originalStart).AddDays(-1) }).ToString();
        Update(original);
    }

    // ---------- 내부 ----------

    private static void Validate(CalendarEvent e)
    {
        e.Title = e.Title.Trim();
        if (e.Title.Length == 0)
        {
            throw new ArgumentException("제목이 비어 있습니다.", nameof(e));
        }
        if (e.IsAllDay)
        {
            e.Start = e.Start.Date;
            e.End = e.End.Date < e.Start ? e.Start : e.End.Date;
        }
        else if (e.End < e.Start)
        {
            e.End = e.Start;
        }
    }

    private static void Bind(SqliteCommand command, CalendarEvent e)
    {
        command.Parameters.AddWithValue("$list", (object?)e.ListId ?? DBNull.Value);
        command.Parameters.AddWithValue("$title", e.Title);
        command.Parameters.AddWithValue("$notes", string.IsNullOrEmpty(e.Notes) ? DBNull.Value : e.Notes);
        command.Parameters.AddWithValue("$location", string.IsNullOrEmpty(e.Location) ? DBNull.Value : e.Location);
        command.Parameters.AddWithValue("$color", (object?)e.Color ?? DBNull.Value);
        command.Parameters.AddWithValue("$allDay", e.IsAllDay ? 1 : 0);
        command.Parameters.AddWithValue("$start", FormatPoint(e.Start, e.IsAllDay));
        command.Parameters.AddWithValue("$end", FormatPoint(e.End, e.IsAllDay));
        command.Parameters.AddWithValue("$rrule", (object?)RecurrenceRule.Parse(e.RRule)?.ToString() ?? DBNull.Value);
    }

    internal static string FormatPoint(DateTime value, bool allDay) =>
        DbTime.Due(DateOnly.FromDateTime(value), allDay ? null : TimeOnly.FromDateTime(value))!;

    internal static DateTime ParsePoint(string value)
    {
        var (date, time) = DbTime.ParseDue(value);
        return date!.Value.ToDateTime(time ?? TimeOnly.MinValue);
    }

    private List<CalendarEvent> Query(string where, params (string Name, object Value)[] parameters)
    {
        using var connection = _db.Open();
        using var command = connection.CreateCommand();
        command.CommandText = SelectColumns + "\n" + where;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        using var reader = command.ExecuteReader();
        var list = new List<CalendarEvent>();
        while (reader.Read())
        {
            var start = ParsePoint(reader.GetString(7));
            list.Add(new CalendarEvent
            {
                Id = reader.GetInt64(0),
                ListId = reader.IsDBNull(1) ? null : reader.GetInt64(1),
                Title = reader.GetString(2),
                Notes = reader.IsDBNull(3) ? null : reader.GetString(3),
                Location = reader.IsDBNull(4) ? null : reader.GetString(4),
                Color = reader.IsDBNull(5) ? null : reader.GetString(5),
                IsAllDay = reader.GetBoolean(6),
                Start = start,
                End = reader.IsDBNull(8) ? start : ParsePoint(reader.GetString(8)),
                RRule = reader.IsDBNull(9) ? null : reader.GetString(9),
                Status = (ItemStatus)reader.GetInt32(10),
                CompletedAt = DbTime.ParseStamp(reader.IsDBNull(11) ? null : reader.GetString(11)),
                CreatedAt = DbTime.ParseStamp(reader.GetString(12))!.Value,
                UpdatedAt = DbTime.ParseStamp(reader.GetString(13))!.Value,
                DeletedAt = DbTime.ParseStamp(reader.IsDBNull(14) ? null : reader.GetString(14)),
                ListName = reader.IsDBNull(15) ? null : reader.GetString(15),
                ListColor = reader.IsDBNull(16) ? null : reader.GetString(16),
            });
        }
        return list;
    }

    private Dictionary<(long, DateTime), OccurrenceOverride> LoadOverrides(List<long> ids)
    {
        var result = new Dictionary<(long, DateTime), OccurrenceOverride>();
        if (ids.Count == 0)
        {
            return result;
        }

        using var connection = _db.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT item_id, original_start, status, override_json FROM occurrences WHERE item_id IN ({string.Join(',', ids)})";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var data = reader.IsDBNull(3) ? null : JsonSerializer.Deserialize(reader.GetString(3), OverrideJsonContext.Default.OverrideData);
            result[(reader.GetInt64(0), ParsePoint(reader.GetString(1)))] = new OccurrenceOverride(
                reader.IsDBNull(2) ? null : (ItemStatus)reader.GetInt32(2),
                data?.Start is { } s ? ParsePoint(s) : null,
                data?.End is { } e ? ParsePoint(e) : null,
                data?.Title);
        }
        return result;
    }

    private void UpsertOverride(long id, DateTime originalStart, Func<OccurrenceOverride, OccurrenceOverride> change)
    {
        var e = Get(id) ?? throw new InvalidOperationException($"일정 {id} 없음");
        var key = FormatPoint(originalStart, e.IsAllDay);
        var current = LoadOverrides([id]).GetValueOrDefault((id, ParsePoint(key)), new OccurrenceOverride(null, null, null, null));
        var updated = change(current);
        var json = updated.Start is null && updated.End is null && updated.Title is null
            ? null
            : JsonSerializer.Serialize(new OverrideData(
                updated.Start is { } s ? FormatPoint(s, e.IsAllDay) : null,
                updated.End is { } en ? FormatPoint(en, e.IsAllDay) : null,
                updated.Title), OverrideJsonContext.Default.OverrideData);

        Execute("""
            INSERT INTO occurrences(item_id, original_start, status, override_json) VALUES ($id, $key, $status, $json)
            ON CONFLICT(item_id, original_start) DO UPDATE SET status = excluded.status, override_json = excluded.override_json
            """, command =>
        {
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$key", key);
            command.Parameters.AddWithValue("$status", updated.Status is { } st ? (int)st : DBNull.Value);
            command.Parameters.AddWithValue("$json", (object?)json ?? DBNull.Value);
        });
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

    private void OnChanged() => Changed?.Invoke(this, EventArgs.Empty);

    private sealed record OccurrenceOverride(ItemStatus? Status, DateTime? Start, DateTime? End, string? Title);
}

internal sealed record OverrideData(string? Start, string? End, string? Title);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(OverrideData))]
internal sealed partial class OverrideJsonContext : JsonSerializerContext;
