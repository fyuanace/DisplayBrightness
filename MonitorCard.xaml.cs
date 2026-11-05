using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace DisplayBrightness;

public partial class MonitorCard : UserControl
{
    internal MonitorCard(DdcMonitor monitor, int correction, int manualPercent)
    {
        Monitor = monitor;
        InitializeComponent();
        MonitorName.Text = monitor.Description;
        MonitorDescription.Text = monitor.Position;
        AutomationProperties.SetName(ManualInput, $"{monitor.Name}亮度调节");
        AutomationProperties.SetName(CorrectionInput, $"{monitor.Name}亮度修正");
        CorrectionInput.SetNumber(correction);
        ManualInput.SetNumber(manualPercent);
    }

    internal DdcMonitor Monitor { get; }
    internal Brightness? Reading { get; private set; }
    internal string Status { get; private set; } = "";
    internal int? ManualPercent => ManualInput.Number;
    internal int? Correction => CorrectionInput.Number;

    internal event EventHandler? ManualConfirmed;
    internal event EventHandler? InputEdited;
    internal event EventHandler? CorrectionConfirmed;

    internal void SetManualTarget(int percent) => ManualInput.SetNumber(percent);

    internal void SetReading(Brightness? reading, string status)
    {
        Reading = reading;
        CurrentValue.Text = reading is Brightness value ? $"{value.Percent}%" : "—";
        Status = status;
    }

    internal void UpdateMode(bool useSync, bool useSchedule)
    {
        InputLabel.Text = useSync ? "亮度修正" : "亮度调节";
        ManualInput.Visibility = useSync ? Visibility.Collapsed : Visibility.Visible;
        CorrectionInput.Visibility = useSync ? Visibility.Visible : Visibility.Collapsed;
        ManualInput.IsEnabled = !useSchedule;
    }

    private void InputChanged(object? sender, EventArgs e) => InputEdited?.Invoke(this, EventArgs.Empty);

    private void CorrectionInputConfirmed(object sender, RoutedEventArgs e) =>
        CorrectionConfirmed?.Invoke(this, EventArgs.Empty);

    private void ManualInputConfirmed(object sender, RoutedEventArgs e) =>
        ManualConfirmed?.Invoke(this, EventArgs.Empty);
}
