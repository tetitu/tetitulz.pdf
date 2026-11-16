// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

namespace Tetitulz.Pdf.Plugins.HtmlScript.Tests;

/// <summary>
/// A page script's <c>import()</c> reads nothing off the disk. The vendored loader handed every
/// specifier but an http(s) one to Jint's default module loader, which reads a <c>file:</c> URI's file:
/// <c>import('file:///…/appsettings.json', { with: { type: 'json' } })</c> in a page's own classic script
/// gave the script the converting host's file, which it could write into the PDF, and a missing file
/// failed differently from a present one. The plugin's contract is that nothing a script does reaches
/// the file system.
/// </summary>
[Collection(PluginHostCollection.Name)]
public sealed class ModuleImportTests
{
    private static string ImportOf(string uri) =>
        "<html><body><p id='out'>START</p><script>" +
        "import('" + uri + "', { with: { type: 'json' } })" +
        ".then(function (m) { document.getElementById('out').textContent = 'READ ' + m.default.v; }," +
        " function (e) { document.getElementById('out').textContent = 'REFUSED'; });" +
        "</script></body></html>";

    /// <summary>
    /// A present file is refused like any other specifier: its text is not in the output, and the
    /// script saw the import fail.
    /// </summary>
    [Fact]
    public void AHostilePagesImportOfALocalFile_ReadsNothing()
    {
        string dir = Path.Combine(Path.GetTempPath(), "w22-module-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, "secret.json");
        File.WriteAllText(file, "{\"v\":\"S3CR3T-MODULE\"}");
        try
        {
            var (text, _) = PageScriptTests.Convert(ImportOf(new Uri(file).AbsoluteUri));

            Assert.DoesNotContain("S3CR3T-MODULE", text, StringComparison.Ordinal);
            Assert.Contains("REFUSED", text, StringComparison.Ordinal);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    /// <summary>
    /// The control for the oracle: a file that is not there fails the same way, so a script cannot
    /// tell the two apart.
    /// </summary>
    [Fact]
    public void AnImportOfAMissingFile_FailsTheSameWay()
    {
        string missing = Path.Combine(Path.GetTempPath(), "w22-module-" + Guid.NewGuid().ToString("N"), "absent.json");

        var (text, _) = PageScriptTests.Convert(ImportOf(new Uri(missing).AbsoluteUri));

        Assert.Contains("REFUSED", text, StringComparison.Ordinal);
    }
}
