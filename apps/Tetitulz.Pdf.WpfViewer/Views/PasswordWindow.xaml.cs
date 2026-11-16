// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Windows;

namespace Tetitulz.Pdf.WpfViewer.Views;

/// <summary>Asks for a password: to open a document, or — with <see cref="ShowConfirm"/> — a new one, typed twice.</summary>
public partial class PasswordWindow : Window
{
    /// <summary>The password typed; null until OK.</summary>
    public string? EnteredPassword { get; private set; }

    public string Prompt { get; set; } = "Enter password:";

    /// <summary>Visible when a new password is set, so it is typed twice.</summary>
    public Visibility ShowConfirm { get; set; } = Visibility.Collapsed;

    public PasswordWindow()
    {
        DataContext = this;
        InitializeComponent();
        Loaded += (_, _) => PwBox.Focus();
    }

    private void OK_Click(object sender, RoutedEventArgs e)
    {
        if (ShowConfirm == Visibility.Visible && PwBox.Password != PwConfirm.Password)
        {
            MessageBox.Show(this, "The two passwords are not the same.", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        EnteredPassword = PwBox.Password;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
