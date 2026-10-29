using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace CassiniFlow.Desktop.Controls;

public partial class NumberStepper : UserControl
{
    public static readonly StyledProperty<double> ValueProperty =
        AvaloniaProperty.Register<NumberStepper, double>(
            nameof(Value),
            defaultValue: 0,
            defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<double> MinimumProperty =
        AvaloniaProperty.Register<NumberStepper, double>(
            nameof(Minimum),
            defaultValue: double.MinValue);

    public static readonly StyledProperty<double> MaximumProperty =
        AvaloniaProperty.Register<NumberStepper, double>(
            nameof(Maximum),
            defaultValue: double.MaxValue);

    public static readonly StyledProperty<double> IncrementProperty =
        AvaloniaProperty.Register<NumberStepper, double>(
            nameof(Increment),
            defaultValue: 1.0);

    public static readonly StyledProperty<string> FormatStringProperty =
        AvaloniaProperty.Register<NumberStepper, string>(
            nameof(FormatString),
            defaultValue: "0");

    private bool _isUpdatingText;

    public double Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public double Minimum
    {
        get => GetValue(MinimumProperty);
        set => SetValue(MinimumProperty, value);
    }

    public double Maximum
    {
        get => GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    public double Increment
    {
        get => GetValue(IncrementProperty);
        set => SetValue(IncrementProperty, value);
    }

    public string FormatString
    {
        get => GetValue(FormatStringProperty);
        set => SetValue(FormatStringProperty, value);
    }

    public NumberStepper()
    {
        InitializeComponent();
        UpdateDisplayedText();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == ValueProperty)
        {
            UpdateDisplayedText();
        }
        else if (change.Property == FormatStringProperty)
        {
            UpdateDisplayedText();
        }
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (IsEnabled)
        {
            if (e.Delta.Y > 0)
            {
                StepValue(Increment);
            }
            else if (e.Delta.Y < 0)
            {
                StepValue(-Increment);
            }
            e.Handled = true;
        }
    }

    private void OnDecrementClick(object? sender, RoutedEventArgs e)
    {
        StepValue(-Increment);
    }

    private void OnIncrementClick(object? sender, RoutedEventArgs e)
    {
        StepValue(Increment);
    }

    private void StepValue(double step)
    {
        double current = Value;
        double next = current + step;

        // Éviter les dérives d'arrondi à virgule flottante (ex: 1.0500000000000002)
        int decimals = GetDecimalPlaces(Increment);
        next = Math.Round(next, decimals);

        next = Math.Clamp(next, Minimum, Maximum);
        Value = next;
        UpdateDisplayedText();
    }

    private void OnTextBoxLostFocus(object? sender, RoutedEventArgs e)
    {
        CommitText();
    }

    private void OnTextBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Return)
        {
            CommitText();
            e.Handled = true;
        }
        else if (e.Key == Key.Up)
        {
            StepValue(Increment);
            e.Handled = true;
        }
        else if (e.Key == Key.Down)
        {
            StepValue(-Increment);
            e.Handled = true;
        }
    }

    private void CommitText()
    {
        if (_isUpdatingText) return;

        string text = ValueTextBox.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(text))
        {
            UpdateDisplayedText();
            return;
        }

        // Remplacer virgule par point pour supporter les saisies FR et US
        text = text.Replace(',', '.');
        // Nettoyer les suffixes textuels comme "%" ou "px" pour extraire la valeur numérique pure
        text = System.Text.RegularExpressions.Regex.Replace(text, @"[^\d\.\-]", string.Empty);

        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed))
        {
            int decimals = GetDecimalPlaces(Increment);
            parsed = Math.Round(parsed, decimals);
            parsed = Math.Clamp(parsed, Minimum, Maximum);
            Value = parsed;
        }

        UpdateDisplayedText();
    }

    private void UpdateDisplayedText()
    {
        if (ValueTextBox == null) return;
        _isUpdatingText = true;
        try
        {
            string fmt = string.IsNullOrEmpty(FormatString) ? "0" : FormatString;
            if (fmt.Contains("{0}"))
            {
                ValueTextBox.Text = string.Format(CultureInfo.InvariantCulture, fmt, Value);
            }
            else
            {
                // Échapper le caractère '%' pour éviter que .NET multiplie par 100
                string safeFmt = fmt.Contains('\'') ? fmt : fmt.Replace("%", "'%'");
                ValueTextBox.Text = Value.ToString(safeFmt, CultureInfo.InvariantCulture);
            }
        }
        finally
        {
            _isUpdatingText = false;
        }
    }

    private static int GetDecimalPlaces(double val)
    {
        string s = val.ToString(CultureInfo.InvariantCulture);
        int idx = s.IndexOf('.');
        return idx < 0 ? 0 : s.Length - idx - 1;
    }
}
