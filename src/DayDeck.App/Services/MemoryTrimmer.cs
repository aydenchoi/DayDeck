using System.Runtime;

namespace DayDeck.App.Services;

/// <summary>
/// 창을 닫고 트레이에만 남을 때 관리 힙을 압축하고 Working Set을 OS에 돌려준다.
/// 다음에 필요한 페이지는 다시 읽혀 오므로 동작에는 영향이 없다.
/// </summary>
internal static class MemoryTrimmer
{
    public static void Trim()
    {
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);

        NativeMethods.SetProcessWorkingSetSize(NativeMethods.GetCurrentProcess(), -1, -1);
    }
}
