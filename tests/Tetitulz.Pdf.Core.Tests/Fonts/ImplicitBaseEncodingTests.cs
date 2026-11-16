// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Text;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Fonts;

/// <summary>
/// §9.6.6.4: a simple font with no <c>/Encoding</c> — and, one level down, an <c>/Encoding</c>
/// dictionary with no <c>/BaseEncoding</c> — uses <b>StandardEncoding</b> as its implicit base, not
/// WinAnsiEncoding.
/// </summary>
/// <remarks>
/// <para><c>PdfTrueTypeFont</c> was corrected to do this and its <c>PdfType1Font</c> sibling was
/// not. The two disagreed on the codes where StandardEncoding and WinAnsi differ: byte
/// <c>0x27</c> extracted as U+0027 APOSTROPHE through Type 1 and U+2019 RIGHT SINGLE QUOTATION MARK
/// through TrueType; <c>0x60</c> gave U+0060 GRAVE ACCENT against U+2018 LEFT SINGLE QUOTATION
/// MARK.</para>
///
/// <para><b>A <c>/Subtype /Type1</c> dictionary with no <c>/Encoding</c> is the single most common
/// simple-font shape there is</b>, so this was not an edge case — the same byte extracted to a
/// different character depending only on which subtype the producer had written.</para>
///
/// <para>Extraction is the instrument because that is where the divergence shows: both readers feed
/// the same text extractor, and the code-to-character map is exactly what they disagreed about.</para>
/// </remarks>
public class ImplicitBaseEncodingTests
{
    /// <summary>
    /// <b>The defect.</b> The two subtypes must extract the same character for the codes where
    /// StandardEncoding and WinAnsi differ.
    /// </summary>
    [Theory]
    [InlineData(0x27)]   // quoteright in StandardEncoding, apostrophe in WinAnsi
    [InlineData(0x60)]   // quoteleft vs grave
    public void TheTwoSimpleFontSubtypesAgreeWithNoEncoding(int code)
    {
        string viaType1 = Extract("Type1", code, encodingEntry: null);
        string viaTrueType = Extract("TrueType", code, encodingEntry: null);

        Assert.Equal(viaTrueType, viaType1);
    }

    /// <summary>
    /// <b>The value, not merely the agreement.</b> StandardEncoding maps <c>0x27</c> to
    /// <c>quoteright</c> (U+2019) and <c>0x60</c> to <c>quoteleft</c> (U+2018) — asserting that
    /// stops the pair being satisfied by both having moved to WinAnsi.
    /// </summary>
    [Theory]
    [InlineData(0x27, '’')]
    [InlineData(0x60, '‘')]
    public void TheImplicitBaseIsStandardEncoding(int code, char expected)
        => Assert.Equal(expected.ToString(), Extract("Type1", code, encodingEntry: null));

    /// <summary>
    /// One level down: an <c>/Encoding</c> dictionary carrying only <c>/Differences</c> has no
    /// <c>/BaseEncoding</c>, so the same implicit base applies.
    /// </summary>
    [Theory]
    [InlineData(0x27)]
    [InlineData(0x60)]
    public void TheTwoSubtypesAgreeWithAnEncodingDictionaryThatHasNoBaseEncoding(int code)
    {
        const string entry = " /Encoding << /Type /Encoding /Differences [200 /bullet] >>";

        Assert.Equal(Extract("TrueType", code, entry), Extract("Type1", code, entry));
    }

    /// <summary>
    /// <b>The floor.</b> An explicit <c>/WinAnsiEncoding</c> must still be honoured — otherwise the
    /// fix would be "always StandardEncoding", which breaks every font that says otherwise.
    /// </summary>
    [Theory]
    [InlineData(0x27, '\'')]
    [InlineData(0x60, '`')]
    public void AnExplicitWinAnsiEncodingIsStillHonoured(int code, char expected)
    {
        const string entry = " /Encoding /WinAnsiEncoding";

        Assert.Equal(expected.ToString(), Extract("Type1", code, entry));
        Assert.Equal(expected.ToString(), Extract("TrueType", code, entry));
    }

    // ------------------------------------------------------------------

    private static string Extract(string subtype, int code, string encodingEntry)
    {
        using var ms = new MemoryStream(Pdf(subtype, code, encodingEntry));
        using var doc = PdfDocument.Open(ms);
        return PdfTextExtractor.ExtractText(doc.Pages[0]).Trim();
    }

    /// <summary>
    /// One page showing a single byte through a non-symbolic simple font of the given subtype. No
    /// <c>/ToUnicode</c>: it would override the encoding, which is the whole thing under test.
    /// </summary>
    private static byte[] Pdf(string subtype, int code, string encodingEntry)
    {
        string content = $"BT /F0 12 Tf 50 700 Td <{code:X2}> Tj ET";

        var objs = new List<string>
        {
            "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n",
            "2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n",
            "3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 800] /Contents 4 0 R "
                + "/Resources << /Font << /F0 5 0 R >> >> >>\nendobj\n",
            $"4 0 obj\n<< /Length {content.Length} >>\nstream\n{content}\nendstream\nendobj\n",
            $"5 0 obj\n<< /Type /Font /Subtype /{subtype} /BaseFont /Custom "
                + $"/FirstChar 0 /LastChar 255 /FontDescriptor 6 0 R{encodingEntry} >>\nendobj\n",
            // /Flags 32 = nonsymbolic (bit 6). The symbolic case keeps WinAnsi in both readers by
            // design, so a symbolic descriptor here would test nothing.
            "6 0 obj\n<< /Type /FontDescriptor /FontName /Custom /Flags 32 /ItalicAngle 0 "
                + "/Ascent 700 /Descent -200 /CapHeight 700 /StemV 80 "
                + "/FontBBox [0 -200 1000 700] >>\nendobj\n",
        };
        return Build(objs);
    }

    private static byte[] Build(List<string> objects)
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
