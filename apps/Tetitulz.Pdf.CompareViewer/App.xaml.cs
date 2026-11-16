// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using System.Windows;
using Serilog;
using Tetitulz.Pdf.Core.Plugins;
using Tetitulz.Pdf.Plugins.CMap;
using Application = System.Windows.Application;
using MessageBox   = System.Windows.MessageBox;

namespace Tetitulz.Pdf.CompareViewer;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        string logDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TetitulzCompare", "logs");
        Directory.CreateDirectory(logDir);

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.File(
                Path.Combine(logDir, "log-.txt"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .WriteTo.Debug()
            .CreateLogger();

        Log.Information("TetitulzPdfCompare starting");

        PdfPluginHost.Instance.Register(new AdobeCMapPlugin());

        base.OnStartup(e);

        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error(args.Exception, "DispatcherUnhandledException");
            MessageBox.Show(
                $"Unhandled error:\n\n{args.Exception.GetType().Name}: {args.Exception.Message}\n\n{args.Exception.StackTrace}",
                "CompareViewer Error", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error(args.Exception, "UnobservedTaskException");
            args.SetObserved();
            Dispatcher.Invoke(() =>
                MessageBox.Show(
                    $"Background task error:\n\n{args.Exception.InnerException?.Message ?? args.Exception.Message}",
                    "CompareViewer Task Error", MessageBoxButton.OK, MessageBoxImage.Warning));
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            Log.Fatal("AppDomain.UnhandledException: {ExceptionObject}", args.ExceptionObject);
            Log.CloseAndFlush();
            MessageBox.Show(
                $"Fatal error:\n\n{args.ExceptionObject}",
                "Fatal Error", MessageBoxButton.OK, MessageBoxImage.Error);
        };
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.Information("TetitulzPdfCompare exiting");
        Log.CloseAndFlush();
        base.OnExit(e);
    }
}
