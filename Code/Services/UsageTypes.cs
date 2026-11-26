namespace GameMaker.Services;

// 사용량 미터 공용 모양 — 한도 창 하나 (5시간 창 · 주간 창 …). Claude 와 GPT(Codex) 가 출처는 달라도 같은 모양으로 화면에 간다.
// Label = 미터 왼쪽 글 ("5 Hours" · "Weekly"), WindowMinutes = 창 길이 (모르면 null)
public sealed record UsageWindow(string Label, double UsedPercent, DateTime? ResetsAtUtc, double? WindowMinutes)
{
    // 다시 차는 시각이 지난 값은 더 이상 사실이 아님
    public bool IsLive(DateTime nowUtc)
    {
        return null == ResetsAtUtc || ResetsAtUtc.Value > nowUtc;
    }

    // 창 길이 → 라벨, 줄임말 없이 (300분 = "5 Hours", 7일 = "Weekly")
    public static string LabelOf(double minutes)
    {
        TimeSpan window = TimeSpan.FromMinutes(minutes);
        if (TimeSpan.FromDays(7) == window)
        {
            return "Weekly";
        }

        if (TimeSpan.FromDays(1) == window)
        {
            return "Daily";
        }

        if (window > TimeSpan.FromDays(1) && 0 == window.TotalHours % 24)
        {
            return $"{window.TotalDays:0} Days";
        }

        return $"{window.TotalHours:0.#} Hours";
    }
}

// 읽은 결과 — Error 가 있으면 못 읽은 이유 (화면 툴팁에 보임), Details = 요금제·크레딧 같은 덧붙임 줄
public sealed record UsageSnapshot(IReadOnlyList<UsageWindow> Windows, DateTime ReceivedUtc, IReadOnlyList<string> Details, string? Error)
{
    public static UsageSnapshot Fail(string error)
    {
        return new UsageSnapshot(Array.Empty<UsageWindow>(), DateTime.UtcNow, Array.Empty<string>(), error);
    }
}
