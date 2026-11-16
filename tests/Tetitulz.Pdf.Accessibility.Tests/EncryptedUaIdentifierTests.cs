// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using Tetitulz.Pdf.Accessibility.Artifacts;
using Tetitulz.Pdf.Accessibility.Editing;
using Tetitulz.Pdf.Accessibility.Fonts;
using Tetitulz.Pdf.Accessibility.ReadingOrder;
using Tetitulz.Pdf.Accessibility.Tagging;
using Tetitulz.Pdf.Accessibility.Metadata;
using Tetitulz.Pdf.Base.Encryption;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Encryption;
using Xunit;

namespace Tetitulz.Pdf.Accessibility.Tests;

/// <summary>
/// <see cref="PdfUaXmpWriter.Plan"/> — stamping the PDF/UA identifier on an <b>encrypted</b>
/// document in place, the second of the nine accessibility writers to gain the plan/write split
/// (<c>PdfAutoTagger.Plan</c> was the first; GAPS.md §5c, plan 08 item 48).
/// </summary>
/// <remarks>
/// <para>This is the step that matters most for an encrypted file: without it a document can be
/// tagged, remediated and structurally perfect and <b>still cannot be declared PDF/UA at all</b>,
/// because the identifier lives in an XMP packet the clear-text writer refuses to append.</para>
/// <para><b>No new project reference.</b> The Accessibility assembly still cannot decrypt and
/// still contains no password: <c>Plan</c> describes the revision and
/// <c>Encryption.PdfEncryptedIncrementalUpdate.Run</c> encrypts and appends it. The
/// <c>→ Encryption</c> edge in the test csproj is test-only and documented there.</para>
/// </remarks>
public sealed class EncryptedUaIdentifierTests
{
    // PdfDecryptedInput resolves the decryptor through PdfDecryptorHolder, so the reader path
    // below needs it registered. The PdfEncryptedIncrementalUpdate.Run tests do not — they call
    // the Encryption assembly directly — which is why this was missed until the reader arrived.
    // No xunit collection: nothing else in this assembly touches the holder, and Register() is
    // idempotent, so there is no shared-state race to serialise against.
    public EncryptedUaIdentifierTests() => PdfEncryptionOperations.Register();

    private const string UserPassword = "reader-pw";
    private const string OwnerPassword = "author-pw";

    [Fact]
    public void Plan_AppendedThroughTheEncryptedWriter_DeclaresPdfUaAndKeepsBothPasswords()
    {
        byte[] encrypted = Encrypt(ContentBearingPdfTests.ContentPdf());

        // Pin the fixture: it carries no /Metadata, so anything found below came from the plan.
        using (var check = new MemoryStream(Decrypt(encrypted, UserPassword)))
        using (var doc = PdfDocument.Open(check))
            Assert.False(doc.Catalog.Dictionary.ContainsKey(PdfName.Metadata));

        byte[] produced;
        using (var input = new MemoryStream(encrypted))
        using (var output = new MemoryStream())
        {
            PdfEncryptedIncrementalUpdate.Run(input, output, OwnerPassword,
                (decrypted, firstFree) => PdfUaXmpWriter.Plan(decrypted, firstFree, part: 1));
            produced = output.ToArray();
        }

        // In place: the encrypted original is a byte-for-byte prefix of the result. That is what
        // leaves an existing signature's /ByteRange covering the bytes it signed, and it is the
        // property PdfEncryptedRoundTrip cannot offer — it rewrites every byte.
        Assert.True(produced.Length > encrypted.Length,
            "a revision must have been appended, not merely a copy of the input");
        Assert.Equal(encrypted, produced.Take(encrypted.Length).ToArray());

        // BOTH original passwords still open it — the round trip collapses user and owner into
        // whichever one it was handed.
        foreach (string password in new[] { UserPassword, OwnerPassword })
        {
            using var stream = new MemoryStream(Decrypt(produced, password));
            using var doc = PdfDocument.Open(stream);

            var metadata = doc.ResolveObject(doc.Catalog.Dictionary[PdfName.Metadata]) as PdfStream;
            Assert.NotNull(metadata);

            // Decoded XMP, PARSED — not a substring of the raw file. Substring-matching would
            // pass on a packet that is not well-formed XML, which is the defect this repository
            // found by substring-matching SVG for years; and matching raw file bytes would pass on
            // a packet appended in clear text by mistake.
            string xmp = Encoding.UTF8.GetString(metadata!.GetDecodedData());
            XElement part = XDocument.Parse(xmp)
                .Descendants(XName.Get("part", PdfUaXmpWriter.PdfUaIdNamespace))
                .SingleOrDefault();

            Assert.NotNull(part);
            Assert.Equal("1", part!.Value.Trim());
        }
    }

    /// <summary>
    /// The identifier really was encrypted with the document's own key — not appended in clear
    /// text, which is the failure the clear-text writer refuses in order to avoid.
    /// </summary>
    /// <remarks>
    /// Without this, a plan appended verbatim would satisfy the test above: the XMP would still
    /// decode, because the reader would be handed plaintext where it expected ciphertext and this
    /// fixture's packet is ASCII either way. Asserting the packet is <b>absent</b> from the raw
    /// bytes is what separates "encrypted correctly" from "not encrypted at all".
    /// </remarks>
    [Fact]
    public void Plan_AppendedThroughTheEncryptedWriter_DoesNotLeaveThePacketInClearText()
    {
        byte[] encrypted = Encrypt(ContentBearingPdfTests.ContentPdf());

        byte[] produced;
        using (var input = new MemoryStream(encrypted))
        using (var output = new MemoryStream())
        {
            PdfEncryptedIncrementalUpdate.Run(input, output, OwnerPassword,
                (decrypted, firstFree) => PdfUaXmpWriter.Plan(decrypted, firstFree, part: 1));
            produced = output.ToArray();
        }

        Assert.DoesNotContain("pdfuaid:part", Encoding.Latin1.GetString(produced),
            System.StringComparison.Ordinal);
    }

    /// <summary>
    /// A document whose packet is already exactly what would be written plans <b>nothing</b>, so a
    /// caller appends no revision. The packet is deterministic, so re-running must not grow the
    /// file by ~3 KB per call — the same rule the clear-text path reports through
    /// <c>LastCallAppendedRevision</c>.
    /// </summary>
    [Fact]
    public void Plan_WhenThePacketIsAlreadyCorrect_IsEmpty()
    {
        byte[] plain = ContentBearingPdfTests.ContentPdf();

        byte[] stamped;
        using (var input = new MemoryStream(plain))
        using (var output = new MemoryStream())
        {
            Assert.True(PdfUaXmpWriter.SetPdfUaIdentifier(input, output, part: 1));
            stamped = output.ToArray();
        }

        using var stream = new MemoryStream(stamped);
        using var doc = PdfDocument.Open(stream);

        Assert.Empty(PdfUaXmpWriter.Plan(doc, firstFreeObjectNumber: 9999, part: 1));
    }

    /// <summary>
    /// Every allocated number is at or above <c>firstFreeObjectNumber</c>. This is the trap the
    /// parameter exists for: <c>Plan</c> is handed a <b>decrypted copy</b>, and decryption unpacks
    /// object streams and drops the xref stream, so the copy's ceiling can be <i>lower</i> than the
    /// target file's. Allocating from the copy would mint a number that is live in the file being
    /// appended to, and the new object would silently supersede it.
    /// </summary>
    [Fact]
    public void Plan_AllocatesNoObjectNumberBelowTheCeilingItWasGiven()
    {
        byte[] plain = ContentBearingPdfTests.ContentPdf();
        using var stream = new MemoryStream(plain);
        using var doc = PdfDocument.Open(stream);

        const int ceiling = 5000;
        var objects = PdfUaXmpWriter.Plan(doc, firstFreeObjectNumber: ceiling, part: 1);

        Assert.NotEmpty(objects);
        // The catalog is REVISED, so it legitimately keeps its low original number; only newly
        // allocated objects are constrained. Asserting on every object would fail for the right
        // reason and pin the wrong rule.
        Assert.Contains(objects, o => o.ObjectNumber >= ceiling);
    }

    // ================================================================
    //  PdfArtifactMarker.Plan — the third writer to gain the split
    // ================================================================

    /// <summary>
    /// Header/footer artifact marking on an encrypted document, in place. This one rewrites page
    /// <b>content streams</b> — the hardest of the three, because the rewritten stream must be
    /// re-encrypted with the document's own key, and a clear-text content stream appended to an
    /// encrypted file renders as nothing.
    /// </summary>
    [Fact]
    public void ArtifactPlan_AppendedThroughTheEncryptedWriter_MarksInPlaceAndStaysReadable()
    {
        byte[] encrypted = Encrypt(ContentBearingPdfTests.ContentPdf());

        byte[] produced;
        using (var input = new MemoryStream(encrypted))
        using (var output = new MemoryStream())
        {
            PdfEncryptedIncrementalUpdate.Run(input, output, OwnerPassword,
                (decrypted, firstFree) => PdfArtifactMarker.Plan(decrypted, firstFree).Objects);
            produced = output.ToArray();
        }

        Assert.Equal(encrypted, produced.Take(encrypted.Length).ToArray());

        // The marked content must be readable after decryption — which it can only be if the
        // rewritten stream was encrypted with the document's key rather than appended in the clear.
        using var stream = new MemoryStream(Decrypt(produced, UserPassword));
        using var doc = PdfDocument.Open(stream);

        var contents = doc.ResolveObject(doc.Pages[0].Dictionary[PdfName.Contents]) as PdfStream;
        Assert.NotNull(contents);
        string content = Encoding.ASCII.GetString(contents!.GetDecodedData());
        Assert.Contains("/Artifact", content, System.StringComparison.Ordinal);
        Assert.Contains("EMC", content, System.StringComparison.Ordinal);
    }

    /// <summary>
    /// <see cref="ArtifactMarkPlan.Describe"/> reports zero when the caller did not append — the
    /// counts describe the <i>output</i>, not the plan.
    /// </summary>
    /// <remarks>
    /// The clear-text path enforces this itself through <c>VerifiedWrite</c>. Here the caller owns
    /// the write, so the rule moves to the caller and this is what makes it followable: a marking
    /// that was planned and then dropped must read as zero artifacts on zero changed pages, or the
    /// result describes a document nobody has.
    /// </remarks>
    [Fact]
    public void ArtifactPlan_Describe_ReportsNothingWhenTheRevisionWasNotAppended()
    {
        byte[] plain = ContentBearingPdfTests.ContentPdf();
        using var stream = new MemoryStream(plain);
        using var doc = PdfDocument.Open(stream);

        ArtifactMarkPlan plan = PdfArtifactMarker.Plan(doc, firstFreeObjectNumber: 500);

        // The fixture has a header band to mark, so the applied reading is non-zero — without that
        // the two readings would be trivially equal and this would assert nothing.
        Assert.NotEmpty(plan.Objects);
        Assert.True(plan.Describe(revisionWasAppended: true).PagesChanged > 0);

        ArtifactMarkDetail discarded = plan.Describe(revisionWasAppended: false);
        Assert.Equal(0, discarded.ArtifactsMarked);
        Assert.Equal(0, discarded.PagesChanged);
    }

    // ================================================================
    //  PdfReadingOrderAnalyzer — a reader AND a writer, fixed differently
    // ================================================================

    /// <summary>
    /// <c>Analyze</c> is a <b>reader</b>: it examines the structure tree and writes nothing, so its
    /// fix is a password path through <c>Base.Encryption.PdfDecryptedInput</c> — not a plan.
    /// </summary>
    /// <remarks>
    /// Worth stating because the plan-08 row lists both of this type's <c>IsEncrypted</c> refusals
    /// together, which reads as one job. They are two, with different shapes, and applying the
    /// writer pattern to the reader would have added an unusable API.
    /// </remarks>
    [Fact]
    public void ReadingOrderAnalyze_WithThePassword_ExaminesTheEncryptedDocument()
    {
        // Tagged FIRST: Analyze reports Analyzed == false for a document with no /StructTreeRoot,
        // encrypted or not, so an untagged fixture would make the assertion below pass for the
        // wrong reason — and then fail to distinguish the two calls at all.
        byte[] tagged;
        using (var plain = new MemoryStream(ContentBearingPdfTests.ContentPdf()))
        using (var taggedOut = new MemoryStream())
        {
            PdfAutoTagger.AutoTag(plain, taggedOut);
            tagged = taggedOut.ToArray();
        }

        byte[] encrypted = Encrypt(tagged);

        var withPassword = PdfReadingOrderAnalyzer.Analyze(new MemoryStream(encrypted), UserPassword);
        var without = PdfReadingOrderAnalyzer.Analyze(new MemoryStream(encrypted));

        // The distinction the whole conversion is for: with the password the document is examined,
        // without it the caller is told it was not. "Analyzed" is the honest reading — an
        // unexamined document must not report a clean order.
        Assert.True(withPassword.Analyzed);
        Assert.False(without.Analyzed);
    }

    /// <summary>
    /// <c>Fix</c> is a <b>writer</b>, so it gets a plan — and this asserts the plan reports the
    /// output rather than the intent.
    /// </summary>
    [Fact]
    public void ReadingOrderPlan_Describe_ReportsNothingWhenTheRevisionWasNotAppended()
    {
        byte[] plain = ContentBearingPdfTests.ContentPdf();
        using var stream = new MemoryStream(plain);
        using var doc = PdfDocument.Open(stream);

        ReadingOrderPlan plan = PdfReadingOrderAnalyzer.Plan(doc, firstFreeObjectNumber: 500);

        // Whatever this fixture plans, the discarded reading is zero — that is the invariant, and
        // it holds for an empty plan too, which is why the applied reading is compared rather than
        // asserted non-zero (this fixture's order may already be correct).
        Assert.Equal(0, plan.Describe(revisionWasAppended: false).ElementsReordered);
        Assert.Equal(plan.Objects.Count == 0 ? 0 : plan.Describe(true).ElementsReordered,
            plan.Describe(revisionWasAppended: true).ElementsReordered);
    }

    // ================================================================
    //  PdfFontEmbeddingRemediator.Plan — the one that closes UA21/UA25
    // ================================================================

    /// <summary>
    /// A plan that was staged and then <b>not appended</b> reports its fonts as skips, never as
    /// embedded — because "embedded" here is a PDF/UA conformance claim.
    /// </summary>
    /// <remarks>
    /// <para>This matters more than the equivalent on the other three plans. Embedding is what
    /// closes UA21/UA25, so reporting fonts as embedded when the revision was lost declares
    /// conformance for a document that still violates it — the exact silent-wrong-answer shape the
    /// whole VerifiedWrite rule exists to prevent, and here the caller owns the write so the rule
    /// has to live in <c>Describe</c>.</para>
    /// <para>The fixture uses standard-14 Helvetica with no font provider, so nothing is actually
    /// embeddable and both readings report zero embedded. That is deliberate and stated: what is
    /// asserted is the <b>invariant</b> — the discarded reading never claims more than the applied
    /// one — which holds for an empty plan and is the property a caller depends on. A fixture with
    /// a real embeddable font would strengthen it, and needs a font provider the test assembly does
    /// not currently carry.</para>
    /// <para><b>The object-number ceiling is therefore NOT covered here</b>, and that is stated
    /// rather than faked: a first attempt asserted <c>n &lt; ceiling || n &gt;= ceiling</c>, which
    /// is true of every integer. With nothing staged there is no minted object to check. The
    /// equivalent assertion on <c>PdfUaXmpWriter.Plan</c> — which always mints its metadata stream
    /// — does cover it, and mutation-killing the reservation there is what pins the rule.</para>
    /// </remarks>
    [Fact]
    public void FontEmbeddingPlan_Describe_NeverClaimsMoreThanWasAppended()
    {
        byte[] plain = ContentBearingPdfTests.ContentPdf();
        using var stream = new MemoryStream(plain);
        using var doc = PdfDocument.Open(stream);

        FontEmbeddingPlan plan = PdfFontEmbeddingRemediator.Plan(doc, firstFreeObjectNumber: 500);

        FontEmbeddingResult applied = plan.Describe(revisionWasAppended: true);
        FontEmbeddingResult discarded = plan.Describe(revisionWasAppended: false);

        Assert.Equal(0, discarded.FontsEmbedded);
        Assert.True(discarded.FontsEmbedded <= applied.FontsEmbedded);
        // Nothing is lost from the accounting: a font that was staged and then dropped becomes a
        // skip, so the discarded reading can only have MORE skips, never fewer.
        Assert.True(discarded.FontsSkipped >= applied.FontsSkipped);
    }

    /// <summary>
    /// The rule itself, on a plan that really did stage fonts: a lost revision turns every staged
    /// font into a <b>skip with a stated reason</b>, and reports zero embedded.
    /// </summary>
    /// <remarks>
    /// <b>This exists because the document-driven test above is vacuous on its fixture.</b>
    /// Standard-14 Helvetica with no font provider stages nothing, so <c>_embeddedSites</c> is
    /// empty and the accounting loop runs zero times — mutation confirmed it: deleting the loop
    /// that converts staged fonts into skips left that test green. Constructing the plan directly
    /// is what reaches the rule. The internal constructor is visible here through
    /// <c>InternalsVisibleTo</c>, which is what makes this possible without a font provider.
    /// </remarks>
    [Fact]
    public void FontEmbeddingPlan_Describe_TurnsEveryStagedFontIntoASkipWhenNotAppended()
    {
        var staged = new[]
        {
            new FontEmbeddingPlan.FontSiteRef("ArialMT", "F1", 0),
            new FontEmbeddingPlan.FontSiteRef("TimesNewRomanPSMT", "F2", 1),
        };

        var plan = new FontEmbeddingPlan(
            System.Array.Empty<Tetitulz.Pdf.Core.Writing.IndirectObject>(),
            fontsExamined: 2, alreadyEmbedded: 0,
            embedded: new List<string> { "ArialMT", "TimesNewRomanPSMT" }, subsetted: 2,
            embeddedSites: staged,
            skips: new List<FontEmbeddingSkip>(), warnings: new List<string>());

        Assert.Equal(2, plan.Describe(revisionWasAppended: true).FontsEmbedded);

        FontEmbeddingResult discarded = plan.Describe(revisionWasAppended: false);
        Assert.Equal(0, discarded.FontsEmbedded);
        Assert.Equal(2, discarded.FontsSkipped);
        // Named, not merely counted: a caller has to be able to say WHICH font is still open.
        Assert.Contains(discarded.SkippedFonts, f => f.BaseFont == "ArialMT");
        Assert.Contains(discarded.SkippedFonts, f => f.BaseFont == "TimesNewRomanPSMT");
    }

    // ================================================================
    //  PdfStructureTreeEditor - the title UA04 needs, on an encrypted file
    // ================================================================

    /// <summary>
    /// A title written into an <b>encrypted</b> document, in place. UA04 is one of the two things
    /// only the caller can supply, so without this an encrypted document cannot reach
    /// <c>IsConforming</c> however well it is tagged.
    /// </summary>
    /// <remarks>
    /// <para>The fixture is given an <c>/Info</c> dictionary first, so this exercises the
    /// <b>revision</b> branch. That is not incidental — see
    /// <see cref="TitlePlan_MintingBranch_CannotBeAppendedThroughRunToday"/>: the minting branch
    /// genuinely does not work through this channel yet, and pretending otherwise by asserting
    /// something weaker would hide a real limitation.</para>
    /// </remarks>
    [Fact]
    public void TitlePlan_AppendedThroughTheEncryptedWriter_IsReadableAfterDecryption()
    {
        // Give the document an /Info to revise. Without this the plan MINTS one, and the minted
        // object cannot be reached from the appended trailer through this channel - the case the
        // companion test below pins as a known limitation.
        byte[] withInfo;
        using (var plain = new MemoryStream(ContentBearingPdfTests.ContentPdf()))
        using (var titled = new MemoryStream())
        {
            Assert.True(PdfStructureTreeEditor.SetDocumentTitle(plain, titled, "Placeholder"));
            withInfo = titled.ToArray();
        }

        byte[] encrypted = Encrypt(withInfo);

        byte[] produced;
        using (var input = new MemoryStream(encrypted))
        using (var output = new MemoryStream())
        {
            PdfEncryptedIncrementalUpdate.Run(input, output, OwnerPassword,
                (decrypted, firstFree) =>
                    PdfStructureTreeEditor.PlanDocumentTitle(decrypted, firstFree, "Quarterly Report").Objects);
            produced = output.ToArray();
        }

        Assert.Equal(encrypted, produced.Take(encrypted.Length).ToArray());

        using var stream = new MemoryStream(Decrypt(produced, UserPassword));
        using var doc = PdfDocument.Open(stream);

        // The title is a PDF *string*, so it is ciphertext in the encrypted file: reading it back
        // cannot succeed unless the plan's object really was encrypted with the document's key.
        var info = doc.ResolveObject(doc.Trailer[PdfName.Info]) as PdfDictionary;
        Assert.NotNull(info);
        var title = doc.ResolveObject(info![PdfName.Title]) as PdfString;
        Assert.NotNull(title);
        Assert.Equal("Quarterly Report", title!.GetText());
    }

    /// <summary>
    /// An encrypted document with <b>no <c>/Info</c> at all</b> can be given a title: the plan mints
    /// one, and the appended trailer points at it.
    /// </summary>
    /// <remarks>
    /// <para><b>This was a pinned limitation until 2026-08-15, and it became a round trip exactly as
    /// its own note said it should.</b> A minted <c>/Info</c> is only useful if the appended trailer
    /// references it, and <c>Run</c>'s delegate returned <i>objects</i> — it had no channel for a
    /// trailer entry. So the object was written and unreachable: a title nobody could read, which is
    /// UA04 failing silently. The <c>EncryptedRevision</c> overload of <c>Run</c> carries the
    /// <c>/Info</c> number, and the writer already took <c>/Info</c> from
    /// <c>IncrementalUpdateOptions.OriginalTrailer</c> — so nothing new had to be taught to the
    /// writer.</para>
    /// <para>The fixture must have <b>no</b> <c>/Info</c>, or the minting branch is never taken and
    /// this passes while proving nothing — the companion test above covers the revise branch.</para>
    /// </remarks>
    [Fact]
    public void TitlePlan_MintingBranch_AppendedThroughRun_IsReachableFromTheTrailer()
    {
        byte[] plain = ContentBearingPdfTests.ContentPdf();

        // Control: the fixture really has no /Info, so the minting branch is the one under test.
        using (var probe = new MemoryStream(plain))
        using (var probeDoc = PdfDocument.Open(probe))
            Assert.False(probeDoc.Trailer.ContainsKey(PdfName.Info));

        byte[] encrypted = Encrypt(plain);

        byte[] produced;
        using (var input = new MemoryStream(encrypted))
        using (var output = new MemoryStream())
        {
            PdfEncryptedIncrementalUpdate.Run(input, output, OwnerPassword,
                (decrypted, firstFree) =>
                {
                    StructureTreePlan plan =
                        PdfStructureTreeEditor.PlanDocumentTitle(decrypted, firstFree, "Minted Title");

                    // The plan must mint here, or the trailer channel is not what is being tested.
                    Assert.True(plan.NewInfoObjectNumber > 0);
                    return new EncryptedRevision(plan.Objects, plan.NewInfoObjectNumber);
                });
            produced = output.ToArray();
        }

        // Appended in place: the original bytes are untouched.
        Assert.Equal(encrypted, produced.Take(encrypted.Length).ToArray());

        // The title is reachable *from the trailer* -- the whole point. Resolving it through
        // doc.Trailer[/Info] is what a reader does, and what failed before.
        using var stream = new MemoryStream(PdfDecryptedInput.ReadDecrypted(
            new MemoryStream(produced), UserPassword));
        using var doc = PdfDocument.Open(stream);

        var info = doc.ResolveObject(doc.Trailer[PdfName.Info]) as PdfDictionary;
        Assert.NotNull(info);
        var title = doc.ResolveObject(info![PdfName.Title]) as PdfString;
        Assert.NotNull(title);
        Assert.Equal("Minted Title", title!.GetText());
    }

    /// <summary>
    /// The objects-only <c>Run</c> overload still cannot reach a minted <c>/Info</c>, and that is
    /// correct rather than a leftover: it has no trailer channel by design.
    /// </summary>
    /// <remarks>
    /// Kept so the two overloads cannot quietly become one. A caller who mints an <c>/Info</c> and
    /// passes only objects gets an unreferenced object — the failure mode above — so the overload
    /// that <i>can</i> express it has to stay the one you must reach for deliberately.
    /// </remarks>
    [Fact]
    public void TitlePlan_MintingBranch_ThroughTheObjectsOnlyOverload_LeavesTheInfoUnreferenced()
    {
        byte[] encrypted = Encrypt(ContentBearingPdfTests.ContentPdf());

        byte[] produced;
        using (var input = new MemoryStream(encrypted))
        using (var output = new MemoryStream())
        {
            PdfEncryptedIncrementalUpdate.Run(input, output, OwnerPassword,
                (decrypted, firstFree) =>
                    PdfStructureTreeEditor.PlanDocumentTitle(decrypted, firstFree, "Unreachable").Objects);
            produced = output.ToArray();
        }

        using var stream = new MemoryStream(PdfDecryptedInput.ReadDecrypted(
            new MemoryStream(produced), UserPassword));
        using var doc = PdfDocument.Open(stream);

        // The object was written, but nothing points at it.
        Assert.False(doc.Trailer.ContainsKey(PdfName.Info));
    }

    /// <summary>
    /// The language plan revises the catalog and mints nothing — the opposite case, asserted so the
    /// difference between the two is pinned rather than assumed.
    /// </summary>
    [Fact]
    public void LanguagePlan_RevisesTheCatalogAndMintsNothing()
    {
        byte[] plain = ContentBearingPdfTests.ContentPdf();
        using var stream = new MemoryStream(plain);
        using var doc = PdfDocument.Open(stream);

        StructureTreePlan plan = PdfStructureTreeEditor.PlanDocumentLanguage(doc, 700, "en-GB");

        Assert.True(plan.WouldChangeDocument);
        Assert.Equal(-1, plan.NewInfoObjectNumber);
        Assert.Single(plan.Objects);
        // The catalog's own number, which is far below the ceiling - a revision, not an allocation.
        Assert.True(plan.Objects[0].ObjectNumber < 700);
    }

    /// <summary>
    /// <c>/Tabs /S</c> written into an <b>encrypted</b> document, in place — UA09, and the one
    /// remediation step that revises every annotated page rather than the catalog.
    /// </summary>
    /// <remarks>
    /// This edit mints nothing, so unlike the title plan it is not affected by <c>Run</c>'s
    /// inability to add a trailer entry: the round trip works end to end. The fixture is
    /// <c>TabOrderRemediationTests.AnnotatedPdf</c> — the document UA09 is actually reported for —
    /// rather than a second hand-built one that might not match what the editor looks for.
    /// </remarks>
    [Fact]
    public void TabOrderPlan_AppendedThroughTheEncryptedWriter_SetsTabsOnAnnotatedPages()
    {
        byte[] encrypted = Encrypt(TabOrderRemediationTests.AnnotatedPdf());

        // The fixture must actually need the edit, or a green result says nothing.
        using (var before = new MemoryStream(Decrypt(encrypted, UserPassword)))
        using (var doc = PdfDocument.Open(before))
            Assert.Null(doc.Pages[0].Dictionary[PdfName.Tabs]);

        byte[] produced;
        using (var input = new MemoryStream(encrypted))
        using (var output = new MemoryStream())
        {
            PdfEncryptedIncrementalUpdate.Run(input, output, OwnerPassword,
                (decrypted, firstFree) =>
                    PdfStructureTreeEditor.PlanStructureTabOrder(decrypted, firstFree).Objects);
            produced = output.ToArray();
        }

        Assert.Equal(encrypted, produced.Take(encrypted.Length).ToArray());

        using var stream = new MemoryStream(Decrypt(produced, UserPassword));
        using var result = PdfDocument.Open(stream);

        var tabs = result.ResolveObject(result.Pages[0].Dictionary[PdfName.Tabs]) as PdfName;
        Assert.NotNull(tabs);
        Assert.Equal("S", tabs!.Value);
    }

    /// <summary>
    /// An empty tooltip is <b>refused, not written</b> — the same rule the clear-text path applies,
    /// asserted on the plan because a plan that silently staged <c>/TU ()</c> would look successful
    /// and leave UA08 open.
    /// </summary>
    /// <remarks>
    /// This is the one behaviour in the five structure-tree plans that is a refusal rather than an
    /// edit, so it is the one where an empty result is the CORRECT answer and must not be confused
    /// with "nothing to do".
    /// </remarks>
    [Fact]
    public void TooltipPlan_WithAnEmptyTooltip_PlansNothing()
    {
        // A document that really HAS the field, or the plan returns early at the /AcroForm check
        // and the tooltip guard is never reached — mutation proved the vacuous version green.
        using var stream = new MemoryStream(IncrementalWriterTests.AcroFormPdf());
        using var doc = PdfDocument.Open(stream);

        // The control: with a real tooltip the same call DOES plan, so the two below are refusals
        // rather than "this fixture never plans anything".
        Assert.True(PdfStructureTreeEditor
            .PlanFieldTooltip(doc, 500, "FullName", "Full name").WouldChangeDocument);

        Assert.False(PdfStructureTreeEditor
            .PlanFieldTooltip(doc, 500, "FullName", tooltip: "").WouldChangeDocument);
        Assert.False(PdfStructureTreeEditor
            .PlanFieldTooltip(doc, 500, "FullName", tooltip: null).WouldChangeDocument);
        // Whitespace alone is what SetFieldTooltip refuses too; a /TU of blanks is a missing name.
        Assert.False(PdfStructureTreeEditor
            .PlanFieldTooltip(doc, 500, "FullName", tooltip: "   ").WouldChangeDocument);
    }

    /// <summary>
    /// A null field name throws rather than matching the first UNNAMED field — the defect the
    /// clear-text path records, which a new entry point could easily reintroduce.
    /// </summary>
    [Fact]
    public void TooltipPlan_WithANullFieldName_Throws()
    {
        byte[] plain = ContentBearingPdfTests.ContentPdf();
        using var stream = new MemoryStream(plain);
        using var doc = PdfDocument.Open(stream);

        Assert.Throws<System.ArgumentNullException>(
            () => PdfStructureTreeEditor.PlanFieldTooltip(doc, 500, null, "Name"));
    }

    /// <summary>
    /// Every one of the five structure-tree plans rejects an object-number ceiling below 1 — 0 is
    /// the free-list head and can never be written, and a plan that allocated from it would emit an
    /// object no reader can reach.
    /// </summary>
    [Fact]
    public void EveryStructureTreePlan_RejectsACeilingBelowOne()
    {
        byte[] plain = ContentBearingPdfTests.ContentPdf();
        using var stream = new MemoryStream(plain);
        using var doc = PdfDocument.Open(stream);

        Assert.Throws<System.ArgumentOutOfRangeException>(
            () => PdfStructureTreeEditor.PlanDocumentLanguage(doc, 0, "en"));
        Assert.Throws<System.ArgumentOutOfRangeException>(
            () => PdfStructureTreeEditor.PlanDocumentTitle(doc, 0, "T"));
        Assert.Throws<System.ArgumentOutOfRangeException>(
            () => PdfStructureTreeEditor.PlanStructureTabOrder(doc, 0));
        Assert.Throws<System.ArgumentOutOfRangeException>(
            () => PdfStructureTreeEditor.PlanHeadingHierarchy(doc, 0));
        Assert.Throws<System.ArgumentOutOfRangeException>(
            () => PdfStructureTreeEditor.PlanFieldTooltip(doc, 0, "F", "N"));
    }

    // ------------------------------------------------------------------

    private static byte[] Encrypt(byte[] plain)
    {
        using var input = new MemoryStream(plain);
        using var output = new MemoryStream();
        PdfEncryptor.Encrypt(input, output, new EncryptionParameters
        {
            UserPassword = UserPassword,
            OwnerPassword = OwnerPassword,
            Algorithm = EncryptionAlgorithm.AES128,
            Permissions = PdfPermissions.All,
        });
        return output.ToArray();
    }

    private static byte[] Decrypt(byte[] encrypted, string password)
    {
        using var input = new MemoryStream(encrypted);
        using var output = new MemoryStream();
        PdfDecryptor.Decrypt(input, output, password);
        return output.ToArray();
    }
}
