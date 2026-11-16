// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Tetitulz.Pdf.WpfViewer.Converters;

/// <summary>Converts bool to Visibility (true=Visible, false=Collapsed).</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c)
        => value is true ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, Type t, object p, CultureInfo c)
        => value is Visibility.Visible;
}

/// <summary>Inverts a bool (for inverted visibility bindings).</summary>
public sealed class InvertBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c)
        => value is true ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object value, Type t, object p, CultureInfo c)
        => value is Visibility.Collapsed;
}

/// <summary>Adds 1 to an int (for 0-based to 1-based page display).</summary>
public sealed class PageIndexToDisplayConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c)
        => value is int i ? (i + 1).ToString() : "0";
    public object ConvertBack(object value, Type t, object p, CultureInfo c)
        => int.TryParse(value?.ToString(), out int v) ? v - 1 : 0;
}

/// <summary>Formats zoom as percentage string.</summary>
public sealed class ZoomToPercentConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c)
        => value is double d ? $"{d * 100:F0}%" : "100%";
    public object ConvertBack(object value, Type t, object p, CultureInfo c)
        => throw new NotSupportedException();
}
