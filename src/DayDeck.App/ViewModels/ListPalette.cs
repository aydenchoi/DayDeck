namespace DayDeck.App.ViewModels;

/// <summary>목록 색 선택지. 밝은/어두운 테마 모두에서 흰 글자와 구분되는 중간 밝기의 색만 고른다.</summary>
public static class ListPalette
{
    public static readonly (string Name, string Hex)[] Colors =
    [
        ("파랑", "#2B6CD4"),
        ("초록", "#0F7B0F"),
        ("보라", "#7A3FBF"),
        ("주황", "#C75B00"),
        ("청록", "#00807A"),
        ("빨강", "#C42B1C"),
        ("분홍", "#C2386F"),
        ("노랑", "#B58900"),
        ("하늘", "#0A8BC2"),
        ("갈색", "#8A5A2B"),
        ("남색", "#3D4FA8"),
        ("회색", "#6B6B6B"),
    ];
}
