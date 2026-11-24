using System.Runtime.InteropServices;

namespace DayDeck.App.Services;

internal static partial class NativeMethods
{
    public const int ASFW_ANY = -1;

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AllowSetForegroundWindow(int processId);

    [LibraryImport("kernel32.dll")]
    public static partial nint GetCurrentProcess();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetProcessWorkingSetSize(nint process, nint minimumWorkingSetSize, nint maximumWorkingSetSize);

    /// <summary>
    /// GetProcessMemoryInfo가 채우는 PROCESS_MEMORY_COUNTERS_EX2. PrivateWorkingSetSize는 Windows 10 1809부터 채워진다.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct ProcessMemoryCountersEx2
    {
        public uint Cb;
        public uint PageFaultCount;
        public nuint PeakWorkingSetSize;
        public nuint WorkingSetSize;
        public nuint QuotaPeakPagedPoolUsage;
        public nuint QuotaPagedPoolUsage;
        public nuint QuotaPeakNonPagedPoolUsage;
        public nuint QuotaNonPagedPoolUsage;
        public nuint PagefileUsage;
        public nuint PeakPagefileUsage;
        public nuint PrivateUsage;
        public nuint PrivateWorkingSetSize;
        public ulong SharedCommitUsage;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "K32GetProcessMemoryInfo", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetProcessMemoryInfo(nint process, ref ProcessMemoryCountersEx2 counters, uint size);

    /// <summary>
    /// 이 프로세스의 개인 작업 집합(바이트). 작업 관리자 "세부 정보"의 "메모리(활성 개인 작업 집합)"과 같은 값이다.
    /// Working Set과 달리 다른 프로세스와 함께 쓰는 DLL·런타임 페이지는 세지 않는다. 얻을 수 없으면 null.
    /// </summary>
    public static long? GetPrivateWorkingSet()
    {
        var counters = new ProcessMemoryCountersEx2 { Cb = (uint)Marshal.SizeOf<ProcessMemoryCountersEx2>() };
        if (!GetProcessMemoryInfo(GetCurrentProcess(), ref counters, counters.Cb) || counters.PrivateWorkingSetSize == 0)
        {
            return null;
        }

        return (long)counters.PrivateWorkingSetSize;
    }

    [LibraryImport("dwmapi.dll")]
    public static partial int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);
}
