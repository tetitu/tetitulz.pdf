// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Tetitulz.Pdf.Base;
using Tetitulz.Pdf.Base.Operations.Barcodes;
using Tetitulz.Pdf.Base.Rendering;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// Reading Code 128 and EAN-13 back out of a page.
/// </summary>
/// <remarks>
/// <para><b>A round trip against this library's own writer proves less than it looks like.</b> Both
/// halves share <c>Code128Symbols</c> and <c>EanSymbols</c>, so a wrong row in either table would
/// encode and decode consistently and the loop would close — CLAUDE.md's third blind spot. The
/// tables are therefore pinned separately, by structural invariants, in
/// <see cref="BarcodeSymbolTableTests"/>; what the round trips here add is everything <i>between</i>
/// the tables — rendering, thresholding, run extraction, quantisation and the guards.</para>
///
/// <para><b>Two checks do not depend on the tables at all.</b> Each symbology's check character is
/// computed from decoded values by a rule with nothing to do with bar patterns, and
/// <see cref="AMisreadSymbolIsRefusedRatherThanReturnedWrong"/> shows it is load-bearing rather
/// than decorative. And <see cref="TheTruncatedStopTheWriterUsedToEmitIsNotReadable"/> works
/// entirely from hand-built runs.</para>
/// </remarks>
public class BarcodeReaderTests
{
    // ================================================================
    //  Round trips through a real render
    // ================================================================

    /// <summary>A Code 128 barcode this library writes reads back as what went in.</summary>
    [Fact]
    public void ACode128BarcodeRoundTripsThroughARenderedPage()
    {
        const string payload = "SHIP-4711/B";

        IReadOnlyList<BarcodeReadResult> found = ReadBack(payload, BarcodeType.Code128);

        BarcodeReadResult result = Assert.Single(found);
        Assert.Equal(BarcodeType.Code128, result.Type);
        Assert.Equal(payload, result.Text);
        Assert.Null(PdfBarcodeReader.LastRefusal);
    }

    /// <summary>Every character subset B can carry survives the trip.</summary>
    /// <remarks>
    /// <b>One payload exercises one row of the table.</b> The alphabet below reaches the low
    /// punctuation, the digits, both letter cases and the characters above <c>z</c>, so a single
    /// mis-transcribed row is far more likely to be hit than by any one realistic string.
    /// </remarks>
    [Theory]
    [InlineData(" !\"#$%&'()*+,-./")]
    [InlineData("0123456789:;<=>?")]
    [InlineData("@ABCDEFGHIJKLMNO")]
    [InlineData("PQRSTUVWXYZ[\\]^_")]
    [InlineData("`abcdefghijklmno")]
    [InlineData("pqrstuvwxyz{|}~")]
    public void EveryCode128SubsetBCharacterRoundTrips(string payload)
    {
        IReadOnlyList<BarcodeReadResult> found = ReadBack(payload, BarcodeType.Code128);

        Assert.Equal(payload, Assert.Single(found).Text);
    }

    /// <summary>An EAN-13 barcode reads back with all thirteen digits.</summary>
    /// <remarks>
    /// <b>The first digit is the one worth watching.</b> It is not drawn as a digit anywhere — it
    /// is carried by which parity table each of the six left digits used — so a reader that skipped
    /// the parity word would return the other twelve and look almost right. The payload starts with
    /// 5, whose parity pattern is not the all-odd one digit 0 would give.
    /// </remarks>
    [Fact]
    public void AnEan13BarcodeRoundTripsIncludingItsUnprintedFirstDigit()
    {
        const string payload = "5901234123457";

        IReadOnlyList<BarcodeReadResult> found = ReadBack(payload, BarcodeType.Ean13);

        BarcodeReadResult result = Assert.Single(found);
        Assert.Equal(BarcodeType.Ean13, result.Type);
        Assert.Equal(payload, result.Text);
        Assert.Equal('5', result.Text[0]);
    }

    /// <summary>The reported box covers the symbol, and is taller than one scan row.</summary>
    /// <remarks>
    /// <b>A single-row hit is what a coincidence looks like.</b> A barcode is drawn tall so that any
    /// horizontal cut carries the payload, so a genuine one decodes on many consecutive rows and the
    /// merged result has real height. Asserting that is what separates "found a barcode" from "found
    /// a row of ink that happened to quantise".
    /// </remarks>
    [Fact]
    public void TheReportedBoxSpansTheSymbolAndManyRows()
    {
        BarcodeReadResult result = Assert.Single(ReadBack("HEIGHT", BarcodeType.Code128));

        Assert.True(result.Right > result.Left, "the box has no width");
        Assert.True(result.Bottom - result.Top > 10,
                    $"decoded on only {result.Bottom - result.Top} px of rows");
    }

    /// <summary>A QR code this library writes reads back off a rendered page.</summary>
    /// <remarks>
    /// <b>The end-to-end pass for the 2-D path</b>, and the one that exercises what the grid tests
    /// cannot: finding the symbol among the rest of the page, working out its module size from the
    /// finder patterns, and sampling it. The payload is long enough to need a version with an
    /// alignment pattern, so the sampling has to be right across the whole symbol rather than just
    /// near the corner it was anchored from.
    /// </remarks>
    [Fact]
    public void AQrCodeRoundTripsThroughARenderedPage()
    {
        const string payload = "https://example.com/order/4711";

        byte[] pdf = PdfBarcodeWriter.GeneratePdf(payload, BarcodeType.QrCode,
            new BarcodeOptions { Size = 160f, ShowText = false });

        using var input = new MemoryStream(pdf, writable: false);
        IReadOnlyList<BarcodeReadResult> found = PdfBarcodeReader.Read(input);

        BarcodeReadResult result = Assert.Single(found);
        Assert.Equal(BarcodeType.QrCode, result.Type);
        Assert.Equal(payload, result.Text);
        Assert.Null(PdfBarcodeReader.LastRefusal);
    }

    /// <summary>Non-ASCII survives, because a QR payload is bytes and this one says UTF-8.</summary>
    [Fact]
    public void AQrCodeCarriesNonAsciiThroughUnchanged()
    {
        const string payload = "חשבונית ₪1,234 — naïve";

        byte[] pdf = PdfBarcodeWriter.GeneratePdf(payload, BarcodeType.QrCode,
            new BarcodeOptions { Size = 200f, ShowText = false });

        using var input = new MemoryStream(pdf, writable: false);

        Assert.Equal(payload, Assert.Single(PdfBarcodeReader.Read(input)).Text);
    }

    /// <summary>
    /// A QR code on a page that also carries text and a linear barcode is still found, and so is
    /// the linear one.
    /// </summary>
    /// <remarks>
    /// <para><b>A page holding nothing but one barcode is the easy case, and it was the only case
    /// tested.</b> Real ink produces finder-shaped triples of its own — the locator deliberately
    /// over-offers rather than risk missing a real symbol — so what matters is that a false
    /// candidate cannot shadow a genuine one. An earlier locator returned the first triple it could
    /// sample and stopped; on this page that is a coin flip.</para>
    ///
    /// <para>Both symbologies are asserted together because they share the scan: the 2-D pass runs
    /// first and the linear readers walk the same rows afterwards, so a regression in either could
    /// swallow the other.</para>
    /// </remarks>
    [Fact]
    public void BothSymbolsAreFoundOnAPageThatAlsoCarriesText()
    {
        const string qr = "https://example.com/order/4711";
        const string linear = "SHIP-4711/B";

        byte[] pdf = MixedPagePdf(qr, linear);

        using var input = new MemoryStream(pdf, writable: false);
        IReadOnlyList<BarcodeReadResult> found = PdfBarcodeReader.Read(input);

        Assert.Contains(found, r => r.Type == BarcodeType.QrCode && r.Text == qr);
        Assert.Contains(found, r => r.Type == BarcodeType.Code128 && r.Text == linear);
        Assert.Null(PdfBarcodeReader.LastRefusal);
    }

    /// <summary>
    /// A page dense with text yields only a handful of finder candidates, so the cubic pairing
    /// loop stays cheap.
    /// </summary>
    /// <remarks>
    /// <para><b>An optimisation whose presence changes no answer needs its own assertion.</b>
    /// Removing the locator's vertical confirmation left every other test green — the extra
    /// candidates it admits are thrown out later by the format field and by Reed–Solomon, and the
    /// real symbol is still found. Only the <i>count</i> can see the difference, which is
    /// CLAUDE.md's second blind spot and the same technique <c>CMapCodec.UsesCidRangeIndex</c>
    /// uses.</para>
    ///
    /// <para><b>The count is what bounds the work.</b> Finder triples are searched with three
    /// nested loops, so candidates cost cubically. Horizontal 1:1:3:1:1 runs are common in ordinary
    /// prose — the word "ill" makes one at the right size — so a locator that trusted the
    /// horizontal scan alone would let a page of text set the price of reading it.</para>
    /// </remarks>
    [Fact]
    public void ADenseTextPageYieldsAlmostNoFinderCandidates()
    {
        byte[] pdf = DenseTextPagePdf();

        using var input = new MemoryStream(pdf, writable: false);
        RenderedPage page = PdfOperations.RenderPageToPixels(input, 0, PdfBarcodeReader.DefaultDpi);

        int threshold = BarcodeScanline.Threshold(
            page.Pixels, page.Width, page.Height, page.Stride);
        int candidates = QrLocator.CountFinderCandidates(
            page.Pixels, page.Width, page.Height, page.Stride, threshold);

        Assert.True(candidates <= 8,
                    $"a page of prose offered {candidates} finder candidates; the vertical "
                    + "confirmation is not filtering them");
    }

    /// <summary>A DataMatrix this library writes reads back off a rendered page.</summary>
    /// <remarks>
    /// <b>The end-to-end pass for ECC-200.</b> The locator has no finder squares to look for — it
    /// finds the solid L and tries each size whose proportions its arms fit, letting the decoder's strict
    /// finder and clock-track check throw out the wrong ones. Getting a payload back therefore
    /// exercises the sizing as well as the decoding.
    /// </remarks>
    [Fact]
    public void ADataMatrixRoundTripsThroughARenderedPage()
    {
        const string payload = "SHIP-4711/B";

        byte[] pdf = PdfBarcodeWriter.GeneratePdf(payload, BarcodeType.DataMatrix,
            new BarcodeOptions { Size = 120f, ShowText = false });

        using var input = new MemoryStream(pdf, writable: false);
        IReadOnlyList<BarcodeReadResult> found = PdfBarcodeReader.Read(input);

        BarcodeReadResult result = Assert.Single(found);
        Assert.Equal(BarcodeType.DataMatrix, result.Type);
        Assert.Equal(payload, result.Text);
        Assert.Null(PdfBarcodeReader.LastRefusal);
    }

    /// <summary>A rectangular DataMatrix reads back off a rendered page.</summary>
    /// <remarks>
    /// <b>The L's arms are no longer equal</b>: a 16×48 symbol's left bar is a third of its bottom
    /// bar, and it is two regions wide, so this exercises the proportion check, the separate module
    /// width and height, and the region transform along the long axis.
    /// </remarks>
    [Fact]
    public void ARectangularDataMatrixRoundTripsThroughARenderedPage()
    {
        const string payload = "RECTANGULAR-DATAMATRIX-16x48-4711-ABCDEFGHIJ";

        string content = PdfBarcodeWriter.GenerateContentStream(payload, BarcodeType.DataMatrix, 0f, 0f,
            new BarcodeOptions { DataMatrixRectangular = true });
        Assert.Contains("% DataMatrix (16x48)", content, StringComparison.Ordinal);

        byte[] pdf = PdfBarcodeWriter.GeneratePdf(payload, BarcodeType.DataMatrix,
            new BarcodeOptions { Size = 240f, ShowText = false, DataMatrixRectangular = true });

        using var input = new MemoryStream(pdf, writable: false);
        IReadOnlyList<BarcodeReadResult> found = PdfBarcodeReader.Read(input);

        BarcodeReadResult result = Assert.Single(found);
        Assert.Equal(BarcodeType.DataMatrix, result.Type);
        Assert.Equal(payload, result.Text);
        Assert.Null(PdfBarcodeReader.LastRefusal);
        Assert.InRange((double)(result.Bottom - result.Top) / (result.Right - result.Left), 0.3, 0.37);
    }

    /// <summary>A dense text page offers no Data Matrix candidate at all.</summary>
    /// <remarks>
    /// <para><b>The L is a weak signal on its own, and this is what says the other filters
    /// matter.</b> "A dark run with a dark column of the same length rising from its left end" is a
    /// shape ordinary page furniture makes constantly — the corner of a table rule, a bold initial,
    /// the meeting of an underline and a vertical stroke. Only requiring the two arms to stand in a
    /// size's proportions, and requiring the enclosed box's top edge to alternate, keeps prose out.
    /// Admitting the six rectangular proportions still left this page with none.</para>
    ///
    /// <para>Asserted as a count of candidates rather than as an answer, because a false candidate
    /// changes no answer: the decoder's finder check rejects it. What it changes is how much work
    /// the page costs, and that only a count can see.</para>
    /// </remarks>
    [Fact]
    public void ADenseTextPageOffersNoDataMatrixCandidates()
    {
        byte[] pdf = DenseTextPagePdf();

        using var input = new MemoryStream(pdf, writable: false);
        RenderedPage page = PdfOperations.RenderPageToPixels(input, 0, PdfBarcodeReader.DefaultDpi);

        int threshold = BarcodeScanline.Threshold(
            page.Pixels, page.Width, page.Height, page.Stride);

        var candidates = DataMatrixLocator.Locate(
            page.Pixels, page.Width, page.Height, page.Stride, threshold);

        Assert.Empty(candidates);
    }

    // ================================================================
    //  The defect the reader existed to find
    // ================================================================

    /// <summary>
    /// An eleven-module Stop cannot be read, and the thirteen-module one the writer emits can.
    /// </summary>
    /// <remarks>
    /// <para><b>The behavioural proof.</b> Code 128's Stop is 13 modules; its first 11 alone omit
    /// the terminating bar, so the symbol ends in white. The table test asserts the pattern; this
    /// asserts the consequence, which is that a conforming reader cannot find the end of such a
    /// symbol: after a truncated Stop there is nothing but quiet zone, and quiet zone will not
    /// quantise to Stop's remaining elements.</para>
    ///
    /// <para><b>Both symbols here are built by hand from module strings</b>, not by calling the
    /// writer, so this says something about Code 128 rather than about what this library currently
    /// happens to emit.</para>
    /// </remarks>
    [Fact]
    public void TheTruncatedStopTheWriterUsedToEmitIsNotReadable()
    {
        // Start-B, 'A' (value 33), check, then Stop -- assembled from the shared table so the only
        // thing that differs between the two cases is Stop's width.
        const int A = 'A' - 32;
        int check = (Code128Symbols.StartB + (1 * A)) % Code128Symbols.ChecksumModulus;

        string body = Modules(Code128Symbols.StartB) + Modules(A) + Modules(check);
        string correct = body + Bits(Code128Symbols.Pattern(Code128Symbols.Stop), 13);
        string truncated = body + Bits(Code128Symbols.Pattern(Code128Symbols.Stop) >> 2, 11);

        Assert.Equal("A", DecodeModules(correct));
        Assert.Null(DecodeModules(truncated));
    }

    // ================================================================
    //  Refusals — and the acceptance control that keeps them meaningful
    // ================================================================

    /// <summary>A page with no barcode returns nothing and reports nothing.</summary>
    /// <remarks>
    /// <b>The acceptance control for <see cref="PdfBarcodeReader.LastRefusal"/>.</b> Most pages a
    /// caller passes have no barcode on them, so a channel that spoke up here would be one nobody
    /// reads by the second page. Text is used rather than a blank page precisely because ink is
    /// what a scanner might mistake for bars.
    /// </remarks>
    [Fact]
    public void APageOfOrdinaryTextYieldsNothingAndReportsNothing()
    {
        byte[] pdf = TextPagePdf();

        using var input = new MemoryStream(pdf, writable: false);
        IReadOnlyList<BarcodeReadResult> found = PdfBarcodeReader.Read(input);

        Assert.Empty(found);
        Assert.Null(PdfBarcodeReader.LastRefusal);
    }

    /// <summary>A Code 128 code set C symbol reads each value as a pair of digits.</summary>
    /// <remarks>
    /// <para><b>The failure this prevents is a plausible wrong answer, not an error.</b> Code set C
    /// encodes each value as a <i>pair</i> of digits, so reading a C symbol with B's mapping yields
    /// half as many characters — every one of them a legal character, the check digit satisfied,
    /// and nothing anywhere to say the payload is wrong. Value 42 is <c>"42"</c> in C and <c>'J'</c>
    /// in B.</para>
    ///
    /// <para>The symbols here are hand-built, because this library's writer produces only B.</para>
    /// </remarks>
    [Fact]
    public void ACode128SubsetCSymbolReadsEachValueAsTwoDigits()
    {
        Assert.Equal("420507", DecodeModules(SymbolModules(StartC, 42, 5, 7)));
        Assert.Null(PdfBarcodeReader.LastRefusal);
    }

    internal const int StartA = 103, StartC = 105;
    internal const int ShiftValue = 98, CodeCValue = 99, CodeBValue = 100, CodeAValue = 101;

    /// <summary>Start, the values, the check character and Stop, as a module string.</summary>
    internal static string SymbolModules(int start, params int[] values)
    {
        var sb = new System.Text.StringBuilder(Modules(start));
        int checksum = start;
        for (int i = 0; i < values.Length; i++)
        {
            sb.Append(Modules(values[i]));
            checksum += (i + 1) * values[i];
        }
        sb.Append(Modules(checksum % Code128Symbols.ChecksumModulus));
        sb.Append(Bits(Code128Symbols.Pattern(Code128Symbols.Stop), Code128Symbols.StopModules));
        return sb.ToString();
    }

    [Theory]
    // Code set A: 'A' is 33, as in B; 73 is HT (64 + 9), where B has 'i'.
    [InlineData("A\t", StartA, new[] { 33, 73 })]
    // Code set C switching to B mid-symbol, and B switching to C.
    [InlineData("1234A", StartC, new[] { 12, 34, CodeBValue, 33 })]
    [InlineData("X1234", Code128Symbols.StartB, new[] { 56, CodeCValue, 12, 34 })]
    // C to A, then A to B.
    [InlineData("07\tb", StartC, new[] { 7, CodeAValue, 73, CodeBValue, 66 })]
    // Shift reads one value in the other of A and B, then the original set resumes.
    [InlineData("a\tb", Code128Symbols.StartB, new[] { 65, ShiftValue, 73, 66 })]
    [InlineData("AaB", StartA, new[] { 33, ShiftValue, 65, 34 })]
    public void Code128SubsetSwitchesAndShift_ChangeHowFollowingValuesRead(string expected, int start, int[] values)
    {
        Assert.Equal(expected, DecodeModules(SymbolModules(start, values)));
    }

    [Fact]
    public void ACode128ShiftWithNothingAfterIt_IsRefused()
    {
        Assert.Null(DecodeModules(SymbolModules(Code128Symbols.StartB, 33, ShiftValue)));
        Assert.Contains("Shift", PdfBarcodeReader.LastRefusal, StringComparison.Ordinal);
    }

    /// <summary>A symbol whose bars have been altered is refused, not returned wrong.</summary>
    /// <remarks>
    /// <b>This is what makes the check digit evidence rather than decoration.</b> The EAN-13 below
    /// is well-formed in every structural respect — both guards land, every digit pattern is in the
    /// tables, the parity word names a first digit — and one digit has been swapped for another.
    /// Only the check digit can tell, and a reader that skipped it would hand back a valid-looking
    /// product code for a product nobody scanned.
    /// </remarks>
    [Fact]
    public void AMisreadSymbolIsRefusedRatherThanReturnedWrong()
    {
        // 5901234123457 is a valid code; changing the third digit invalidates it while leaving
        // every pattern in the symbol a legal one.
        string modules = Ean13Modules("5911234123457");

        Assert.Null(DecodeModules(modules));
        Assert.Contains("check digit", PdfBarcodeReader.LastRefusal, StringComparison.Ordinal);
    }

    /// <summary>A Code 128 symbol whose check character is wrong is refused, not returned.</summary>
    /// <remarks>
    /// <b>The Code 128 counterpart of the EAN-13 case above, and it was missing.</b> Deleting the
    /// check-character comparison entirely left the whole suite green: every Code 128 fixture here
    /// carried a <i>correct</i> check character, so nothing ever asked what happens to a wrong one.
    /// The symbol below is well-formed in every other respect — Start, a legal character, Stop — so
    /// only the check character can reject it.
    /// </remarks>
    [Fact]
    public void ACode128SymbolWithAWrongCheckCharacterIsRefused()
    {
        const int A = 'A' - 32;
        int correct = (Code128Symbols.StartB + (1 * A)) % Code128Symbols.ChecksumModulus;

        string modules = Modules(Code128Symbols.StartB) + Modules(A)
                       + Modules((correct + 1) % Code128Symbols.ChecksumModulus)
                       + Bits(Code128Symbols.Pattern(Code128Symbols.Stop), 13);

        Assert.Null(DecodeModules(modules));
        Assert.Contains("check character", PdfBarcodeReader.LastRefusal, StringComparison.Ordinal);
    }

    /// <summary>
    /// An EAN-13 whose elements do not add up to 95 modules is refused, even when every pattern in
    /// it is a legal one.
    /// </summary>
    /// <remarks>
    /// <para><b>EAN's patterns are seven bits with leading zeros, and that is what makes this
    /// necessary.</b> A left-hand pattern begins with a space, so its integer value does not encode
    /// its own width: <c>0001011</c> and <c>00001011</c> are both 11. Widen one element of digit 9
    /// by a module and the group still matches digit 9 — the symbol decodes, the check digit passes,
    /// and a malformed symbol is accepted as a good one.</para>
    ///
    /// <para>Code 128 is immune to the same trick because every one of its characters starts with a
    /// bar, so the width is in the value. That asymmetry is exactly why the total is checked in the
    /// shared quantiser rather than in whichever reader happened to think of it.</para>
    /// </remarks>
    [Fact]
    public void AnEan13WhoseElementsDoNotSumToNinetyFiveModulesIsRefused()
    {
        // Digit 9's left-odd pattern is 0001011: a leading space of three modules. Widening it to
        // four leaves the pattern's integer value unchanged and the symbol one module too long.
        string valid = Ean13Modules("5901234123457");
        int at = valid.IndexOf("0001011", StringComparison.Ordinal);
        Assert.True(at > 0, "the fixture no longer contains digit 9's left-odd pattern");

        string stretched = valid.Substring(0, at) + "0" + valid.Substring(at);

        Assert.Null(DecodeModules(stretched));
    }

    /// <summary>
    /// An EAN-13 whose edge guard is drawn twice as wide — <c>110011</c>, each element two modules — is
    /// refused at either end. Read as one block it is 98 modules and does not divide; read a piece at a time
    /// (the foreshortened symbol's route) the guard's three elements still divide into three
    /// modules, so only its module, twice its neighbouring digit's, refuses it. The control, unwidened, reads.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AnEan13WithAWidenedEdgeGuardIsRefused(bool leading)
    {
        string valid = Ean13Modules("5901234123457");
        Assert.Equal("5901234123457", DecodeModules(valid));
        string widened = leading
            ? "110011" + valid.Substring(3)
            : valid.Substring(0, valid.Length - 3) + "110011";

        Assert.Null(DecodeModules(widened));
    }

    /// <summary>
    /// An EAN-13 whose centre guard is not <c>01010</c> is refused, even though everything else
    /// about it checks out.
    /// </summary>
    /// <remarks>
    /// <para><b>Constructing this took working out what the centre guard can actually catch, and
    /// the answer is narrower than it looks.</b> Patterns are compared as integers, so leading
    /// zeros are invisible: a guard widened to <c>001010</c> is still 10 and slips straight through.
    /// Widths 1,2,1,1,1 give <c>011010</c> = 26, which does not.</para>
    ///
    /// <para><b>The rest of the symbol had to stay valid, or something else would reject it first.</b>
    /// The extra centre module is taken back from digit 9's leading space — <c>0001011</c> shortened
    /// to <c>001011</c>, both of which are the integer 11, so that digit still reads as 9. The
    /// result is 95 modules in 59 elements that decodes to the right thirteen digits and passes its
    /// own check digit. Only the guard can tell it is malformed, which is precisely why deleting the
    /// guard left the suite green.</para>
    /// </remarks>
    [Fact]
    public void AnEan13WithAMalformedCentreGuardIsRefused()
    {
        string valid = Ean13Modules("5901234123457");

        // Digit 9 leads the left half; shorten its leading space by one module.
        Assert.Equal("0001011", valid.Substring(3, 7));
        string modules = valid.Substring(0, 3) + "001011" + valid.Substring(10);

        // The guard now sits one module earlier; spend the freed module widening its first bar.
        const int Centre = 3 + 6 + (5 * EanSymbols.DigitModules);
        Assert.Equal("01010", modules.Substring(Centre, EanSymbols.CentreGuardModules));
        modules = modules.Substring(0, Centre) + "011010"
                + modules.Substring(Centre + EanSymbols.CentreGuardModules);

        Assert.Equal(EanSymbols.SymbolModules, modules.Length);
        Assert.Null(DecodeModules(modules));
    }

    /// <summary>A barcode on a page rendered with no background still reads.</summary>
    /// <remarks>
    /// <b>Unpainted is light, and every other fixture here is opaque so none of them can say so.</b>
    /// The pixels are premultiplied, so a fully transparent pixel is stored as zero in all four
    /// bytes — indistinguishable from opaque black to anything that reads the colour and ignores
    /// alpha. A reader that did would see the whole page as ink and find no bars at all.
    /// </remarks>
    [Fact]
    public void ABarcodeReadsOnAPageRenderedWithATransparentBackground()
    {
        const string payload = "NOBG-77";

        byte[] pdf = PdfBarcodeWriter.GeneratePdf(payload, BarcodeType.Code128,
            new BarcodeOptions { ModuleWidth = 2f, Height = 60f, ShowText = false });

        using var input = new MemoryStream(pdf, writable: false);
        RenderedPage page = PdfOperations.RenderPageToPixels(
            input, 0, PdfBarcodeReader.DefaultDpi,
            new PdfRenderOptions { BackgroundArgb = PdfRenderOptions.Transparent });

        Assert.Equal(payload, Assert.Single(PdfBarcodeReader.ReadPixels(page)).Text);
    }

    /// <summary>A low-contrast barcode reads, because the threshold is measured not assumed.</summary>
    /// <remarks>
    /// <b>Every other fixture is black on white, where any threshold at all works.</b> These bars
    /// are a light grey well above mid-scale, so a fixed midpoint would call the entire page white
    /// and find nothing; Otsu puts the split between the two tones that are actually present. Bars
    /// are not always black — a form printed in a house colour is an ordinary thing.
    /// </remarks>
    [Fact]
    public void ALowContrastBarcodeReadsBecauseTheThresholdIsMeasured()
    {
        const string payload = "GREY-9";

        // 0.7 grey is luminance ~178, comfortably above a mid-scale threshold of 128.
        string modules = Code128Modules(payload);

        Assert.Equal(payload, DecodeModules(modules, fillGrey: 0.7f));
    }

    // ================================================================
    //  Fixtures
    // ================================================================

    /// <summary>Writes a barcode, renders it, and reads it back.</summary>
    private static IReadOnlyList<BarcodeReadResult> ReadBack(string payload, BarcodeType type)
    {
        byte[] pdf = PdfBarcodeWriter.GeneratePdf(payload, type,
            new BarcodeOptions { ModuleWidth = 2f, Height = 60f, ShowText = false });

        using var input = new MemoryStream(pdf, writable: false);
        return PdfBarcodeReader.Read(input);
    }

    /// <summary>An 11-module character pattern as its module string.</summary>
    internal static string Modules(int value)
        => Bits(Code128Symbols.Pattern(value), Code128Symbols.ModulesOf(value));

    internal static string Bits(int pattern, int modules)
    {
        var chars = new char[modules];
        for (int i = 0; i < modules; i++)
            chars[i] = ((pattern >> (modules - 1 - i)) & 1) != 0 ? '1' : '0';
        return new string(chars);
    }

    /// <summary>A complete EAN-13 module string for a thirteen-digit code.</summary>
    internal static string Ean13Modules(string code)
    {
        int parity = EanSymbols.FirstDigitParity(code[0] - '0');
        var sb = new System.Text.StringBuilder();

        sb.Append(Bits(EanSymbols.EdgeGuard, EanSymbols.EdgeGuardModules));
        for (int i = 1; i <= 6; i++)
        {
            bool even = ((parity >> (6 - i)) & 1) != 0;
            int digit = code[i] - '0';
            sb.Append(Bits(even ? EanSymbols.LeftEven(digit) : EanSymbols.LeftOdd(digit),
                           EanSymbols.DigitModules));
        }
        sb.Append(Bits(EanSymbols.CentreGuard, EanSymbols.CentreGuardModules));
        for (int i = 7; i <= 12; i++)
            sb.Append(Bits(EanSymbols.Right(code[i] - '0'), EanSymbols.DigitModules));
        sb.Append(Bits(EanSymbols.EdgeGuard, EanSymbols.EdgeGuardModules));

        return sb.ToString();
    }

    /// <summary>
    /// Paints a module string onto a page and reads it back, returning the payload or null.
    /// </summary>
    /// <remarks>
    /// Goes through the real renderer and the real scanner rather than calling a decoder directly,
    /// so a hand-built symbol is subject to exactly the thresholding and quantisation a written one
    /// is. Otherwise these would be testing a different code path from the round trips above.
    /// </remarks>
    private static string DecodeModules(string modules, float fillGrey = 0f)
    {
        byte[] pdf = ModulePagePdf(modules, fillGrey);

        using var input = new MemoryStream(pdf, writable: false);
        IReadOnlyList<BarcodeReadResult> found = PdfBarcodeReader.Read(input);

        return found.Count == 0 ? null : found[0].Text;
    }

    /// <summary>The complete Code 128 subset B module string for a payload.</summary>
    private static string Code128Modules(string payload)
    {
        var sb = new System.Text.StringBuilder(Modules(Code128Symbols.StartB));
        int checksum = Code128Symbols.StartB;

        for (int i = 0; i < payload.Length; i++)
        {
            int value = payload[i] - Code128Symbols.SubsetBFirstAscii;
            sb.Append(Modules(value));
            checksum += (i + 1) * value;
        }

        sb.Append(Modules(checksum % Code128Symbols.ChecksumModulus));
        sb.Append(Bits(Code128Symbols.Pattern(Code128Symbols.Stop), Code128Symbols.StopModules));
        return sb.ToString();
    }

    private static byte[] ModulePagePdf(string modules, float fillGrey = 0f)
    {
        const float ModuleWidth = 2f, Height = 60f, Left = 40f, Bottom = 400f;

        var content = new System.Text.StringBuilder();
        content.AppendLine(string.Format(
            System.Globalization.CultureInfo.InvariantCulture, "q {0} g", fillGrey));
        for (int i = 0; i < modules.Length; i++)
        {
            if (modules[i] != '1') continue;
            content.AppendLine(string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                "{0} {1} {2} {3} re f",
                Left + (i * ModuleWidth), Bottom, ModuleWidth, Height));
        }
        content.AppendLine("Q");

        // A band around the symbol, not a Letter page: the reader renders at 300 dpi, and a full
        // page is 8.4 megapixels — a 34 MB buffer per test on the large-object heap, most of it
        // white. The band keeps 40 pt of white on every side, so the symbol is read exactly as it
        // would be on the full page (every test here that expects a decode is the control).
        return OnePagePdf(content.ToString(),
            mediaBox: string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "[0 {0} 612 {1}]", Bottom - 40f, Bottom + Height + 40f));
    }

    /// <summary>A page with prose, a QR code and a Code 128 barcode on it.</summary>
    private static byte[] MixedPagePdf(string qr, string linear)
    {
        var content = new System.Text.StringBuilder();

        content.AppendLine("BT /F1 14 Tf 60 720 Td (Invoice 4711 - illuminated title lines) Tj ET");
        content.AppendLine("BT /F1 11 Tf 60 700 Td (Bill to: A. Client, 12 Mill Hill, London) Tj ET");
        content.AppendLine(PdfBarcodeWriter.GenerateContentStream(
            qr, BarcodeType.QrCode, 60f, 380f,
            new BarcodeOptions { Size = 160f, ShowText = false }));
        content.AppendLine(PdfBarcodeWriter.GenerateContentStream(
            linear, BarcodeType.Code128, 60f, 250f,
            new BarcodeOptions { ModuleWidth = 2f, Height = 60f, ShowText = false }));

        return OnePagePdf(content.ToString());
    }

    /// <summary>
    /// A page packed with the letter shapes that produce 1:1:3:1:1 runs, and ruled like a table.
    /// </summary>
    /// <remarks>
    /// <b>The rules are not decoration.</b> A long horizontal run of ink with a vertical stroke
    /// rising from its left end is the shape a Data Matrix's L makes — and also the shape every
    /// table border and underline makes. Without them this page could not tell whether the
    /// locator's arm-length check was doing anything.
    /// </remarks>
    private static byte[] DenseTextPagePdf()
    {
        var content = new System.Text.StringBuilder();
        content.AppendLine("q 0 0 0 rg");

        for (int line = 0; line < 40; line++)
        {
            double y = 740 - (line * 18);

            content.AppendLine(string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                "BT /F1 11 Tf 40 {0} Td (illillill milli lIlIlI 111 iii lll Hill Mill Bill Till "
                + "illuminated fulfilling illegible) Tj ET", y));

            if (line % 4 != 3) continue;

            // A full-width rule with a left-hand vertical stroke meeting it: the L shape, at a
            // width whose implied square is taller than the page.
            content.AppendLine(string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                "{0} {1} {2} {3} re f", 40, y - 4, 520, 1.5));
            content.AppendLine(string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                "{0} {1} {2} {3} re f", 40, y - 4, 1.5, 70));

            // And a SHORT rule with a short tick, which is the case the arm-length check is for:
            // the implied square fits on the page and its top edge lands in the prose above, so
            // nothing but the mismatch between a 24pt run and a 6pt stroke rules it out.
            content.AppendLine(string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                "{0} {1} {2} {3} re f", 300, y - 4, 24, 1.5));
            content.AppendLine(string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                "{0} {1} {2} {3} re f", 300, y - 4, 1.5, 6));
        }

        content.AppendLine("Q");
        return OnePagePdf(content.ToString());
    }

    private static byte[] TextPagePdf()
        => OnePagePdf("BT /F1 18 Tf 60 500 Td (Invoice 4711 -- no barcode here) Tj ET\n");

    /// <summary>A one-page PDF drawing the given content stream, with Helvetica as /F1.</summary>
    internal static byte[] OnePagePdf(string content, string mediaBox = "[0 0 612 792]")
    {
        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            $"<< /Type /Page /Parent 2 0 R /MediaBox {mediaBox} "
                + "/Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>",
            $"<< /Length {content.Length} >>\nstream\n{content}endstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        };

        var sb = new System.Text.StringBuilder();
        var offsets = new List<int>();
        sb.Append("%PDF-1.7\n");

        for (int i = 0; i < bodies.Count; i++)
        {
            offsets.Add(sb.Length);
            sb.Append($"{i + 1} 0 obj\n{bodies[i]}\nendobj\n");
        }

        int xref = sb.Length;
        sb.Append($"xref\n0 {bodies.Count + 1}\n0000000000 65535 f \n");
        foreach (int offset in offsets) sb.Append(offset.ToString("D10") + " 00000 n \n");
        sb.Append($"trailer\n<< /Size {bodies.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF");

        return System.Text.Encoding.ASCII.GetBytes(sb.ToString());
    }
}
