using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DayDeck.App.ViewModels;
using DayDeck.Core.Diagnostics;

namespace DayDeck.App.Views;

/// <summary>
/// 선택한 할 일의 상세 편집 (할 일 목록 오른쪽). 위는 제목과 할 일 정보(마감·우선순위·알림·하위 할 일 등, 접을 수 있음),
/// 아래는 메모 화면처럼 서식이 적용된 모양 그대로 고치는 메모(보기 편집기). "원문"을 누르면 마크다운 입력란으로 고친다.
/// </summary>
public partial class TaskDetailPanel : UserControl
{
    private const string InfoExpandedKey = "tasks.detail.info-expanded";
    private static readonly CultureInfo Korean = CultureInfo.GetCultureInfo("ko-KR");
    private readonly App _app = App.Current;
    private readonly MarkdownRichEditor _notesEditor;
    private TaskDetailViewModel? _model;

    public TaskDetailPanel()
    {
        InitializeComponent();
        SubtaskInput.TextChanged += (_, _) =>
            SubtaskPlaceholder.Visibility = SubtaskInput.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

        // 메모: 할 일 메모에는 폴더가 없으므로 그림은 넣지 않는다 (표 붙여 넣기·서식은 메모 화면과 같다).
        _notesEditor = new MarkdownRichEditor(_ => null) { PasteImagesEnabled = () => false };
        System.Windows.Automation.AutomationProperties.SetName(_notesEditor, "할 일 메모 (보기)");
        System.Windows.Automation.AutomationProperties.SetAutomationId(_notesEditor, "TaskNotesRich");
        _notesEditor.MarkdownChanged += (_, _) =>
        {
            if (_model is { } model)
            {
                model.Notes = _notesEditor.Markdown;
            }
        };
        NotesHost.Children.Add(_notesEditor);
        NotesToolbar.Target = NotesBox;
        NotesToolbar.RichTarget = _notesEditor;
        NotesToolbar.PreviewChanged += (_, _) => ShowNotesEditor();

        SetInfoExpanded(_app.AppState.Get(InfoExpandedKey) != "false");
    }

    /// <summary>보기 편집기와 원문 입력란 중 하나만 보인다. 바꿀 때 내용을 맞춘다.</summary>
    private void ShowNotesEditor()
    {
        var raw = NotesToolbar.IsShowingRaw;
        if (raw)
        {
            _notesEditor.Flush(); // 고친 내용을 먼저 할 일에 넣는다 (입력란은 바인딩으로 같은 값을 보인다).
        }
        else if (_model is { } model && _notesEditor.Markdown != model.Notes)
        {
            _notesEditor.Load(model.Notes);
        }
        NotesBox.Visibility = raw ? Visibility.Visible : Visibility.Collapsed;
        _notesEditor.Visibility = raw ? Visibility.Collapsed : Visibility.Visible;
        if (IsLoaded)
        {
            (raw ? (UIElement)NotesBox : _notesEditor).Focus();
        }
    }

    // ---------- 할 일 정보 (접기) ----------

    private void OnInfoToggle(object sender, RoutedEventArgs e)
    {
        var expanded = InfoScroll.Visibility != Visibility.Visible;
        SetInfoExpanded(expanded);
        try
        {
            _app.AppState.Set(InfoExpandedKey, expanded ? "true" : "false");
        }
        catch (Exception ex)
        {
            Log.Warn("할 일 정보 접기 상태 저장 실패", ex);
        }
    }

    private void SetInfoExpanded(bool expanded)
    {
        InfoScroll.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        UpdateInfoHeader();
    }

    /// <summary>"▾ 할 일 정보 · 마감 10/30 (금) 18:00 · !!! · 업무 · ⏰ 1 · 하위 1/3" (접어도 무엇이 있는지 보이게).</summary>
    private void UpdateInfoHeader()
    {
        var title = (InfoScroll.Visibility == Visibility.Visible ? "▾" : "▸") + " 할 일 정보";
        if (_model is not { } model)
        {
            InfoHeader.Text = title;
            return;
        }
        var parts = new List<string>();
        if (model.DueDate is { } due)
        {
            parts.Add("마감 " + due.ToString("M/d (ddd)", Korean) + (model.DueTime != TaskDetailViewModel.NoTime ? " " + model.DueTime : ""));
        }
        if (model.PriorityIndex > 0)
        {
            parts.Add(new string('!', model.PriorityIndex));
        }
        if (model.IsMyDay)
        {
            parts.Add("☀ 오늘");
        }
        parts.Add(model.SelectedList.Name);
        if (model.SelectedRepeat.RRule is not null)
        {
            parts.Add("🔁 " + model.SelectedRepeat.Label);
        }
        if (model.TagsText.Trim().Length > 0)
        {
            parts.Add("🏷 " + model.TagsText.Trim());
        }
        if (model.Reminders.Count > 0)
        {
            parts.Add($"⏰ {model.Reminders.Count}");
        }
        if (model.Subtasks.Count > 0)
        {
            parts.Add($"하위 {model.Subtasks.Count(s => s.IsDone)}/{model.Subtasks.Count}");
        }
        InfoHeader.Text = title + "  ·  " + string.Join(" · ", parts.Where(p => p.Length > 0));
    }

    public event EventHandler? CloseRequested;

    public long? CurrentId => _model?.Id;

    public void Show(long id)
    {
        if (_model?.Id == id)
        {
            return;
        }
        Load(id);
    }

    /// <summary>목록에서 상태를 바꿨을 때 패널 내용을 다시 읽는다. 편집 중인 제목/메모는 먼저 저장한다.</summary>
    public void Refresh()
    {
        if (_model is { } model)
        {
            Load(model.Id);
        }
    }

    public void Close()
    {
        if (_model is not null)
        {
            _notesEditor.Flush(); // 기다리던 메모 변경을 넣은 뒤 저장(Unregister)한다.
            _app.AutoSave.Unregister(_model);
            _model = null;
            DataContext = null;
            UpdateInfoHeader();
        }
    }

    public void FocusTitle()
    {
        TitleBox.Focus();
        TitleBox.CaretIndex = TitleBox.Text.Length;
    }

    private void Load(long id)
    {
        Close();
        var item = _app.Tasks.Get(id);
        if (item is null || item.DeletedAt is not null)
        {
            CloseRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        var model = new TaskDetailViewModel(item, _app.Tasks, _app.Reminders, _app.AutoSave, _app.Settings.Current.Calendar.TimeSlotMinutes);
        _model = model;
        _app.AutoSave.Register(model);
        DataContext = model;
        _notesEditor.Load(model.Notes);
        model.PropertyChanged += (_, _) => UpdateInfoHeader();
        model.Reminders.CollectionChanged += (_, _) => UpdateInfoHeader();
        model.Subtasks.CollectionChanged += (_, _) => UpdateInfoHeader();
        UpdateInfoHeader();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            CloseRequested?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
            return;
        }
        base.OnPreviewKeyDown(e);
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnLostKeyboardFocus(e);
        // 패널 밖으로 포커스가 나가면 바로 저장한다.
        if (_model is not null && !IsKeyboardFocusWithin)
        {
            _notesEditor.Flush();
            _model.Save();
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);

    private void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        if (_model is not { } model)
        {
            return;
        }

        model.Save();
        var title = model.Title;
        var id = model.Id;
        CloseRequested?.Invoke(this, EventArgs.Empty);
        _app.Tasks.Delete(id);
        (Window.GetWindow(this) as MainWindow)?.ShowUndo($"'{title}' 삭제됨", () => _app.Tasks.Restore(id));
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => _model?.ToggleCancelled();

    private void OnTagsKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter && sender is System.Windows.Controls.TextBox box)
        {
            box.GetBindingExpression(System.Windows.Controls.TextBox.TextProperty)?.UpdateSource();
            e.Handled = true;
        }
    }

    private void OnDueToday(object sender, RoutedEventArgs e) => _model?.SetDue(DateOnly.FromDateTime(DateTime.Now));

    private void OnDueTomorrow(object sender, RoutedEventArgs e) => _model?.SetDue(DateOnly.FromDateTime(DateTime.Now).AddDays(1));

    private void OnDueNextWeek(object sender, RoutedEventArgs e)
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        var daysUntilMonday = ((int)DayOfWeek.Monday - (int)today.DayOfWeek + 7) % 7;
        _model?.SetDue(today.AddDays(daysUntilMonday == 0 ? 7 : daysUntilMonday));
    }

    private void OnDueClear(object sender, RoutedEventArgs e) => _model?.SetDue(null);

    private void OnAddRelativeReminder(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string minutes })
        {
            _model?.AddRelativeReminder(int.Parse(minutes, System.Globalization.CultureInfo.InvariantCulture));
        }
    }

    private void OnRemindInHour(object sender, RoutedEventArgs e) => _model?.AddAbsoluteReminder(DateTime.Now.AddHours(1));

    private void OnRemindThisEvening(object sender, RoutedEventArgs e)
    {
        var evening = DateTime.Today.AddHours(18);
        _model?.AddAbsoluteReminder(evening > DateTime.Now ? evening : evening.AddDays(1));
    }

    private void OnRemindTomorrowMorning(object sender, RoutedEventArgs e) =>
        _model?.AddAbsoluteReminder(DateOnly.FromDateTime(DateTime.Now).AddDays(1)
            .ToDateTime(_app.Settings.Current.Reminders.GetDateOnlyReminderTime()));

    private void OnRemoveReminder(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: ReminderItemViewModel reminder })
        {
            _model?.RemoveReminder(reminder);
        }
    }

    private void OnSubtaskKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && _model is not null)
        {
            _model.AddSubtask(SubtaskInput.Text);
            SubtaskInput.Clear();
            e.Handled = true;
        }
    }

    private void OnSubtaskToggle(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: SubtaskViewModel subtask })
        {
            _model?.ToggleSubtask(subtask);
        }
    }

    private void OnSubtaskDelete(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: SubtaskViewModel subtask })
        {
            _model?.DeleteSubtask(subtask);
        }
    }
}
