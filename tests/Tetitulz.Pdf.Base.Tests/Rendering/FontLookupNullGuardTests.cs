// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Base.Rendering;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// A resource lookup with no font name selected answers "no", rather than throwing.
/// </summary>
/// <remarks>
/// <para><c>FontCache.LookupFontDict</c> guarded only its <c>resources</c> argument and then built
/// <c>new PdfName(resourceName)</c> — and <c>PdfName</c>'s constructor throws
/// <c>ArgumentNullException</c> on null. Its sibling in <c>PdfStructuredTextExtractor</c> guarded
/// all three arguments from the start; this copy guarded one.</para>
///
/// <para><b>What is and is not demonstrated here, stated rather than implied.</b> The throw is
/// real and is asserted below. The only caller in the tree is <c>SvgContentWriter</c>'s
/// <c>fc.IsEmbedded(_gs.FontName, …)</c>, and a fixture reaching it with a null name could not be
/// constructed: that call sits in the standard-typeface branch of the glyph-outlining path, which
/// is entered only once a font has already been resolved, so a content stream that shows text
/// without a <c>Tf</c> never arrives there. <b>This is therefore a divergence fix — two siblings
/// answering one question, one of them guarding a third of its arguments — not a demonstrated
/// crash.</b> An export-level test was written first and dropped when mutating the guard back left
/// it green: a test whose name claims a crash it cannot reach is worse than no test.</para>
/// </remarks>
public sealed class FontLookupNullGuardTests
{
    /// <summary>
    /// The guard itself, asserted directly so a future caller cannot reintroduce the throw by
    /// asking the same question from somewhere the current one does not reach.
    /// </summary>
    [Fact]
    public void IsEmbeddedAnswersFalseForAnEmptyFontNameRatherThanThrowing()
    {
        using var ms = new MemoryStream(PdfShowingTextWithNoTf());
        using var doc = PdfDocument.Open(ms);
        using var cache = new FontCache();

        var resources = new PdfDictionary();
        resources.Set(PdfName.Font, new PdfDictionary());

        Assert.False(cache.IsEmbedded(null, resources, doc));
        Assert.False(cache.IsEmbedded("F1", null, doc));
        Assert.False(cache.IsEmbedded("F1", resources, null));
    }

    // ------------------------------------------------------------------

    /// <summary>
    /// A one-page document whose content stream opens a text object and shows a string without
    /// ever issuing <c>Tf</c> — legal to write, and what a producer emits after a truncated
    /// resource edit.
    /// </summary>
    private static byte[] PdfShowingTextWithNoTf()
    {
        const string content = "BT 72 700 Td (no font selected) Tj ET\n";

        var sb = new StringBuilder();
        var offsets = new System.Collections.Generic.List<int>();
        void Obj(string body) { offsets.Add(sb.Length); sb.Append(body); }

        sb.Append("%PDF-1.7\n");
        Obj("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        Obj("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        Obj("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R "
          + "/Resources << /Font << >> >> >>\nendobj\n");
        Obj($"4 0 obj\n<< /Length {content.Length} >>\nstream\n{content}endstream\nendobj\n");

        int xref = sb.Length;
        sb.AppendFormat("xref\n0 {0}\n0000000000 65535 f \n", offsets.Count + 1);
        foreach (int off in offsets) sb.AppendFormat("{0:D10} 00000 n \n", off);
        sb.AppendFormat("trailer\n<< /Size {0} /Root 1 0 R >>\nstartxref\n{1}\n%%EOF\n",
            offsets.Count + 1, xref);

        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
