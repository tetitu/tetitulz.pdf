// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Base;
using Tetitulz.Pdf.Base.Tests.TestInfrastructure;
using Tetitulz.Pdf.Core.Plugins;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// Registering a plugin must not change what <see cref="PdfOperations.ExtractText(Stream, string)"/>
/// returns.
/// </summary>
/// <remarks>
/// <para><b>It did, and the two answers differed by every page separator in the document.</b> The
/// facade had two loops: with no plugin registered it called
/// <c>PdfTextExtractor.ExtractText(doc)</c>, which joins pages with a newline; with any plugin
/// registered it ran its own loop to get each page's text for the hooks and appended them with
/// <b>no separator at all</b>. So a two-page file extracted as <c>"page1\npage2"</c> or
/// <c>"page1page2"</c> depending on whether something entirely unrelated — an image decoder, an OCR
/// engine — was registered at that moment.</para>
///
/// <para><b>It surfaced as an intermittent test failure, which is the only way it could.</b>
/// <c>BatesNumberingTests.StampingOneAtATimeMatchesStampingTheBundle</c> extracts the same content
/// twice and compares; when a test class running in parallel registered or unregistered between
/// those two extractions, the separators disappeared from one side. Ten consecutive full-suite runs
/// did not reproduce it, so the cause was found by reading the two paths rather than by catching it
/// in the act.</para>
///
/// <para><b>The fix is one loop, not two matching ones.</b> Core's whole-document overload now takes
/// a per-page callback, so the join and the first-error aggregation exist once and observing the
/// pages cannot change the answer.</para>
/// </remarks>
[Collection(PluginHostCollection.Name)]
public sealed class TextExtractionPluginNeutralityTests : IDisposable
{
    public void Dispose() => PdfPluginHost.Instance.ResetForTesting();

    /// <summary>A plugin that does nothing at all — the point is that it is merely *present*.</summary>
    private sealed class InertPlugin : IPdfPlugin
    {
        public string Name => "Test.InertPlugin";
        public string Description => "Registered to prove that registration alone changes nothing.";
        public Version Version => new Version(1, 0, 0);
        public void Initialize() { }
        public void Shutdown() { }
    }

    [Fact]
    public void RegisteringAnUnrelatedPluginDoesNotChangeTheExtractedText()
    {
        byte[] pdf = TwoPagePdf();

        PdfPluginHost.Instance.ResetForTesting();
        string withoutPlugin = PdfOperations.ExtractText(new MemoryStream(pdf));

        PdfPluginHost.Instance.Register(new InertPlugin());
        string withPlugin = PdfOperations.ExtractText(new MemoryStream(pdf));

        // The fixture's own potency: both pages are present and separated, so the equality below
        // cannot be satisfied by two empty or two single-page strings.
        Assert.Contains("PageOne", withoutPlugin, StringComparison.Ordinal);
        Assert.Contains("PageTwo", withoutPlugin, StringComparison.Ordinal);
        Assert.Contains("\n", withoutPlugin, StringComparison.Ordinal);

        Assert.Equal(withoutPlugin, withPlugin);
    }

    // ------------------------------------------------------------------

    private static byte[] TwoPagePdf()
    {
        string one = "BT /F1 12 Tf 20 150 Td (PageOne) Tj ET";
        string two = "BT /F1 12 Tf 20 150 Td (PageTwo) Tj ET";

        return Build(new[]
        {
            "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n",
            "2 0 obj\n<< /Type /Pages /Kids [3 0 R 5 0 R] /Count 2 >>\nendobj\n",
            "3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R "
                + "/Resources << /Font << /F1 7 0 R >> >> >>\nendobj\n",
            $"4 0 obj\n<< /Length {one.Length} >>\nstream\n{one}\nendstream\nendobj\n",
            "5 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 6 0 R "
                + "/Resources << /Font << /F1 7 0 R >> >> >>\nendobj\n",
            $"6 0 obj\n<< /Length {two.Length} >>\nstream\n{two}\nendstream\nendobj\n",
            "7 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n",
        });
    }

    private static byte[] Build(string[] objects)
    {
        var acc = new List<byte>();
        var offsets = new List<int>();
        void Emit(string s) => acc.AddRange(Encoding.Latin1.GetBytes(s));

        Emit("%PDF-1.7\n");
        foreach (var o in objects) { offsets.Add(acc.Count); Emit(o); }

        int xref = acc.Count;
        int size = offsets.Count + 1;
        Emit($"xref\n0 {size}\n0000000000 65535 f \n");
        foreach (int o in offsets) Emit($"{o:D10} 00000 n \n");
        Emit($"trailer\n<< /Size {size} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return acc.ToArray();
    }
}
