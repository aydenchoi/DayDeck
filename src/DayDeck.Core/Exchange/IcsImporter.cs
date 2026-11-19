using DayDeck.Core.Calendar;
using DayDeck.Core.Todo;

namespace DayDeck.Core.Exchange;

public sealed record ImportSummary(int Events, int Tasks, int Duplicates, int Skipped);

/// <summary>ICS 파일 가져오기/내보내기를 저장소와 연결한다. 같은 제목·같은 시각의 항목은 중복으로 보고 건너뛴다.</summary>
public sealed class IcsImporter
{
    private readonly TaskRepository _tasks;
    private readonly EventRepository _events;
    private readonly TimeProvider _time;

    public IcsImporter(TaskRepository tasks, EventRepository events, TimeProvider time)
    {
        _tasks = tasks;
        _events = events;
        _time = time;
    }

    public string ExportAll() => IcsFormat.Export(_events.GetAll(), _tasks.GetAll(), _time.GetLocalNow().DateTime);

    public ImportSummary Import(string content)
    {
        var parsed = IcsFormat.Import(content);
        var existingEvents = _events.GetAll().Select(e => (e.Title, e.Start)).ToHashSet();
        var existingTasks = _tasks.GetAll().Select(t => (t.Title, t.DueDate)).ToHashSet();
        var lists = _tasks.GetLists().ToDictionary(l => l.Name, l => l.Id, StringComparer.CurrentCultureIgnoreCase);
        int events = 0, tasks = 0, duplicates = 0;

        long? ListId(string? name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return null;
            }
            if (!lists.TryGetValue(name, out var id))
            {
                id = _tasks.AddList(name);
                lists[name] = id;
            }
            return id;
        }

        foreach (var e in parsed.Events)
        {
            if (!existingEvents.Add((e.Title, e.Start)))
            {
                duplicates++;
                continue;
            }
            e.ListId = ListId(e.ListName);
            _events.Add(e);
            events++;
        }

        foreach (var t in parsed.Tasks)
        {
            if (!existingTasks.Add((t.Title, t.DueDate)))
            {
                duplicates++;
                continue;
            }
            t.ListId = ListId(t.ListName);
            var completed = t.Status != ItemStatus.Open;
            var id = _tasks.Add(t);
            if (completed)
            {
                _tasks.SetCompleted(id, true);
            }
            tasks++;
        }

        return new ImportSummary(events, tasks, duplicates, parsed.Skipped);
    }
}
