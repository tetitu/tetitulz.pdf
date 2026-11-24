// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using Tetitulz.Pdf.Core.Plugins;

namespace Tetitulz.Pdf.Core.Tests.Plugins;

/// <summary>
/// <see cref="PdfPluginHost.LastRegistrationError"/> documents itself as
/// "the most recent … scan skipped a type or assembly, <b>or null if none was skipped</b>", and as
/// "the only signal that something was dropped" because assembly scanning is failure-tolerant.
///
/// <para>Neither half held. None of the three scan methods reset the slot on entry, so a clean
/// scan reported the PREVIOUS scan's failure — the <c>PdfDecryptor.LastPermsValidation</c> defect
/// CLAUDE.md records: per-thread storage is not per-operation scope. And
/// <c>RegisterFromDirectory</c>'s catch wrote only <c>Debug.WriteLine</c>, which a Release build
/// compiles out entirely, so a DLL that failed to load left no trace anywhere.</para>
/// </summary>
[Collection(nameof(PluginHostCollection))]
public sealed class PdfPluginHostScanDiagnosticTests : IDisposable
{
    public PdfPluginHostScanDiagnosticTests()
    {
        PdfPluginHost.Instance.ResetForTesting();
        PdfPluginHost.ResetLastRegistrationError();
    }

    public void Dispose()
    {
        PdfPluginHost.Instance.ResetForTesting();
        PdfPluginHost.ResetLastRegistrationError();
    }

    [Fact]
    public void RegisterFromAssembly_AfterAFailingScan_ACleanScanReportsNothingSkipped()
    {
        var host = PdfPluginHost.Instance;

        // The exact sequence from the finding: a scan that legitimately drops something, then a
        // clean scan on the same thread. The host used to hand the second caller the FIRST scan's
        // failure, so a host checking "was anything skipped?" failed startup over someone else's
        // problem.
        RunScanThatDropsADll();
        Assert.NotNull(PdfPluginHost.LastRegistrationError);

        // An assembly with no plugin types at all skips nothing.
        host.RegisterFromAssembly(typeof(string).Assembly);

        Assert.Null(PdfPluginHost.LastRegistrationError);
    }

    [Fact]
    public void RegisterFromDirectory_AfterAFailingScan_AnEmptyDirectoryReportsNothingSkipped()
    {
        RunScanThatDropsADll();
        Assert.NotNull(PdfPluginHost.LastRegistrationError);

        string dir = Path.Combine(Path.GetTempPath(), "tetitulz-plugin-scan-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            PdfPluginHost.Instance.RegisterFromDirectory(dir);
            Assert.Null(PdfPluginHost.LastRegistrationError);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    /// <summary>Runs a directory scan over one unloadable DLL, so the slot holds a real reason.</summary>
    private static void RunScanThatDropsADll()
    {
        string dir = Path.Combine(Path.GetTempPath(), "tetitulz-plugin-scan-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "Tetitulz.Pdf.Plugins.Broken.dll"),
            new byte[] { 0x00, 0x01, 0x02, 0x03, 0x04 });
        try { PdfPluginHost.Instance.RegisterFromDirectory(dir); }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void RegisterFromDirectory_UnloadableDll_RecordsWhyItWasDropped()
    {
        // A file matching the plugin naming pattern that is not a managed assembly at all — the
        // stand-in for a wrong-architecture or blocked-file DLL. Assembly.LoadFrom throws, and the
        // failure must reach the scan's diagnostic, not a Debug.WriteLine alone.
        string dir = Path.Combine(Path.GetTempPath(), "tetitulz-plugin-scan-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string dll = Path.Combine(dir, "Tetitulz.Pdf.Plugins.Broken.dll");
        File.WriteAllBytes(dll, new byte[] { 0x00, 0x01, 0x02, 0x03, 0x04 });
        try
        {
            int registered = PdfPluginHost.Instance.RegisterFromDirectory(dir);

            Assert.Equal(0, registered);
            Assert.NotNull(PdfPluginHost.LastRegistrationError);
            Assert.Contains("Tetitulz.Pdf.Plugins.Broken.dll", PdfPluginHost.LastRegistrationError!);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

}
