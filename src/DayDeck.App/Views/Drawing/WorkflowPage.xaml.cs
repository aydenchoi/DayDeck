using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using DayDeck.Core.AutoSave;
using DayDeck.Core.Diagnostics;
using DayDeck.Core.Drawings;

namespace DayDeck.App.Views.Drawing;

/// <summary>
/// 워크플로우 화면: 왼쪽은 그림 목록, 오른쪽은 Excalidraw 방식의 그림판.
/// 그림은 <c>Data\Drawings\*.excalidraw</c>로 자동 저장되어 excalidraw.com·Obsidian Excalidraw에서도 그대로 열린다.
/// </summary>
public partial class WorkflowPage : UserControl, IAutoSaveTarget
{
    private const string LastKey = "drawings.last";
    private const string RoughnessKey = "drawings.roughness";

    // Excalidraw 기본 색 (선 / 채우기).
    private readonly App _app = App.Current;
    private readonly DrawingStore _store;
    private readonly DispatcherTimer _saveTimer;
    private string? _path;
    private bool _dirty;
    private bool _syncingTool;
    private bool _syncingList;

    public WorkflowPage()
    {
        InitializeComponent();
        _store = _app.Drawings;
        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        _saveTimer.Tick += (_, _) =>
        {
            _saveTimer.Stop();
            Save();
        };
        Board.DocumentChanged += (_, _) =>
        {
            _dirty = true;
            SaveStatus.Text = "● 수정됨";
            _saveTimer.Stop();
            _saveTimer.Start();
            _app.AutoSave.NotifyEdited(this);
            UpdateEmpty();
        };
        Board.SelectionChanged += (_, _) => UpdateProperties();
        Board.ToolChanged += (_, _) => SyncToolButtons();
        BuildPalettes();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public bool IsDirty => _dirty;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _app.AutoSave.Register(this);
        _store.Changed += OnStoreChanged;
        SyncToolButtons();
        var list = RefreshList();
        if (double.TryParse(_app.AppState.Get(RoughnessKey), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var savedRoughness))
        {
            Board.DrawStyle.Roughness = savedRoughness;
        }
        var last = _app.AppState.Get(LastKey);
        var open = list.FirstOrDefault(d => string.Equals(d.Path, last, StringComparison.OrdinalIgnoreCase)) ?? list.FirstOrDefault();
        if (open is not null)
        {
            Open(open.Path);
        }
        else
        {
            ShowNoDrawing();
        }
        Board.Focus();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        Save();
        _saveTimer.Stop();
        _store.Changed -= OnStoreChanged;
        _app.AutoSave.Unregister(this);
    }

    private void OnStoreChanged(object? sender, EventArgs e) => RefreshList();

    // ---------- 목록 ----------

    private IReadOnlyList<DrawingInfo> RefreshList()
    {
        var list = _store.List();
        _syncingList = true;
        DrawingList.ItemsSource = list;
        DrawingList.SelectedItem = list.FirstOrDefault(d => d.Path == _path);
        _syncingList = false;
        ListEmpty.Visibility = list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        return list;
    }

    private void OnDrawingSelected(object sender, SelectionChangedEventArgs e)
    {
        if (!_syncingList && DrawingList.SelectedItem is DrawingInfo info && info.Path != _path)
        {
            Open(info.Path);
        }
    }

    private void Open(string path)
    {
        Save();
        ExcalidrawDocument doc;
        try
        {
            doc = _store.Load(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
        {
            Log.Warn($"그림을 열 수 없음: {path}", ex);
            MessageBox.Show(Window.GetWindow(this), $"그림을 열 수 없습니다.\n\n{ex.Message}", "워크플로우", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        _path = path;
        _dirty = false;
        SaveStatus.Text = "";
        NoDrawing.Visibility = Visibility.Collapsed;
        Board.Load(doc);
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => Board.FitToContent());
        try
        {
            _app.AppState.Set(LastKey, path);
        }
        catch (Exception ex)
        {
            Log.Warn("마지막 그림 저장 실패", ex);
        }
        _syncingList = true;
        DrawingList.SelectedItem = (DrawingList.ItemsSource as IEnumerable<DrawingInfo>)?.FirstOrDefault(d => d.Path == path);
        _syncingList = false;
        UpdateEmpty();
        UpdateProperties();
        Board.Focus();
    }

    private void ShowNoDrawing()
    {
        _path = null;
        Board.Load(ExcalidrawDocument.CreateEmpty());
        NoDrawing.Visibility = Visibility.Visible;
        CanvasEmpty.Visibility = Visibility.Collapsed;
    }

    private void UpdateEmpty() =>
        CanvasEmpty.Visibility = _path is not null && !Board.Document.Visible.Any() ? Visibility.Visible : Visibility.Collapsed;

    public void Save()
    {
        if (!_dirty || _path is null)
        {
            return;
        }
        try
        {
            _store.Save(_path, Board.Document);
            _dirty = false;
            SaveStatus.Text = $"✓ 저장됨 {DateTime.Now:HH:mm}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"그림 저장 실패: {_path}", ex);
            SaveStatus.Text = "⚠ 저장 실패";
        }
    }

    private void OnNew(object sender, RoutedEventArgs e)
    {
        Save();
        if (InputDialog.Ask(Window.GetWindow(this), "새 워크플로우", "이름", $"워크플로우 {DateTime.Now:MM-dd}") is not { } name)
        {
            return;
        }
        var path = _store.Create(name);
        RefreshList();
        Open(path);
    }

    private void OnListMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is not DrawingInfo info || DrawingList.ContextMenu is not { } menu)
        {
            e.Handled = true;
            return;
        }
        menu.Items.Clear();
        var rename = new MenuItem { Header = "이름 바꾸기" };
        rename.Click += (_, _) => Rename(info);
        var copy = new MenuItem { Header = "복사본 만들기" };
        copy.Click += (_, _) => CopyDrawing(info);
        var delete = new MenuItem { Header = "삭제" };
        delete.Click += (_, _) => Delete(info);
        var reveal = new MenuItem { Header = "탐색기에서 보기" };
        reveal.Click += (_, _) => Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{info.Path}\"") { UseShellExecute = true });
        menu.Items.Add(rename);
        menu.Items.Add(copy);
        menu.Items.Add(reveal);
        menu.Items.Add(new Separator());
        menu.Items.Add(delete);
    }

    private void Rename(DrawingInfo info)
    {
        if (InputDialog.Ask(Window.GetWindow(this), "이름 바꾸기", "새 이름", info.Name) is not { } name)
        {
            return;
        }
        if (info.Path == _path)
        {
            Save();
        }
        try
        {
            var renamed = _store.Rename(info.Path, name);
            if (info.Path == _path)
            {
                _path = renamed;
                _app.AppState.Set(LastKey, renamed);
            }
            RefreshList();
        }
        catch (IOException ex)
        {
            MessageBox.Show(Window.GetWindow(this), ex.Message, "이름 바꾸기", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void CopyDrawing(DrawingInfo info)
    {
        if (info.Path == _path)
        {
            Save();
        }
        try
        {
            var target = _store.Create(info.Name + " 복사본");
            _store.Save(target, _store.Load(info.Path));
            RefreshList();
            Open(target);
        }
        catch (Exception ex) when (ex is IOException or FormatException)
        {
            MessageBox.Show(Window.GetWindow(this), ex.Message, "복사본 만들기", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Delete(DrawingInfo info)
    {
        var answer = MessageBox.Show(Window.GetWindow(this),
            $"'{info.Name}'을(를) 삭제할까요?\n파일은 Drawings\\.trash 폴더로 옮겨집니다.",
            "워크플로우 삭제", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel);
        if (answer != MessageBoxResult.OK)
        {
            return;
        }
        if (info.Path == _path)
        {
            Save();
            _path = null;
        }
        _store.Delete(info.Path);
        var list = RefreshList();
        if (_path is null)
        {
            if (list.FirstOrDefault() is { } next)
            {
                Open(next.Path);
            }
            else
            {
                ShowNoDrawing();
            }
        }
    }

    private void OnImport(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Excalidraw 그림 (*.excalidraw;*.json)|*.excalidraw;*.json|모든 파일 (*.*)|*.*",
            Multiselect = true,
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true)
        {
            return;
        }
        string? last = null;
        foreach (var file in dialog.FileNames)
        {
            try
            {
                var doc = ExcalidrawDocument.Parse(File.ReadAllText(file));
                var name = System.IO.Path.GetFileNameWithoutExtension(file);
                last = _store.Create(name);
                _store.Save(last, doc);
            }
            catch (Exception ex) when (ex is IOException or FormatException or UnauthorizedAccessException)
            {
                MessageBox.Show(Window.GetWindow(this), $"{System.IO.Path.GetFileName(file)}\n\n{ex.Message}", "가져오기", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        RefreshList();
        if (last is not null)
        {
            Open(last);
        }
    }

    private void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(_store.Root);
        Process.Start(new ProcessStartInfo(_store.Root) { UseShellExecute = true });
    }

    // ---------- 도구 ----------

    private void OnToolChecked(object sender, RoutedEventArgs e)
    {
        if (_syncingTool || sender is not RadioButton { Tag: string tag } || !Enum.TryParse<DrawingTool>(tag, out var tool))
        {
            return;
        }
        Board.Tool = tool;
        Board.Focus();
    }

    private void SyncToolButtons()
    {
        _syncingTool = true;
        foreach (var radio in ToolButtons.Children.OfType<RadioButton>())
        {
            radio.IsChecked = (string)radio.Tag == Board.Tool.ToString();
        }
        _syncingTool = false;
        UpdateProperties();
    }

    private void OnUndo(object sender, RoutedEventArgs e)
    {
        Board.Undo();
        Board.Focus();
    }

    private void OnRedo(object sender, RoutedEventArgs e)
    {
        Board.Redo();
        Board.Focus();
    }

    private void OnZoomIn(object sender, RoutedEventArgs e) => Board.SetZoom(Board.Zoom * 1.2);

    private void OnZoomOut(object sender, RoutedEventArgs e) => Board.SetZoom(Board.Zoom / 1.2);

    private void OnZoomFit(object sender, RoutedEventArgs e)
    {
        Board.FitToContent();
        UpdateZoomText();
    }

    private void OnDuplicate(object sender, RoutedEventArgs e)
    {
        Board.Duplicate();
        Board.Focus();
    }

    private void OnDelete(object sender, RoutedEventArgs e)
    {
        Board.DeleteSelection();
        Board.Focus();
    }

    // ---------- 내보내기 ----------

    private void OnCopyImage(object sender, RoutedEventArgs e)
    {
        if (Board.RenderPng(selectionOnly: true) is not { } image)
        {
            return;
        }
        try
        {
            Clipboard.SetImage(image);
            SaveStatus.Text = "그림을 복사했습니다";
        }
        catch (System.Runtime.InteropServices.ExternalException ex)
        {
            Log.Warn("그림 복사 실패", ex);
        }
    }

    private void OnExportPng(object sender, RoutedEventArgs e)
    {
        if (Board.RenderPng(selectionOnly: false) is not { } image)
        {
            return;
        }
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "PNG 그림 (*.png)|*.png",
            FileName = (_path is null ? "워크플로우" : DrawingStore.NameOf(_path)) + ".png",
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true)
        {
            return;
        }
        try
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            using var stream = File.Create(dialog.FileName);
            encoder.Save(stream);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(Window.GetWindow(this), ex.Message, "PNG 저장", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ---------- 속성 창 ----------

    private void BuildPalettes()
    {
        BuildColorRows();
        OpacitySlider.ValueChanged += (_, _) =>
        {
            OpacityText.Text = $"{OpacitySlider.Value:0}%";
            if (!_syncingOpacity && !_draggingOpacity)
            {
                ApplyOpacity();
            }
        };
        OpacitySlider.AddHandler(System.Windows.Controls.Primitives.Thumb.DragStartedEvent,
            new System.Windows.Controls.Primitives.DragStartedEventHandler((_, _) => _draggingOpacity = true));
        OpacitySlider.AddHandler(System.Windows.Controls.Primitives.Thumb.DragCompletedEvent,
            new System.Windows.Controls.Primitives.DragCompletedEventHandler((_, _) =>
            {
                _draggingOpacity = false;
                ApplyOpacity();
            }));
        foreach (var (label, style) in new[] { ("칠하기", "solid"), ("빗금", "hachure"), ("엇갈린 빗금", "cross-hatch"), ("지그재그", "zigzag") })
        {
            FillStyles.Children.Add(Choice(label, () =>
            {
                Board.DrawStyle.FillStyle = style;
                Board.ApplyToSelection(el => el.FillStyle = style);
            }));
        }
        // Excalidraw의 손그림 정도: 건축가(0) / 예술가(1) / 만화가(2).
        foreach (var (label, roughness) in new[] { ("반듯하게", 0.0), ("손그림", 1.0), ("더 거칠게", 2.0) })
        {
            Roughnesses.Children.Add(Choice(label, () =>
            {
                Board.DrawStyle.Roughness = roughness;
                Board.ApplyToSelection(el => el.Roughness = roughness);
                // 고른 손그림 정도는 다음에 열어도 쓴다.
                _app.AppState.Set(RoughnessKey, roughness.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }));
        }
        foreach (var (label, round) in new[] { ("각지게", false), ("둥글게", true) })
        {
            Edges.Children.Add(Choice(label, () =>
            {
                Board.DrawStyle.RoundEdges = round;
                Board.ApplyToSelection(el =>
                {
                    if (el.Type is ExcalidrawElement.Rectangle or ExcalidrawElement.Diamond or ExcalidrawElement.Line)
                    {
                        el.Roundness = !round ? null : el.Type == ExcalidrawElement.Rectangle ? new Roundness(3) : new Roundness(2);
                    }
                });
            }));
        }
        foreach (var (label, curved) in new[] { ("직선", false), ("곡선", true) })
        {
            ArrowTypes.Children.Add(Choice(label, () =>
            {
                Board.DrawStyle.CurvedArrows = curved;
                Board.ApplyToSelection(el =>
                {
                    if (el.Type == ExcalidrawElement.Arrow)
                    {
                        el.Roundness = curved ? new Roundness(2) : null;
                    }
                });
            }));
        }
        var heads = new (string Label, string? Head)[] { ("없음", null), ("화살", "arrow"), ("삼각형", "triangle"), ("막대", "bar"), ("점", "dot"), ("원", "circle_outline"), ("마름모", "diamond") };
        foreach (var (label, head) in heads)
        {
            StartHeads.Children.Add(Choice(label, () =>
            {
                Board.DrawStyle.StartArrowhead = head;
                Board.ApplyToSelection(el =>
                {
                    if (el.Type == ExcalidrawElement.Arrow)
                    {
                        el.StartArrowhead = head;
                    }
                });
            }));
            EndHeads.Children.Add(Choice(label, () =>
            {
                Board.DrawStyle.EndArrowhead = head;
                Board.ApplyToSelection(el =>
                {
                    if (el.Type == ExcalidrawElement.Arrow)
                    {
                        el.EndArrowhead = head;
                    }
                });
            }));
        }
        foreach (var (label, family) in new[] { ("손글씨", 5), ("보통", 6), ("만화", 8), ("코드", 3) })
        {
            FontFamilies.Children.Add(Choice(label, () =>
            {
                Board.DrawStyle.FontFamily = family;
                Board.ApplyToSelection(el =>
                {
                    if (el.Type == ExcalidrawElement.Text)
                    {
                        el.FontFamily = family;
                        el.LineHeight = DrawingRenderer.DefaultLineHeight(family);
                    }
                }, includeBoundText: true);
            }));
        }
        foreach (var (label, width) in new[] { ("가늘게", 1.0), ("보통", 2.0), ("굵게", 4.0) })
        {
            StrokeWidths.Children.Add(Choice(label, () =>
            {
                Board.DrawStyle.StrokeWidth = width;
                Board.ApplyToSelection(el => el.StrokeWidth = width);
            }));
        }
        foreach (var (label, style) in new[] { ("실선", "solid"), ("점선", "dashed"), ("작은 점", "dotted") })
        {
            StrokeStyles.Children.Add(Choice(label, () =>
            {
                Board.DrawStyle.StrokeStyle = style;
                Board.ApplyToSelection(el => el.StrokeStyle = style);
            }));
        }
        foreach (var (label, size) in new[] { ("작게", 16.0), ("보통", 20.0), ("크게", 28.0), ("아주 크게", 36.0) })
        {
            FontSizes.Children.Add(Choice(label, () =>
            {
                Board.DrawStyle.FontSize = size;
                Board.ApplyToSelection(el =>
                {
                    if (el.Type == ExcalidrawElement.Text)
                    {
                        el.FontSize = size;
                    }
                }, includeBoundText: true);
            }));
        }
    }

    // ---------- 색 (Excalidraw 색 고르기) ----------

    private bool _syncingOpacity;
    private bool _draggingOpacity;

    /// <summary>열린 색 고르기 창. 열려 있는 동안은 색 줄을 다시 만들지 않는다 (창이 붙은 단추가 사라지지 않게).</summary>
    private ColorPickerPopup? _picker;

    private void ApplyStroke(string hex)
    {
        Board.DrawStyle.StrokeColor = hex;
        Board.ApplyToSelection(el => el.StrokeColor = hex, includeBoundText: true);
        BuildColorRows();
    }

    private void ApplyBackground(string hex)
    {
        Board.DrawStyle.BackgroundColor = hex;
        Board.ApplyToSelection(el =>
        {
            if (el.IsShape || el.Type == ExcalidrawElement.Line || el.Type == ExcalidrawElement.FreeDraw)
            {
                el.BackgroundColor = hex;
            }
        });
        BuildColorRows();
    }

    private void ApplyOpacity()
    {
        var opacity = Math.Round(OpacitySlider.Value);
        Board.DrawStyle.Opacity = opacity;
        Board.ApplyToSelection(el => el.Opacity = opacity, includeBoundText: true);
    }

    private string CurrentStroke => Board.Selection.FirstOrDefault()?.StrokeColor ?? Board.DrawStyle.StrokeColor;

    private string CurrentBackground =>
        Board.Selection.FirstOrDefault(s => s.IsShape || s.Type is ExcalidrawElement.Line or ExcalidrawElement.FreeDraw)?.BackgroundColor
        ?? Board.DrawStyle.BackgroundColor;

    /// <summary>그림에서 쓴 색 (팔레트에 없는 색을 색 고르기 창에 보인다).</summary>
    private IEnumerable<string> UsedColors() =>
        Board.Document.Visible.SelectMany(e => new[] { e.StrokeColor, e.BackgroundColor });

    /// <summary>선 색·채우기 줄: Excalidraw처럼 자주 쓰는 5색 + 지금 색(누르면 모든 색 고르기).</summary>
    private void BuildColorRows()
    {
        if (_picker?.IsOpen == true)
        {
            return;
        }
        BuildColorRow(StrokeColors, ExcalidrawPalette.StrokePicks, CurrentStroke, "선 색", 4, ApplyStroke);
        BuildColorRow(BackgroundColors, ExcalidrawPalette.BackgroundPicks, CurrentBackground, "채우기 색", 1, ApplyBackground);
    }

    private void BuildColorRow(WrapPanel panel, (string Name, string Hex)[] picks, string current, string title, int shade, Action<string> apply)
    {
        panel.Children.Clear();
        foreach (var (name, hex) in picks)
        {
            panel.Children.Add(ColorPickerPopup.SwatchButton(hex, name, () =>
            {
                apply(hex);
                Board.Focus();
            }, selected: string.Equals(hex, current, StringComparison.OrdinalIgnoreCase)));
        }
        panel.Children.Add(new Border { Width = 1, Height = 20, Margin = new Thickness(2, 3, 6, 4), Background = (Brush)FindResource("LineBrush") });
        Button? more = null;
        more = ColorPickerPopup.SwatchButton(current, $"{title}: 모든 색 고르기", () =>
        {
            var popup = new ColorPickerPopup(more!, title, current, shade, UsedColors(), hex => apply(hex));
            popup.Closed += (_, _) =>
            {
                _picker = null;
                BuildColorRows();
                BuildCanvasRow();
                Board.Focus();
            };
            _picker = popup;
            // 단추를 누른 마우스 동작이 끝난 뒤에 연다 (바로 열면 그 동작에 창이 닫힐 수 있다).
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, () => popup.IsOpen = true);
        }, selected: !picks.Any(p => string.Equals(p.Hex, current, StringComparison.OrdinalIgnoreCase)));
        more.ToolTip = $"지금 색 {current} — 누르면 모든 색·명도·색 코드";
        panel.Children.Add(more);
    }

    /// <summary>
    /// 바탕색 단추: Excalidraw처럼 그림 위에 바탕색 줄(자주 쓰는 바탕 5가지 + 지금 색)을 펼친다. 지금 색 칸을 누르면 모든 색.
    /// </summary>
    private void OnCanvasBackground(object sender, RoutedEventArgs e)
    {
        if (_path is null)
        {
            SaveStatus.Text = "워크플로우를 먼저 고르세요";
            return;
        }
        CanvasPanel.Visibility = CanvasPanel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        BuildCanvasRow();
    }

    private void OnCloseCanvasPanel(object sender, RoutedEventArgs e)
    {
        CanvasPanel.Visibility = Visibility.Collapsed;
        Board.Focus();
    }

    private void BuildCanvasRow()
    {
        if (CanvasPanel.Visibility != Visibility.Visible || _picker?.IsOpen == true)
        {
            return;
        }
        BuildColorRow(CanvasColors, ExcalidrawPalette.CanvasPicks, Board.Document.ViewBackgroundColor, "바탕색", 0, hex =>
        {
            Board.SetViewBackground(hex);
            BuildCanvasRow();
        });
    }

    private Button Choice(string label, Action apply)
    {
        var button = new Button { Content = label, FontSize = 11.5, Padding = new Thickness(6, 1, 6, 1), Margin = new Thickness(0, 0, 4, 4) };
        button.Click += (_, _) =>
        {
            apply();
            Board.Focus();
        };
        return button;
    }

    private void UpdateProperties()
    {
        UpdateZoomText();
        var selection = Board.Selection;
        var drawingTool = Board.Tool is not (DrawingTool.Select or DrawingTool.Hand or DrawingTool.Eraser);
        if (_path is null || (selection.Count == 0 && !drawingTool))
        {
            PropertyPanel.Visibility = Visibility.Collapsed;
            return;
        }
        PropertyPanel.Visibility = Visibility.Visible;
        // 그리는 중이면 다음에 그릴 모양을, 고른 게 있으면 그 모양을 보여 준다.
        var types = selection.Count > 0
            ? selection.Select(s => s.Type).ToHashSet()
            : [Board.Tool switch
            {
                DrawingTool.Text => ExcalidrawElement.Text,
                DrawingTool.Arrow => ExcalidrawElement.Arrow,
                DrawingTool.Line => ExcalidrawElement.Line,
                DrawingTool.Pen => ExcalidrawElement.FreeDraw,
                _ => ExcalidrawElement.Rectangle,
            }];
        var hasShape = types.Overlaps([ExcalidrawElement.Rectangle, ExcalidrawElement.Diamond, ExcalidrawElement.Ellipse]);
        var hasText = types.Contains(ExcalidrawElement.Text)
                      || selection.Any(s => DrawingGeometry.BoundText(Board.Document, s) is not null);
        BackgroundSection.Visibility = hasShape ? Visibility.Visible : Visibility.Collapsed;
        StrokeSection.Visibility = types.All(t => t == ExcalidrawElement.Text) ? Visibility.Collapsed : Visibility.Visible;
        FontSection.Visibility = hasText ? Visibility.Visible : Visibility.Collapsed;
        BuildColorRows();
        BuildCanvasRow();
        _syncingOpacity = true;
        OpacitySlider.Value = selection.Count > 0 ? selection[0].Opacity : Board.DrawStyle.Opacity;
        OpacityText.Text = $"{OpacitySlider.Value:0}%";
        _syncingOpacity = false;
        EdgeSection.Visibility = types.Overlaps([ExcalidrawElement.Rectangle, ExcalidrawElement.Diamond, ExcalidrawElement.Line]) ? Visibility.Visible : Visibility.Collapsed;
        ArrowSection.Visibility = types.Contains(ExcalidrawElement.Arrow) ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateZoomText() => ZoomText.Content = $"{Board.Zoom:P0}";
}
