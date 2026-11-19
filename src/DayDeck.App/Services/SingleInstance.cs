using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using DayDeck.Core.Diagnostics;

namespace DayDeck.App.Services;

/// <summary>
/// 사용자 세션당 한 개만 실행되도록 한다. 두 번째 실행은 명명된 파이프로
/// 첫 번째 인스턴스에 명령(예: 창 열기)을 전달하고 종료한다.
/// </summary>
internal sealed class SingleInstance : IDisposable
{
    public const string ShowCommand = "show";
    public const string QuickAddCommand = "quickadd";
    public const string TodayCommand = "today";

    private static readonly string Name = $"DayDeck-{Environment.UserName}-{Process.GetCurrentProcess().SessionId}";

    private readonly Mutex _mutex;
    private bool _disposed;
    private readonly CancellationTokenSource _cts = new();

    public SingleInstance()
    {
        _mutex = new Mutex(initiallyOwned: true, @"Local\" + Name, out var createdNew);
        IsFirstInstance = createdNew;
    }

    public bool IsFirstInstance { get; }

    public static void SendToFirstInstance(string command)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", Name, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            client.Connect(timeout: 2000);
            using var writer = new StreamWriter(client);
            writer.WriteLine(command);
        }
        catch (Exception ex) when (ex is TimeoutException or IOException)
        {
            // 첫 인스턴스가 막 종료되는 중일 수 있다. 무시한다.
        }
    }

    /// <summary>다른 인스턴스가 보낸 명령을 받기 시작한다. 콜백은 스레드풀에서 호출된다.</summary>
    public void StartListening(Action<string> onCommand)
    {
        if (!IsFirstInstance)
        {
            throw new InvalidOperationException("첫 번째 인스턴스만 명령을 받을 수 있습니다.");
        }

        _ = ListenAsync(onCommand, _cts.Token);
    }

    private static async Task ListenAsync(Action<string> onCommand, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(
                    Name, PipeDirection.In, maxNumberOfServerInstances: 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(ct).ConfigureAwait(false);

                using var reader = new StreamReader(server);
                var command = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(command))
                {
                    onCommand(command.Trim());
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (IOException ex)
            {
                Log.Warn("단일 인스턴스 파이프 오류", ex);
                await Task.Delay(500, CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    public void Dispose()
    {
        // 업데이트 후 다시 시작할 때 Main이 먼저 놓아 주므로 두 번 불릴 수 있다.
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _cts.Cancel();
        _cts.Dispose();
        if (IsFirstInstance)
        {
            _mutex.ReleaseMutex();
        }
        _mutex.Dispose();
    }
}
