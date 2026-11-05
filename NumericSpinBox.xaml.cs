using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace DisplayBrightness;

public partial class NumericSpinBox : UserControl
{
    private int? confirmedNumber;

    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(NumericSpinBox),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, TextUpdated));

    public static readonly DependencyProperty SuffixProperty = DependencyProperty.Register(
        nameof(Suffix), typeof(string), typeof(NumericSpinBox), new PropertyMetadata(""));

    public NumericSpinBox()
    {
        InitializeComponent();
        SetResourceReference(BorderBrushProperty, "LineBrush");
    }

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public string Suffix
    {
        get => (string)GetValue(SuffixProperty);
        set => SetValue(SuffixProperty, value);
    }

    public int Minimum { get; set; }
    public int Maximum { get; set; } = 100;
    public string NumberFormat { get; set; } = "0";
    public bool Wrap { get; set; }
    public bool ConfirmOnFocusLost { get; set; }
    public int? Number => ParseNumber() is int number && number >= Minimum && number <= Maximum ? number : null;

    public event EventHandler? TextChanged;
    public event RoutedEventHandler? Confirmed;

    public void FocusEditor() => Editor.Focus();

    private int? ParseNumber() => int.TryParse(Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
        ? value : null;

    private static void TextUpdated(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        var spin = (NumericSpinBox)sender;
        spin.UpdateBorder();
        spin.TextChanged?.Invoke(spin, EventArgs.Empty);
    }

    protected override void OnIsKeyboardFocusWithinChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnIsKeyboardFocusWithinChanged(e);
        UpdateBorder();
        if (!ConfirmOnFocusLost)
            return;

        if (IsKeyboardFocusWithin)
            confirmedNumber = Number;
        else if (Number is int number && number != confirmedNumber)
            Confirm();
    }

    private void UpdateBorder() => SetResourceReference(BorderBrushProperty,
        Number is null ? "ErrorBrush" : IsKeyboardFocusWithin ? "AccentBrush" : "LineBrush");

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        switch (e.Key)
        {
            case Key.Up:
            case Key.Down:
                e.Handled = true;
                Step(e.Key == Key.Up ? 1 : -1);
                break;
            case Key.Enter when Confirmed is not null:
                e.Handled = true;
                Confirm();
                break;
        }
    }

    private void Confirm()
    {
        confirmedNumber = Number;
        if (confirmedNumber is int number)
            SetNumber(number);
        Confirmed?.Invoke(this, new RoutedEventArgs());
    }

    internal void SetNumber(int value)
    {
        SetCurrentValue(TextProperty, value.ToString(NumberFormat, CultureInfo.InvariantCulture));
        GetBindingExpression(TextProperty)?.UpdateSource();
    }

    private void Step(int direction)
    {
        int current = Math.Clamp(ParseNumber() ?? 0, Minimum, Maximum);
        int next = current + direction;
        if (Wrap && next < Minimum)
            next = Maximum;
        else if (Wrap && next > Maximum)
            next = Minimum;

        SetNumber(Math.Clamp(next, Minimum, Maximum));
        Editor.SelectAll();
    }

    private void IncreaseClicked(object sender, RoutedEventArgs e)
    {
        FocusEditor();
        Step(1);
    }

    private void DecreaseClicked(object sender, RoutedEventArgs e)
    {
        FocusEditor();
        Step(-1);
    }

    private void EditorFocused(object sender, KeyboardFocusChangedEventArgs e) => Editor.SelectAll();
}
