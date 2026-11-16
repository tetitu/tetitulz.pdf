// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Windows;
using System.Windows.Controls;

namespace Tetitulz.Pdf.WpfViewer.Views;

/// <summary>
/// Small modal questions — a line of text, two of them, or a choice that may be typed — built in
/// code so each command asks for exactly what it needs, at the moment it needs it.
/// </summary>
public static class Prompts
{
    /// <summary>One line (or, with <paramref name="multiline"/>, several) of text; null on Cancel or an empty answer.</summary>
    public static string? Text(string title, string label, string initial = "", bool multiline = false)
    {
        var box = new TextBox
        {
            Text = initial, MinWidth = 380, Margin = new Thickness(0, 6, 0, 0),
            AcceptsReturn = multiline, TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap,
            Height = multiline ? 90 : double.NaN, VerticalScrollBarVisibility = multiline ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled,
        };
        bool ok = Show(title, new UIElement[] { Label(label), box }, box);
        string answer = box.Text.Trim();
        return ok && answer.Length > 0 ? box.Text : null;
    }

    /// <summary>Two lines of text, either of which may be empty but not both; null on Cancel.</summary>
    public static (string First, string Second)? TwoTexts(string title, string label1, string initial1, string label2, string initial2)
    {
        var first = new TextBox { Text = initial1, MinWidth = 380, Margin = new Thickness(0, 6, 0, 10) };
        var second = new TextBox { Text = initial2, MinWidth = 380, Margin = new Thickness(0, 6, 0, 0) };
        bool ok = Show(title, new UIElement[] { Label(label1), first, Label(label2), second }, first);
        if (!ok || (first.Text.Trim().Length == 0 && second.Text.Trim().Length == 0)) return null;
        return (first.Text, second.Text);
    }

    /// <summary>One of <paramref name="choices"/>, or anything typed instead; null on Cancel or an empty answer.</summary>
    public static string? Choice(string title, string label, IEnumerable<string> choices, string initial)
    {
        var combo = new ComboBox { IsEditable = true, MinWidth = 380, Margin = new Thickness(0, 6, 0, 0), Text = initial };
        foreach (string c in choices) combo.Items.Add(c);
        bool ok = Show(title, new UIElement[] { Label(label), combo }, combo);
        string answer = combo.Text.Trim();
        return ok && answer.Length > 0 ? answer : null;
    }

    /// <summary>A question answered Yes or No, No being the default; false for anything but Yes.</summary>
    public static bool YesNo(string caption, string text)
    {
        Window? owner = Application.Current?.MainWindow;
        MessageBoxResult answer = owner != null
            ? MessageBox.Show(owner, text, caption, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No)
            : MessageBox.Show(text, caption, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        return answer == MessageBoxResult.Yes;
    }

    /// <summary>Save, Don't save or Cancel, for unsaved changes; Cancel being the default.</summary>
    public static MessageBoxResult SaveChanges(string documentName)
    {
        Window? owner = Application.Current?.MainWindow;
        string text = $"{documentName} has changes that are not saved.\n\nSave them first?";
        return owner != null
            ? MessageBox.Show(owner, text, "Unsaved changes", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel)
            : MessageBox.Show(text, "Unsaved changes", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel);
    }

    private static TextBlock Label(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, MaxWidth = 380 };

    private static bool Show(string title, UIElement[] content, Control focus)
    {
        var panel = new StackPanel { Margin = new Thickness(16) };
        foreach (UIElement e in content) panel.Children.Add(e);

        var ok = new Button { Content = "OK", IsDefault = true, MinWidth = 80, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(8, 3, 8, 3) };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 80, Padding = new Thickness(8, 3, 8, 3) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        panel.Children.Add(buttons);

        var window = new Window
        {
            Title = title, Content = panel, SizeToContent = SizeToContent.WidthAndHeight, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Owner = Application.Current?.MainWindow,
            ShowInTaskbar = false,
        };
        ok.Click += (_, _) => window.DialogResult = true;
        window.Loaded += (_, _) => focus.Focus();
        return window.ShowDialog() == true;
    }
}
