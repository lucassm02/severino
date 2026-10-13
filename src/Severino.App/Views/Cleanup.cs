using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Severino.Core.Network;

namespace Severino.App.Views;

public enum InputKind
{
    None,

    /// <summary>A host or IP: no scheme, no path, no port (which goes to <see cref="Cleanup.PortBoxProperty"/>).</summary>
    Host,

    Port,

    /// <summary>A route's address, path included.</summary>
    Address,

    /// <summary>Names, one per line.</summary>
    Names,
}

/// <summary>
/// Cleans a field when something is pasted into it and when it loses focus, with
/// <see cref="InputCleanup"/>: "http://10.0.0.5:8080/" pasted as a host leaves 10.0.0.5, and the
/// 8080 goes to the port field next to it. Never while typing: "10.0.0.5:" is halfway to a port.
/// Works on a TextBox and on an editable ComboBox.
/// </summary>
public static class Cleanup
{
    public static readonly DependencyProperty AsProperty = DependencyProperty.RegisterAttached(
        "As", typeof(InputKind), typeof(Cleanup), new PropertyMetadata(InputKind.None, OnAsChanged));

    /// <summary>For a host: the field that gets the port that came with it.</summary>
    public static readonly DependencyProperty PortBoxProperty = DependencyProperty.RegisterAttached(
        "PortBox", typeof(Control), typeof(Cleanup));

    /// <summary>For a host: the http/https selector that gets the scheme that came with it.</summary>
    public static readonly DependencyProperty SchemeBoxProperty = DependencyProperty.RegisterAttached(
        "SchemeBox", typeof(Selector), typeof(Cleanup));

    public static InputKind GetAs(DependencyObject element) => (InputKind)element.GetValue(AsProperty);
    public static void SetAs(DependencyObject element, InputKind value) => element.SetValue(AsProperty, value);
    public static Control? GetPortBox(DependencyObject element) => (Control?)element.GetValue(PortBoxProperty);
    public static void SetPortBox(DependencyObject element, Control? value) => element.SetValue(PortBoxProperty, value);
    public static Selector? GetSchemeBox(DependencyObject element) => (Selector?)element.GetValue(SchemeBoxProperty);
    public static void SetSchemeBox(DependencyObject element, Selector? value) => element.SetValue(SchemeBoxProperty, value);

    private static void OnAsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Control control)
            return;
        DataObject.RemovePastingHandler(control, OnPasting);
        control.IsKeyboardFocusWithinChanged -= OnFocusWithinChanged;
        if ((InputKind)e.NewValue == InputKind.None)
            return;
        DataObject.AddPastingHandler(control, OnPasting);
        control.IsKeyboardFocusWithinChanged += OnFocusWithinChanged;
    }

    private static void OnPasting(object sender, DataObjectPastingEventArgs e)
    {
        if (sender is not Control control || e.OriginalSource is not TextBox box
            || e.SourceDataObject.GetData(DataFormats.UnicodeText, true) is not string pasted)
            return;
        var (clean, port, scheme) = Clean(GetAs(control), pasted);
        if (clean == pasted && port is null && scheme is null)
            return;

        e.CancelCommand();
        box.SelectedText = clean;
        box.CaretIndex = box.SelectionStart + box.SelectionLength;
        Pass(control, port, scheme);
    }

    private static void OnFocusWithinChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true || sender is not Control control || TextOf(control) is not { Length: > 0 } text)
            return;
        var (clean, port, scheme) = Clean(GetAs(control), text);
        if (clean != text)
            SetText(control, clean);
        Pass(control, port, scheme);
    }

    private static (string Text, int? Port, string? Scheme) Clean(InputKind kind, string text)
    {
        switch (kind)
        {
            case InputKind.Host:
                var host = InputCleanup.Host(text);
                return (host.Host, host.Port, host.Scheme);
            case InputKind.Port:
                return (InputCleanup.Port(text), null, null);
            case InputKind.Address:
                return (InputCleanup.Address(text), null, null);
            case InputKind.Names:
                return (InputCleanup.Names(text), null, null);
            default:
                return (text, null, null);
        }
    }

    /// <summary>The port and the scheme a host brought, to the fields beside it.</summary>
    private static void Pass(Control control, int? port, string? scheme)
    {
        if (port is { } number && GetPortBox(control) is { } portBox)
            SetText(portBox, number.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (scheme is not null && GetSchemeBox(control) is { } schemeBox && schemeBox.Items.Contains(scheme))
            schemeBox.SelectedItem = scheme;
    }

    private static string? TextOf(Control control) => control switch
    {
        TextBox box => box.Text,
        ComboBox combo => combo.Text,
        _ => null,
    };

    private static void SetText(Control control, string text)
    {
        switch (control)
        {
            case TextBox box:
                box.SetCurrentValue(TextBox.TextProperty, text);
                break;
            case ComboBox combo:
                combo.SetCurrentValue(ComboBox.TextProperty, text);
                break;
        }
    }
}
