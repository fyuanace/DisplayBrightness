using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace DisplayBrightness;

public partial class MonitorCard : UserControl
{
    private bool sync;
    private int basePercent;
    private bool scheduleEnabled;
    private int? scheduledTarget;

    internal MonitorCard(DdcMonitor monitor, int correction)
    {
        Monitor = monitor;
        InitializeComponent();
        MonitorName.Text = monitor.Description;
        MonitorDescription.Text = monitor.Position;
        AutomationProperties.SetName(ManualSlider, $"{monitor.Name}目标亮度");
        AutomationProperties.SetName(ManualApply, $"应用{monitor.Name}亮度");
        AutomationProperties.SetName(CorrectionInput, $"{monitor.Name}亮度修正");
        CorrectionInput.Text = correction.ToString("+0;-0;0", CultureInfo.InvariantCulture);
    }

    internal DdcMonitor Monitor { get; }
    internal Brightness? Reading { get; private set; }
    internal string Status { get; private set; } = "";
    internal int ManualPercent => (int)ManualSlider.Value;
    internal int? Correction => int.TryParse(CorrectionInput.Text, NumberStyles.Integer,
        CultureInfo.InvariantCulture, out int value) && value is >= -100 and <= 100 ? value : null;

    internal event EventHandler? ApplyRequested;
    internal event EventHandler? CorrectionEdited;

    internal void SetManualTarget(int percent) => ManualSlider.Value = percent;

    internal void SetReading(Brightness? reading, string status, bool preserveDraft = false)
    {
        bool hasDraft = Reading is Brightness previous && ManualSlider.Value != previous.Percent;
        Reading = reading;
        CurrentValue.Text = reading is Brightness value ? $"{value.Percent}%" : "—";
        if (reading is Brightness current && (!preserveDraft || !hasDraft))
            ManualSlider.Value = current.Percent;
        Status = status;
        UpdateTarget();
    }

    internal void UpdateMode(bool useSync, int percent, bool useSchedule, int? planTarget)
    {
        sync = useSync;
        basePercent = percent;
        scheduleEnabled = useSchedule;
        scheduledTarget = planTarget;
        ManualControls.Visibility = sync ? Visibility.Collapsed : Visibility.Visible;
        CorrectionControls.Visibility = sync ? Visibility.Visible : Visibility.Collapsed;
        ManualControls.IsEnabled = !scheduleEnabled;
        TargetLabel.Text = scheduleEnabled ? "计划亮度" : "目标亮度";
        UpdateTarget();
    }

    private void UpdateTarget()
    {
        if (TargetValue is null || ManualApply is null || CorrectionInput is null)
            return;

        int? target = scheduleEnabled ? scheduledTarget
            : sync ? (Correction is int correction ? SyncPreferences.Adjust(basePercent, correction) : null)
            : Reading is null ? null : ManualPercent;
        TargetValue.Text = target is int percent ? $"{percent}%" : "—";
        ManualSlider.IsEnabled = Reading.HasValue;
        ManualApply.IsEnabled = Reading is Brightness current && current.Percent != ManualPercent;
        if (Correction.HasValue)
            CorrectionInput.ClearValue(BorderBrushProperty);
        else
            CorrectionInput.SetResourceReference(BorderBrushProperty, "ErrorBrush");
    }

    private void ManualChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => UpdateTarget();

    private void CorrectionChanged(object sender, TextChangedEventArgs e)
    {
        UpdateTarget();
        CorrectionEdited?.Invoke(this, EventArgs.Empty);
    }

    private void ApplyClicked(object sender, RoutedEventArgs e) => ApplyRequested?.Invoke(this, EventArgs.Empty);
}
