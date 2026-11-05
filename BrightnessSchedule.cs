using System.IO;

namespace DisplayBrightness;

internal sealed record BrightnessPeriod(TimeOnly Start, int Percent,
    Dictionary<string, int>? MonitorPercents = null)
{
    public int TargetFor(string monitorId, SyncPreferences sync)
    {
        if (sync.Enabled)
            return SyncPreferences.Adjust(Percent, sync.CorrectionFor(monitorId));

        return MonitorPercents?.GetValueOrDefault(monitorId, Percent) ?? Percent;
    }
}

internal sealed record BrightnessSchedule(bool Enabled, BrightnessPeriod[] Periods)
{
    private const string Filename = "schedule.json";

    public static BrightnessSchedule Empty { get; } = new(false, []);

    public ScheduledPeriod? ActiveAt(DateTime now)
    {
        if (!Enabled || Periods.Length == 0)
            return null;

        TimeOnly time = TimeOnly.FromDateTime(now);
        BrightnessPeriod latest = Periods[0];
        BrightnessPeriod? activeToday = null;
        foreach (BrightnessPeriod period in Periods)
        {
            if (period.Start > latest.Start)
                latest = period;
            if (period.Start <= time && (activeToday is null || period.Start > activeToday.Start))
                activeToday = period;
        }

        return activeToday is null ? new(latest, now.Date.AddDays(-1)) : new(activeToday, now.Date);
    }

    public string? ValidationError()
    {
        if (Periods is null || (Enabled && Periods.Length == 0))
            return "请至少添加一个开始时间，或关闭定时亮度。";

        var rowsByStart = new Dictionary<TimeOnly, int>();
        for (int i = 0; i < Periods.Length; i++)
        {
            BrightnessPeriod period = Periods[i];
            if (period is null || period.Percent is < 0 or > 100 ||
                period.MonitorPercents?.Values.Any(value => value is < 0 or > 100) == true)
                return $"第 {i + 1} 行的亮度无效。";
            if (rowsByStart.TryGetValue(period.Start, out int earlier))
                return $"第 {earlier} 行与第 {i + 1} 行的开始时间重复。";
            rowsByStart.Add(period.Start, i + 1);
        }

        return null;
    }

    public static BrightnessSchedule Load()
    {
        var schedule = LocalSettings.Load(Filename, Empty);
        if (schedule.ValidationError() is not null)
            throw new InvalidDataException("定时亮度配置无效。");
        return schedule;
    }

    public void Save() => LocalSettings.Save(Filename, this);
}

internal sealed record ScheduledPeriod(BrightnessPeriod Period, DateTime StartDate);
