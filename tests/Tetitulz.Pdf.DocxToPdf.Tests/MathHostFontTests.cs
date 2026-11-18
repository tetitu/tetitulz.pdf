// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Tetitulz.Pdf.DocxToPdf.Rendering;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.DocxToPdf.Tests;

/// <summary>
/// An equation's symbols outside WinAnsi are drawn from the host's fonts when the DOCX embeds none: the
/// equation path asked only for an embedded font, unlike body runs and list marks, and reserved the space
/// instead (Wave 22, DocxToPdf-3:8). The host's font list is replaced with one synthetic face for the test, so
/// the machine's own fonts cannot answer instead of the code.
/// </summary>
[Collection(DiagnosticsCollection.Name)]
public sealed class MathHostFontTests : IDisposable
{
    private readonly List<string> _written = new List<string>();

    public void Dispose()
    {
        foreach (string path in _written)
            try { File.Delete(path); } catch { /* a leftover temp file is not a test failure */ }
    }

    private string HostFontFile(string covered)
    {
        string path = Path.Combine(Path.GetTempPath(), "tetitulz-docx-math-" + Guid.NewGuid().ToString("N") + ".ttf");
        File.WriteAllBytes(path, MinimalTrueType.BuildNamed(covered, "MathProbe"));
        _written.Add(path);
        return path;
    }

    private static byte[] EquationDocx()
        => HostileDocxInputTests.RawDocx(
            $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><w:document xmlns:w=\"{HostileDocxInputTests.WordNamespace}\" "
            + "xmlns:m=\"http://schemas.openxmlformats.org/officeDocument/2006/math\"><w:body>"
            + "<w:p><m:oMath><m:r><m:t>∀x</m:t></m:r></m:oMath></w:p></w:body></w:document>");

    private (string Pdf, List<string> Reports) Convert(string covered)
    {
        var reports = new List<string>();
        int collectingThread = System.Environment.CurrentManagedThreadId;
        void Collect(string m) { if (System.Environment.CurrentManagedThreadId == collectingThread) reports.Add(m); }
        DocxToPdfConverter.DiagnosticReported += Collect;
        try
        {
            using (FontResolver.UseHostFontFilesForTest(new[] { HostFontFile(covered) }))
            {
                byte[] pdf = DocxToPdfConverter.ConvertToBytes(EquationDocx(),
                    new DocxToPdfOptions { CompressStreams = false, UseLegacyRenderer = true });
                return (Encoding.Latin1.GetString(pdf), reports);
            }
        }
        finally { DocxToPdfConverter.DiagnosticReported -= Collect; }
    }

    [Fact]
    public void AnEquationSymbolOutsideWinAnsi_IsDrawnFromAHostFontThatCoversIt()
    {
        var (pdf, reports) = Convert("∀x");

        Assert.DoesNotContain(reports, r => r.Contains("MathMeasure.Glyphs", StringComparison.Ordinal));
        Assert.Contains("/FontFile2", pdf, StringComparison.Ordinal);   // the host face, embedded
    }

    /// <summary>The control: a host face that lacks the symbol leaves it reserved and reported, as before.</summary>
    [Fact]
    public void AnEquationSymbolNoHostFontCovers_IsReported()
    {
        var (_, reports) = Convert("x");

        Assert.Contains(reports, r => r.Contains("MathMeasure.Glyphs", StringComparison.Ordinal));
    }
}
