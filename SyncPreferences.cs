using System.IO;

namespace DisplayBrightness;

internal sealed record MonitorPreferences(string Name, string Position, int? ManualPercent = null);

internal sealed record SyncPreferences(bool Enabled, int? BasePercent, Dictionary<string, int> Corrections,
    Dictionary<string, MonitorPreferences>? Monitors = null)
{
    private const string Filename = "sync.json";
    public static SyncPreferences Default => new(false, null, new(StringComparer.OrdinalIgnoreCase));

    public static SyncPreferences Load()
    {
        var preferences = LocalSettings.Load(Filename, Default);
        if (preferences.BasePercent is < 0 or > 100 || preferences.Corrections is null ||
            preferences.Corrections.Values.Any(value => value is < -100 or > 100) ||
            preferences.Monitors?.Values.Any(monitor => monitor is null || monitor.ManualPercent is < 0 or > 100) == true)
            throw new InvalidDataException("同步亮度设置无效。");

        return preferences with
        {
            Corrections = new(preferences.Corrections, StringComparer.OrdinalIgnoreCase),
            Monitors = preferences.CopyMonitors()
        };
    }

    public void Save() => LocalSettings.Save(Filename, this);

    public int CorrectionFor(string monitorId) => Corrections.GetValueOrDefault(monitorId);

    public int? ManualPercentFor(string monitorId) => Monitors?.GetValueOrDefault(monitorId)?.ManualPercent;

    public SyncPreferences RecordMonitors(IEnumerable<DdcMonitor> monitors)
    {
        var records = CopyMonitors();
        bool changed = false;
        foreach (var monitor in monitors)
        {
            var record = new MonitorPreferences(monitor.Description, monitor.Position, ManualPercentFor(monitor.Id));
            if (records.GetValueOrDefault(monitor.Id) == record)
                continue;
            records[monitor.Id] = record;
            changed = true;
        }

        return changed ? this with { Monitors = records } : this;
    }

    public SyncPreferences WithManualPercent(DdcMonitor monitor, int percent)
    {
        var records = CopyMonitors();
        records[monitor.Id] = new(monitor.Description, monitor.Position, percent);
        return this with { Monitors = records };
    }

    private Dictionary<string, MonitorPreferences> CopyMonitors() => Monitors is null
        ? new(StringComparer.OrdinalIgnoreCase) : new(Monitors, StringComparer.OrdinalIgnoreCase);

    public static int Adjust(int basePercent, int correction) => Math.Clamp(basePercent + correction, 0, 100);
}
