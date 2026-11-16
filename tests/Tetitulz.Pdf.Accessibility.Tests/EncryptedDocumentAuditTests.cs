// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Accessibility;
using Tetitulz.Pdf.Accessibility.Checking;
using Tetitulz.Pdf.Accessibility.Models;
using Tetitulz.Pdf.Plugins.Conformance;
using Xunit;

namespace Tetitulz.Pdf.Accessibility.Tests;

/// <summary>
/// Locks shut the asymmetry between the two halves of the audit.
///
/// <para>
/// <c>PdfUaChecker</c> (the Conformance plugin) reads the security handler's <c>/P</c> bit 10 and
/// keeps auditing whatever it finds there. <see cref="PdfDeepAccessibilityChecker"/> used to answer
/// <em>any</em> encrypted document with a single UA06 Error and stop — without ever looking at the
/// permission bits. So an encrypted but perfectly accessible file got half an audit, and the
/// combined report could say "encrypted, cannot check" in one breath and list real findings in the
/// next. These tests pin down the fixed behaviour:
/// </para>
/// <list type="bullet">
///   <item>extraction permitted ⇒ no UA06, and every structural check still runs;</item>
///   <item>extraction denied ⇒ UA06, worded as a <i>permission</i> failure, and the structural
///     checks still run — exactly as the plugin does;</item>
///   <item>whatever the permissions, the checks that genuinely cannot run are named, once, in the
///     report rather than silently omitted;</item>
///   <item>a document without an <c>/Encrypt</c> dictionary is audited exactly as before.</item>
/// </list>
///
/// <para>
/// The <c>/Encrypt</c> dictionaries below are written by hand and no object is actually
/// enciphered: the checker's whole job here is to read <c>/Filter</c>, <c>/R</c> and <c>/P</c> out
/// of a dictionary, and that is what the fixtures exercise. Every fixture is an in-memory
/// <c>byte[]</c>; nothing is read from disk.
/// </para>
/// </summary>
public class EncryptedDocumentAuditTests
{
    // =====================================================================
    //  /P permission bits (ISO 32000-1 Table 22)
    // =====================================================================

    /// <summary>Bit 10, 1-based — "extract text and graphics for use by assistive technology".</summary>
    private const int ExtractForAccessibility = 1 << 9;   // 512

    /// <summary>
    /// Every reserved-must-be-1 bit (7, 8 and 13–32) set and every grantable permission cleared.
    /// Bit 10 is therefore clear: this value denies accessibility extraction.
    /// </summary>
    private const int PermissionsDenyingEverything = unchecked((int)0xFFFFF0C0);

    /// <summary>The same, with accessibility extraction granted and nothing else.</summary>
    private const int PermissionsAllowingAccessibility = PermissionsDenyingEverything | ExtractForAccessibility;

    // =====================================================================
    //  PDF assembly helpers
    // =====================================================================

    /// <summary>How (and whether) a fixture carries an encryption dictionary.</summary>
    private enum EncryptionMode
    {
        /// <summary>No /Encrypt entry at all.</summary>
        None,

        /// <summary>Standard handler, /R 3, /P bit 10 set.</summary>
        AccessibilityPermitted,

        /// <summary>Standard handler, /R 3, /P bit 10 clear.</summary>
        AccessibilityDenied,

        /// <summary>Standard handler, /R 6 (AES-256) with bit 10 clear — deprecated, treated as set.</summary>
        Revision6WithBitClear,

        /// <summary>A non-standard security handler, whose permissions cannot be read.</summary>
        NonStandardHandler,

        /// <summary>Standard handler, /R 3, no /P entry at all.</summary>
        NoPermissionsEntry,

        /// <summary>
        /// Standard handler, /R 3, bit 10 clear — written in the <b>unsigned</b> spelling of the
        /// same 32-bit pattern (<c>/P 4294963392</c> rather than <c>/P -3904</c>), which is what
        /// a common viewer and most production writers emit.
        /// </summary>
        AccessibilityDeniedUnsignedSpelling,

        /// <summary>The acceptance control for the above: bit 10 <b>set</b>, unsigned spelling.</summary>
        AccessibilityPermittedUnsignedSpelling,
    }

    /// <summary>
    /// The unsigned spelling of a signed 32-bit <c>/P</c>: the identical bit pattern, printed as
    /// the non-negative integer it denotes. Derived rather than typed, so the two spellings cannot
    /// drift apart in the fixture and leave the test asserting something other than it claims.
    /// </summary>
    private static string UnsignedSpelling(int signedPermissions)
        => ((uint)signedPermissions).ToString(CultureInfo.InvariantCulture);

    private static string EncryptionDictionary(EncryptionMode mode)
    {
        const string keys = "/O <0102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F20> "
                          + "/U <2122232425262728292A2B2C2D2E2F303132333435363738393A3B3C3D3E3F40>";

        return mode switch
        {
            EncryptionMode.AccessibilityPermitted =>
                "<< /Filter /Standard /V 2 /R 3 /Length 128 /P "
                + PermissionsAllowingAccessibility.ToString(CultureInfo.InvariantCulture) + " " + keys + " >>",

            EncryptionMode.AccessibilityDenied =>
                "<< /Filter /Standard /V 2 /R 3 /Length 128 /P "
                + PermissionsDenyingEverything.ToString(CultureInfo.InvariantCulture) + " " + keys + " >>",

            EncryptionMode.Revision6WithBitClear =>
                "<< /Filter /Standard /V 5 /R 6 /Length 256 /P "
                + PermissionsDenyingEverything.ToString(CultureInfo.InvariantCulture) + " " + keys + " >>",

            EncryptionMode.NonStandardHandler =>
                "<< /Filter /AcmeCrypto /V 4 /R 4 /P "
                + PermissionsAllowingAccessibility.ToString(CultureInfo.InvariantCulture) + " " + keys + " >>",

            EncryptionMode.NoPermissionsEntry =>
                "<< /Filter /Standard /V 2 /R 3 /Length 128 " + keys + " >>",

            EncryptionMode.AccessibilityDeniedUnsignedSpelling =>
                "<< /Filter /Standard /V 2 /R 3 /Length 128 /P "
                + UnsignedSpelling(PermissionsDenyingEverything) + " " + keys + " >>",

            EncryptionMode.AccessibilityPermittedUnsignedSpelling =>
                "<< /Filter /Standard /V 2 /R 3 /Length 128 /P "
                + UnsignedSpelling(PermissionsAllowingAccessibility) + " " + keys + " >>",

            _ => throw new ArgumentOutOfRangeException(nameof(mode)),
        };
    }

    private static string StreamObject(string content)
        => "<< /Length " + content.Length.ToString(CultureInfo.InvariantCulture)
           + " >>\nstream\n" + content + "endstream";

    private const string HelveticaFont =
        "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>";

    /// <summary>
    /// Serialises <paramref name="objectBodies"/> as objects 1..n with a classic cross-reference
    /// table, appending the encryption dictionary as one more object when
    /// <paramref name="mode"/> asks for one and pointing the trailer's <c>/Encrypt</c> at it.
    /// Everything written is ASCII, so <c>StringBuilder.Length</c> is a valid byte offset.
    /// </summary>
    private static byte[] BuildPdf(IReadOnlyList<string> objectBodies, EncryptionMode mode)
    {
        var bodies = new List<string>(objectBodies);

        string encryptEntry = string.Empty;
        if (mode != EncryptionMode.None)
        {
            bodies.Add(EncryptionDictionary(mode));
            encryptEntry = " /Encrypt " + bodies.Count.ToString(CultureInfo.InvariantCulture) + " 0 R"
                         + " /ID [<0102030405060708090A0B0C0D0E0F10> <0102030405060708090A0B0C0D0E0F10>]";
        }

        var sb = new StringBuilder();
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

    // =====================================================================
    //  Fixtures
    // =====================================================================

    /// <summary>
    /// A document whose structure tree is broken in four independent, purely <em>structural</em>
    /// ways, none of which needs a single decrypted byte to detect:
    /// <list type="bullet">
    ///   <item>no <c>/ParentTree</c> at all (UA14);</item>
    ///   <item>two structure elements that are leaves referencing no content (UA16, objects 8 and 9);</item>
    ///   <item>a <c>&lt;Bogus&gt;</c> element whose type is neither standard nor role-mapped (UA13);</item>
    ///   <item>a <c>&lt;Document&gt;</c> subtree that transitively reaches no content (UA30, object 7).</item>
    /// </list>
    /// Its Helvetica is not embedded either (UA21), which PDF/UA grants no exemption for.
    /// The page draws text outside any marked content, so the <em>unencrypted</em> variant of the
    /// same file additionally reports UA17 and UA18 — the two content-level findings that the
    /// encrypted variant must honestly report as not attempted rather than silently drop.
    /// </summary>
    private static byte[] BrokenStructurePdf(EncryptionMode mode) => BuildPdf(new[]
    {
        // 1 — catalog
        "<< /Type /Catalog /Pages 2 0 R /StructTreeRoot 6 0 R /MarkInfo << /Marked true >> /Lang (en-US) >>",

        // 2 — page tree
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",

        // 3 — page, with no /StructParents
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] "
        + "/Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R /Tabs /S >>",

        // 4 — content: text drawn inside no marked-content sequence
        StreamObject("BT /F1 12 Tf 72 700 Td (Text that belongs to no structure element.) Tj ET\n"),

        // 5 — font, not embedded
        HelveticaFont,

        // 6 — structure tree root, with NO /ParentTree
        "<< /Type /StructTreeRoot /K [7 0 R] >>",

        // 7 — has element children, so UA16 cannot see it; reaches no content, so UA30 must
        "<< /Type /StructElem /S /Document /P 6 0 R /K [8 0 R 9 0 R] >>",

        // 8, 9 — hollow leaves; 9 also uses a type no consumer can interpret
        "<< /Type /StructElem /S /H1 /P 7 0 R >>",
        "<< /Type /StructElem /S /Bogus /P 7 0 R >>",
    }, mode);

    /// <summary>
    /// A tagged document whose one <em>page</em> declares a <c>/StructParents</c> key the
    /// <c>/ParentTree</c> does not define. The finding is page-scoped, so it can only be produced
    /// by a checker that actually enumerated the pages — which, on an encrypted document, means
    /// walking <c>/Root → /Pages</c> by hand, because the parser skips page loading entirely for
    /// such a file and <c>doc.Pages</c> is empty. Nothing else about the file is wrong.
    /// </summary>
    private static byte[] OrphanedStructParentsKeyPdf(EncryptionMode mode) => BuildPdf(new[]
    {
        // 1 — catalog
        "<< /Type /Catalog /Pages 2 0 R /StructTreeRoot 6 0 R /MarkInfo << /Marked true >> /Lang (en-US) >>",

        // 2 — page tree
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",

        // 3 — page: /StructParents 7 is a key the /ParentTree below never defines
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] "
        + "/Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R /Tabs /S /StructParents 7 >>",

        // 4 — properly marked content
        StreamObject("/P <</MCID 0>> BDC\nBT /F1 12 Tf 72 700 Td (Tagged text.) Tj ET\nEMC\n"),

        // 5 — font
        HelveticaFont,

        // 6 — structure tree root with a /ParentTree
        "<< /Type /StructTreeRoot /K [7 0 R] /ParentTree 8 0 R >>",

        // 7 — a paragraph that does own the page's MCID 0
        "<< /Type /StructElem /S /P /P 6 0 R /K [0] /Pg 3 0 R >>",

        // 8 — the number tree: defines key 0, never key 7
        "<< /Nums [0 [7 0 R]] >>",
    }, mode);

    // =====================================================================
    //  Assertion helpers
    // =====================================================================

    private static IReadOnlyList<AccessibilityIssue> DeepCheck(byte[] pdf)
        => PdfDeepAccessibilityChecker.Check(new MemoryStream(pdf));

    private static AccessibilityAuditReport Audit(byte[] pdf)
        => PdfAccessibility.Audit(new MemoryStream(pdf));

    private static HashSet<string> CodesOf(IEnumerable<AccessibilityIssue> issues)
        => new HashSet<string>(issues.Select(i => i.Code), StringComparer.Ordinal);

    private static IReadOnlyList<AccessibilityIssue> WithCode(IEnumerable<AccessibilityIssue> issues, string code)
        => issues.Where(i => string.Equals(i.Code, code, StringComparison.Ordinal)).ToList();

    private static bool Mentions(string text, string term)
        => text != null && text.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0;

    /// <summary>The single Info issue that states what an encrypted document's audit could not do.</summary>
    private static IReadOnlyList<AccessibilityIssue> CoverageNotes(IEnumerable<AccessibilityIssue> issues)
        => issues.Where(i => Mentions(i.Message, "was not attempted")).ToList();

    /// <summary>UA99 issues that report a check having <em>crashed</em>, which is never expected here.</summary>
    private static IReadOnlyList<AccessibilityIssue> CrashNotes(IEnumerable<AccessibilityIssue> issues)
        => issues.Where(i => Mentions(i.Message, "could not be completed")).ToList();

    // =====================================================================
    //  Extraction permitted — no UA06, and the audit really happens
    // =====================================================================

    [Fact]
    public void EncryptedPdf_ThatPermitsExtraction_ReportsNoUa06()
    {
        var issues = DeepCheck(BrokenStructurePdf(EncryptionMode.AccessibilityPermitted));

        Assert.Empty(WithCode(issues, "UA06"));
    }

    [Fact]
    public void EncryptedPdf_ThatPermitsExtraction_StillReportsStructuralFindings()
    {
        var issues = DeepCheck(BrokenStructurePdf(EncryptionMode.AccessibilityPermitted));

        // Every one of these is derived from dictionary structure alone, which encryption does
        // not touch. Before the fix the whole list was replaced by a single UA06.
        Assert.NotEmpty(WithCode(issues, "UA13"));   // /S /Bogus resolves to no standard type
        Assert.NotEmpty(WithCode(issues, "UA14"));   // no /ParentTree
        Assert.NotEmpty(WithCode(issues, "UA30"));   // the <Document> subtree reaches no content
        Assert.NotEmpty(WithCode(issues, "UA21"));   // Helvetica is not embedded

        // Two hollow leaves, reported one issue each and located.
        var hollowLeaves = WithCode(issues, "UA16");
        Assert.Equal(2, hollowLeaves.Count);
        Assert.All(hollowLeaves, i => Assert.Equal(IssueSeverity.Error, i.Severity));
        Assert.Contains(hollowLeaves, i => i.ObjectNumber == 8);
        Assert.Contains(hollowLeaves, i => i.ObjectNumber == 9);
    }

    [Fact]
    public void EncryptedPdf_StillEnumeratesPages_SoPageScopedChecksRun()
    {
        // The parser loads no page tree for an encrypted document, so this finding exists only if
        // the checker walked /Root -> /Pages itself.
        var issues = DeepCheck(OrphanedStructParentsKeyPdf(EncryptionMode.AccessibilityPermitted));

        var orphaned = WithCode(issues, "UA14");
        Assert.Single(orphaned);
        Assert.Equal(IssueSeverity.Error, orphaned[0].Severity);
        Assert.True(Mentions(orphaned[0].Message, "/StructParents 7"),
            "UA14 should name the page's orphaned key; got: " + orphaned[0].Message);
        Assert.Equal(1, orphaned[0].PageNumber);
    }

    [Fact]
    public void EncryptedPdf_ThatPermitsExtraction_DoesNotClaimTheAuditWasSkipped()
    {
        var issues = DeepCheck(BrokenStructurePdf(EncryptionMode.AccessibilityPermitted));

        Assert.DoesNotContain(issues, i => Mentions(i.Message, "extended checks skipped"));
        Assert.DoesNotContain(issues, i => i.Severity == IssueSeverity.Error && Mentions(i.Message, "encrypt"));
    }

    // =====================================================================
    //  Extraction denied — UA06, worded as a permission failure
    // =====================================================================

    [Fact]
    public void EncryptedPdf_ThatDeniesExtraction_ReportsUa06AsAPermissionFailure()
    {
        var issues = DeepCheck(BrokenStructurePdf(EncryptionMode.AccessibilityDenied));

        var ua06 = Assert.Single(WithCode(issues, "UA06"));
        Assert.Equal(IssueSeverity.Error, ua06.Severity);

        // The finding must be about the permission bit, not about the mere fact of encryption:
        // "the document is encrypted" was the old, spec-wrong wording.
        Assert.True(Mentions(ua06.Message, "denies content extraction for accessibility"),
            "UA06 must name the denied permission; got: " + ua06.Message);
        Assert.True(Mentions(ua06.Message, "/P bit 10"),
            "UA06 must cite ISO 32000-1 Table 22 /P bit 10; got: " + ua06.Message);
        Assert.False(Mentions(ua06.Message, "extended checks skipped"));
    }

    [Fact]
    public void EncryptedPdf_ThatDeniesExtraction_StillRunsTheStructuralChecks()
    {
        // Mirrors PdfUaChecker: whatever the permissions say, the structural audit continues.
        // A denied permission is one more finding, not a reason to stop looking.
        var issues = DeepCheck(BrokenStructurePdf(EncryptionMode.AccessibilityDenied));

        Assert.NotEmpty(WithCode(issues, "UA13"));
        Assert.NotEmpty(WithCode(issues, "UA14"));
        Assert.NotEmpty(WithCode(issues, "UA30"));
        Assert.Equal(2, WithCode(issues, "UA16").Count);
    }

    // =====================================================================
    //  The /P rule is the plugin's rule, applied case for case
    // =====================================================================

    [Fact]
    public void EncryptedPdf_AtRevision6_ReportsNoUa06_BecauseBit10IsDeprecatedThere()
    {
        // ISO 32000-2: /P bit 10 is deprecated and shall be treated as if set, so a revision-6
        // document with the bit clear is conformant. PdfUaChecker applies exactly this rule.
        var issues = DeepCheck(BrokenStructurePdf(EncryptionMode.Revision6WithBitClear));

        Assert.Empty(WithCode(issues, "UA06"));
        Assert.NotEmpty(WithCode(issues, "UA14"));
    }

    [Fact]
    public void EncryptedPdf_WithNonStandardSecurityHandler_ReportsUa06AsUnverifiable()
    {
        var issues = DeepCheck(BrokenStructurePdf(EncryptionMode.NonStandardHandler));

        var ua06 = Assert.Single(WithCode(issues, "UA06"));
        Assert.Equal(IssueSeverity.Error, ua06.Severity);
        Assert.True(Mentions(ua06.Message, "AcmeCrypto"),
            "UA06 should name the handler it cannot read; got: " + ua06.Message);
    }

    [Fact]
    public void EncryptedPdf_WithNoPermissionsEntry_ReportsUa06AsUnverifiable()
    {
        var issues = DeepCheck(BrokenStructurePdf(EncryptionMode.NoPermissionsEntry));

        var ua06 = Assert.Single(WithCode(issues, "UA06"));
        Assert.Equal(IssueSeverity.Error, ua06.Severity);
        Assert.True(Mentions(ua06.Message, "/P"),
            "UA06 should say the permissions entry is missing; got: " + ua06.Message);
    }

    // =====================================================================
    //  The signed and unsigned spellings of /P denote one bit pattern
    // =====================================================================
    //  §7.6.3 Table 22 defines /P as a signed 32-bit integer, but producers write the unsigned
    //  spelling of the same bits — /P 4294963392 for -3904. Reading that through
    //  PdfNumber.IntValue is a double -> int cast of an out-of-range value, which SATURATES to
    //  int.MaxValue on .NET Core 3.0+: every bit set, so bit 10 reads as "accessibility
    //  extraction permitted" for every such document. Core.Document.PdfPermissionValue exists to
    //  answer this once, and names PDF/UA UA06 as one of the four defects it closed — the deep
    //  checker was the copy that never moved onto it, while PdfUaChecker did.

    /// <summary>
    /// The finding this pins is a <b>false pass</b>, which is the dangerous direction: a document
    /// that withholds its content from assistive technology audited clean.
    /// </summary>
    [Fact]
    public void EncryptedPdf_DenyingExtraction_InTheUnsignedSpelling_StillReportsUa06()
    {
        var issues = DeepCheck(BrokenStructurePdf(EncryptionMode.AccessibilityDeniedUnsignedSpelling));

        var ua06 = Assert.Single(WithCode(issues, "UA06"));
        Assert.Equal(IssueSeverity.Error, ua06.Severity);
        Assert.True(Mentions(ua06.Message, "denies content extraction for accessibility"),
            "UA06 must name the denied permission; got: " + ua06.Message);

        // The reported /P is the signed reading of the bits the file carries, not the saturated
        // one: asserting the number is what separates "read correctly" from "happened to report".
        Assert.True(Mentions(ua06.Message, "/P = " + PermissionsDenyingEverything.ToString(CultureInfo.InvariantCulture)),
            "UA06 must report /P as the signed reading of the file's bits; got: " + ua06.Message);
    }

    /// <summary>
    /// The acceptance control. Without it, the test above passes for a checker that reports UA06
    /// on every encrypted document — a refusal for the wrong reason.
    /// </summary>
    [Fact]
    public void EncryptedPdf_PermittingExtraction_InTheUnsignedSpelling_ReportsNoUa06()
    {
        var issues = DeepCheck(BrokenStructurePdf(EncryptionMode.AccessibilityPermittedUnsignedSpelling));

        Assert.Empty(WithCode(issues, "UA06"));

        // …and the audit really ran, so the absence above is a verdict rather than a skipped check.
        Assert.NotEmpty(WithCode(issues, "UA14"));
        Assert.Equal(2, WithCode(issues, "UA16").Count);
    }

    /// <summary>
    /// The spelling is not a fact about the document: the same bits must produce the same verdict
    /// either way. Asserted over the whole issue set, because a checker that agreed on UA06 alone
    /// while diverging elsewhere would still be reading /P twice.
    /// </summary>
    [Fact]
    public void BothSpellingsOfOnePermissionBitmask_AuditIdentically()
    {
        AssertSameCodes(EncryptionMode.AccessibilityDenied,
                        EncryptionMode.AccessibilityDeniedUnsignedSpelling);
        AssertSameCodes(EncryptionMode.AccessibilityPermitted,
                        EncryptionMode.AccessibilityPermittedUnsignedSpelling);

        static void AssertSameCodes(EncryptionMode signed, EncryptionMode unsigned)
            => Assert.Equal(
                string.Join(",", CodesOf(DeepCheck(BrokenStructurePdf(signed)))
                                 .OrderBy(c => c, StringComparer.Ordinal)),
                string.Join(",", CodesOf(DeepCheck(BrokenStructurePdf(unsigned)))
                                 .OrderBy(c => c, StringComparer.Ordinal)));
    }

    /// <summary>
    /// The deep checker's doc comment claims it applies "the same rule, in the same order, with the
    /// same outcomes" as <c>PdfUaChecker.CheckEncryption</c>. CLAUDE.md: a comment asserting that
    /// two implementations agree is a duplication report. This is that claim, asserted — and it is
    /// the assertion the signed-only fixtures could never make, because both readings of a signed
    /// /P agree.
    /// </summary>
    [Fact]
    public void TheTwoHalvesReachTheSameUa06Verdict_OnAnUnsignedPermissionBitmask()
    {
        AssertBothHalves(EncryptionMode.AccessibilityDeniedUnsignedSpelling, expectUa06: true);
        AssertBothHalves(EncryptionMode.AccessibilityPermittedUnsignedSpelling, expectUa06: false);

        static void AssertBothHalves(EncryptionMode mode, bool expectUa06)
        {
            byte[] pdf = BrokenStructurePdf(mode);

            bool pluginSaysDenied = PdfUaChecker.Validate(new MemoryStream(pdf)).Violations
                .Any(v => string.Equals(v.Code, "UA06", StringComparison.Ordinal));
            bool deepSaysDenied = WithCode(DeepCheck(pdf), "UA06").Count > 0;

            Assert.Equal(expectUa06, pluginSaysDenied);
            Assert.Equal(expectUa06, deepSaysDenied);
        }
    }

    // =====================================================================
    //  What could not be checked is stated once, and exactly
    // =====================================================================

    [Fact]
    public void EncryptedPdf_ReportsExactlyOneInfoIssueNamingTheChecksItCouldNotRun()
    {
        var issues = DeepCheck(BrokenStructurePdf(EncryptionMode.AccessibilityPermitted));

        var note = Assert.Single(CoverageNotes(issues));
        Assert.Equal(IssueSeverity.Info, note.Severity);
        Assert.Equal("UA99", note.Code);

        // Each content-level check is named individually — "some checks were skipped" would leave
        // a reader no better off than the silence this replaces.
        foreach (string code in new[] { "UA15", "UA17", "UA18", "UA24" })
            Assert.True(Mentions(note.Message, code),
                "the coverage note must name " + code + "; got: " + note.Message);

        // …and it says why.
        Assert.True(Mentions(note.Message, "encrypted"), note.Message);
        Assert.True(Mentions(note.Message, "content stream"), note.Message);
    }

    [Fact]
    public void EncryptedPdf_DoesNotReportContentLevelFindings_ButTheSameFileUnencryptedDoes()
    {
        // The proof that the skip is deliberate and honest rather than accidental: the identical
        // document without an /Encrypt dictionary reports both content-level defects.
        var encrypted   = DeepCheck(BrokenStructurePdf(EncryptionMode.AccessibilityPermitted));
        var unencrypted = DeepCheck(BrokenStructurePdf(EncryptionMode.None));

        Assert.NotEmpty(WithCode(unencrypted, "UA17"));   // text outside all marked content
        Assert.NotEmpty(WithCode(unencrypted, "UA18"));   // page has no /StructParents

        Assert.Empty(WithCode(encrypted, "UA17"));
        Assert.Empty(WithCode(encrypted, "UA18"));

        // And nothing was invented in their place: everything the encrypted audit reports, the
        // real audit of the same document reports too (bar the coverage note itself).
        var encryptedCodes = CodesOf(encrypted);
        encryptedCodes.Remove("UA99");
        Assert.Subset(CodesOf(unencrypted), encryptedCodes);
    }

    [Fact]
    public void EncryptedPdf_RunsEveryCheckItClaimsToRun_NoneCrashes()
    {
        // A check that throws is reported as UA99 "could not be completed". An encrypted document
        // must not push any check down that path — undecryptable input is refused up front, never
        // fed in and left to blow up mid-check.
        foreach (EncryptionMode mode in new[]
                 {
                     EncryptionMode.AccessibilityPermitted,
                     EncryptionMode.AccessibilityDenied,
                     EncryptionMode.Revision6WithBitClear,
                     EncryptionMode.NonStandardHandler,
                     EncryptionMode.NoPermissionsEntry,
                 })
        {
            var issues = DeepCheck(BrokenStructurePdf(mode));
            Assert.Empty(CrashNotes(issues));

            var pageScoped = DeepCheck(OrphanedStructParentsKeyPdf(mode));
            Assert.Empty(CrashNotes(pageScoped));
        }
    }

    // =====================================================================
    //  An unencrypted document is audited exactly as before
    // =====================================================================

    [Fact]
    public void UnencryptedPdf_ReportsTheSameIssueSetAsBefore()
    {
        var issues = DeepCheck(BrokenStructurePdf(EncryptionMode.None));

        // The complete set of codes this fixture produces. UA06 and the encryption coverage note
        // are absent because neither has anything to say about a document with no /Encrypt entry.
        Assert.Equal(
            "UA13,UA14,UA16,UA17,UA18,UA21,UA30",
            string.Join(",", CodesOf(issues).OrderBy(c => c, StringComparer.Ordinal)));

        Assert.Equal(2, WithCode(issues, "UA16").Count);
        Assert.Empty(CoverageNotes(issues));
        Assert.Empty(CrashNotes(issues));
        Assert.DoesNotContain(issues, i => Mentions(i.Message, "encrypt"));
    }

    [Fact]
    public void UnencryptedPdf_WithOrphanedStructParentsKey_IsUnaffected()
    {
        var issues = DeepCheck(OrphanedStructParentsKeyPdf(EncryptionMode.None));

        var orphaned = Assert.Single(WithCode(issues, "UA14"));
        Assert.Equal(1, orphaned.PageNumber);
        Assert.Empty(CoverageNotes(issues));
    }

    // =====================================================================
    //  The combined report must not contradict itself
    // =====================================================================

    [Fact]
    public void CombinedAudit_OfAnEncryptedButAccessibleDocument_IsSelfConsistent()
    {
        AccessibilityAuditReport report = Audit(BrokenStructurePdf(EncryptionMode.AccessibilityPermitted));

        // Neither half claims the document cannot be checked …
        Assert.Empty(WithCode(report.Issues, "UA06"));
        Assert.DoesNotContain(report.Issues, i => Mentions(i.Message, "extended checks skipped"));

        // … while the deep half reports real findings …
        Assert.NotEmpty(WithCode(report.Issues, "UA16"));
        Assert.NotEmpty(WithCode(report.Issues, "UA30"));

        // … and the base half reports its own, over the same document.
        Assert.NotEmpty(report.Issues.Where(i => string.CompareOrdinal(i.Code, "UA01") >= 0
                                              && string.CompareOrdinal(i.Code, "UA09") <= 0));

        // Exactly one issue in the whole report speaks about encryption at all, and it is the Info
        // note that says which checks are missing — not an Error claiming nothing could be done.
        var mentioningEncryption = report.Issues.Where(i => Mentions(i.Message, "encrypt")).ToList();
        var single = Assert.Single(mentioningEncryption);
        Assert.Equal(IssueSeverity.Info, single.Severity);
        Assert.True(Mentions(single.Message, "was not attempted"), single.Message);
    }

    [Fact]
    public void CombinedAudit_OfAnEncryptedDocumentThatDeniesExtraction_AgreesWithItself()
    {
        // Both halves *produce* UA06 — the deep checker is a public API in its own right and cannot
        // rely on the plugin having run — and the combined report collapses the pair to one, so a
        // remediation UI is not shown two entries for a single permission bit. What matters here is
        // that the survivor does not contradict the rest: it must be about the extraction
        // permission, and it may not claim the audit was abandoned while the report plainly
        // contains its results.
        AccessibilityAuditReport report = Audit(BrokenStructurePdf(EncryptionMode.AccessibilityDenied));

        AccessibilityIssue ua06 = Assert.Single(WithCode(report.Issues, "UA06"));
        Assert.True(Mentions(ua06.Message, "extraction"),
            "UA06 must be about the extraction permission; got: " + ua06.Message);
        Assert.False(Mentions(ua06.Message, "checks skipped"),
            "UA06 may not claim the audit stopped; got: " + ua06.Message);

        Assert.NotEmpty(WithCode(report.Issues, "UA16"));
        Assert.NotEmpty(WithCode(report.Issues, "UA30"));
    }

    [Fact]
    public void CombinedAudit_KeepsTheDeepUa06AndDropsThePluginsCopy()
    {
        // The two halves apply the same /P bit 10 rule, so on this document each emits its own
        // UA06. The one kept is the deep checker's, and that is not arbitrary: only it carries the
        // WCAG criterion and a remediation naming the permission to grant when re-encrypting.
        // Collapsing the other way would silently coarsen the report.
        var deepOnly = WithCode(DeepCheck(BrokenStructurePdf(EncryptionMode.AccessibilityDenied)), "UA06");
        var pluginOnly = PdfUaChecker.Validate(new MemoryStream(BrokenStructurePdf(EncryptionMode.AccessibilityDenied)))
            .Violations.Where(v => string.Equals(v.Code, "UA06", StringComparison.Ordinal)).ToList();

        // The fixture reaches both halves: neither is silently absent, which would make the
        // collapse below untested rather than proven.
        Assert.Single(deepOnly);
        Assert.Single(pluginOnly);

        AccessibilityIssue survivor = Assert.Single(
            WithCode(Audit(BrokenStructurePdf(EncryptionMode.AccessibilityDenied)).Issues, "UA06"));

        Assert.Equal(WcagLevel.A, survivor.WcagLevel);
        Assert.Equal("1.3.1", survivor.WcagCriterion);
        Assert.True(Mentions(survivor.Remediation, "Grant the accessibility-extraction permission"),
            "the deep checker's remediation must survive, not the plugin's; got: " + survivor.Remediation);
    }

    [Fact]
    public void CombinedAudit_CollapsingUa06_LosesNoOtherPluginFinding()
    {
        // The collapse is a targeted removal from the plugin half, so every other code that half
        // reported has to reach the report untouched. The buffered-flush shape this replaced could
        // drop all of them at once, which is why the claim is asserted over the plugin's whole
        // output rather than over a hand-listed code or two.
        byte[] pdf = BrokenStructurePdf(EncryptionMode.AccessibilityDenied);

        var reported = CodesOf(Audit(pdf).Issues);
        var expected = PdfUaChecker.Validate(new MemoryStream(pdf)).Violations
            .Select(v => v.Code)
            // UA28/UA29/UA50 are the three the deep checker supersedes outright; UA06 is the
            // conditional case under test and is expected to survive exactly once, asserted above.
            .Where(c => c is not ("UA28" or "UA29" or "UA50"))
            .ToList();

        Assert.NotEmpty(expected);
        Assert.All(expected, code => Assert.Contains(code, reported));
    }

    [Fact]
    public void CombinedAudit_OfAnUnencryptedDocument_IsUnchangedByTheUa06Collapse()
    {
        // Neither half has anything to say about /P on a document with no /Encrypt entry, so the
        // conditional must not fire — and, more to the point, must not remove anything on the way
        // past. This is the case a blanket rule would look identical on, so it is here to pin that
        // the removal is scoped rather than merely dormant.
        AccessibilityAuditReport report = Audit(BrokenStructurePdf(EncryptionMode.None));

        Assert.Empty(WithCode(report.Issues, "UA06"));
        Assert.NotEmpty(WithCode(report.Issues, "UA16"));
        Assert.NotEmpty(WithCode(report.Issues, "UA30"));
        Assert.NotEmpty(report.Issues.Where(i => string.CompareOrdinal(i.Code, "UA01") >= 0
                                              && string.CompareOrdinal(i.Code, "UA09") <= 0));
    }
}
