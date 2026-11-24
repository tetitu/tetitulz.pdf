// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Tetitulz.Pdf.Plugins.Maintenance;
using Xunit;

namespace Tetitulz.Pdf.Plugins.Maintenance.Tests;

/// <summary>
/// <see cref="PdfOptimizer.WithoutPdfUaPartClaim"/>: the optimizer's removal of a pdfuaid:part claim
/// when it removes the structure tree. It replaced a regular expression that took time quadratic in
/// the count of unclosed openers in the file's XMP. The expression is kept here as the reference the
/// scan must agree with.
/// </summary>
public sealed class PdfUaPartClaimRemovalTests
{
    private static readonly Regex Reference = new Regex(
        @"[ \t]*<pdfuaid:part>.*?</pdfuaid:part>\s*|[ \t]*pdfuaid:part\s*=\s*""[^""]*""",
        RegexOptions.Singleline);

    private static string ByReference(string xmp) => Reference.Replace(xmp, string.Empty);

    [Theory]
    [InlineData("<rdf:Description>\n  <pdfuaid:part>1</pdfuaid:part>\n  <dc:x/></rdf:Description>")]
    [InlineData("<rdf:Description pdfuaid:part=\"1\" dc:y=\"2\"/>")]
    [InlineData("<d  \t pdfuaid:part = \"1\"/>")]
    [InlineData("<pdfuaid:part>1</pdfuaid:part><pdfuaid:part>2</pdfuaid:part>")]
    [InlineData("<pdfuaid:part>unclosed <a pdfuaid:part=\"1\"/>")]
    [InlineData("<a pdfuaid:part=\"unclosed")]
    [InlineData("<pdfuaid:part x='1'>1</pdfuaid:part>")]
    [InlineData("<pdfuaid:part=\"1\"")]
    [InlineData("</pdfuaid:part> pdfuaid:part")]
    [InlineData("nothing to remove")]
    [InlineData("<pdfuaid:part>1</pdfuaid:part>  pdfuaid:part=\"2\" x")]
    public void The_scan_removes_what_the_regular_expression_removed(string xmp)
        => Assert.Equal(ByReference(xmp), PdfOptimizer.WithoutPdfUaPartClaim(xmp));

    // ------------------------------------------------------------------
    //  The packet as the file stores it
    // ------------------------------------------------------------------

    private const string Packet = "<rdf:Description pdfuaid:part=\"1\" dc:format=\"application/pdf\"/>";

    /// <summary>
    /// The claim is found in the packet as decoded and as encoded: a Flate-filtered packet, read as raw
    /// UTF-8, kept its claim with nothing reported. The stripped packet is written back unfiltered, in
    /// its own encoding with its byte-order mark — UTF-8, and UTF-16 in either byte order with or
    /// without one. The control is the unfiltered UTF-8 packet, which was stripped before too.
    /// </summary>
    [Theory]
    [InlineData("utf-8", false, false)]
    [InlineData("utf-8", true, false)]
    [InlineData("utf-16BE", false, true)]
    [InlineData("utf-16BE", true, false)]
    [InlineData("utf-16LE", false, true)]
    [InlineData("utf-16LE", false, false)]
    public void The_claim_is_removed_from_the_packet_as_the_file_stores_it(string encodingName, bool flate, bool bom)
    {
        Encoding encoding = Encoding.GetEncoding(encodingName);
        byte[] preamble = bom ? encoding.GetPreamble() : Array.Empty<byte>();
        byte[] text = preamble.Concat(encoding.GetBytes(Packet)).ToArray();
        var metadata = new Core.Objects.PdfStream { RawData = flate ? Core.IO.Filters.FlateEncodeFilter.Encode(text) : text };
        if (flate) metadata.Set(Core.Objects.PdfName.Filter, Core.Objects.PdfName.FlateDecode);

        PdfOptimizer.StripPdfUaIdentifier(metadata, new PdfOptimizer.OptimizationStats());

        Assert.Null(metadata[Core.Objects.PdfName.Filter]);
        byte[] written = metadata.RawData;
        Assert.Equal(preamble, written.Take(preamble.Length).ToArray());
        Assert.Equal("<rdf:Description dc:format=\"application/pdf\"/>",
            encoding.GetString(written, preamble.Length, written.Length - preamble.Length));
    }

    /// <summary>
    /// A packet that is not valid in the encoding it appears to be in is left exactly as it is: a lenient
    /// read replaced the invalid byte with U+FFFD and would have written the replacement back.
    /// </summary>
    [Fact]
    public void A_packet_that_does_not_decode_as_its_encoding_is_left_as_it_is()
    {
        byte[] invalid = Encoding.UTF8.GetBytes(Packet).Concat(new byte[] { 0xC3 }).ToArray();   // a truncated sequence
        var metadata = new Core.Objects.PdfStream { RawData = invalid };

        PdfOptimizer.StripPdfUaIdentifier(metadata, new PdfOptimizer.OptimizationStats());

        Assert.Same(invalid, metadata.RawData);
    }

    /// <summary>A potency check: the first case's claim is gone and the rest is kept.</summary>
    [Fact]
    public void A_claim_is_removed_and_the_rest_of_the_packet_kept()
    {
        Assert.Equal("<rdf:Description>\n<dc:x/></rdf:Description>",
            PdfOptimizer.WithoutPdfUaPartClaim("<rdf:Description>\n  <pdfuaid:part>1</pdfuaid:part>\n  <dc:x/></rdf:Description>"));
    }

    /// <summary>Random packets made of the tokens that matter, at a fixed seed, agree with the reference.</summary>
    [Fact]
    public void Random_packets_agree_with_the_regular_expression()
    {
        string[] tokens = { "<pdfuaid:part>", "</pdfuaid:part>", "pdfuaid:part", "=", "\"", " ", "\t", "\n", "x", "<", "/" };
        var random = new Random(20261020);
        for (int n = 0; n < 4000; n++)
        {
            var sb = new StringBuilder();
            int length = random.Next(0, 14);
            for (int i = 0; i < length; i++) sb.Append(tokens[random.Next(tokens.Length)]);
            string xmp = sb.ToString();
            Assert.True(ByReference(xmp) == PdfOptimizer.WithoutPdfUaPartClaim(xmp), "differs on: " + xmp);
        }
    }

    /// <summary>
    /// Two hundred thousand openers with no close (3 MB) finish on a thread given ten seconds, and
    /// come back unchanged, since nothing in them closes. The regular expression took 57 s on a tenth
    /// of this. A scan that sought each opener's close afresh would read the packet's remainder two
    /// hundred thousand times, about 300 GB.
    /// </summary>
    [Fact]
    public void Adversarial_unclosed_openers_are_scanned_once()
    {
        // One attribute whose quote never closes, at the end: every opener before it is unclosed too.
        string xmp = string.Concat(Enumerable.Repeat("<pdfuaid:part>a", 200000)) + " pdfuaid:part=\"a";
        string result = null;
        var worker = new Thread(() => result = PdfOptimizer.WithoutPdfUaPartClaim(xmp)) { IsBackground = true };
        worker.Start();

        Assert.True(worker.Join(TimeSpan.FromSeconds(10)), "the scan did not finish");
        Assert.Same(xmp, result);
    }
}
