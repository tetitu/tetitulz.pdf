// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base.Operations.Documents;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// <see cref="PdfBatesNumbering"/> — one running number stamped across a bundle, and removable
/// afterwards.
/// </summary>
/// <remarks>
/// <para><b>The stamps are asserted by extracting the text of the output.</b> A stamp that reached
/// the file but not the page — wrong coordinates, an unreferenced content stream, a missing font
/// resource — produces a perfectly valid PDF, and only reading the page back can tell that from a
/// stamp that worked.</para>
///
/// <para><b>Removal is asserted the same way, and paired with a control.</b> "The number is gone"
/// is also true of a removal that emptied the page, so every removal case asserts the document's own
/// text survived.</para>
/// </remarks>
public sealed class BatesNumberingTests
{
    private const string Body = "original content";

    /// <summary>A document of <paramref name="pages"/> pages, each carrying the same text.</summary>
    private static byte[] Document(int pages)
    {
        string content = $"BT /F1 12 Tf 72 400 Td ({Body}) Tj ET\n";

        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            null,   // filled in below, once the kid numbers are known
            $"<< /Length {content.Length} >>\nstream\n{content}endstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
        };

        var kids = new StringBuilder();
        for (int i = 0; i < pages; i++)
        {
            int objNum = bodies.Count + 1;
            kids.Append(objNum).Append(" 0 R ");
            bodies.Add(
                "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 3 0 R " +
                "/Resources << /Font << /F1 4 0 R >> >> >>");
        }
        bodies[1] = $"<< /Type /Pages /Kids [{kids.ToString().Trim()}] /Count {pages} >>";

        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new int[bodies.Count + 1];
        for (int i = 0; i < bodies.Count; i++)
        {
            offsets[i + 1] = sb.Length;
            sb.Append(i + 1).Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(bodies.Count + 1).Append('\n').Append("0000000000 65535 f \n");
        for (int i = 1; i <= bodies.Count; i++) sb.AppendFormat("{0:D10} 00000 n \n", offsets[i]);
        sb.Append("trailer\n<< /Size ").Append(bodies.Count + 1).Append(" /Root 1 0 R >>\n");
        sb.AppendFormat("startxref\n{0}\n%%EOF\n", xref);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    private static (byte[] output, long next) Stamp(byte[] pdf, BatesNumberingOptions options = null)
    {
        using var input = new MemoryStream(pdf, writable: false);
        using var output = new MemoryStream();
        long next = PdfBatesNumbering.Apply(input, output, options);
        return (output.ToArray(), next);
    }

    private static (byte[] output, int removed) Unstamp(byte[] pdf)
    {
        using var input = new MemoryStream(pdf, writable: false);
        using var output = new MemoryStream();
        int removed = PdfBatesNumbering.Remove(input, output);
        return (output.ToArray(), removed);
    }

    private static string Text(byte[] pdf)
    {
        using var ms = new MemoryStream(pdf, writable: false);
        return PdfOperations.ExtractText(ms) ?? string.Empty;
    }

    // ================================================================ stamping

    /// <summary>
    /// Every page gets its own number, padded, and the document's own text is still there.
    /// </summary>
    [Fact]
    public void EachPageGetsItsOwnPaddedNumber()
    {
        var (stamped, next) = Stamp(Document(3));
        string text = Text(stamped);

        Assert.Contains("000001", text);
        Assert.Contains("000002", text);
        Assert.Contains("000003", text);
        Assert.DoesNotContain("000004", text);
        Assert.Equal(4, next);

        // The stamp is added, not substituted.
        Assert.Equal(3, text.Split(new[] { Body }, StringSplitOptions.None).Length - 1);
    }

    /// <summary>The prefix and suffix travel, and the pad width is a minimum rather than a cap.</summary>
    /// <remarks>
    /// Truncating an over-wide number would give two pages the same stamp while looking correct,
    /// which is the one thing a bundle index cannot survive.
    /// </remarks>
    [Fact]
    public void PrefixSuffixAndOverWidthNumbersAreWrittenInFull()
    {
        var options = new BatesNumberingOptions
        {
            Prefix = "EX-",
            Suffix = "/a",
            Digits = 3,
            StartNumber = 998,
        };

        string text = Text(Stamp(Document(3), options).output);

        Assert.Contains("EX-998/a", text);
        Assert.Contains("EX-999/a", text);
        Assert.Contains("EX-1000/a", text);   // wider than /Digits, and not truncated
    }

    /// <summary>
    /// The number a page carries follows the page, not the order the appender visited it in.
    /// </summary>
    /// <remarks>
    /// Asserted through <see cref="PdfBatesNumbering.Format"/> against the page's own extracted
    /// text, page by page: a counter incremented per callback gives the right SET of numbers in the
    /// wrong places, and a test that only checked the set would pass.
    /// </remarks>
    [Fact]
    public void TheNumberOnAPageIsAFunctionOfThePageIndex()
    {
        var options = new BatesNumberingOptions { StartNumber = 10, Digits = 4 };
        byte[] stamped = Stamp(Document(3), options).output;

        for (int page = 0; page < 3; page++)
        {
            using var ms = new MemoryStream(stamped, writable: false);
            // 1-based, as PdfOperations.ExtractText(Stream, int) documents.
            string text = PdfOperations.ExtractText(ms, page + 1) ?? "";
            Assert.Contains(PdfBatesNumbering.Format(10 + page, options), text);
        }
    }

    /// <summary>
    /// A bundle is numbered continuously across its documents, and the reported next number
    /// continues it.
    /// </summary>
    [Fact]
    public void ABundleIsNumberedContinuouslyAcrossDocuments()
    {
        var inputs = new List<Stream>
        {
            new MemoryStream(Document(2), writable: false),
            new MemoryStream(Document(3), writable: false),
        };
        var outputs = new List<Stream> { new MemoryStream(), new MemoryStream() };

        long next = PdfBatesNumbering.Apply(inputs, outputs, new BatesNumberingOptions());

        string first = Text(((MemoryStream)outputs[0]).ToArray());
        string second = Text(((MemoryStream)outputs[1]).ToArray());

        Assert.Contains("000001", first);
        Assert.Contains("000002", first);
        Assert.DoesNotContain("000003", first);

        Assert.Contains("000003", second);   // continues rather than restarting
        Assert.Contains("000005", second);
        Assert.DoesNotContain("000001", second);

        Assert.Equal(6, next);

        foreach (var s in inputs.Concat(outputs)) s.Dispose();
    }

    /// <summary>
    /// The next number one call reports is the number the following call starts at.
    /// </summary>
    /// <remarks>
    /// This is the whole cross-document contract, and it is what lets a caller stamp from a queue
    /// rather than from a list — the state is a number they hold, not an object this library keeps.
    /// The single-document loop below must produce exactly what the list overload produces.
    /// </remarks>
    [Fact]
    public void StampingOneAtATimeMatchesStampingTheBundle()
    {
        byte[] a = Document(2), b = Document(3);

        var oneAtATime = new List<string>();
        long next = 1;
        foreach (byte[] pdf in new[] { a, b })
        {
            var (output, after) = Stamp(pdf, new BatesNumberingOptions { StartNumber = next });
            oneAtATime.Add(Text(output));
            next = after;
        }

        var inputs = new List<Stream>
        {
            new MemoryStream(a, writable: false), new MemoryStream(b, writable: false),
        };
        var outputs = new List<Stream> { new MemoryStream(), new MemoryStream() };
        long bundleNext = PdfBatesNumbering.Apply(inputs, outputs, new BatesNumberingOptions());

        Assert.Equal(bundleNext, next);
        Assert.Equal(oneAtATime[0], Text(((MemoryStream)outputs[0]).ToArray()));
        Assert.Equal(oneAtATime[1], Text(((MemoryStream)outputs[1]).ToArray()));

        foreach (var s in inputs.Concat(outputs)) s.Dispose();
    }

    /// <summary>Mismatched input and output lists are refused rather than half-stamped.</summary>
    [Fact]
    public void AMismatchedBundleIsRefused()
    {
        var inputs = new List<Stream> { new MemoryStream(Document(1), writable: false) };
        var outputs = new List<Stream>();

        var ex = Assert.Throws<ArgumentException>(
            () => PdfBatesNumbering.Apply(inputs, outputs, new BatesNumberingOptions()));
        Assert.Contains("one output per input", ex.Message);

        foreach (var s in inputs) s.Dispose();
    }

    /// <summary>The stamp is a pagination artifact, so a reader does not read it as content.</summary>
    /// <remarks>
    /// §14.8.2.2. Asserted on the raw content stream because that is where the marking lives —
    /// extraction cannot see it, which is exactly why the plain header/footer writer went so long
    /// without it.
    /// </remarks>
    [Fact]
    public void TheStampIsMarkedAsAPaginationArtifact()
    {
        byte[] stamped = Stamp(Document(1)).output;
        string stream = StampStream(stamped);

        Assert.Contains("/Artifact <</Type /Pagination /Subtype /Footer>> BDC", stream);
        Assert.Contains("EMC", stream);
    }

    /// <summary>The header position puts the stamp near the top of the page.</summary>
    [Fact]
    public void TheHeaderPositionStampsNearTheTop()
    {
        byte[] footer = Stamp(Document(1)).output;
        byte[] header = Stamp(Document(1), new BatesNumberingOptions { InHeader = true }).output;

        Assert.Contains("/Subtype /Header", StampStream(header));
        Assert.Contains("/Subtype /Footer", StampStream(footer));

        // The y coordinate really moved, or "Header" is a label on identical output.
        Assert.True(StampY(header) > 700, $"header y was {StampY(header)}");
        Assert.True(StampY(footer) < 100, $"footer y was {StampY(footer)}");
    }

    // ================================================================ removal

    /// <summary>
    /// Removal takes the stamps off and leaves the document's own content.
    /// </summary>
    [Fact]
    public void RemovalTakesOffTheStampsAndNothingElse()
    {
        byte[] stamped = Stamp(Document(3)).output;
        var (cleaned, removed) = Unstamp(stamped);

        Assert.Equal(3, removed);

        string text = Text(cleaned);
        Assert.DoesNotContain("000001", text);
        Assert.DoesNotContain("000003", text);
        Assert.Equal(3, text.Split(new[] { Body }, StringSplitOptions.None).Length - 1);
    }

    /// <summary>
    /// Re-stamping after removal replaces the number rather than adding a second one — which is
    /// what a renumbered bundle does.
    /// </summary>
    [Fact]
    public void ABundleCanBeRenumbered()
    {
        byte[] first = Stamp(Document(2), new BatesNumberingOptions { StartNumber = 1 }).output;
        byte[] cleaned = Unstamp(first).output;
        byte[] second = Stamp(cleaned, new BatesNumberingOptions { StartNumber = 500 }).output;

        string text = Text(second);
        Assert.Contains("000500", text);
        Assert.Contains("000501", text);
        Assert.DoesNotContain("000001", text);   // the old numbering is gone, not underneath
    }

    /// <summary>
    /// A document this class never stamped is reported as carrying none, and comes back unchanged.
    /// </summary>
    /// <remarks>
    /// Guessing at a stamp somebody else applied would delete a page's real content. The honest
    /// answer for an unrecognised document is zero, and no revision at all — appending one that
    /// changes no object would grow the file to say that nothing happened.
    /// </remarks>
    [Fact]
    public void AnUnstampedDocumentIsLeftAlone()
    {
        byte[] original = Document(2);
        var (result, removed) = Unstamp(original);

        Assert.Equal(0, removed);
        Assert.Equal(original, result);
    }

    // ----------------------------------------------------------------

    /// <summary>The decoded text of the appended stamp stream on page 0.</summary>
    private static string StampStream(byte[] pdf)
    {
        using var ms = new MemoryStream(pdf, writable: false);
        using var doc = PdfDocument.Open(ms);

        var contents = (PdfArray)doc.ResolveObject(doc.Pages[0].Dictionary[PdfName.Contents]);
        for (int i = 0; i < contents.Count; i++)
        {
            var stream = doc.ResolveObject(contents[i]) as PdfStream;
            string text = Encoding.ASCII.GetString(stream.GetDecodedData());
            if (text.StartsWith("% Tetitulz.Pdf Bates stamp", StringComparison.Ordinal))
                return text;
        }

        throw new InvalidOperationException("no stamp stream on page 0");
    }

    /// <summary>The y coordinate the stamp's text was positioned at.</summary>
    private static double StampY(byte[] pdf)
    {
        string stream = StampStream(pdf);
        int td = stream.IndexOf(" Td", StringComparison.Ordinal);
        Assert.True(td > 0, "the stamp positions its text with Td");

        string[] parts = stream.Substring(0, td)
            .Split(new[] { ' ', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
        return double.Parse(parts[parts.Length - 1],
            System.Globalization.CultureInfo.InvariantCulture);
    }
}
