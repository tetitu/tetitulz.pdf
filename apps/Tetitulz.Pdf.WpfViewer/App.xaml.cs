// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Windows;
using System.Windows.Threading;

namespace Tetitulz.Pdf.WpfViewer;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Started by COM as File Explorer's PDF preview handler: no window of its own; it serves
        // previews until it has been idle a while.
        if (Shell.PreviewServer.IsEmbeddingStart(e.Args))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            DispatcherUnhandledException += (_, args) => args.Handled = true;   // a bad file must not end every preview
            new Shell.PreviewServer().Start(Shutdown);
            return;
        }

        // The form scripts the viewer runs as fields are filled (keystroke, validate, calculate):
        // the viewer references no engine, so the application registers one.
        Tetitulz.Pdf.Core.Plugins.PdfPluginHost.Instance.Register(new Tetitulz.Pdf.Plugins.JavaScript.JintJavaScriptPlugin());

        // Catch all unhandled UI thread exceptions — show message instead of crash
        DispatcherUnhandledException += OnDispatcherUnhandledException;

        // Catch all unhandled background thread exceptions
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;

        // Catch all unobserved Task exceptions (fire-and-forget async)
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        new MainWindow().Show();
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show($"An error occurred:\n\n{e.Exception.Message}", "Tetitulz PDF Viewer",
            MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true; // prevent crash
    }

    private static void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
        {
            MessageBox.Show($"Background error:\n\n{ex.Message}", "Tetitulz PDF Viewer",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        e.SetObserved(); // prevent crash from unobserved Task exceptions
    }
}
