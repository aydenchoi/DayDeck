using DayDeck.Core.Data;
using DayDeck.Core.Notes;
using DayDeck.Core.Todo;

namespace DayDeck.Core.Search;

public enum SearchHitKind
{
    Task,
    Event,
    Note,
}

/// <summary>전체 검색 결과 한 건.</summary>
public sealed record SearchHit(SearchHitKind Kind, long? ItemId, string? NotePath, string Title, string Detail, bool IsDone);

/// <summary>
/// 전체 검색 (Ctrl+K): 할 일, 일정, 메모를 한 번에 찾는다.
/// 한국어는 띄어쓰기 없이 붙여 쓰는 경우가 많아 단어 단위 FTS 대신 부분 문자열(LIKE)로 찾는다.
/// 데이터가 개인 규모(수천 건)라 충분히 빠르다.
/// </summary>
public sealed class SearchService
{
    private readonly Database _db;
    private readonly NoteStore? _notes;

    public SearchService(Database db, NoteStore? notes)
    {
        _db = db;
        _notes = notes;
    }

    public IReadOnlyList<SearchHit> Search(string query, int limitPerKind = 20)
    {
        query = query.Trim();
        if (query.Length == 0)
        {
            return [];
        }

        var hits = new List<SearchHit>();
        using (var connection = _db.Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT i.id, i.kind, i.title, i.notes, i.location, i.due_at, i.start_at, i.status, l.name,
                       instr(lower(i.title), lower($q)) > 0 AS in_title
                FROM items i LEFT JOIN lists l ON l.id = i.list_id
                WHERE i.deleted_at IS NULL AND i.parent_id IS NULL
                  AND (i.title LIKE $like ESCAPE '\' OR i.location LIKE $like ESCAPE '\' OR coalesce(i.notes, '') <> '')
                ORDER BY in_title DESC, i.status, coalesce(i.start_at, i.due_at, i.updated_at) DESC
                """;
            command.Parameters.AddWithValue("$q", query);
            command.Parameters.AddWithValue("$like", "%" + EscapeLike(query) + "%");
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var kind = reader.GetInt32(1) == (int)ItemKind.Event ? SearchHitKind.Event : SearchHitKind.Task;
                // 메모는 마크다운이므로 "보기"에 보이는 글자에서 찾는다 (**, 태그는 빼고).
                var notes = reader.IsDBNull(3) ? null : MarkdownLines.ToPlainText(reader.GetString(3));
                var inTitleOrPlace = reader.GetString(2).Contains(query, StringComparison.CurrentCultureIgnoreCase)
                                     || (!reader.IsDBNull(4) && reader.GetString(4).Contains(query, StringComparison.CurrentCultureIgnoreCase));
                if (!inTitleOrPlace && notes?.Contains(query, StringComparison.CurrentCultureIgnoreCase) != true)
                {
                    continue;
                }
                var when = kind == SearchHitKind.Event
                    ? reader.IsDBNull(6) ? null : reader.GetString(6).Replace('T', ' ')
                    : reader.IsDBNull(5) ? null : reader.GetString(5).Replace('T', ' ') + " 마감";
                var detail = string.Join(" · ", new[] { when, reader.IsDBNull(8) ? null : reader.GetString(8), reader.IsDBNull(4) ? null : reader.GetString(4), Snippet(notes, query) }
                    .Where(s => !string.IsNullOrEmpty(s)));
                hits.Add(new SearchHit(kind, reader.GetInt64(0), null, reader.GetString(2), detail, reader.GetInt32(7) != 0));
            }
        }

        var result = hits.Where(h => h.Kind == SearchHitKind.Task).Take(limitPerKind)
            .Concat(hits.Where(h => h.Kind == SearchHitKind.Event).Take(limitPerKind))
            .ToList();

        if (_notes is not null)
        {
            result.AddRange(_notes.Search(query, limit: limitPerKind)
                .Select(n => new SearchHit(SearchHitKind.Note, null, n.Note.RelativePath, n.Note.Title,
                    (n.Note.Folder.Length > 0 ? n.Note.Folder + " · " : "") + n.Snippet, false)));
        }
        return result;
    }

    private static string? Snippet(string? text, string query)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }
        var index = text.IndexOf(query, StringComparison.CurrentCultureIgnoreCase);
        if (index < 0)
        {
            return null;
        }
        var start = Math.Max(0, index - 20);
        var end = Math.Min(text.Length, index + query.Length + 30);
        return (start > 0 ? "…" : "") + string.Join(' ', text[start..end].Split((char[])[' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
               + (end < text.Length ? "…" : "");
    }

    private static string EscapeLike(string value) =>
        value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
}
