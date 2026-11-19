namespace DayDeck.Core.Calendar;

/// <summary>
/// 한국 음력의 특정 날짜(설날, 부처님오신날, 추석)를 양력으로 구한다.
/// 표를 넣는 대신 천문 계산(J. Meeus, Astronomical Algorithms)으로 합삭(새달) 시각과 중기(절기) 시각을 구하고,
/// 한국 표준시(UTC+9) 날짜로 판단한다. 음력 n월은 그 달의 중기를 포함하는 달이다
/// (1월: 우수 330°, 4월: 소만 60°, 8월: 추분 180°).
/// 정확도는 수 분 이내이며, 알려진 날짜로 단위 테스트한다.
/// </summary>
public static class LunarCalendar
{
    private const double SynodicMonth = 29.530588861;
    private static readonly TimeSpan Kst = TimeSpan.FromHours(9);

    // 2033년 음력 문제: 윤11월이 들어가 "우수를 포함한 달 = 1월" 규칙이 맞지 않는 해.
    private static readonly Dictionary<int, DateOnly> SeollalExceptions = new()
    {
        [2034] = new DateOnly(2034, 2, 19),
    };

    /// <summary>음력 1월 1일.</summary>
    public static DateOnly Seollal(int year) =>
        SeollalExceptions.TryGetValue(year, out var date) ? date : MonthStartContainingTerm(year, 330, approxMonth: 2, approxDay: 19);

    /// <summary>음력 4월 8일.</summary>
    public static DateOnly BuddhasBirthday(int year) => MonthStartContainingTerm(year, 60, approxMonth: 5, approxDay: 21).AddDays(7);

    /// <summary>음력 8월 15일.</summary>
    public static DateOnly Chuseok(int year) => MonthStartContainingTerm(year, 180, approxMonth: 9, approxDay: 23).AddDays(14);

    /// <summary>
    /// 양력 날짜의 음력 날짜 (달력의 음력 표시용). 그 날을 포함하는 음력 달의 1일은 직전 합삭(한국 날짜),
    /// 달 번호는 그 달에 든 중기로 정하고, 중기가 없는 달은 앞 달 번호의 윤달로 본다.
    /// (한 해에 중기 없는 달이 둘 생기는 드문 해(예: 2033년)는 첫 달만 윤달이어야 하지만 여기서는 구분하지 않는다.)
    /// </summary>
    public static LunarDate FromSolar(DateOnly date)
    {
        var (start, next) = LunarMonthAround(date);
        var day = date.DayNumber - start.DayNumber + 1;
        if (MonthNumber(start, next) is { } month)
        {
            return new LunarDate(month, day, false);
        }
        var (previousStart, _) = LunarMonthAround(start.AddDays(-1));
        var previous = MonthNumber(previousStart, start) ?? 1;
        return new LunarDate(previous, day, true);
    }

    /// <summary>날짜를 포함하는 음력 달의 1일과 다음 달 1일 (한국 날짜).</summary>
    private static (DateOnly Start, DateOnly Next) LunarMonthAround(DateOnly date)
    {
        var k = Math.Floor((date.Year + (date.DayOfYear - 1) / 365.25 - 2000) * 12.3685);
        for (var i = 2; i >= -3; i--)
        {
            var start = KstDate(NewMoonJd(k + i));
            if (start <= date)
            {
                return (start, KstDate(NewMoonJd(k + i + 1)));
            }
        }
        throw new InvalidOperationException($"{date} 음력 달을 찾지 못했습니다.");
    }

    /// <summary>음력 달 [start, next)에 든 중기로 정한 달 번호. 중기가 없으면 null (윤달).</summary>
    private static int? MonthNumber(DateOnly start, DateOnly next)
    {
        // 한국 날짜 start 0시 = UTC 전날 15시
        var startJd = ToJd(start.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc) - Kst);
        var longitude = SunLongitude(startJd);
        var term = Math.Ceiling(longitude / 30) * 30 % 360;
        var termJd = SolarTermJd(term, startJd + (term - longitude + 360) % 360 / 360 * 365.2422);
        if (KstDate(termJd) >= next)
        {
            return null;
        }
        // 우수(330°) → 1월, 춘분(0°) → 2월, … 동지(270°) → 11월, 대한 다음 중기 대한(300°) → 12월
        return ((int)Math.Round(term / 30) + 1) % 12 + 1;
    }

    /// <summary>해당 중기(태양 황경)를 포함하는 음력 달의 1일 (한국 날짜).</summary>
    private static DateOnly MonthStartContainingTerm(int year, double longitude, int approxMonth, int approxDay)
    {
        var termJd = SolarTermJd(longitude, ToJd(new DateTime(year, approxMonth, approxDay, 0, 0, 0, DateTimeKind.Utc)));
        var termDate = KstDate(termJd);

        // 중기 직전의 합삭을 찾는다 (한국 날짜 기준으로 중기 날짜 이하인 마지막 합삭).
        var k = Math.Floor((year + (approxMonth - 1) / 12.0 - 2000) * 12.3685);
        for (var i = 1; i >= -2; i--)
        {
            var newMoon = KstDate(NewMoonJd(k + i));
            if (newMoon <= termDate)
            {
                return newMoon;
            }
        }
        throw new InvalidOperationException($"{year}년 음력 달을 찾지 못했습니다.");
    }

    private static DateOnly KstDate(double jdUt) => DateOnly.FromDateTime(FromJd(jdUt) + Kst);

    /// <summary>k번째 합삭의 율리우스일 (UT). Meeus 49장.</summary>
    internal static double NewMoonJd(double k)
    {
        var t = k / 1236.85;
        var t2 = t * t;
        var t3 = t2 * t;
        var t4 = t3 * t;

        var jde = 2451550.09766 + SynodicMonth * k + 0.00015437 * t2 - 0.000000150 * t3 + 0.00000000073 * t4;
        var e = 1 - 0.002516 * t - 0.0000074 * t2;
        var m = Rad(2.5534 + 29.10535670 * k - 0.0000014 * t2 - 0.00000011 * t3);
        var mp = Rad(201.5643 + 385.81693528 * k + 0.0107582 * t2 + 0.00001238 * t3 - 0.000000058 * t4);
        var f = Rad(160.7108 + 390.67050284 * k - 0.0016118 * t2 - 0.00000227 * t3 + 0.000000011 * t4);
        var om = Rad(124.7746 - 1.56375588 * k + 0.0020672 * t2 + 0.00000215 * t3);

        var correction =
            -0.40720 * Math.Sin(mp)
            + 0.17241 * e * Math.Sin(m)
            + 0.01608 * Math.Sin(2 * mp)
            + 0.01039 * Math.Sin(2 * f)
            + 0.00739 * e * Math.Sin(mp - m)
            - 0.00514 * e * Math.Sin(mp + m)
            + 0.00208 * e * e * Math.Sin(2 * m)
            - 0.00111 * Math.Sin(mp - 2 * f)
            - 0.00057 * Math.Sin(mp + 2 * f)
            + 0.00056 * e * Math.Sin(2 * mp + m)
            - 0.00042 * Math.Sin(3 * mp)
            + 0.00042 * e * Math.Sin(m + 2 * f)
            + 0.00038 * e * Math.Sin(m - 2 * f)
            - 0.00024 * e * Math.Sin(2 * mp - m)
            - 0.00017 * Math.Sin(om)
            - 0.00007 * Math.Sin(mp + 2 * m)
            + 0.00004 * Math.Sin(2 * mp - 2 * f)
            + 0.00004 * Math.Sin(3 * m)
            + 0.00003 * Math.Sin(mp + m - 2 * f)
            + 0.00003 * Math.Sin(2 * mp + 2 * f)
            - 0.00003 * Math.Sin(mp + m + 2 * f)
            + 0.00003 * Math.Sin(mp - m + 2 * f)
            - 0.00002 * Math.Sin(mp - m - 2 * f)
            - 0.00002 * Math.Sin(3 * mp + m)
            + 0.00002 * Math.Sin(4 * mp);

        double[] a =
        [
            299.77 + 0.107408 * k - 0.009173 * t2, 251.88 + 0.016321 * k, 251.83 + 26.651886 * k, 349.42 + 36.412478 * k,
            84.66 + 18.206239 * k, 141.74 + 53.303771 * k, 207.14 + 2.453732 * k, 154.84 + 7.306860 * k,
            34.52 + 27.261239 * k, 207.19 + 0.121824 * k, 291.34 + 1.844379 * k, 161.72 + 24.198154 * k,
            239.56 + 25.513099 * k, 331.55 + 3.592518 * k,
        ];
        double[] coefficients =
        [
            0.000325, 0.000165, 0.000164, 0.000126, 0.000110, 0.000062, 0.000060,
            0.000056, 0.000047, 0.000042, 0.000040, 0.000037, 0.000035, 0.000023,
        ];
        for (var i = 0; i < a.Length; i++)
        {
            correction += coefficients[i] * Math.Sin(Rad(a[i]));
        }

        // 역학시(TT) → 세계시(UT). 2020~2040년 ΔT는 약 70초.
        return jde + correction - 70.0 / 86400;
    }

    /// <summary>태양의 겉보기 황경이 <paramref name="longitude"/>가 되는 율리우스일 (UT). 근사값에서 반복으로 맞춘다.</summary>
    internal static double SolarTermJd(double longitude, double approxJd)
    {
        var jd = approxJd;
        for (var i = 0; i < 20; i++)
        {
            var diff = NormalizeDegrees(longitude - SunLongitude(jd) + 180) - 180;
            jd += diff / 360 * 365.2422;
            if (Math.Abs(diff) < 1e-6)
            {
                break;
            }
        }
        return jd;
    }

    /// <summary>태양의 겉보기 황경 (도). Meeus 25장 저정밀 식, 오차 약 0.01°.</summary>
    internal static double SunLongitude(double jd)
    {
        var t = (jd - 2451545.0) / 36525;
        var l0 = 280.46646 + 36000.76983 * t + 0.0003032 * t * t;
        var m = Rad(357.52911 + 35999.05029 * t - 0.0001537 * t * t);
        var c = (1.914602 - 0.004817 * t - 0.000014 * t * t) * Math.Sin(m)
                + (0.019993 - 0.000101 * t) * Math.Sin(2 * m)
                + 0.000289 * Math.Sin(3 * m);
        var omega = Rad(125.04 - 1934.136 * t);
        return NormalizeDegrees(l0 + c - 0.00569 - 0.00478 * Math.Sin(omega));
    }

    internal static double ToJd(DateTime utc) => utc.ToOADate() + 2415018.5;

    internal static DateTime FromJd(double jd) => DateTime.FromOADate(jd - 2415018.5);

    private static double Rad(double degrees) => NormalizeDegrees(degrees) * Math.PI / 180;

    private static double NormalizeDegrees(double degrees)
    {
        var d = degrees % 360;
        return d < 0 ? d + 360 : d;
    }
}

/// <summary>음력 날짜.</summary>
public readonly record struct LunarDate(int Month, int Day, bool IsLeap)
{
    /// <summary>"9.18", 윤달이면 "윤6.1".</summary>
    public override string ToString() => $"{(IsLeap ? "윤" : "")}{Month}.{Day}";
}

