// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using Tetitulz.Pdf.Base.Operations.Metadata;
using Tetitulz.Pdf.Core.Document;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations.Metadata;

/// <summary>
/// The decisions in <see cref="PdfMdpVerifier"/>, <see cref="PdfDeclarationsReader"/> and
/// <see cref="PdfDeclarationsWriter"/> that the Metadata sweep found nothing asserting.
/// </summary>
public class MetadataDecisionTests
{
    // ---------------------------------------------------------------- DocMDP

    /// <summary>
    /// A certified document — a signature field whose <c>/Reference</c> is a DocMDP transform —
    /// whose certification <c>/ByteRange</c> ends exactly where the file does, with
    /// <paramref name="p"/> as its <c>/TransformParams</c> entry ("" for none, the spec default) and,
    /// when <paramref name="modified"/>, an incremental update after it that replaces the page's
    /// content.
    /// </summary>
    private static byte[] Certified(string p, bool modified)
    {
        string Bodies(long end) => string.Join("\n",
            Obj(1, "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [6 0 R] /SigFlags 3 >> >>"),
            Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R /Annots [6 0 R] >>"),
            Obj(4, "<< /Length 4 >>\nstream\nq Q\nendstream"),
            Obj(5, "<< /Type /Sig /Filter /Adobe.PPKLite /SubFilter /adbe.pkcs7.detached"
                   + $" /ByteRange [0 0000000010 0000000020 {end - 20:D10}] /Contents <00>"
                   + $" /Reference [<< /Type /SigRef /TransformMethod /DocMDP /TransformParams << /Type /TransformParams {p} /V /1.2 >> >>] >>"),
            Obj(6, "<< /Type /Annot /Subtype /Widget /FT /Sig /T (Signature1) /Rect [0 0 10 10] /V 5 0 R >>"));

        string Document(long end)
        {
            string head = "%PDF-1.7\n";
            var offsets = new List<int>();
            var sb = new StringBuilder(head);
            foreach (string obj in Bodies(end).Split(new[] { "\nendobj\n" }, StringSplitOptions.RemoveEmptyEntries))
            {
                offsets.Add(sb.Length);
                sb.Append(obj).Append("\nendobj\n");
            }
            int xref = sb.Length;
            sb.Append($"xref\n0 {offsets.Count + 1}\n0000000000 65535 f \n");
            foreach (int o in offsets) sb.Append($"{o:D10} 00000 n \n");
            sb.Append($"trailer\n<< /Size {offsets.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
            return sb.ToString();
        }

        string original = Document(Document(20).Length);  // the /ByteRange numbers are fixed-width
        if (!modified) return Encoding.ASCII.GetBytes(original);

        int prev = original.LastIndexOf("startxref\n", StringComparison.Ordinal);
        string prevXref = original.Substring(prev + 10).Split('\n')[0];
        var update = new StringBuilder(original);
        int at = update.Length;
        update.Append("4 0 obj\n<< /Length 8 >>\nstream\n0 0 m S\nendstream\nendobj\n");
        int xrefAt = update.Length;
        update.Append($"xref\n4 1\n{at:D10} 00000 n \ntrailer\n<< /Size 7 /Root 1 0 R /Prev {prevXref} >>\nstartxref\n{xrefAt}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(update.ToString());

        static string Obj(int n, string body) => $"{n} 0 obj\n{body}\nendobj\n";
    }

    /// <summary>
    /// The verifier and the analyser it wraps answer the same question, so every field of the
    /// verifier's result is the analyser's — asserted over a certified document modified after
    /// certification at <c>/P 1</c>, where every field is something other than its default, and an
    /// unmodified one; the potency assertions are that they are.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TheVerifierReports_ExactlyWhatTheAnalyserFinds(bool modified)
    {
        byte[] pdf = Certified("/P 1", modified);
        PdfMdpResult result = PdfMdpVerifier.Verify(new MemoryStream(pdf));

        DocMdpAnalysis analysis;
        using (var doc = PdfDocument.Open(new MemoryStream(pdf)))
            analysis = DocMdpAnalyzer.Analyze(doc, pdf.Length);

        Assert.Equal(
            (analysis.HasCertification, analysis.PermissionLevel, analysis.PermissionDescription,
             analysis.HasIncrementalUpdates, analysis.CertByteRangeEnd, analysis.FileLength,
             analysis.IsCompliant, analysis.Summary),
            (result.HasCertificationSignature, result.PermissionLevel, result.PermissionDescription,
             result.HasIncrementalUpdates, result.CertificationByteRangeEnd, result.FileLength,
             result.IsCompliant, result.Summary));
        Assert.Equal(analysis.ModifiedObjectTypes, result.ModifiedObjectTypes);
        Assert.Equal(analysis.Violations, result.Violations);

        Assert.True(result.HasCertificationSignature);
        Assert.Equal(1, result.PermissionLevel);
        Assert.False(string.IsNullOrEmpty(result.PermissionDescription));
        Assert.False(string.IsNullOrEmpty(result.Summary));
        Assert.True(result.CertificationByteRangeEnd > 0);
        Assert.Equal(modified, result.HasIncrementalUpdates);
        Assert.Equal(!modified, result.IsCompliant);
        if (modified)
        {
            Assert.NotEmpty(result.Violations);
            Assert.NotEmpty(result.ModifiedObjectTypes);
        }
    }

    /// <summary>
    /// A certification with no <c>/P</c> takes the spec default, and says so; the next verification,
    /// of one whose <c>/P</c> is explicit, says it is not defaulted — the flag is the call's own.
    /// </summary>
    [Fact]
    public void ADefaultedPermission_IsReported_AndOnlyForTheCallThatFoundIt()
    {
        PdfMdpVerifier.Verify(new MemoryStream(Certified("", modified: false)));
        Assert.True(PdfMdpVerifier.LastDocMdpUsedDefaultP);

        PdfMdpVerifier.Verify(new MemoryStream(Certified("/P 1", modified: false)));
        Assert.False(PdfMdpVerifier.LastDocMdpUsedDefaultP);
    }

    // ---------------------------------------------------------------- declarations, read

    /// <summary>A one-page PDF whose catalog's <c>/Metadata</c> is the given stream dictionary and data.</summary>
    private static byte[] WithMetadata(string dictionary, string data)
    {
        var bodies = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R /Metadata 4 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] >>",
            $"<< {dictionary} /Length {Encoding.UTF8.GetByteCount(data)} >>\nstream\n{data}\nendstream",
        };
        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int i = 0; i < bodies.Length; i++)
        {
            offsets.Add(Encoding.UTF8.GetByteCount(sb.ToString()));
            sb.Append($"{i + 1} 0 obj\n{bodies[i]}\nendobj\n");
        }
        int xref = Encoding.UTF8.GetByteCount(sb.ToString());
        sb.Append($"xref\n0 {bodies.Length + 1}\n0000000000 65535 f \n");
        foreach (int o in offsets) sb.Append($"{o:D10} 00000 n \n");
        sb.Append($"trailer\n<< /Size {bodies.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    private const string Rdf = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";

    /// <summary>
    /// Only <c>pdfd:conformsTo</c> is a claim: an empty one is none, and a <c>conformsTo</c> in
    /// another namespace — as an element or as an attribute — is someone else's property.
    /// </summary>
    [Fact]
    public void OnlyANonEmptyConformsToInTheDeclarationsNamespace_IsAClaim()
    {
        string xmp =
            "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"" + Rdf + "\">"
            + "<rdf:Description rdf:about=\"\" xmlns:pdfd=\"http://pdfa.org/declarations/\" xmlns:o=\"urn:other\" o:conformsTo=\"urn:other-attribute\">"
            + "<pdfd:declarations><rdf:Bag>"
            + "<rdf:li rdf:parseType=\"Resource\"><pdfd:conformsTo>   </pdfd:conformsTo></rdf:li>"
            + "<rdf:li rdf:parseType=\"Resource\"><o:conformsTo>urn:other-element</o:conformsTo></rdf:li>"
            + "<rdf:li rdf:parseType=\"Resource\" o:conformsTo=\"urn:other-on-li\"/>"
            + "<rdf:li rdf:parseType=\"Resource\"><pdfd:conformsTo>urn:real</pdfd:conformsTo></rdf:li>"
            + "</rdf:Bag></pdfd:declarations></rdf:Description></rdf:RDF></x:xmpmeta>";

        var uris = PdfDeclarationsReader.ReadConformsTo(new MemoryStream(WithMetadata("/Type /Metadata /Subtype /XML", xmp)));
        Assert.Equal(new[] { "urn:real" }, uris);
    }

    /// <summary>A metadata stream that will not decode is reported as that, not as a packet that is not XML.</summary>
    [Fact]
    public void AnUndecodableMetadataStream_IsNamedAsSuch()
    {
        var uris = PdfDeclarationsReader.ReadConformsTo(
            new MemoryStream(WithMetadata("/Type /Metadata /Subtype /XML /Filter /FlateDecode", "not deflate data at all")));
        Assert.Null(uris);
        Assert.Contains("could not be decoded", PdfDeclarationsReader.LastError);
    }

    // ---------------------------------------------------------------- declarations, written

    /// <summary>An empty input is refused as empty, and the revision flag says nothing was appended.</summary>
    [Fact]
    public void AnEmptyInput_IsRefusedAsEmpty()
    {
        Assert.False(PdfDeclarationsWriter.AddDeclaration(new MemoryStream(), new MemoryStream(), "urn:x"));
        Assert.Contains("empty", PdfDeclarationsWriter.LastError);
        Assert.False(PdfDeclarationsWriter.LastCallAppendedRevision);
    }

    /// <summary>A successful write says it appended a revision, and the claim reads back.</summary>
    [Fact]
    public void ASuccessfulWrite_SaysItAppendedARevision()
    {
        byte[] pdf = WithMetadata("/Type /Metadata /Subtype /XML", XmpEmpty);
        using var output = new MemoryStream();
        Assert.True(PdfDeclarationsWriter.AddDeclaration(new MemoryStream(pdf), output, "urn:declared"));
        Assert.True(PdfDeclarationsWriter.LastCallAppendedRevision);
        Assert.Equal(new[] { "urn:declared" }, PdfDeclarationsReader.ReadConformsTo(new MemoryStream(output.ToArray())));
    }

    private const string XmpEmpty =
        "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"" + Rdf + "\"><rdf:Description rdf:about=\"\"/></rdf:RDF></x:xmpmeta>";

    private static XmlDocument Load(string packet)
    {
        var doc = new XmlDocument();
        doc.LoadXml(packet.Substring(packet.IndexOf("<x:xmpmeta", StringComparison.Ordinal),
            packet.LastIndexOf("</x:xmpmeta>", StringComparison.Ordinal) + 12 - packet.IndexOf("<x:xmpmeta", StringComparison.Ordinal)));
        return doc;
    }

    /// <summary>A claim with only a date still carries its claim data.</summary>
    [Fact]
    public void AClaimWithOnlyADate_CarriesItsClaimData()
    {
        string packet = PdfDeclarationsWriter.BuildDeclarationsXmpPacket(null, "urn:x",
            claimDate: new DateTimeOffset(2026, 10, 6, 0, 0, 0, TimeSpan.Zero));
        Assert.Contains("<pdfd:claimDate>2026-10-06</pdfd:claimDate>", packet);
    }

    /// <summary>A second declaration goes into the existing Bag, not a second Bag beside it.</summary>
    [Fact]
    public void ASecondDeclaration_JoinsTheExistingBag()
    {
        string once = PdfDeclarationsWriter.BuildDeclarationsXmpPacket(null, "urn:first");
        string twice = PdfDeclarationsWriter.BuildDeclarationsXmpPacket(once, "urn:second");

        var ns = new XmlNamespaceManager(new NameTable());
        ns.AddNamespace("rdf", Rdf);
        ns.AddNamespace("pdfd", PdfDeclarationsWriter.PdfDeclarationsNamespace);
        XmlDocument doc = Load(twice);
        var bags = doc.SelectNodes("//pdfd:declarations/rdf:Bag", ns);
        Assert.Equal(1, bags.Count);
        Assert.Equal(2, bags[0].SelectNodes("rdf:li", ns).Count);
    }

    /// <summary>
    /// A packet nested past the search cap is not merged with — its content is out of reach and
    /// re-indenting it costs the square of its depth — so the result is a fresh packet without it.
    /// </summary>
    [Fact]
    public void APacketNestedPastTheCap_IsReplacedRatherThanMerged()
    {
        string deep = string.Concat(Enumerable.Repeat("<o:n xmlns:o=\"urn:o\">", 80)) + "deep-marker"
                    + string.Concat(Enumerable.Repeat("</o:n>", 80));
        string existing = "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"" + Rdf
                        + "\"><rdf:Description rdf:about=\"\">" + deep + "</rdf:Description></rdf:RDF></x:xmpmeta>";

        string packet = PdfDeclarationsWriter.BuildDeclarationsXmpPacket(existing, "urn:x");
        Assert.DoesNotContain("deep-marker", packet);
        Assert.Contains("urn:x", packet);

        // The control: the same marker one level down is merged with, and kept.
        string shallow = existing.Replace(deep, "<o:n xmlns:o=\"urn:o\">deep-marker</o:n>");
        Assert.Contains("deep-marker", PdfDeclarationsWriter.BuildDeclarationsXmpPacket(shallow, "urn:x"));
    }

    /// <summary>A packet past the merge budget is replaced by a fresh one rather than parsed and re-serialised.</summary>
    [Fact]
    public void APacketPastTheMergeBudget_IsReplacedRatherThanMerged()
    {
        string padding = new string(' ', PdfDeclarationsWriter.MaxMergedPacketChars);
        string existing = "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"" + Rdf
                        + "\"><rdf:Description rdf:about=\"\"><o:n xmlns:o=\"urn:o\">budget-marker</o:n>"
                        + padding + "</rdf:Description></rdf:RDF></x:xmpmeta>";

        string packet = PdfDeclarationsWriter.BuildDeclarationsXmpPacket(existing, "urn:x");
        Assert.DoesNotContain("budget-marker", packet);
        Assert.Contains("urn:x", packet);
    }
}
