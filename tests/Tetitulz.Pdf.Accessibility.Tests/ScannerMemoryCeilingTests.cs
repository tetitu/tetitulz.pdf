// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Accessibility.Checking;
using Tetitulz.Pdf.Accessibility.Internal;
using Tetitulz.Pdf.Accessibility.Models;
using Xunit;

namespace Tetitulz.Pdf.Accessibility.Tests;

/// <summary>
/// The resource ceilings that keep a hostile upload from choosing how much memory this library
/// allocates.
///
/// <para>
/// <b>What went wrong.</b> <see cref="ContentStreamScanner"/> pre-sized its token list at
/// <c>content.Length / 12</c> with no cap and no hard token ceiling, so a 512 MiB content stream —
/// which Core will happily inflate from a few kilobytes of Flate — asked for roughly 1.4 GiB on the
/// Large Object Heap <i>before the first token was parsed</i>. A ceiling did exist, but it lived in
/// <c>ContentStreamTagger</c> (<c>PageTagOptions.MaxContentBytes</c>), so it protected the tagging
/// path and nothing else: <see cref="PdfDeepAccessibilityChecker"/> and
/// <c>PdfReadingOrderAnalyzer</c> both call <c>ScanDetailed</c> directly and were fully exposed. A
/// guard that one caller can forget is not a guard, so the ceilings now live at the allocation.
/// </para>
///
/// <para>
/// <b>What these tests must prove</b>, rather than merely survive: that an over-ceiling stream is
/// <i>refused</i> — no token list allocated at all, a reason that names the limit, and
/// <see cref="ContentScanResult.LimitExceeded"/> set — and never silently truncated into a partial
/// token list a caller would mistake for a complete scan. The fixtures stay small by lowering the
/// ceiling through the parameter instead of allocating half a gigabyte in a unit test; the one
/// place scale itself is the point (the token bomb) uses millions of real operators inside the
/// default byte ceiling.
/// </para>
///
/// <para>Everything here is built in memory; nothing is read from disk.</para>
/// </summary>
public class ScannerMemoryCeilingTests
{
    // =====================================================================
    //  Helpers
    // =====================================================================

    private static byte[] Ascii(string s) => Encoding.ASCII.GetBytes(s);

    /// <summary>
    /// <paramref name="unit"/> repeated until the result is exactly <paramref name="totalBytes"/>
    /// long. Built as a <c>byte[]</c> rather than a string so a multi-megabyte fixture costs one
    /// allocation of exactly the size the test is reasoning about.
    /// </summary>
    private static byte[] Repeat(string unit, int totalBytes)
    {
        byte[] pattern = Ascii(unit);
        var buffer = new byte[totalBytes];
        for (int i = 0; i < totalBytes; i++) buffer[i] = pattern[i % pattern.Length];
        return buffer;
    }

    private static string[] Ops(IReadOnlyList<ContentToken> tokens)
    {
        var ops = new string[tokens.Count];
        for (int i = 0; i < tokens.Count; i++) ops[i] = tokens[i].Operator;
        return ops;
    }

    /// <summary>A perfectly ordinary page: one text object inside one marked-content sequence.</summary>
    private const string NormalContent =
        "/P <</MCID 0>> BDC\nBT /F1 12 Tf 72 700 Td (Hello) Tj ET\nEMC\n";

    // =====================================================================
    //  Byte ceiling
    // =====================================================================

    /// <summary>
    /// One byte over the ceiling is refused, and the refusal is <i>free</i>: no token list is
    /// allocated (the result carries the shared empty list by reference), no pass is made over the
    /// bytes, and the reason names both the limit and the actual size so a host can tell a
    /// too-large document from a corrupt one.
    /// </summary>
    [Fact]
    public void StreamJustOverTheByteCeiling_IsRefusedWithoutAllocatingATokenList()
    {
        const int ceiling = 4096;
        byte[] content = Repeat("q Q ", ceiling + 1);

        var clock = Stopwatch.StartNew();
        ContentScanResult result = ContentStreamScanner.ScanDetailed(content, ceiling, ContentStreamScanner.DefaultMaxTokens);
        clock.Stop();

        Assert.False(result.IsSafe);
        Assert.True(result.LimitExceeded,
            "An over-size refusal must be distinguishable from a malformed stream. Error: " + result.Error);

        // The point of the fix: the refusal happens before anything proportional to the input is
        // allocated. Reference equality with the shared empty list is what proves no List was built.
        Assert.Same(ContentStreamScanner.EmptyTokens, result.Tokens);
        Assert.Empty(result.Tokens);

        // The reason must name the limit and the size, not apologise generically.
        Assert.Contains(ceiling.ToString(CultureInfo.InvariantCulture), result.Error, StringComparison.Ordinal);
        Assert.Contains((ceiling + 1).ToString(CultureInfo.InvariantCulture), result.Error, StringComparison.Ordinal);
        Assert.Contains("refused", result.Error, StringComparison.Ordinal);

        // And it must be reported, not only returned.
        Assert.Equal(result.Error, ContentStreamScanner.LastScanError);

        // Deliberately generous: the refusal is O(1), so any bound at all distinguishes it from a
        // scan. A tight bound would only make the test flaky on a loaded CI agent.
        // Hang-catcher, not a performance bound — a tight ceiling turns CPU contention during a
        // parallel run into a false regression. See GAPS.md §8a.
        Assert.True(clock.ElapsedMilliseconds < 120_000,
            "Refusing an over-size stream must not scan it. Took " + clock.ElapsedMilliseconds + " ms.");
    }

    /// <summary>
    /// The boundary is <b>inclusive</b>: a stream of exactly the ceiling is analysed normally. A
    /// ceiling that refused its own value would be a silently different limit from the documented
    /// one, and every caller's constant would be off by one.
    /// </summary>
    [Fact]
    public void StreamExactlyAtTheByteCeiling_IsStillAnalysed()
    {
        const int ceiling = 4096;
        byte[] content = Repeat("q Q ", ceiling);

        ContentScanResult result = ContentStreamScanner.ScanDetailed(content, ceiling, ContentStreamScanner.DefaultMaxTokens);

        Assert.True(result.IsSafe, "Error: " + result.Error);
        Assert.False(result.LimitExceeded);
        Assert.Equal(ceiling / 2, result.Tokens.Count);   // "q Q " → two operators per four bytes
        Assert.All(Ops(result.Tokens), op => Assert.True(op == "q" || op == "Q", "Unexpected operator " + op));
    }

    /// <summary>
    /// A ceiling of zero or less means "the caller has bounded this itself", which must genuinely
    /// disable the check rather than refuse everything.
    /// </summary>
    [Fact]
    public void ByteCeilingOfZero_DisablesTheCheck()
    {
        byte[] content = Repeat("q Q ", 8192);

        ContentScanResult result = ContentStreamScanner.ScanDetailed(content, 0, 0);

        Assert.True(result.IsSafe, "Error: " + result.Error);
        Assert.False(result.LimitExceeded);
        Assert.Equal(4096, result.Tokens.Count);
    }

    // =====================================================================
    //  Token ceiling
    // =====================================================================

    /// <summary>
    /// The token bomb the byte ceiling alone cannot stop: 3 million two-byte operators occupy 6 MB,
    /// comfortably inside the default 32 MiB byte ceiling, while asking for 3 million
    /// <c>ContentToken</c>s — around 72 MB of list, and linear in a stream an attacker can make
    /// sixteen times larger still. The token ceiling must fire, must stop where it says it stops,
    /// and must refuse rather than hand back the millions of tokens it did build.
    /// </summary>
    [Fact]
    public void TokenBombWithinTheByteCeiling_HitsTheTokenCeilingAndReportsIt()
    {
        const int tokenCeiling = 1_000_000;
        byte[] content = Repeat("q ", 6 * 1024 * 1024);   // 3,145,728 operators

        Assert.True(content.Length <= ContentStreamScanner.DefaultMaxScanBytes,
            "The fixture must be inside the byte ceiling, or it would prove the wrong guard.");

        ContentScanResult result = ContentStreamScanner.ScanDetailed(
            content, ContentStreamScanner.DefaultMaxScanBytes, tokenCeiling);

        Assert.False(result.IsSafe);
        Assert.True(result.LimitExceeded, "Error: " + result.Error);
        Assert.Contains(tokenCeiling.ToString(CultureInfo.InvariantCulture), result.Error, StringComparison.Ordinal);
        Assert.Contains("token", result.Error, StringComparison.Ordinal);

        // Refused, not truncated: a caller cannot tell a partial token list from a complete one, so
        // it would count, measure or splice against a stream it saw a third of.
        Assert.Same(ContentStreamScanner.EmptyTokens, result.Tokens);
    }

    /// <summary>
    /// The same guard at a size a test can assert exactly: the scan stops at the ceiling, not one
    /// operator past it and not at the end of the stream.
    /// </summary>
    [Fact]
    public void TokenCeiling_StopsAtTheStatedNumber()
    {
        byte[] content = Repeat("q ", 4000);   // 2,000 operators

        ContentScanResult result = ContentStreamScanner.ScanDetailed(content, 0, 100);

        Assert.False(result.IsSafe);
        Assert.True(result.LimitExceeded);
        Assert.Contains("100 operator tokens", result.Error, StringComparison.Ordinal);
        Assert.Contains("100-token", result.Error, StringComparison.Ordinal);
    }

    /// <summary>
    /// The hole the token ceiling would otherwise have. A whitespace-free run of operators
    /// (<c>"qqqq…q"</c>) is a <i>single</i> keyword, so it is decomposed into one sub-operator per
    /// byte inside one iteration of the scan loop — millions of tokens added between two tests of a
    /// per-iteration ceiling. Bounding the keyword is what makes the token ceiling hard, so it has
    /// its own test.
    /// </summary>
    [Fact]
    public void WhitespaceFreeOperatorRun_IsRefusedRatherThanDecomposedIntoATokenPerByte()
    {
        byte[] content = Repeat("q", 100_000);

        ContentScanResult result = ContentStreamScanner.ScanDetailed(content);

        Assert.False(result.IsSafe);
        Assert.True(result.LimitExceeded, "Error: " + result.Error);
        Assert.Contains("256", result.Error, StringComparison.Ordinal);
        Assert.Same(ContentStreamScanner.EmptyTokens, result.Tokens);
    }

    /// <summary>
    /// Operands are held until an operator consumes them, so a stream that never reaches one
    /// retains an object per operand — the token bomb wearing different clothes, and invisible to a
    /// ceiling that counts only tokens.
    /// </summary>
    [Fact]
    public void OperandFloodWithNoOperator_IsRefused()
    {
        byte[] content = Repeat("1 ", 100_000);   // 50,000 numbers, no operator, nothing released

        ContentScanResult result = ContentStreamScanner.ScanDetailed(content);

        Assert.False(result.IsSafe);
        Assert.True(result.LimitExceeded, "Error: " + result.Error);
        Assert.Contains("operands", result.Error, StringComparison.Ordinal);
    }

    /// <summary>
    /// A <i>malformed</i> stream must keep the contract it always had — partial tokens, no
    /// <see cref="ContentScanResult.LimitExceeded"/> — because callers distinguish the two: a
    /// document defect is repaired, an over-ceiling document is re-run with a higher ceiling.
    /// </summary>
    [Fact]
    public void MalformedStream_IsNotReportedAsACeilingRefusal()
    {
        ContentScanResult result = ContentStreamScanner.ScanDetailed(Ascii("q Q >\n"));

        Assert.False(result.IsSafe);
        Assert.False(result.LimitExceeded);
        Assert.Contains("Stray '>'", result.Error, StringComparison.Ordinal);
        Assert.NotEmpty(result.Tokens);            // the two operators before the stray delimiter
    }

    // =====================================================================
    //  Negative control: the ordinary path is untouched
    // =====================================================================

    /// <summary>
    /// The control that makes every test above meaningful: a normal page scans to exactly the same
    /// tokens, at exactly the same byte offsets, with the ceilings in force as without them. A guard
    /// that changed ordinary output would be a regression dressed as a fix.
    /// </summary>
    [Fact]
    public void OrdinaryStream_ScansIdenticallyWithAndWithoutTheCeilings()
    {
        byte[] content = Ascii(NormalContent);

        ContentScanResult guarded   = ContentStreamScanner.ScanDetailed(content);
        ContentScanResult unguarded = ContentStreamScanner.ScanDetailed(content, 0, 0);

        Assert.True(guarded.IsSafe, "Error: " + guarded.Error);
        Assert.False(guarded.LimitExceeded);
        Assert.Equal(Ops(unguarded.Tokens), Ops(guarded.Tokens));
        Assert.Equal(unguarded.Tokens.Count, guarded.Tokens.Count);

        for (int i = 0; i < guarded.Tokens.Count; i++)
        {
            Assert.Equal(unguarded.Tokens[i].StartOffset, guarded.Tokens[i].StartOffset);
            Assert.Equal(unguarded.Tokens[i].EndOffset,   guarded.Tokens[i].EndOffset);
        }

        Assert.Equal(new[] { "BDC", "BT", "Tf", "Td", "Tj", "ET", "EMC" }, Ops(guarded.Tokens));
        Assert.True(guarded.HasMarkedContent);
    }

    // =====================================================================
    //  The audit path (defect 2): the same ceiling, applied one layer up
    // =====================================================================

    private static string StreamObject(string content)
        => "<< /Length " + content.Length.ToString(CultureInfo.InvariantCulture)
           + " >>\nstream\n" + content + "endstream";

    private static byte[] BuildPdf(IReadOnlyList<string> objectBodies)
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.7\n");

        var offsets = new List<int>(objectBodies.Count);
        for (int i = 0; i < objectBodies.Count; i++)
        {
            offsets.Add(sb.Length);
            sb.Append((i + 1).ToString(CultureInfo.InvariantCulture))
              .Append(" 0 obj\n").Append(objectBodies[i]).Append("\nendobj\n");
        }

        int xref = sb.Length;
        int size = objectBodies.Count + 1;
        sb.Append("xref\n0 ").Append(size.ToString(CultureInfo.InvariantCulture))
          .Append("\n0000000000 65535 f \n");
        foreach (int offset in offsets)
            sb.AppendFormat(CultureInfo.InvariantCulture, "{0:D10} 00000 n \n", offset);
        sb.Append("trailer\n<< /Size ").Append(size.ToString(CultureInfo.InvariantCulture))
          .Append(" /Root 1 0 R >>\n");
        sb.AppendFormat(CultureInfo.InvariantCulture, "startxref\n{0}\n%%EOF\n", xref);

        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    /// <summary>
    /// Two pages that differ only in the size of their content stream, and one non-embedded font
    /// used by both — so the audit has a document-level finding (UA21) that owes nothing to page
    /// content and can be asserted independently of which pages were read.
    /// </summary>
    private static byte[] TwoPagePdf(string firstPageContent, string secondPageContent) => BuildPdf(new List<string>
    {
        // 1 — catalog
        "<< /Type /Catalog /Pages 2 0 R /MarkInfo << /Marked true >> /Lang (en-US) "
        + "/ViewerPreferences << /DisplayDocTitle true >> >>",

        // 2 — page tree
        "<< /Type /Pages /Kids [3 0 R 5 0 R] /Count 2 >>",

        // 3 — page 1
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] "
        + "/Resources << /Font << /F1 7 0 R >> >> /Contents 4 0 R >>",

        // 4 — page 1 content
        StreamObject(firstPageContent),

        // 5 — page 2
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] "
        + "/Resources << /Font << /F1 7 0 R >> >> /Contents 6 0 R >>",

        // 6 — page 2 content
        StreamObject(secondPageContent),

        // 7 — font, deliberately not embedded
        "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
    });

    /// <summary>A page's worth of ordinary text, sized to the caller's taste.</summary>
    private static string TextContent(int lines)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < lines; i++)
        {
            sb.Append("BT /F1 12 Tf 72 ")
              .Append((700 - (i % 40) * 16).ToString(CultureInfo.InvariantCulture))
              .Append(" Td (Line of text) Tj ET\n");
        }
        return sb.ToString();
    }

    /// <summary>The coverage notice, isolated from UA99's other uses by its message.</summary>
    private static IReadOnlyList<AccessibilityIssue> CoverageGaps(IEnumerable<AccessibilityIssue> issues)
        => issues.Where(i =>
                string.Equals(i.Code, "UA99", StringComparison.Ordinal) &&
                i.Message.IndexOf("content stream could not be read", StringComparison.Ordinal) >= 0)
           .ToList();

    private static string Describe(IEnumerable<AccessibilityIssue> issues)
        => string.Join(" | ", issues.Select(i => i.ToString()));

    /// <summary>
    /// Defect 2. <c>Audit</c> had no equivalent of the tagger's per-page ceiling, so one hostile
    /// page decided how much memory and CPU the whole audit spent. Now the page is refused — and,
    /// because refusing silently would be its own defect, the report names the page and the limit
    /// while the rest of the document is still audited in full.
    /// </summary>
    [Fact]
    public void Audit_OversizedPage_IsNamedInTheReportAndTheRestOfTheDocumentIsStillChecked()
    {
        const int pageCeiling = 1024;
        byte[] pdf = TwoPagePdf(TextContent(200), TextContent(1));   // ~6.6 KB vs ~34 bytes

        IReadOnlyList<AccessibilityIssue> issues = PdfDeepAccessibilityChecker.Check(
            new MemoryStream(pdf), pageCeiling, PdfDeepAccessibilityChecker.DefaultMaxDocumentContentBytes);

        AccessibilityIssue gap = Assert.Single(CoverageGaps(issues));

        Assert.Equal(IssueSeverity.Info, gap.Severity);      // not established ≠ wrong
        Assert.Equal(1, gap.PageNumber);                     // the oversized page, named
        Assert.Equal(3, gap.ObjectNumber);                   // its page object
        Assert.Contains(pageCeiling.ToString(CultureInfo.InvariantCulture), gap.Message, StringComparison.Ordinal);
        Assert.Contains("did not run for this page", gap.Message, StringComparison.Ordinal);
        Assert.Contains("UA17 (text drawn outside every marked-content sequence)",
            gap.Message, StringComparison.Ordinal);

        // The rest of the file is audited exactly as before: UA21 is a dictionary-level fact about
        // the font, so refusing a page must not cost it.
        Assert.Contains(issues, i => string.Equals(i.Code, "UA21", StringComparison.Ordinal));

        // …and the page that fits is not swept up in the refusal.
        Assert.DoesNotContain(CoverageGaps(issues), i => i.PageNumber == 2);
    }

    /// <summary>
    /// The document-wide budget, which is what stops "one oversized page" becoming "a thousand
    /// pages just under the per-page ceiling". Once it is spent the remaining pages lose their
    /// content-level checks and say so; nothing is dropped in silence.
    /// </summary>
    [Fact]
    public void Audit_DocumentContentBudgetExhausted_IsReportedForTheRemainingPages()
    {
        // The budget is exactly one page's worth of content, and the content is stored uncompressed,
        // so page 1 fits precisely and spends it to zero — page 2 is then never decoded at all.
        string page = TextContent(10);
        byte[] pdf = TwoPagePdf(page, page);

        IReadOnlyList<AccessibilityIssue> issues = PdfDeepAccessibilityChecker.Check(
            new MemoryStream(pdf), PdfDeepAccessibilityChecker.DefaultMaxPageContentBytes, page.Length);

        AccessibilityIssue gap = Assert.Single(CoverageGaps(issues));

        Assert.Equal(IssueSeverity.Info, gap.Severity);
        Assert.Equal(2, gap.PageNumber);            // page 1 fitted; page 2 is the one that was cut
        Assert.Contains("already spent", gap.Message, StringComparison.Ordinal);
        Assert.Contains(page.Length.ToString(CultureInfo.InvariantCulture), gap.Message, StringComparison.Ordinal);
        Assert.Contains("did not run for this page", gap.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The negative control for the audit path: an ordinary small document must produce exactly the
    /// same report under the default ceilings as under none at all — same issues, same order, same
    /// text — and no coverage notice. The ceilings exist to bound a hostile file, not to change what
    /// a normal one says.
    /// </summary>
    [Fact]
    public void Audit_OrdinaryDocument_IsUnaffectedByTheCeilings()
    {
        byte[] pdf = TwoPagePdf(TextContent(3), TextContent(3));

        IReadOnlyList<AccessibilityIssue> withDefaults = PdfDeepAccessibilityChecker.Check(new MemoryStream(pdf));
        IReadOnlyList<AccessibilityIssue> withNoLimits = PdfDeepAccessibilityChecker.Check(new MemoryStream(pdf), 0, 0);

        Assert.True(CoverageGaps(withDefaults).Count == 0,
            "A small document must produce no coverage notice. Issues: " + Describe(withDefaults));

        Assert.Equal(withNoLimits.Count, withDefaults.Count);
        for (int i = 0; i < withDefaults.Count; i++)
        {
            Assert.Equal(withNoLimits[i].Code,     withDefaults[i].Code);
            Assert.Equal(withNoLimits[i].Severity, withDefaults[i].Severity);
            Assert.Equal(withNoLimits[i].Message,  withDefaults[i].Message);
        }
    }
}
