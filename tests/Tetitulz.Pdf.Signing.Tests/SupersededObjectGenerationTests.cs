// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Core;
using Tetitulz.Pdf.Signing.Tests.TestInfrastructure;
using Tetitulz.Pdf.Signing.Timestamping;
using Xunit;

namespace Tetitulz.Pdf.Signing.Tests;

/// <summary>
/// A revision that supersedes an existing object re-declares it at the generation the document
/// already gave it (§7.5.6) — in the object header, in the cross-reference entry, and in every
/// reference the revision mints to it.
/// </summary>
/// <remarks>
/// <para><b>All three writers in this assembly build their own xref sections</b>, so none of them
/// reaches <c>IncrementalUpdateWriter</c>'s generation threading, and all six of the existing
/// objects they supersede were written as generation 0. Against a document whose object really is
/// generation <i>n</i> that supersedes nothing: a strict reader matching <c>N G R</c> against the
/// xref keeps the old object and drops the timestamp or signature entirely.</para>
///
/// <para><b>These assert on the emitted bytes, and that is not a stylistic choice.</b> This
/// library's own reader cannot see the defect — <c>PdfParser.GetObject</c> and
/// <c>ResolveObject</c> key on the object <i>number</i> alone and never compare generation — so an
/// "open the output and check the timestamp is there" test passes with the defect fully present.
/// That is banned pattern 3 arrived at through the reader rather than the assertion.</para>
///
/// <para><b>Why a dead TSA rather than the stand-in.</b> <c>Helpers/DocTimeStampAppender</c> is a
/// test-only re-implementation of the revision shape that hardcodes generation 0 itself, so fixing
/// it would test the stand-in. <c>AddTimestamp</c> writes every object header, the whole xref
/// section and the trailer <i>before</i> it contacts the TSA, so pointing it at an unreachable host
/// exercises the production writer and leaves all the bytes under test in the caller's stream.</para>
/// </remarks>
public sealed class SupersededObjectGenerationTests
{
    private const int CatalogGeneration = 2;

    /// <summary>
    /// The object header must carry the catalog's own generation.
    /// </summary>
    [Fact]
    public void TheTimestamperSupersedesTheCatalogAtItsOwnGeneration()
    {
        string revision = AppendedRevision();
        Assert.Contains($"1 {CatalogGeneration} obj", revision, StringComparison.Ordinal);
        Assert.DoesNotContain("1 0 obj", revision, StringComparison.Ordinal);
    }

    /// <summary>
    /// The xref entry must agree with that header. Changing one alone makes the file
    /// self-inconsistent, which is worse than either error on its own.
    /// </summary>
    [Fact]
    public void TheCrossReferenceEntryAgreesWithTheObjectHeader()
    {
        Assert.Contains($"{CatalogGeneration:D5} n", XrefSection(AppendedRevision()),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The trailer's <c>/Root</c> must name the generation the catalog was just written at. This
    /// writer mints that reference itself rather than copying the original trailer's, which is how
    /// it stayed at 0 while the two writers going through <c>PdfTrailerBuilder</c> did not.
    /// </summary>
    [Fact]
    public void TheTrailerRootNamesTheGenerationTheCatalogWasWrittenAt()
    {
        string revision = AppendedRevision();
        Assert.Contains($"/Root 1 {CatalogGeneration} R", revision, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>The same rule in the second writer</b>, which reaches it through a different route:
    /// <c>PdfSigner</c> writes the catalog through <c>PdfWriter.WriteIndirectObject</c> and its
    /// xref through the <i>generation-aware</i> <c>WriteXrefTable</c> overload — it had been calling
    /// the one that assumes 0 for every entry, which that overload's own doc comment warns against
    /// for exactly this case.
    /// </summary>
    [Fact]
    public void TheSignerSupersedesTheCatalogAtItsOwnGeneration()
    {
        var root = SigningTestCrypto.Ca();
        var signer = SigningTestCrypto.Leaf(root, "CN=Generation Test");
        var (pfx, pw) = SigningTestCrypto.Pfx(signer, root.Cert);

        byte[] pdf = PdfWithGenerationTwoCatalog();
        byte[] signed = SignedPdfFixtures.Sign(pdf, pfx, pw);

        Assert.True(signed.Length > pdf.Length, "the signer must have appended a revision");
        string revision = Encoding.ASCII.GetString(signed, pdf.Length, signed.Length - pdf.Length);

        Assert.Contains($"1 {CatalogGeneration} obj", revision, StringComparison.Ordinal);
        Assert.DoesNotContain("1 0 obj", revision, StringComparison.Ordinal);

        // …and the xref entry that has to agree with it. The catalog is the only object in this
        // revision that is not generation 0, so the entry's presence identifies it without the
        // test having to know how PdfWriter grouped the subsections.
        Assert.Contains($"{CatalogGeneration:D5} n", XrefSection(revision), StringComparison.Ordinal);
    }

    /// <summary>The appended revision's cross-reference section, without its trailer.</summary>
    private static string XrefSection(string revision)
    {
        int start = revision.IndexOf("xref", StringComparison.Ordinal);
        Assert.True(start >= 0, "the appended revision must contain a cross-reference section");

        int end = revision.IndexOf("trailer", start, StringComparison.Ordinal);
        Assert.True(end > start, "the cross-reference section must be followed by a trailer");

        return revision.Substring(start, end - start);
    }

    /// <summary>
    /// The premise, and the half that stops every case above from passing on a fixture that never
    /// had a generation to preserve: the input really does declare its catalog at generation 2, in
    /// the object header <i>and</i> in the xref, and a revision really was appended.
    /// </summary>
    [Fact]
    public void TheFixtureReallyCarriesANonZeroGeneration()
    {
        byte[] pdf = PdfWithGenerationTwoCatalog();
        string text = Encoding.ASCII.GetString(pdf);

        Assert.Contains($"1 {CatalogGeneration} obj", text, StringComparison.Ordinal);
        Assert.Contains($"{CatalogGeneration:D5} n", text, StringComparison.Ordinal);
        Assert.Contains($"/Root 1 {CatalogGeneration} R", text, StringComparison.Ordinal);

        Assert.True(AppendedRevision().Length > 0, "the timestamper must have appended a revision");
    }

    // ------------------------------------------------------------------

    /// <summary>
    /// Runs the production timestamper against an unreachable TSA and returns everything it wrote
    /// past the original bytes.
    /// </summary>
    /// <remarks>
    /// <para><b>Loopback port 1, not a <c>.invalid</c> host name.</b> A reserved TLD still goes to
    /// the resolver, which took ~11 s per call here; a closed loopback port is refused by the
    /// kernel with no DNS involved. Neither reaches the network, but only one of them is fast for
    /// a reason that does not depend on the host's resolver configuration.</para>
    ///
    /// <para>Memoised because all four cases assert on the same bytes, and the writer's output is
    /// deterministic in everything they look at. xUnit runs a class's methods sequentially, and
    /// <see cref="Lazy{T}"/> is thread-safe regardless.</para>
    /// </remarks>
    private static readonly Lazy<string> Revision = new Lazy<string>(() =>
    {
        byte[] pdf = PdfWithGenerationTwoCatalog();

        using var input = new MemoryStream(pdf);
        using var output = new MemoryStream();

        // Everything under test is written before the network call; the throw is expected and is
        // not what is being asserted.
        Assert.ThrowsAny<Exception>(() =>
            PdfDocumentTimestamper.AddTimestamp(input, output, "http://127.0.0.1:1/tsa"));

        byte[] written = output.ToArray();
        Assert.True(written.Length > pdf.Length,
            "the writer must have appended past the copied original");

        return Encoding.ASCII.GetString(written, pdf.Length, written.Length - pdf.Length);
    });

    private static string AppendedRevision() => Revision.Value;

    /// <summary>
    /// A one-page document whose catalog is object 1 <b>generation 2</b> — the legal case that
    /// arises when an object number is freed and reused. Offsets are computed, so the file parses.
    /// </summary>

    private static byte[] PdfWithGenerationTwoCatalog()
    {
        const string content = "BT /F1 12 Tf 72 700 Td (timestamped) Tj ET\n";

        var sb = new StringBuilder();
        var offsets = new List<int>();
        var gens = new List<int>();
        void Obj(int gen, string body) { offsets.Add(sb.Length); gens.Add(gen); sb.Append(body); }

        sb.Append("%PDF-1.7\n");
        Obj(CatalogGeneration, $"1 {CatalogGeneration} obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        Obj(0, "2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        Obj(0, "3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R "
             + "/Resources << /Font << /F1 5 0 R >> >> >>\nendobj\n");
        Obj(0, $"4 0 obj\n<< /Length {content.Length} >>\nstream\n{content}endstream\nendobj\n");
        Obj(0, "5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n");

        int xref = sb.Length;
        sb.AppendFormat("xref\n0 {0}\n0000000000 65535 f \n", offsets.Count + 1);
        for (int i = 0; i < offsets.Count; i++)
            sb.AppendFormat("{0:D10} {1:D5} n \n", offsets[i], gens[i]);

        sb.AppendFormat(
            "trailer\n<< /Size {0} /Root 1 {1} R >>\nstartxref\n{2}\n%%EOF\n",
            offsets.Count + 1, CatalogGeneration, xref);

        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
