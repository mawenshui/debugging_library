using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace FieldKb.Client.Wpf;

public static class ComboBoxBehaviors
{
    public static readonly DependencyProperty OpenDropDownOnClickProperty =
        DependencyProperty.RegisterAttached(
            "OpenDropDownOnClick",
            typeof(bool),
            typeof(ComboBoxBehaviors),
            new PropertyMetadata(false, OnOpenDropDownOnClickChanged));

    public static void SetOpenDropDownOnClick(DependencyObject element, bool value)
    {
        element.SetValue(OpenDropDownOnClickProperty, value);
    }

    public static bool GetOpenDropDownOnClick(DependencyObject element)
    {
        return (bool)element.GetValue(OpenDropDownOnClickProperty);
    }

    private static void OnOpenDropDownOnClickChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ComboBox combo)
        {
            return;
        }

        if (e.NewValue is true)
        {
            combo.PreviewMouseLeftButtonDown += OnPreviewMouseLeftButtonDown;
        }
        else
        {
            combo.PreviewMouseLeftButtonDown -= OnPreviewMouseLeftButtonDown;
        }
    }

    private static void OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ComboBox combo)
        {
            return;
        }

        if (!combo.IsEnabled)
        {
            return;
        }

        if (combo.IsDropDownOpen && (IsInsideComboBoxItem(e.OriginalSource as DependencyObject) || IsInsideScrollBar(e.OriginalSource as DependencyObject)))
        {
            return;
        }

        if (combo.IsEditable && IsInsideEditableTextBox(e.OriginalSource as DependencyObject))
        {
            return;
        }

        if (IsInsideToggleButton(e.OriginalSource as DependencyObject))
        {
            return;
        }

        combo.Focus();
        combo.IsDropDownOpen = !combo.IsDropDownOpen;
        e.Handled = true;
    }

    private static bool IsInsideEditableTextBox(DependencyObject? source)
    {
        var current = source;
        while (current is not null)
        {
            if (current is TextBox)
            {
                return true;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return false;
    }

    private static bool IsInsideToggleButton(DependencyObject? source)
    {
        var current = source;
        while (current is not null)
        {
            if (current is ToggleButton)
            {
                return true;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return false;
    }

    private static bool IsInsideComboBoxItem(DependencyObject? source)
    {
        var current = source;
        while (current is not null)
        {
            if (current is ComboBoxItem)
            {
                return true;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return false;
    }

    private static bool IsInsideScrollBar(DependencyObject? source)
    {
        var current = source;
        while (current is not null)
        {
            if (current is ScrollBar)
            {
                return true;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return false;
    }
}
