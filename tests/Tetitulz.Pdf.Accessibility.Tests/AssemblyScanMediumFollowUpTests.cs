// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Accessibility.Checking;
using Tetitulz.Pdf.Accessibility.Fonts;
using Tetitulz.Pdf.Accessibility.Models;
using Tetitulz.Pdf.Accessibility.Tagging;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Fonts;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Accessibility.Tests;

/// <summary>
/// The five MEDIUM accessibility findings of plan 10 that the first remediation round left open,
/// plus the reverted <c>/Widths</c> one.
///
/// <para>
/// Each of the six shares a shape: the code did something defensible and then <em>said</em>
/// something that was not true of the document in front of it — a link description judged from
/// ciphertext, an ISO 14289-1 §7.21.4.1 exemption granted on the evidence of one page, a ceiling that
/// abandoned two checks without the report mentioning it, a budget that bounded the small half of
/// what it was about to write, a superseded <c>/StructParents</c> left on the one object class the
/// tagger itself writes that key onto, and a synthesised <c>/Widths</c> array that did not cover the
/// codes the font's own <c>/Differences</c> defines. So every test below asserts on what a caller
/// can actually read back — a reported code and its severity, the bytes of the output — never on
/// the call having returned.
/// </para>
/// </summary>
public class AssemblyScanMediumFollowUpTests
{
    // =====================================================================
    //  1. UA22 must not judge an encrypted /Contents for emptiness
    // =====================================================================

    /// <summary>
    /// An encrypted document whose Link annotation carries an AES-shaped <c>/Contents</c> — 16
    /// bytes of IV and ciphertext, which is what an <em>empty</em> description looks like on the
    /// wire — is reported as having had UA22's emptiness rule weakened, rather than passing in
    /// silence.
    /// </summary>
    /// <remarks>
    /// <para><b>The defect.</b> <c>CheckAnnotations</c> runs through <c>RunCheck</c>, not
    /// <c>RunContentCheck</c>, so <c>HasLinkDescription</c> tested
    /// <c>!string.IsNullOrWhiteSpace(s.GetText())</c> against ciphertext. Four lines of
    /// orchestration away the class applies the opposite rule to the identical situation: UA15 is
    /// routed through <c>RunContentCheck</c> because "an encrypted string cannot be judged empty,
    /// generic or meaningful". The verdict therefore depended on the cipher — under AES an empty
    /// <c>/Contents</c> decodes to non-whitespace bytes and UA22 never fired, under RC4 the same
    /// file reported it — and <c>ReportEncryptionCoverage</c> positively asserted to the caller that
    /// "annotation descriptions and reachability ran in full and their findings are real".</para>
    /// </remarks>
    [Fact]
    public void EncryptedLinkWithCiphertextContents_IsReportedAsAWeakenedCheck()
    {
        IReadOnlyList<AccessibilityIssue> issues = DeepCheck(
            EncryptedLinkPdf("/Contents <000102030405060708090A0B0C0D0E0F>"));

        AccessibilityIssue coverage = Assert.Single(issues.Where(i =>
            i.Code == "UA99" && i.Message.IndexOf("UA22", StringComparison.Ordinal) >= 0));

        // Info, never Error: "this was not judged" is not a finding against the document.
        Assert.Equal(IssueSeverity.Info, coverage.Severity);
        Assert.Contains("weakened", coverage.Message, StringComparison.Ordinal);
        Assert.Contains("cipher", coverage.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The other half of the same issue: the coverage notice must stop claiming that annotation
    /// <em>descriptions</em> ran in full. Reachability (UA23) really does, and still says so.
    /// </summary>
    [Fact]
    public void EncryptedCoverageNotice_NoLongerClaimsAnnotationDescriptionsRanInFull()
    {
        IReadOnlyList<AccessibilityIssue> issues = DeepCheck(
            EncryptedLinkPdf("/Contents <000102030405060708090A0B0C0D0E0F>"));

        AccessibilityIssue notice = Assert.Single(issues.Where(i =>
            i.Code == "UA99" && i.Message.IndexOf("ran in full", StringComparison.Ordinal) >= 0));

        Assert.DoesNotContain("annotation descriptions", notice.Message, StringComparison.Ordinal);
        Assert.Contains("annotation reachability", notice.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// An <em>empty</em> <c>/Contents</c> on an encrypted document is not reported as UA22. Under
    /// RC4 an empty string stays empty on the wire, so before the fix this same file produced the
    /// finding that its AES-encrypted twin did not — the verdict was about the cipher rather than
    /// about the document.
    /// </summary>
    [Fact]
    public void EncryptedLinkWithAnEmptyContents_IsNotReportedAsUa22()
    {
        IReadOnlyList<AccessibilityIssue> issues = DeepCheck(EncryptedLinkPdf("/Contents ()"));

        Assert.DoesNotContain(issues, i => i.Code == "UA22");
    }

    /// <summary>
    /// With no encryption the emptiness rule is unchanged: an empty <c>/Contents</c> is still a
    /// UA22 Error. Without this the fix above would read as "UA22 was switched off".
    /// </summary>
    [Fact]
    public void UnencryptedLinkWithAnEmptyContents_IsStillReportedAsUa22()
    {
        IReadOnlyList<AccessibilityIssue> issues = DeepCheck(LinkPdf("/Contents ()", encrypted: false));

        AccessibilityIssue ua22 = Assert.Single(issues.Where(i => i.Code == "UA22"));
        Assert.Equal(IssueSeverity.Error, ua22.Severity);
    }

    /// <summary>
    /// Whether the key is <em>there</em> is a dictionary fact, and encryption does not touch it. A
    /// link with no <c>/Contents</c> at all is still UA22 on an encrypted document — the fix must
    /// not turn the whole check off.
    /// </summary>
    /// <summary>
    /// The deep checker skips a hidden annotation, and reads <c>/F</c> as a flag word to decide.
    /// Through <c>IntValue</c> both fixtures saturated to every named flag, so an undescribed link a
    /// reader shows was skipped as hidden. The control — <c>/F 2</c>, really Hidden — is still skipped.
    /// </summary>
    [Theory]
    [InlineData("/F 6.5", true)]
    [InlineData("/F 2147483652", true)]
    [InlineData("/F 2", false)]
    public void ALinkWhoseFlagsOnlyLookedHidden_IsReportedAsUa22(string flags, bool reported)
    {
        IReadOnlyList<AccessibilityIssue> issues = DeepCheck(LinkPdf(flags, encrypted: false));

        Assert.Equal(reported, issues.Any(i => i.Code == "UA22"));
    }

    [Fact]
    public void EncryptedLinkWithNoContentsKeyAtAll_IsStillReportedAsUa22()
    {
        IReadOnlyList<AccessibilityIssue> issues = DeepCheck(EncryptedLinkPdf(string.Empty));

        AccessibilityIssue ua22 = Assert.Single(issues.Where(i => i.Code == "UA22"));
        Assert.Equal(IssueSeverity.Error, ua22.Severity);
    }

    // =====================================================================
    //  2. UA21's invisible-text exemption is a document-wide question
    // =====================================================================

    /// <summary>
    /// A non-embedded font drawn invisibly on page 1 and <b>visibly</b> on page 2 is reported as
    /// not embedded.
    /// </summary>
    /// <remarks>
    /// <para><b>The defect.</b> <c>CollectFonts</c> registers each distinct font dictionary once,
    /// against the <em>first</em> page that reaches it, and <c>IsInvisibleOnly</c> then skipped
    /// every page but that one — so ISO 14289-1 §7.21.4.1's exemption for a font used only in an
    /// invisible rendering mode was granted on the evidence of a single page. This fixture is the
    /// shape that produces: page 1 is an OCR text layer (<c>3 Tr</c>), page 2 is real text, and both
    /// draw the same font object. The checker missed the violation while
    /// <c>PdfFontEmbeddingRemediator</c> — the fixer for this very rule — would still have embedded
    /// the font, which is the checker/fixer drift the file's own comments say must be avoided.</para>
    /// </remarks>
    [Fact]
    public void Ua21_FontDrawnInvisiblyOnPageOneAndVisiblyOnPageTwo_IsReportedAsNotEmbedded()
    {
        IReadOnlyList<AccessibilityIssue> issues = DeepCheck(TwoPageFontUsePdf("3 Tr", "0 Tr"));

        AccessibilityIssue ua21 = Assert.Single(issues.Where(i => i.Code == "UA21"));
        Assert.Equal(IssueSeverity.Error, ua21.Severity);
        Assert.Contains("Helvetica", ua21.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The exemption still works: the same font drawn invisibly on <em>both</em> pages is an OCR
    /// layer and is not reported. Without this the test above would pass with the exemption deleted
    /// outright.
    /// </summary>
    [Fact]
    public void Ua21_FontDrawnInvisiblyOnEveryPage_IsStillExempt()
    {
        IReadOnlyList<AccessibilityIssue> issues = DeepCheck(TwoPageFontUsePdf("3 Tr", "3 Tr"));

        Assert.DoesNotContain(issues, i => i.Code == "UA21");
    }

    /// <summary>
    /// And the exemption is still about the font <em>object</em>, not about a resource name: two
    /// pages whose <c>/F1</c> entries are two different font dictionaries are judged separately.
    /// The invisible one is exempt; the visible one is reported.
    /// </summary>
    [Fact]
    public void Ua21_TwoDistinctFontsUnderTheSameResourceName_AreJudgedIndependently()
    {
        IReadOnlyList<AccessibilityIssue> issues = DeepCheck(TwoDistinctFontsSameNamePdf());

        AccessibilityIssue ua21 = Assert.Single(issues.Where(i => i.Code == "UA21"));

        // Object 5 is the invisible OCR font (exempt); object 9 is the visibly painted one.
        Assert.Equal(9, ua21.ObjectNumber);
    }

    // =====================================================================
    //  3. A structure-walk ceiling must reach the report, not just the log
    // =====================================================================

    /// <summary>
    /// A structure tree whose elements all share one enormous <c>/K</c> array exhausts the
    /// edge ceiling, and the <b>report</b> says which checks consequently did not run.
    /// </summary>
    /// <remarks>
    /// <para><b>The defect.</b> Each of the three ceiling bail-outs raised an
    /// <c>AccessibilityDiagnostics</c> event and returned, adding nothing to <c>ctx.Issues</c>. A
    /// diagnostics event is not part of <c>AccessibilityAuditReport</c>, so the result was
    /// byte-identical to one in which UA30 and UA14's mapping ran and found nothing — and
    /// <c>IsConforming</c> could come back true for a document that was never examined for the
    /// hollow-tagging defect this feature exists to detect. That is the exact condition
    /// <c>ReportContentCoverage</c> and <c>ReportEncryptionCoverage</c> were written to
    /// eliminate.</para>
    /// <para><b>Why the fixture is shaped this way.</b> <c>/K</c> may be an indirect reference and
    /// number-tree-style sharing is legal, so <i>n</i> elements naming one <i>m</i>-entry array is
    /// O(n·m) slots from an O(n+m)-byte file — the same quadratic the ceiling exists to stop. The
    /// array is sized so both the walker and the checker's own budget are past their limit by a
    /// wide margin, rather than by an off-by-one that a future change to the accounting would
    /// silently erase.</para>
    /// </remarks>
    [Fact]
    public void StructureTreeSharingOneEnormousKidsArray_ReportsWhichChecksDidNotRun()
    {
        IReadOnlyList<AccessibilityIssue> issues = DeepCheck(StructureEdgeBomb());

        AccessibilityIssue hollow = Assert.Single(issues.Where(i =>
            i.Code == "UA99" &&
            i.Message.IndexOf("which elements reach content", StringComparison.Ordinal) >= 0));

        Assert.Equal(IssueSeverity.Info, hollow.Severity);
        Assert.Contains("2000000-edge", hollow.Message, StringComparison.Ordinal);
        Assert.Contains("UA30", hollow.Message, StringComparison.Ordinal);
        Assert.Contains("not examined", hollow.Message, StringComparison.Ordinal);

        // The MCID-claim collection spends its own budget over the same arrays, and UA14's mapping
        // is a different set of findings from UA30's — so it gets its own notice.
        Assert.Contains(issues, i =>
            i.Code == "UA99" &&
            i.Severity == IssueSeverity.Info &&
            i.Message.IndexOf("marked-content mapping", StringComparison.Ordinal) >= 0);

        // The walker itself truncated, so the element list every other element-level check read is
        // partial too. That is a third, distinct thing the report has to say.
        Assert.Contains(issues, i =>
            i.Code == "UA99" &&
            i.Message.IndexOf("collected only in part", StringComparison.Ordinal) >= 0);
    }

    /// <summary>
    /// An ordinary tagged document reports no such thing. A ceiling notice that fired on real
    /// files would be as useless as no notice at all — the channel callers learn to ignore.
    /// </summary>
    [Fact]
    public void WellFormedStructureTree_IsNotReportedAsPartiallyWalked()
    {
        IReadOnlyList<AccessibilityIssue> issues = DeepCheck(WellFormedTaggedPdf());

        Assert.DoesNotContain(issues, i =>
            i.Message.IndexOf("The audit stopped at", StringComparison.Ordinal) >= 0);
    }

    // =====================================================================
    //  4. The per-page /Resources copy is charged to the duplication budget
    // =====================================================================

    /// <summary>
    /// A tiny Form XObject shared by many pages whose <c>/Resources</c> are inherited and enormous
    /// is <b>refused</b>, because the inline copy of those resources is what the output would
    /// actually be made of.
    /// </summary>
    /// <remarks>
    /// <para><b>The defect.</b> <c>TryRepointXObjectNames</c> shallow-copies the page's whole
    /// effective <c>/Resources</c> — and its <c>/XObject</c> sub-dictionary — as a direct dictionary
    /// onto each repointed page, for reasons it documents at length. When resources are inherited
    /// from a <c>/Pages</c> node, that document-wide dictionary is materialised once per page. But
    /// <c>MaxDuplicatedFormBytes</c> charged only <c>form.Content.Length × (copies − 1)</c>, so this
    /// second and far larger multiplier was entirely unbudgeted: the fixture below charged about a
    /// kilobyte and wrote megabytes, and the same file with 600 pages and a 10 MB resource
    /// dictionary charges 59 900 bytes and writes roughly 6 GB.</para>
    /// <para>The assertions are on the produced bytes, not on the plan: a budget that reports a
    /// refusal and writes the copies anyway would satisfy the first two and fail the third.</para>
    /// </remarks>
    [Fact]
    public void SharedFormUnderAnOversizedInheritedResources_IsRefusedAndSaysWhy()
    {
        byte[] input = SharedFormWithInheritedResources(xobjectEntries: 20_000, pages: 28);
        (AutoTagResult result, byte[] tagged) = AutoTag(input);

        Assert.Equal(0, result.SharedFormsDuplicated);
        Assert.Contains(result.Warnings, w =>
            w.IndexOf("8388608-byte document budget", StringComparison.Ordinal) >= 0);

        // The point of the budget is the bytes. Twenty-seven inline copies of a 20 001-entry
        // dictionary is several megabytes on top of a ~300 KB input.
        Assert.True(tagged.Length < input.Length * 2,
            "the refused duplication still wrote " +
            tagged.Length.ToString(CultureInfo.InvariantCulture) + " bytes from a " +
            input.Length.ToString(CultureInfo.InvariantCulture) + "-byte input");
    }

    /// <summary>
    /// The same document with an ordinary resource dictionary is still duplicated. The charge must
    /// bound a hostile shape without switching shared-form tagging off for real files.
    /// </summary>
    [Fact]
    public void SharedFormUnderOrdinaryInheritedResources_IsStillDuplicated()
    {
        (AutoTagResult result, _) = AutoTag(SharedFormWithInheritedResources(xobjectEntries: 10, pages: 28));

        Assert.Equal(1, result.SharedFormsDuplicated);
    }

    // =====================================================================
    //  5. A declined Form XObject must not keep a superseded /StructParents
    // =====================================================================

    /// <summary>
    /// Re-tagging a document whose Form XObject carries a <c>/StructParents</c> from the superseded
    /// tree — and which this pass declines, because the form already holds marked content — removes
    /// that key from the output.
    /// </summary>
    /// <remarks>
    /// <para><b>The defect.</b> <c>PurgeStaleStructureLinks</c> walked pages and their
    /// <c>/Annots</c> only. A Form XObject is the one object class the tagger itself writes
    /// <c>/StructParents</c> onto, so a form left untagged kept a value the freshly generated
    /// <c>/ParentTree</c> does not define. ISO 32000-1 §14.7.4.4 requires every
    /// <c>/StructParent(s)</c> value to be a key in the ParentTree, so independent checkers report the
    /// output invalid — remediation manufacturing a violation from conformant input, which is the
    /// exact failure this method exists to prevent for pages and annotations.</para>
    /// </remarks>
    [Fact]
    public void AutoTag_DeclinedFormKeepingAStaleStructParents_HasTheKeyRemoved()
    {
        (AutoTagResult result, byte[] tagged) = AutoTag(FormWithStaleStructParentsPdf());

        Assert.True(result.ContentMarked);

        using PdfDocument doc = PdfDocument.Open(new MemoryStream(tagged));
        var form = doc.ResolveObject(new PdfReference(9, 0)) as PdfStream;
        Assert.NotNull(form);

        Assert.Null(form[new PdfName("StructParents")]);

        // Removing the key must not remove the form. PdfStream derives from PdfDictionary, so a
        // clone written as a plain dictionary would drop the content and the page would draw
        // nothing — the trap CLAUDE.md records against PdfDictionary.DeepClone.
        Assert.Contains("Hi", Encoding.ASCII.GetString(form.GetDecodedData()), StringComparison.Ordinal);
    }

    /// <summary>
    /// And the report says so, so a caller can tell that an object was made honestly untagged
    /// rather than quietly altered.
    /// </summary>
    [Fact]
    public void AutoTag_DeclinedFormKeepingAStaleStructParents_IsReported()
    {
        (AutoTagResult result, _) = AutoTag(FormWithStaleStructParentsPdf());

        Assert.Contains(result.Warnings, w =>
            w.IndexOf("Form XObject /StructParents key(s)", StringComparison.Ordinal) >= 0 &&
            w.IndexOf("0 Form XObject", StringComparison.Ordinal) < 0);
    }

    // =====================================================================
    //  6. Synthesised /Widths must cover the codes /Differences defines
    // =====================================================================

    /// <summary>
    /// A Standard-14 font with no <c>/Widths</c> whose <c>/Differences</c> map codes below 32 gets
    /// an array that covers them, with the original AFM advance for each.
    /// </summary>
    /// <remarks>
    /// <para><b>The defect.</b> <c>PlanSimpleFont</c> hard-coded <c>FirstChar 32</c> /
    /// <c>LastChar 255</c>, but the branch it sits in also runs for a font that <em>has</em> an
    /// <c>/Encoding</c> dictionary, and <c>ResolveEffectiveEncoding</c> happily builds
    /// <c>/Differences</c> entries below 32 into the plan. Such a code fell outside the written
    /// array while <c>CompleteDescriptor</c> wrote <c>/MissingWidth 0</c> onto the new descriptor,
    /// so its advance became zero where the un-embedded font had used the built-in AFM metric and
    /// the following glyphs overprinted it. The fixture is the one ISO 32000-1 §9.6.2.2 explicitly
    /// permits: a Standard 14 font with no <c>/Widths</c> at all.</para>
    /// <para>The expected widths are read out of <c>Standard14Metrics</c> rather than written as
    /// literals, and asserted non-zero and different from the substitute's uniform 600 — otherwise
    /// a table that had lost these glyphs would make the test agree with a broken implementation.
    /// </para>
    /// </remarks>
    [Fact]
    public void Standard14WithDifferencesBelowCode32_GetsWidthsThatCoverThem()
    {
        double bullet = Standard14Metrics.GetWidthByUnicode(StandardFonts.Helvetica, '•');
        double emdash = Standard14Metrics.GetWidthByUnicode(StandardFonts.Helvetica, '—');

        Assert.True(bullet > 0 && emdash > 0,
            "the AFM table no longer knows these glyphs, so this test cannot say anything");
        Assert.True(bullet != 600 && emdash != 600,
            "these advances must differ from the substitute program's uniform 600, or a /Widths " +
            "array computed from the wrong font would satisfy the assertions below");

        byte[] output = EmbedInto(DifferencesBelow32Pdf());

        using PdfDocument doc = PdfDocument.Open(new MemoryStream(output, writable: false));
        var font = doc.ResolveObject(new PdfReference(5, 0)) as PdfDictionary;
        Assert.NotNull(font);

        int firstChar = ((PdfNumber)doc.ResolveObject(font[PdfName.FirstChar])).IntValue;
        int lastChar  = ((PdfNumber)doc.ResolveObject(font[PdfName.LastChar])).IntValue;
        var widths    = doc.ResolveObject(font[PdfName.Widths]) as PdfArray;
        Assert.NotNull(widths);

        Assert.Equal(1, firstChar);
        Assert.Equal(255, lastChar);
        Assert.Equal(lastChar - firstChar + 1, widths.Count);

        Assert.Equal((int)Math.Round(bullet), ((PdfNumber)widths[1 - firstChar]).IntValue);
        Assert.Equal((int)Math.Round(emdash), ((PdfNumber)widths[2 - firstChar]).IntValue);
    }

    /// <summary>
    /// The range only ever widens: a Standard-14 font whose encoding names nothing below 32 still
    /// gets exactly the array it always got. Without this the fix could have narrowed or shifted
    /// every ordinary font's widths and this file would not have noticed.
    /// </summary>
    [Fact]
    public void Standard14WithNoDifferences_KeepsTheUnchangedThirtyTwoToTwoFiftyFiveRange()
    {
        byte[] output = EmbedInto(Standard14NoWidthsPdf());

        using PdfDocument doc = PdfDocument.Open(new MemoryStream(output, writable: false));
        var font = doc.ResolveObject(new PdfReference(5, 0)) as PdfDictionary;

        Assert.Equal(32,  ((PdfNumber)doc.ResolveObject(font[PdfName.FirstChar])).IntValue);
        Assert.Equal(255, ((PdfNumber)doc.ResolveObject(font[PdfName.LastChar])).IntValue);
        Assert.Equal(224, (doc.ResolveObject(font[PdfName.Widths]) as PdfArray).Count);
    }

    // =====================================================================
    //  Drivers
    // =====================================================================

    private static IReadOnlyList<AccessibilityIssue> DeepCheck(byte[] pdf)
        => PdfDeepAccessibilityChecker.Check(new MemoryStream(pdf, writable: false));

    private static (AutoTagResult Result, byte[] Bytes) AutoTag(byte[] pdf)
    {
        using var input  = new MemoryStream(pdf);
        using var output = new MemoryStream();
        AutoTagResult result = PdfAutoTagger.AutoTag(input, output,
            new AutoTagOptions { CompressRewrittenStreams = false });
        return (result, output.ToArray());
    }

    private static byte[] EmbedInto(byte[] pdf)
    {
        using var input  = new MemoryStream(pdf, writable: false);
        using var output = new MemoryStream();
        PdfAccessibility.EmbedMissingFonts(input, output,
            new FontEmbeddingOptions { FontProvider = _ => MinimalTrueType.Build("Helo") });
        return output.ToArray();
    }

    // =====================================================================
    //  PDF assembly
    // =====================================================================

    private static string StreamObject(string content)
        => "<< /Length " + content.Length.ToString(CultureInfo.InvariantCulture)
         + " >>\nstream\n" + content + "endstream";

    /// <summary>
    /// A standard-handler <c>/Encrypt</c> dictionary granting accessibility extraction (bit 10), so
    /// nothing here reports UA06. Nothing is actually enciphered: the checker's whole interest is
    /// in whether an <c>/Encrypt</c> entry exists, and the fixtures supply the byte shapes a real
    /// cipher would have produced.
    /// </summary>
    private const string EncryptionDictionary =
        "<< /Filter /Standard /V 2 /R 3 /Length 128 /P -3392 "
        + "/O <0102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F20> "
        + "/U <2122232425262728292A2B2C2D2E2F303132333435363738393A3B3C3D3E3F40> >>";

    private static byte[] BuildPdf(IReadOnlyList<string> objectBodies, bool encrypted = false)
    {
        var bodies = new List<string>(objectBodies);

        string encryptEntry = string.Empty;
        if (encrypted)
        {
            bodies.Add(EncryptionDictionary);
            encryptEntry = " /Encrypt " + bodies.Count.ToString(CultureInfo.InvariantCulture) + " 0 R"
                         + " /ID [<0102030405060708090A0B0C0D0E0F10> <0102030405060708090A0B0C0D0E0F10>]";
        }

        var sb = new StringBuilder(4096);
        sb.Append("%PDF-1.7\n");

        var offsets = new List<int>(bodies.Count);
        for (int i = 0; i < bodies.Count; i++)
        {
            offsets.Add(sb.Length);
            sb.Append((i + 1).ToString(CultureInfo.InvariantCulture))
              .Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }

        int xref = sb.Length;
        int size = bodies.Count + 1;
        sb.Append("xref\n0 ").Append(size.ToString(CultureInfo.InvariantCulture))
          .Append("\n0000000000 65535 f \n");
        foreach (int offset in offsets)
            sb.AppendFormat(CultureInfo.InvariantCulture, "{0:D10} 00000 n \n", offset);
        sb.Append("trailer\n<< /Size ").Append(size.ToString(CultureInfo.InvariantCulture))
          .Append(" /Root 1 0 R").Append(encryptEntry).Append(" >>\n");
        sb.AppendFormat(CultureInfo.InvariantCulture, "startxref\n{0}\n%%EOF\n", xref);

        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    private const string HelveticaFont =
        "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>";

    // ---------------------------------------------------------------------
    //  UA22 fixtures
    // ---------------------------------------------------------------------

    private static byte[] EncryptedLinkPdf(string contentsEntry) => LinkPdf(contentsEntry, encrypted: true);

    /// <summary>
    /// One tagged page carrying one Link annotation, whose <c>/Contents</c> entry the caller
    /// supplies verbatim. Everything else is conformant, so UA22 and the coverage notice are the
    /// only things that can vary between the fixtures.
    /// </summary>
    private static byte[] LinkPdf(string contentsEntry, bool encrypted) => BuildPdf(new[]
    {
        "<< /Type /Catalog /Pages 2 0 R /StructTreeRoot 6 0 R /MarkInfo << /Marked true >> /Lang (en-US) >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 5 0 R >> >> "
        + "/Contents 4 0 R /Tabs /S /StructParents 0 /Annots [9 0 R] >>",
        StreamObject("/P <</MCID 0>> BDC\nBT /F1 12 Tf 72 700 Td (Tagged text.) Tj ET\nEMC\n"),
        HelveticaFont,
        "<< /Type /StructTreeRoot /K [7 0 R] /ParentTree 8 0 R >>",
        "<< /Type /StructElem /S /P /P 6 0 R /K [0 << /Type /OBJR /Obj 9 0 R /Pg 3 0 R >>] /Pg 3 0 R >>",
        "<< /Nums [0 [7 0 R] 1 7 0 R] >>",
        "<< /Type /Annot /Subtype /Link /Rect [72 690 200 710] /StructParent 1 " + contentsEntry + " >>",
    }, encrypted);

    // ---------------------------------------------------------------------
    //  UA21 fixtures
    // ---------------------------------------------------------------------

    /// <summary>
    /// Two pages that draw the <b>same</b> non-embedded font object (5) under the same resource
    /// name, each with its own text rendering mode.
    /// </summary>
    private static byte[] TwoPageFontUsePdf(string page1Mode, string page2Mode) => BuildPdf(new[]
    {
        "<< /Type /Catalog /Pages 2 0 R /StructTreeRoot 8 0 R /MarkInfo << /Marked true >> /Lang (en-US) >>",
        "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 5 0 R >> >> "
        + "/Contents 6 0 R /Tabs /S /StructParents 0 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 5 0 R >> >> "
        + "/Contents 7 0 R /Tabs /S /StructParents 1 >>",
        HelveticaFont,
        StreamObject("/P <</MCID 0>> BDC\nBT /F1 12 Tf " + page1Mode + " 72 700 Td (Page one.) Tj ET\nEMC\n"),
        StreamObject("/P <</MCID 0>> BDC\nBT /F1 12 Tf " + page2Mode + " 72 700 Td (Page two.) Tj ET\nEMC\n"),
        "<< /Type /StructTreeRoot /K [9 0 R 10 0 R] /ParentTree 11 0 R >>",
        "<< /Type /StructElem /S /P /P 8 0 R /K [0] /Pg 3 0 R >>",
        "<< /Type /StructElem /S /P /P 8 0 R /K [0] /Pg 4 0 R >>",
        "<< /Nums [0 [9 0 R] 1 [10 0 R]] >>",
    });

    /// <summary>
    /// Two pages whose <c>/F1</c> entries are two <b>different</b> non-embedded font dictionaries:
    /// object 5 is drawn invisibly, object 9 visibly. A checker keyed on resource names cannot tell
    /// them apart.
    /// </summary>
    private static byte[] TwoDistinctFontsSameNamePdf() => BuildPdf(new[]
    {
        "<< /Type /Catalog /Pages 2 0 R /StructTreeRoot 10 0 R /MarkInfo << /Marked true >> /Lang (en-US) >>",
        "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 5 0 R >> >> "
        + "/Contents 6 0 R /Tabs /S /StructParents 0 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 9 0 R >> >> "
        + "/Contents 7 0 R /Tabs /S /StructParents 1 >>",
        HelveticaFont,                                     // 5 — the OCR layer's font
        StreamObject("/P <</MCID 0>> BDC\nBT /F1 12 Tf 3 Tr 72 700 Td (Scanned.) Tj ET\nEMC\n"),
        StreamObject("/P <</MCID 0>> BDC\nBT /F1 12 Tf 0 Tr 72 700 Td (Visible.) Tj ET\nEMC\n"),
        "<< /Type /StructElem /S /P /P 10 0 R /K [0] /Pg 3 0 R >>",
        "<< /Type /Font /Subtype /Type1 /BaseFont /Times-Roman /Encoding /WinAnsiEncoding >>",  // 9
        "<< /Type /StructTreeRoot /K [8 0 R 11 0 R] /ParentTree 12 0 R >>",
        "<< /Type /StructElem /S /P /P 10 0 R /K [0] /Pg 4 0 R >>",
        "<< /Nums [0 [8 0 R] 1 [11 0 R]] >>",
    });

    // ---------------------------------------------------------------------
    //  Structure-walk ceiling fixtures
    // ---------------------------------------------------------------------

    /// <summary>
    /// Elements per level of the bomb, and slots per shared array. Chosen so that the walker, the
    /// reachability sweep and the MCID-claim collection each overshoot the 2 000 000-edge ceiling
    /// by ~400 000 slots: the walker stops after four elements (each costing one root slot plus
    /// <see cref="SharedSlots"/>), and the two checker passes then re-walk those four elements'
    /// arrays with a fresh budget of their own. A margin this size cannot be erased by an
    /// off-by-one in anyone's accounting.
    /// </summary>
    private const int SharedSlots  = 600_001;
    private const int BombElements = 8;

    /// <summary>
    /// <i>n</i> structure elements that all name one shared <c>/K</c> array. Legal PDF — <c>/K</c>
    /// may be an indirect reference and a structure element may be reached from more than one
    /// place — and O(n·m) slots from an O(n+m)-byte file.
    /// </summary>
    /// <remarks>
    /// The array holds <c>null</c> objects rather than MCIDs or references: every walk charges a
    /// slot for one either way, and a null costs no allocation in any of the three, so the fixture
    /// exercises the ceiling rather than the heap.
    /// </remarks>
    private static byte[] StructureEdgeBomb()
    {
        const int firstElement = 8;

        var kids = new StringBuilder(SharedSlots * 5 + 2);
        kids.Append('[');
        for (int i = 0; i < SharedSlots; i++) kids.Append("null ");
        kids.Append(']');

        var elementRefs = new StringBuilder(BombElements * 8);
        for (int i = 0; i < BombElements; i++)
        {
            if (i > 0) elementRefs.Append(' ');
            elementRefs.Append((firstElement + i).ToString(CultureInfo.InvariantCulture)).Append(" 0 R");
        }

        var bodies = new List<string>(BombElements + 8)
        {
            "<< /Type /Catalog /Pages 2 0 R /StructTreeRoot 5 0 R /MarkInfo << /Marked true >> /Lang (en-US) >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Tabs /S /StructParents 0 >>",
            StreamObject("/P <</MCID 0>> BDC\nBT ET\nEMC\n"),
            "<< /Type /StructTreeRoot /K [" + elementRefs + "] /ParentTree 6 0 R >>",
            "<< /Nums [0 [8 0 R]] >>",
            kids.ToString(),                                    // 7 — the shared /K array
        };

        for (int i = 0; i < BombElements; i++)
            bodies.Add("<< /Type /StructElem /S /P /P 5 0 R /Pg 3 0 R /K 7 0 R >>");

        return BuildPdf(bodies);
    }

    /// <summary>An ordinary, fully conformant tagged page.</summary>
    private static byte[] WellFormedTaggedPdf() => BuildPdf(new[]
    {
        "<< /Type /Catalog /Pages 2 0 R /StructTreeRoot 6 0 R /MarkInfo << /Marked true >> /Lang (en-US) >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 5 0 R >> >> "
        + "/Contents 4 0 R /Tabs /S /StructParents 0 >>",
        StreamObject("/P <</MCID 0>> BDC\nBT /F1 12 Tf 72 700 Td (Tagged text.) Tj ET\nEMC\n"),
        HelveticaFont,
        "<< /Type /StructTreeRoot /K [7 0 R] /ParentTree 8 0 R >>",
        "<< /Type /StructElem /S /P /P 6 0 R /K [0] /Pg 3 0 R >>",
        "<< /Nums [0 [7 0 R]] >>",
    });

    // ---------------------------------------------------------------------
    //  Shared-form duplication fixtures
    // ---------------------------------------------------------------------

    /// <summary>
    /// <paramref name="pages"/> pages that inherit one <c>/Pages</c>-level <c>/Resources</c> whose
    /// <c>/XObject</c> dictionary holds <paramref name="xobjectEntries"/> entries plus the one tiny
    /// Form XObject every page draws. This is the ordinary layout — a shared letterhead under a
    /// document-wide resource dictionary — at the size that makes the per-page copy the dominant
    /// cost.
    /// </summary>
    private static byte[] SharedFormWithInheritedResources(int xobjectEntries, int pages)
    {
        const string formContent = "BT /F1 9 Tf 10 10 Td (Letterhead) Tj ET\n";

        var xobjects = new StringBuilder(xobjectEntries * 14 + 64);
        xobjects.Append("<< /Fm0 5 0 R");
        for (int i = 0; i < xobjectEntries; i++)
            xobjects.Append(" /X").Append(i.ToString(CultureInfo.InvariantCulture)).Append(" 5 0 R");
        xobjects.Append(" >>");

        int firstPage    = 6;
        int firstContent = firstPage + pages;

        var kids = new StringBuilder(pages * 8);
        for (int i = 0; i < pages; i++)
        {
            if (i > 0) kids.Append(' ');
            kids.Append((firstPage + i).ToString(CultureInfo.InvariantCulture)).Append(" 0 R");
        }

        var bodies = new List<string>(2 * pages + 6)
        {
            "<< /Type /Catalog /Pages 2 0 R /Lang (en-US) >>",
            "<< /Type /Pages /Kids [" + kids + "] /Count " + pages.ToString(CultureInfo.InvariantCulture)
            + " /Resources 3 0 R >>",
            "<< /Font << /F1 4 0 R >> /XObject " + xobjects + " >>",
            HelveticaFont,
            "<< /Type /XObject /Subtype /Form /BBox [0 0 200 50] /Resources << /Font << /F1 4 0 R >> >> "
            + "/Length " + formContent.Length.ToString(CultureInfo.InvariantCulture)
            + " >>\nstream\n" + formContent + "endstream",     // 5 — the shared form
        };

        // Each page owns its /Contents. A page whose own stream holds no taggable text is skipped,
        // and a skipped page has no /StructParents for a form copy's content to belong to — so a
        // shared /Contents would make this fixture test the page skip rather than the budget.
        for (int i = 0; i < pages; i++)
            bodies.Add("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents "
                       + (firstContent + i).ToString(CultureInfo.InvariantCulture) + " 0 R >>");

        for (int i = 0; i < pages; i++)
            bodies.Add(StreamObject("BT /F1 12 Tf 72 700 Td (Page "
                                    + (i + 1).ToString(CultureInfo.InvariantCulture) + ") Tj ET\n/Fm0 Do\n"));

        return BuildPdf(bodies);
    }

    /// <summary>
    /// A tagged document whose page 1 draws Form XObject 9, which carries <c>/StructParents 3</c>
    /// from the existing tree and already holds marked content — so the tagger declines it, the
    /// generated <c>/ParentTree</c> starts at key 7, and object 9's key resolves to nothing.
    /// </summary>
    private static byte[] FormWithStaleStructParentsPdf()
    {
        const string formContent = "/P <</MCID 0>> BDC\nBT /F1 9 Tf 10 10 Td (Hi) Tj ET\nEMC\n";

        return BuildPdf(new[]
        {
            "<< /Type /Catalog /Pages 2 0 R /StructTreeRoot 6 0 R /MarkInfo << /Marked true >> /Lang (en-US) >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /StructParents 5 "
            + "/Resources << /Font << /F1 5 0 R >> /XObject << /Fm0 9 0 R >> >> /Contents 4 0 R /Tabs /S >>",
            StreamObject("BT /F1 12 Tf 72 700 Td (Page text.) Tj ET\n/Fm0 Do\n"),
            HelveticaFont,
            "<< /Type /StructTreeRoot /K [7 0 R] /ParentTree 8 0 R /ParentTreeNextKey 7 >>",
            "<< /Type /StructElem /S /P /P 6 0 R /K [0] /Pg 3 0 R >>",
            "<< /Nums [5 [7 0 R]] >>",
            "<< /Type /XObject /Subtype /Form /BBox [0 0 200 50] /StructParents 3 "
            + "/Resources << /Font << /F1 5 0 R >> >> /Length "
            + formContent.Length.ToString(CultureInfo.InvariantCulture)
            + " >>\nstream\n" + formContent + "endstream",
        });
    }

    // ---------------------------------------------------------------------
    //  Font-embedding fixtures
    // ---------------------------------------------------------------------

    private static byte[] Standard14NoWidthsPdf()
        => FontPdf("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>", "Hello");

    private static byte[] DifferencesBelow32Pdf()
        => FontPdf(
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding "
            + "<< /Type /Encoding /Differences [1 /bullet 2 /emdash] >> >>",
            "\\001 \\002");

    /// <summary>A tagged single-page document whose only font is object 5.</summary>
    private static byte[] FontPdf(string fontBody, string text) => BuildPdf(new[]
    {
        "<< /Type /Catalog /Pages 2 0 R /Lang (en-US) /MarkInfo << /Marked true >> /StructTreeRoot 6 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /StructParents 0 "
        + "/Resources << /Font << /F1 5 0 R >> >> >>",
        StreamObject("/P <</MCID 0>> BDC\nBT /F1 12 Tf 72 400 Td (" + text + ") Tj ET\nEMC\n"),
        fontBody,
        "<< /Type /StructTreeRoot /K [7 0 R] /ParentTree 8 0 R /ParentTreeNextKey 1 >>",
        "<< /Type /StructElem /S /P /P 6 0 R /Pg 3 0 R /K 0 >>",
        "<< /Nums [0 [7 0 R]] >>",
    });
}
