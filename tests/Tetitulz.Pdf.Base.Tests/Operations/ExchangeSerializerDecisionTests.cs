// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using Tetitulz.Pdf.Base.Operations.Forms;
using Tetitulz.Pdf.Core;
using Tetitulz.Pdf.Core.Constants;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// The decisions the two exchange serialisers make that no fixture reached — found by mutating
/// <c>XfdfSerializer</c> and <c>FdfSerializer</c> a decision at a time.
/// </summary>
/// <remarks>
/// <para><b>What the existing suites assert is the value that came out</b>, which is the right
/// question for a round trip and blind to almost everything else: whether an empty section was
/// written at all, which of two lenient readings a malformed attribute takes, whether a colour was
/// converted or clamped, and every DoS ceiling. A value survives a round trip either way.</para>
/// <para><b>XFDF is parsed as an <see cref="XDocument"/>, never matched as a substring</b>, for the
/// reason the repository applies to SVG: a substring passes on a file whose structure is wrong.</para>
/// </remarks>
public sealed class ExchangeSerializerDecisionTests
{
    private static readonly XNamespace Ns = "http://ns.adobe.com/xfdf/";

    private static byte[] Written(Action<Stream> write)
    {
        using var ms = new MemoryStream();
        write(ms);
        return ms.ToArray();
    }

    private static XDocument WrittenXfdf(FormData data) =>
        XDocument.Parse(Encoding.UTF8.GetString(Written(s => PdfFormDataExchange.WriteXfdf(data, s))));

    private static FormData ReadXfdf(string xml)
    {
        using var input = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        return PdfFormDataExchange.ReadXfdf(input);
    }

    private static FormData ReadFdf(string fdf)
    {
        using var input = new MemoryStream(Encoding.ASCII.GetBytes(fdf));
        return PdfFormDataExchange.ReadFdf(input);
    }

    private static FormDataAnnotation Highlight()
    {
        var a = new FormDataAnnotation(PdfAnnotationSubtypes.Highlight);
        a.Rect = new double[] { 10, 10, 20, 20 };
        return a;
    }

    /// <summary>The one annotation element of a written file.</summary>
    private static XElement AnnotElement(FormData data) =>
        WrittenXfdf(data).Root!.Element(Ns + "annots")!.Elements().Single();

    // ================================================================
    //  XFDF — writing
    // ================================================================

    /// <summary>
    /// A fields-only file carries no <c>&lt;annots&gt;</c> element at all. An empty one is a claim
    /// that a review found nothing, which is not what a form export means.
    /// </summary>
    [Fact]
    public void WriteXfdf_OmitsTheAnnotsSectionWhenThereAreNoAnnotations()
    {
        var data = new FormData();
        data.Fields.Add(new FormDataField("name", "Ada"));

        Assert.Null(WrittenXfdf(data).Root!.Element(Ns + "annots"));
    }

    /// <summary>
    /// A null entry is skipped rather than throwing. <see cref="FormDataAnnotation"/>'s constructor
    /// already refuses a null or empty subtype, so a null <i>entry</i> in the list is the only way
    /// an unusable annotation can reach the writer.
    /// </summary>
    [Fact]
    public void WriteXfdf_SkipsANullAnnotationAndWritesTheRest()
    {
        var data = new FormData();
        data.Annotations.Add(null);
        data.Annotations.Add(Highlight());

        Assert.Equal("highlight", AnnotElement(data).Name.LocalName);
    }

    /// <summary>
    /// <c>start</c> and <c>end</c> are two points, so a <c>/L</c> that is not four numbers has no
    /// XFDF spelling — and indexing it anyway is how a malformed file becomes an exception.
    /// </summary>
    [Fact]
    public void WriteXfdf_OmitsStartAndEndWhenTheLineIsNotFourNumbers()
    {
        var data = new FormData();
        var line = new FormDataAnnotation(PdfAnnotationSubtypes.Line) { Line = new double[] { 1, 2 } };
        data.Annotations.Add(line);

        XElement written = AnnotElement(data);

        Assert.Null(written.Attribute("start"));
        Assert.Null(written.Attribute("end"));
    }

    /// <summary>A one-component <c>/C</c> is grey: the same value on all three channels.</summary>
    [Fact]
    public void WriteXfdf_WritesAGreyColourOnAllThreeChannels()
    {
        var data = new FormData();
        var a = Highlight();
        a.Color = new double[] { 0.5 };
        data.Annotations.Add(a);

        Assert.Equal("#808080", (string)AnnotElement(data).Attribute("color"));
    }

    /// <summary>
    /// XFDF has nowhere to put a fourth component, so a CMYK <c>/C</c> is converted by §10.4.2.3.
    /// Writing the first three components instead would turn 50% black into black.
    /// </summary>
    [Theory]
    [InlineData(0, 0, 0, 0.5, "#808080")]   // K alone is grey, not black
    [InlineData(0, 1, 0, 0, "#FF00FF")]     // magenta
    public void WriteXfdf_ConvertsCmykBecauseTheFormatHasNoFourthComponent(
        double c, double m, double y, double k, string expected)
    {
        var data = new FormData();
        var a = Highlight();
        a.Color = new[] { c, m, y, k };
        data.Annotations.Add(a);

        Assert.Equal(expected, (string)AnnotElement(data).Attribute("color"));
    }

    /// <summary>
    /// A component outside 0–1 is clamped rather than wrapped. Unclamped, <c>1.5</c> scales to 382
    /// and <c>(int)</c> truncation writes <c>17E</c> — a three-digit channel that makes the whole
    /// attribute unparseable.
    /// </summary>
    [Fact]
    public void WriteXfdf_ClampsAColourComponentOutsideItsRange()
    {
        var data = new FormData();
        var a = Highlight();
        a.Color = new double[] { 1.5, -0.2, 0 };
        data.Annotations.Add(a);

        Assert.Equal("#FF0000", (string)AnnotElement(data).Attribute("color"));
    }

    // ================================================================
    //  XFDF — reading
    // ================================================================

    /// <summary>
    /// An empty <c>&lt;field/&gt;</c> never produces an end element, so the name it pushed has to be
    /// popped where it was pushed. Left on the stack it becomes a prefix of everything after it.
    /// </summary>
    [Fact]
    public void ReadXfdf_AnEmptyFieldElementDoesNotPrefixTheNextFieldsName()
    {
        FormData data = ReadXfdf(
            "<xfdf xmlns=\"http://ns.adobe.com/xfdf/\"><fields>" +
            "<field name=\"a\"/>" +
            "<field name=\"b\"><value>1</value></field>" +
            "</fields></xfdf>");

        Assert.Equal("b", data.Fields.Single().Name);
    }

    /// <summary>
    /// A cleared field is an empty <c>&lt;value/&gt;</c>, not a missing one — and the difference is
    /// what tells "the user emptied this box" from "this box was not in the file".
    /// </summary>
    [Fact]
    public void ReadXfdf_AnEmptyValueElementIsTheEmptyStringNotAnAbsentField()
    {
        FormData data = ReadXfdf(
            "<xfdf xmlns=\"http://ns.adobe.com/xfdf/\"><fields>" +
            "<field name=\"note\"><value/></field></fields></xfdf>");

        Assert.Equal(string.Empty, data.Fields.Single().Value);
    }

    /// <summary>
    /// A value of nothing but spaces is the user's data. The writer says <c>xml:space="preserve"</c>
    /// and the reader has to collect the Whitespace node type to honour it — a value whose text is
    /// entirely whitespace produces no Text node at all.
    /// </summary>
    [Fact]
    public void ReadXfdf_KeepsAValueThatIsOnlyWhitespace()
    {
        FormData data = ReadXfdf(
            "<xfdf xmlns=\"http://ns.adobe.com/xfdf/\" xml:space=\"preserve\"><fields>" +
            "<field name=\"pad\"><value>   </value></field></fields></xfdf>");

        Assert.Equal("   ", data.Fields.Single().Value);
    }

    /// <summary>
    /// The same value from a producer that did <b>not</b> declare <c>xml:space="preserve"</c>.
    /// </summary>
    /// <remarks>
    /// <b>This is the case the reader's <c>Whitespace</c> branch exists for, and the test above
    /// cannot reach it.</b> Inside an <c>xml:space="preserve"</c> scope `XmlReader` reports
    /// whitespace-only content as <c>SignificantWhitespace</c>; outside one it reports
    /// <c>Whitespace</c> — a different case in the same switch. This writer always emits the
    /// attribute, so every fixture that round-trips through it takes the first branch, and a
    /// mutation of the second survived the whole suite until this file existed. The XML infoset has
    /// no element-only content model to appeal to without a DTD, so the characters are the field's
    /// value either way.
    /// </remarks>
    [Fact]
    public void ReadXfdf_KeepsAWhitespaceValueFromAProducerThatDidNotSayPreserve()
    {
        FormData data = ReadXfdf(
            "<xfdf xmlns=\"http://ns.adobe.com/xfdf/\"><fields>" +
            "<field name=\"pad\"><value>   </value></field></fields></xfdf>");

        Assert.Equal("   ", data.Fields.Single().Value);
    }

    /// <summary>
    /// Producers separate a gesture's points with <c>;</c> and its components with <c>,</c>. Both
    /// mean the same flat <c>x y x y</c> array the PDF dictionary wants.
    /// </summary>
    [Fact]
    public void ReadXfdf_AcceptsSemicolonSeparatedGestureCoordinates()
    {
        FormData data = ReadXfdf(
            "<xfdf xmlns=\"http://ns.adobe.com/xfdf/\"><annots>" +
            "<ink page=\"0\"><inklist><gesture>1,2;3,4</gesture></inklist></ink>" +
            "</annots></xfdf>");

        Assert.Equal(new double[] { 1, 2, 3, 4 }, data.Annotations.Single().InkList.Single());
    }

    /// <summary>
    /// A colour attribute that is not <c>#RRGGBB</c> yields no colour, rather than a partial read of
    /// whatever digits were there.
    /// </summary>
    [Theory]
    [InlineData("red")]
    [InlineData("#12")]
    [InlineData("#GGGGGG")]
    public void ReadXfdf_IgnoresAColourAttributeThatIsNotSixHexDigits(string colour)
    {
        FormData data = ReadXfdf(
            "<xfdf xmlns=\"http://ns.adobe.com/xfdf/\"><annots>" +
            "<highlight page=\"0\" color=\"" + colour + "\"/></annots></xfdf>");

        Assert.Null(data.Annotations.Single().Color);
    }

    /// <summary>
    /// Nothing conformant writes <c>vertices</c> as an attribute — this writer puts it in a child
    /// element, because an independent implementation refuses the annotation otherwise — but a lenient read costs one line
    /// and an unread attribute costs the whole polygon.
    /// </summary>
    [Fact]
    public void ReadXfdf_ReadsVerticesWrittenAsAnAttribute()
    {
        FormData data = ReadXfdf(
            "<xfdf xmlns=\"http://ns.adobe.com/xfdf/\"><annots>" +
            "<polygon page=\"0\" vertices=\"1,2,3,4,5,6\"/></annots></xfdf>");

        Assert.Equal(new double[] { 1, 2, 3, 4, 5, 6 }, data.Annotations.Single().Vertices);
    }

    /// <summary>A file declaring more fields than the ceiling is refused, naming what it declared.</summary>
    [Fact]
    public void ReadXfdf_AdversarialFieldCount_IsRefusedRatherThanRead()
    {
        var sb = new StringBuilder("<xfdf xmlns=\"http://ns.adobe.com/xfdf/\"><fields>");
        for (int i = 0; i <= PdfLimits.MaxFormDataFields; i++)
            sb.Append("<field name=\"f").Append(i).Append("\"><value>1</value></field>");
        sb.Append("</fields></xfdf>");

        var ex = Assert.Throws<PdfException>(() => ReadXfdf(sb.ToString()));
        Assert.Contains("fields", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>The same ceiling on the other half of the file — an <c>annots</c> section.</summary>
    [Fact]
    public void ReadXfdf_AdversarialAnnotationCount_IsRefusedRatherThanRead()
    {
        var sb = new StringBuilder("<xfdf xmlns=\"http://ns.adobe.com/xfdf/\"><annots>");
        for (int i = 0; i <= PdfLimits.MaxFormDataFields; i++) sb.Append("<highlight page=\"0\"/>");
        sb.Append("</annots></xfdf>");

        var ex = Assert.Throws<PdfException>(() => ReadXfdf(sb.ToString()));
        Assert.Contains("annotations", ex.Message, StringComparison.Ordinal);
    }

    // ================================================================
    //  FDF — writing
    // ================================================================

    /// <summary>
    /// §7.5.2's binary comment. Without it a transport that guesses at text-versus-binary can
    /// line-end-convert a UTF-16BE field value into mojibake.
    /// </summary>
    [Fact]
    public void WriteFdf_MarksTheFileAsBinary()
    {
        var data = new FormData();
        data.Fields.Add(new FormDataField("name", "Ada"));

        byte[] fdf = Written(s => PdfFormDataExchange.WriteFdf(data, s));

        int comment = Array.IndexOf(fdf, (byte)'%', "%FDF-1.2\n".Length);
        Assert.Equal(new byte[] { 0xE2, 0xE3, 0xCF, 0xD3 }, fdf.Skip(comment + 1).Take(4).ToArray());
    }

    /// <summary>
    /// The outer guard asks whether the model holds annotations; this one asks whether any of them
    /// could be written. A list of nothing but null entries must not produce <c>/Annots []</c>.
    /// </summary>
    [Fact]
    public void WriteFdf_OmitsAnnotsWhenEveryEntryWasSkipped()
    {
        var data = new FormData();
        data.Annotations.Add(null);

        string fdf = Encoding.GetEncoding("iso-8859-1")
                             .GetString(Written(s => PdfFormDataExchange.WriteFdf(data, s)));

        Assert.DoesNotContain("/Annots", fdf, StringComparison.Ordinal);
    }

    /// <summary>
    /// §7.5.5: <c>/Size</c> is one more than the highest object number, because object 0 is the free
    /// head of the list. A file whose <c>/Size</c> equals its object count is short by one.
    /// </summary>
    [Fact]
    public void WriteFdf_DeclaresATrailerSizeThatCountsTheFreeEntry()
    {
        var data = new FormData();
        data.Fields.Add(new FormDataField("name", "Ada"));

        string fdf = Encoding.GetEncoding("iso-8859-1")
                             .GetString(Written(s => PdfFormDataExchange.WriteFdf(data, s)));

        Assert.Contains("/Size 2", fdf, StringComparison.Ordinal);
    }

    /// <summary>
    /// Both halves of <c>/ID</c> survive an FDF round trip. The second is the revision the data was
    /// taken from, which is what tells a receiver the data is coming back to a document that has
    /// since been saved.
    /// </summary>
    [Fact]
    public void Fdf_RoundTripsBothHalvesOfTheDocumentId()
    {
        var data = new FormData { OriginalId = "0123ABCD", ModifiedId = "4567EF89" };
        data.Fields.Add(new FormDataField("name", "Ada"));

        using var round = new MemoryStream(Written(s => PdfFormDataExchange.WriteFdf(data, s)));
        FormData back = PdfFormDataExchange.ReadFdf(round);

        Assert.Equal("0123ABCD", back.OriginalId);
        Assert.Equal("4567EF89", back.ModifiedId);
    }

    // ================================================================
    //  FDF — reading
    // ================================================================

    /// <summary>
    /// One unreadable object is not an unreadable file: each object is parsed from its own bytes, so
    /// the fields that can be read are still the answer.
    /// </summary>
    /// <remarks>
    /// The second object nests arrays past <see cref="PdfLimits.MaxObjectGraphDepth"/>, which is a
    /// shape the parser <b>throws</b> for — an unterminated string or a missing value is tolerated,
    /// so neither reaches the per-object <c>catch</c> this asserts on. Getting that wrong is how the
    /// first version of this test passed while the mutation that rethrows survived.
    /// </remarks>
    [Fact]
    public void ReadFdf_MalformedObjectAmongGoodOnes_StillReadsTheRest()
    {
        string tooDeep = new string('[', PdfLimits.MaxObjectGraphDepth + 5);

        FormData data = ReadFdf(
            "%FDF-1.2\n" +
            "1 0 obj\n<< /FDF << /Fields [<< /T (name) /V (Ada) >>] >> >>\nendobj\n" +
            "2 0 obj\n" + tooDeep + "\nendobj\n" +
            "trailer\n<< /Root 1 0 R >>\n%%EOF\n");

        Assert.Equal("Ada", data.Fields.Single().Value);
    }

    /// <summary>
    /// A reference cycle between two objects — <c>3 0 R</c> → <c>4 0 R</c> → <c>3 0 R</c> — is
    /// bounded by the hop cap. The <c>/Kids</c> cycle test beside this one cannot reach it: that
    /// walk is bounded by its depth cap instead, so following a reference forever was untested.
    /// </summary>
    [Fact]
    public void ReadFdf_AdversarialReferenceCycle_DoesNotHang()
    {
        FormData data = ReadFdf(
            "%FDF-1.2\n" +
            "1 0 obj\n<< /FDF << /Fields [<< /T (name) /V 3 0 R >>] >> >>\nendobj\n" +
            "3 0 obj\n4 0 R\nendobj\n" +
            "4 0 obj\n3 0 R\nendobj\n" +
            "trailer\n<< /Root 1 0 R >>\n%%EOF\n");

        // The cap stops the walk on the last reference it followed rather than on a value, so the
        // field is reported under its name with nothing in it. That is the honest answer: the file
        // names the field and never says what it holds.
        Assert.Equal("name", data.Fields.Single().Name);
        Assert.Equal(string.Empty, data.Fields.Single().Value);
    }

    /// <summary>
    /// A <c>/Kids</c> chain deeper than the cap is refused rather than recursed. Without the cap the
    /// walk is unbounded recursion on caller-supplied input: the mutation that removes it takes the
    /// test host down with a stack overflow rather than failing a test.
    /// </summary>
    [Fact]
    public void ReadFdf_AdversarialKidsDepth_IsRefused()
    {
        var sb = new StringBuilder("%FDF-1.2\n1 0 obj\n<< /FDF << /Fields [");
        for (int i = 0; i <= PdfLimits.MaxTreeDepth; i++) sb.Append("<< /T (n) /Kids [");
        sb.Append("<< /T (leaf) /V (x) >>");
        for (int i = 0; i <= PdfLimits.MaxTreeDepth; i++) sb.Append("] >>");
        sb.Append("] >> >>\nendobj\ntrailer\n<< /Root 1 0 R >>\n%%EOF\n");

        var ex = Assert.Throws<PdfException>(() => ReadFdf(sb.ToString()));
        Assert.Contains("nests", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Forty levels of nodes, each listing the next twice in its <c>/Kids</c>: well under the depth
    /// cap, and each node is read once, so the one leaf is one field. The walk kept no record of the
    /// nodes it had read and went down every path, 2⁴⁰ of them, and the call did not return.
    /// </summary>
    [Fact]
    public void ReadFdf_AdversarialKidsSharedAtEveryLevel_IsReadOnce()
    {
        const int levels = 40;
        var sb = new StringBuilder("%FDF-1.2\n1 0 obj\n<< /FDF << /Fields [2 0 R] >> >>\nendobj\n");
        for (int k = 2; k < 2 + levels; k++)
            sb.Append($"{k} 0 obj\n<< /T (n) /Kids [{k + 1} 0 R {k + 1} 0 R] >>\nendobj\n");
        sb.Append($"{2 + levels} 0 obj\n<< /T (leaf) /V (x) >>\nendobj\ntrailer\n<< /Root 1 0 R >>\n%%EOF\n");

        FormData data = ReadFdf(sb.ToString());

        var field = Assert.Single(data.Fields);
        Assert.Equal(string.Join(".", Enumerable.Repeat("n", levels)) + ".leaf", field.Name);
        Assert.Equal("x", field.Value);
    }

    /// <summary>
    /// §12.7.3.2: a node with no <c>/T</c> contributes nothing to the qualified name. Joining a
    /// trailing period on instead addresses a field no form has.
    /// </summary>
    [Fact]
    public void ReadFdf_AKidWithNoNameKeepsItsParentsName()
    {
        FormData data = ReadFdf(
            "%FDF-1.2\n" +
            "1 0 obj\n<< /FDF << /Fields [<< /T (outer) /Kids [<< /V (x) >>] >>] >> >>\nendobj\n" +
            "trailer\n<< /Root 1 0 R >>\n%%EOF\n");

        Assert.Equal("outer", data.Fields.Single().Name);
    }

    /// <summary>
    /// A value with no name anywhere in its chain addresses nothing, so it is not data. Imported, it
    /// would be a field the filler can never match.
    /// </summary>
    [Fact]
    public void ReadFdf_SkipsAValueWithNoNameInItsChain()
    {
        FormData data = ReadFdf(
            "%FDF-1.2\n" +
            "1 0 obj\n<< /FDF << /Fields [<< /V (orphan) >>] >> >>\nendobj\n" +
            "trailer\n<< /Root 1 0 R >>\n%%EOF\n");

        Assert.Empty(data.Fields);
    }

    /// <summary>
    /// <c>/Kids</c> written as an indirect reference — which producers do — must resolve. Read raw,
    /// the array comes back null, which is indistinguishable from a field with no kids.
    /// </summary>
    [Fact]
    public void ReadFdf_ResolvesKidsWrittenAsAnIndirectReference()
    {
        FormData data = ReadFdf(
            "%FDF-1.2\n" +
            "1 0 obj\n<< /FDF << /Fields [<< /T (address) /Kids 2 0 R >>] >> >>\nendobj\n" +
            "2 0 obj\n[<< /T (city) /V (London) >>]\nendobj\n" +
            "trailer\n<< /Root 1 0 R >>\n%%EOF\n");

        Assert.Equal("address.city", data.Fields.Single().Name);
        Assert.Equal("London", data.Fields.Single().Value);
    }

    /// <summary>
    /// A <c>/V</c> array may hold names as well as strings — a multi-select whose export values are
    /// check-box states. Dropping the names keeps the array and loses half its entries.
    /// </summary>
    [Fact]
    public void ReadFdf_ReadsANameInsideAValueArray()
    {
        FormData data = ReadFdf(
            "%FDF-1.2\n" +
            "1 0 obj\n<< /FDF << /Fields [<< /T (opts) /V [(en) /Yes] >>] >> >>\nendobj\n" +
            "trailer\n<< /Root 1 0 R >>\n%%EOF\n");

        Assert.Equal(new[] { "en", "Yes" }, data.Fields.Single().Values);
    }

    /// <summary>
    /// The byte ceiling, at a scale a test can reach. <see cref="PdfLimits.MaxFormDataBytes"/> is
    /// 64 MiB, so the ceiling is taken as a parameter and a few hundred bytes stand where 64 MiB
    /// would — the shape <c>Woff2Decoder.Decode(byte[], long)</c> established. The forwarding of the
    /// real constant is mutated separately, or every case here would pass over a production path the
    /// ceiling never reaches.
    /// </summary>
    /// <remarks>
    /// Two branches, because they answer different questions: a seekable stream is refused on its
    /// declared <c>Length</c> before a byte is copied, and a forward-only one — an HTTP body, which
    /// is exactly where an oversized upload arrives from — can only be counted as it is read.
    /// </remarks>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ReadFdf_AdversarialOversizedInput_IsRefusedOnBothStreamShapes(bool seekable)
    {
        byte[] bytes = Encoding.ASCII.GetBytes(
            "%FDF-1.2\n1 0 obj\n<< /FDF << /Fields [<< /T (name) /V (Ada) >>] >> >>\nendobj\n" +
            "trailer\n<< /Root 1 0 R >>\n%%EOF\n");

        using Stream input = seekable
            ? new MemoryStream(bytes, writable: false)
            : new ForwardOnlyStream(bytes);

        var ex = Assert.Throws<PdfException>(() => FdfSerializer.Read(input, maxBytes: 16));
        Assert.Contains("larger than 16 bytes", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>A control: the same file inside the ceiling reads normally on both shapes.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ReadFdf_AnInputInsideTheCeilingIsRead(bool seekable)
    {
        byte[] bytes = Encoding.ASCII.GetBytes(
            "%FDF-1.2\n1 0 obj\n<< /FDF << /Fields [<< /T (name) /V (Ada) >>] >> >>\nendobj\n" +
            "trailer\n<< /Root 1 0 R >>\n%%EOF\n");

        using Stream input = seekable
            ? new MemoryStream(bytes, writable: false)
            : new ForwardOnlyStream(bytes);

        Assert.Equal("Ada", FdfSerializer.Read(input, maxBytes: bytes.Length).Fields.Single().Value);
    }

    /// <summary>
    /// The same ceiling <b>through the public entry point</b>, at the real constant — which is what
    /// makes the parameterised cases above evidence about production rather than about an overload.
    /// A stream that merely <i>declares</i> 64 MiB is refused before a byte is copied, so the
    /// fixture costs nothing.
    /// </summary>
    [Fact]
    public void ReadFdf_AdversarialOversizedInput_IsRefusedAtTheRealCeiling()
    {
        using var input = new LyingLengthStream(PdfLimits.MaxFormDataBytes + 1);

        var ex = Assert.Throws<PdfException>(() => PdfFormDataExchange.ReadFdf(input));
        Assert.Contains("larger than", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Seekable, declares <paramref name="length"/> bytes and yields none. The declaration is all
    /// the seekable branch reads, so this is a 64 MiB input that allocates nothing — and under a
    /// mutation that stops forwarding the constant it becomes an <i>empty</i> file, which fails with
    /// a different message rather than passing.
    /// </summary>
    private sealed class LyingLengthStream : Stream
    {
        internal LyingLengthStream(long length) => Length = length;

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length { get; }
        public override long Position { get; set; }

        public override int Read(byte[] buffer, int offset, int count) => 0;
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => Position;
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>A stream that cannot report its length — the shape an upload arrives as.</summary>
    private sealed class ForwardOnlyStream : Stream
    {
        private readonly MemoryStream _inner;

        internal ForwardOnlyStream(byte[] bytes) => _inner = new MemoryStream(bytes, writable: false);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) _inner.Dispose();
            base.Dispose(disposing);
        }
    }

    /// <summary>More objects than the ceiling is refused before they are all parsed.</summary>
    [Fact]
    public void ReadFdf_AdversarialObjectCount_IsRefusedRatherThanRead()
    {
        var sb = new StringBuilder("%FDF-1.2\n1 0 obj\n<< /FDF << /Fields [] >> >>\nendobj\n");
        for (int i = 2; i <= PdfLimits.MaxFormDataFields + 2; i++)
            sb.Append(i).Append(" 0 obj\n<< >>\nendobj\n");
        sb.Append("trailer\n<< /Root 1 0 R >>\n%%EOF\n");

        var ex = Assert.Throws<PdfException>(() => ReadFdf(sb.ToString()));
        Assert.Contains("objects", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>The same ceiling on the field array, which one object can carry entirely.</summary>
    [Fact]
    public void ReadFdf_AdversarialFieldCount_IsRefusedRatherThanRead()
    {
        var sb = new StringBuilder("%FDF-1.2\n1 0 obj\n<< /FDF << /Fields [");
        for (int i = 0; i <= PdfLimits.MaxFormDataFields; i++)
            sb.Append("<< /T (f").Append(i).Append(") /V (x) >>");
        sb.Append("] >> >>\nendobj\ntrailer\n<< /Root 1 0 R >>\n%%EOF\n");

        var ex = Assert.Throws<PdfException>(() => ReadFdf(sb.ToString()));
        Assert.Contains("fields", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The annotation walk runs under the same budget and <b>stops</b> rather than refusing: an
    /// exchange file's annotations are independent of each other, so the ones that fit are still
    /// worth having.
    /// </summary>
    [Fact]
    public void ReadFdf_AdversarialAnnotationCount_StopsAtTheBudget()
    {
        var sb = new StringBuilder("%FDF-1.2\n1 0 obj\n<< /FDF << /Annots [");
        for (int i = 0; i <= PdfLimits.MaxFormDataFields; i++)
            sb.Append("<< /Type /Annot /Subtype /Highlight >>");
        sb.Append("] >> >>\nendobj\ntrailer\n<< /Root 1 0 R >>\n%%EOF\n");

        Assert.Equal(PdfLimits.MaxFormDataFields, ReadFdf(sb.ToString()).Annotations.Count);
    }
}
