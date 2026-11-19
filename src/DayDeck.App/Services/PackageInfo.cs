using System.Runtime.InteropServices;

namespace DayDeck.App.Services;

/// <summary>
/// Microsoft Store(MSIX) 패키지로 설치되어 실행 중인지. 패키지 판은 Store가 업데이트를 맡고,
/// 설치 폴더가 읽기 전용이며, 로그인 자동 실행은 레지스트리 대신 StartupTask로 한다.
/// </summary>
internal static partial class PackageInfo
{
    private const int ErrorInsufficientBuffer = 122;

    private static readonly Lazy<string?> FamilyNameValue = new(ReadFamilyName);

    /// <summary>패키지 패밀리 이름 (예: <c>DayDeck_8wekyb3d8bbwe</c>). 패키지가 아니면 null.</summary>
    public static string? FamilyName => FamilyNameValue.Value;

    public static bool IsPackaged => FamilyName is not null;

    private static unsafe string? ReadFamilyName()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }
        try
        {
            var length = 0u;
            var result = GetCurrentPackageFamilyName(&length, null);
            // 패키지가 아니면 APPMODEL_ERROR_NO_PACKAGE(15700)가 온다.
            if (result != ErrorInsufficientBuffer)
            {
                return null;
            }
            var buffer = new char[length];
            fixed (char* p = buffer)
            {
                result = GetCurrentPackageFamilyName(&length, p);
            }
            return result == 0 ? new string(buffer, 0, (int)length - 1) : null;
        }
        catch (EntryPointNotFoundException)
        {
            return null;
        }
    }

    [LibraryImport("kernel32.dll")]
    private static unsafe partial int GetCurrentPackageFamilyName(uint* packageFamilyNameLength, char* packageFamilyName);
}
