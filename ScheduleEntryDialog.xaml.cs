using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Automation.Peers;

namespace DisplayBrightness;

public partial class ScheduleEntryDialog : Window
{
    private readonly BrightnessSchedule schedule;
    private readonly BrightnessPeriod? original;
    private readonly (string Id, string Name, int DefaultPercent)[] monitors;
    private readonly ScheduleBrightnessField[] fields;
    private readonly bool independent;

    internal ScheduleEntryDialog(BrightnessSchedule schedule, BrightnessPeriod? original,
        bool syncEnabled, int basePercent, (string Id, string Name, int DefaultPercent)[] monitors)
    {
        this.schedule = schedule;
        this.original = original;
        this.monitors = monitors;
        independent = !syncEnabled && monitors.Length > 0;
        fields = independent
            ? monitors.Select(monitor => new ScheduleBrightnessField(monitor.Name,
                original?.MonitorPercents?.GetValueOrDefault(monitor.Id, original.Percent)
                    ?? original?.Percent ?? monitor.DefaultPercent)).ToArray()
            : [new ScheduleBrightnessField(syncEnabled ? "基准亮度" : "默认亮度", original?.Percent ?? basePercent)];

        InitializeComponent();
        Heading.Text = original is null ? "添加定时亮度" : "编辑定时亮度";
        DeleteButton.Visibility = original is null ? Visibility.Collapsed : Visibility.Visible;
        BrightnessFields.ItemsSource = fields;
        var start = original?.Start ?? new TimeOnly((DateTime.Now.Hour + 1) % 24, 0);
        HourPicker.Text = start.Hour.ToString("D2", CultureInfo.InvariantCulture);
        MinutePicker.Text = start.Minute.ToString("D2", CultureInfo.InvariantCulture);
    }

    internal BrightnessSchedule UpdatedSchedule { get; private set; } = BrightnessSchedule.Empty;

    private void DialogRendered(object? sender, EventArgs e) => HourPicker.FocusEditor();

    private void SaveClicked(object sender, RoutedEventArgs e)
    {
        if (HourPicker.Number is not int hour || MinutePicker.Number is not int minute)
        {
            ShowError("小时请输入 0 到 23，分钟请输入 0 到 59。");
            return;
        }

        var start = new TimeOnly(hour, minute);
        if (schedule.Periods.Any(period => !ReferenceEquals(period, original) && period.Start == start))
        {
            ShowError("这个时间已有一条计划，请选择其他时间。");
            return;
        }

        int[] values = new int[fields.Length];
        for (int i = 0; i < fields.Length; i++)
        {
            if (!int.TryParse(fields[i].Value, out values[i]) || values[i] is < 0 or > 100)
            {
                ShowError($"「{fields[i].Name}」请输入 0 到 100 的整数。");
                return;
            }
        }

        var overrides = original?.MonitorPercents is { } previous
            ? new Dictionary<string, int>(previous) : new Dictionary<string, int>();
        if (independent)
        {
            for (int i = 0; i < monitors.Length; i++)
                overrides[monitors[i].Id] = values[i];
        }
        int percent = independent ? original?.Percent ?? values[0] : values[0];
        var entry = new BrightnessPeriod(start, percent, overrides.Count == 0 ? null : overrides);
        var periods = schedule.Periods.Where(period => !ReferenceEquals(period, original))
            .Append(entry).OrderBy(period => period.Start).ToArray();
        SavePlan(schedule with { Periods = periods });
    }

    private void DeleteClicked(object sender, RoutedEventArgs e)
    {
        var periods = schedule.Periods.Where(period => !ReferenceEquals(period, original)).ToArray();
        SavePlan(new BrightnessSchedule(schedule.Enabled && periods.Length > 0, periods));
    }

    private void SavePlan(BrightnessSchedule updated)
    {
        try
        {
            updated.Save();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowError("无法保存计划，请检查本地应用数据目录的写入权限。");
            return;
        }

        UpdatedSchedule = updated;
        DialogResult = true;
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
        UIElementAutomationPeer.FromElement(ErrorText)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }

}

internal sealed class ScheduleBrightnessField(string name, int initialPercent) : INotifyPropertyChanged
{
    private string value = initialPercent.ToString(CultureInfo.InvariantCulture);
    private int percent = initialPercent;
    public string Name { get; } = name;
    public int Percent
    {
        get => percent;
        set => Value = value.ToString(CultureInfo.InvariantCulture);
    }

    public string Value
    {
        get => value;
        set
        {
            if (this.value == value)
                return;

            this.value = value;
            if (int.TryParse(value, out int valid) && valid is >= 0 and <= 100 && valid != percent)
            {
                percent = valid;
                PropertyChanged?.Invoke(this, new(nameof(Percent)));
            }
            PropertyChanged?.Invoke(this, new(nameof(Value)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
