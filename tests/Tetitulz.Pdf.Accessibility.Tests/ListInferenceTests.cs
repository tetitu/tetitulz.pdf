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
using Tetitulz.Pdf.Accessibility.Models;
using Tetitulz.Pdf.Accessibility.Tagging;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Accessibility.Tests;

/// <summary>
/// <c>AutoTagOptions.InferLists</c> — a run of consecutive marked lines sharing a left edge is
/// tagged <c>/L</c> → <c>/LI</c> → <c>/LBody</c> instead of a run of <c>/P</c>.
/// </summary>
/// <remarks>
/// <para><b>Two structural firsts.</b> The tagger's vocabulary was <c>P</c>, <c>H1</c>–<c>H3</c>
/// and artifacts, and every element it built was a <i>direct child of <c>/Document</c></i> — so no
/// role needing a container could be emitted however well it was detected. Both halves changed:
/// the classifier can now answer <c>LBody</c>, and a <c>LogicalElement</c> can have a parent.</para>
///
/// <para><b>The risk this feature carries is over-detection, so most of these tests are
/// refusals.</b> A wrongly-inferred list puts an <c>/L</c> into the tree that a screen reader
/// announces as a list of the wrong length — and this library's own deep checker then reports the
/// shape, which is remediation manufacturing the violation it exists to remove. The acceptance
/// cases are few and the rejection cases are many, deliberately.</para>
///
/// <para>Assertions walk the real structure tree. <c>"/S /L"</c> is a prefix of <c>"/S /LI"</c>, so
/// a substring search for the list is satisfied by the items it is supposed to be the parent
/// of.</para>
/// </remarks>
public sealed class ListInferenceTests
{
    /// <summary>Three marked lines become one list of three items.</summary>
    /// <remarks>
    /// <b>The marker is a hyphen rather than a bullet, and that is forced.</b> See
    /// <see cref="ASingleByteEncodedBulletIsNotDetectedYet"/>: the character a real
    /// <c>/WinAnsiEncoding</c> bullet arrives as is not the one it was written as, so a fixture
    /// using U+2022 would exercise a path no ordinary document reaches.
    /// </remarks>
    [Fact]
    public void ABulletedRunBecomesAListOfItems()
    {
        Tree tree = Tag(Lines(("- Alpha", 72), ("- Beta", 72), ("- Gamma", 72)), infer: true);

        Assert.Equal(1, tree.Count("L"));
        Assert.Equal(3, tree.Count("LI"));
        Assert.Equal(3, tree.Count("LBody"));
        Assert.Equal(0, tree.Count("P"));
    }

    /// <summary>The same page without the option is a run of paragraphs.</summary>
    /// <remarks>
    /// <b>The acceptance control for the whole file, and for the default.</b> Existing callers must
    /// see exactly what they saw before, so the opt-in has to be the only thing that changes the
    /// answer — not the presence of bullets on the page.
    /// </remarks>
    [Fact]
    public void TheSamePageWithoutTheOptionIsStillParagraphs()
    {
        Tree tree = Tag(Lines(("- Alpha", 72), ("- Beta", 72), ("- Gamma", 72)), infer: false);

        Assert.Equal(0, tree.Count("L"));
        Assert.Equal(3, tree.Count("P"));
    }

    /// <summary>The items are children of the list, and each body is inside its own item.</summary>
    /// <remarks>
    /// <b>Counting roles cannot see this, and it is the whole point of the structural change.</b> A
    /// tree with one <c>/L</c> and three <c>/LI</c> hanging off <c>/Document</c> beside it has the
    /// same census as a correct one — and is exactly what the tagger produced before an element
    /// could have a parent. §7.6 is about the containment.
    /// </remarks>
    [Fact]
    public void TheListContainsItsItemsAndEachItemItsBody()
    {
        Tree tree = Tag(Lines(("- Alpha", 72), ("- Beta", 72)), infer: true);

        Assert.Equal(new[] { "LI", "LI" }, tree.ChildRolesOfFirst("L"));
        Assert.Equal(new[] { "LBody" }, tree.ChildRolesOfFirst("LI"));

        // And nothing is reachable twice: an element listed under /Document as well as under its
        // parent has two parents, and its /P agrees with only one of them.
        Assert.Equal(1, tree.RootRoles.Count(r => r == "L"));
        Assert.DoesNotContain("LI", tree.RootRoles);
        Assert.DoesNotContain("LBody", tree.RootRoles);
    }

    /// <summary>Numbered and lettered runs are lists too.</summary>
    [Theory]
    [InlineData("1. Alpha", "2. Beta", "3. Gamma")]
    [InlineData("1) Alpha", "2) Beta", "3) Gamma")]
    [InlineData("a. Alpha", "b. Beta", "c. Gamma")]
    [InlineData("i. Alpha", "ii. Beta", "iii. Gamma")]
    [InlineData("- Alpha", "- Beta", "- Gamma")]
    public void AnOrdinalRunIsAListToo(string a, string b, string c)
    {
        Tree tree = Tag(Lines((a, 72), (b, 72), (c, 72)), infer: true);

        Assert.Equal(1, tree.Count("L"));
        Assert.Equal(3, tree.Count("LI"));
    }

    /// <summary>A single marked line is not a list.</summary>
    /// <remarks>
    /// A sentence beginning "1." is far more often a sentence than a list of one, and PDF/UA is
    /// satisfied by a paragraph either way — so the cost of being wrong is all on one side.
    /// </remarks>
    [Fact]
    public void ASingleMarkedLineIsNotAList()
    {
        Tree tree = Tag(Lines(("1. Alone", 72), ("Ordinary prose follows", 72)), infer: true);

        Assert.Equal(0, tree.Count("L"));
        Assert.Equal(2, tree.Count("P"));
    }

    /// <summary>Ordinary prose is never a list, however it is laid out.</summary>
    /// <remarks>
    /// The case a marker-only rule gets wrong: these lines share a left edge and read like a
    /// paragraph, and none of them begins with anything a list uses.
    /// </remarks>
    [Fact]
    public void OrdinaryProseIsNotAList()
    {
        Tree tree = Tag(Lines(
            ("The first line of a paragraph", 72),
            ("continues onto a second line", 72),
            ("and finishes on a third.", 72)), infer: true);

        Assert.Equal(0, tree.Count("L"));
        Assert.Equal(3, tree.Count("P"));
    }

    /// <summary>Markers at different left edges are not one run.</summary>
    /// <remarks>
    /// <b>This is what the horizontal signal is for.</b> Two lines that both begin with a bullet
    /// but start 40 points apart are not consecutive items of one list — they are a list and its
    /// sublist, or two lists, or a bullet in prose. Without an X the tagger could not tell, and a
    /// marker-only rule joins them.
    /// </remarks>
    [Fact]
    public void MarkersAtDifferentLeftEdgesAreNotOneRun()
    {
        Tree tree = Tag(Lines(("- Alpha", 72), ("- Beta", 144)), infer: true);

        Assert.Equal(0, tree.Count("L"));
    }

    /// <summary>A decimal number does not begin a list item.</summary>
    /// <remarks>
    /// "3.14 is pi" opens with a digit and a dot, and a rule that stopped there would tag a page of
    /// measurements as a list. The whitespace after the punctuation is what rejects it.
    /// </remarks>
    [Fact]
    public void ADecimalNumberDoesNotBeginAnItem()
    {
        Tree tree = Tag(Lines(("3.14 is pi", 72), ("2.71 is e", 72)), infer: true);

        Assert.Equal(0, tree.Count("L"));
        Assert.Equal(2, tree.Count("P"));
    }

    /// <summary>Prose between two runs splits them into two lists.</summary>
    /// <remarks>
    /// The run is what defines a list, so an interruption ends one. Merging across it would produce
    /// a single <c>/L</c> whose announced length is the sum of two — the over-detection this whole
    /// design is shaped against.
    /// </remarks>
    [Fact]
    public void ProseBetweenTwoRunsSplitsThemIntoTwoLists()
    {
        Tree tree = Tag(Lines(
            ("- Alpha", 72), ("- Beta", 72),
            ("An intervening paragraph", 72),
            ("- Gamma", 72), ("- Delta", 72)), infer: true);

        Assert.Equal(2, tree.Count("L"));
        Assert.Equal(4, tree.Count("LI"));
        Assert.Equal(1, tree.Count("P"));
    }

    /// <summary>The list takes its items' place, so reading order is unchanged.</summary>
    /// <remarks>
    /// A container appended after the paragraphs instead of inserted where its items were would
    /// still produce a valid tree — and would read the page in the wrong order, which is the one
    /// thing a structure tree exists to get right.
    /// </remarks>
    [Fact]
    public void TheListSitsWhereItsItemsWere()
    {
        Tree tree = Tag(Lines(
            ("Before the list", 72),
            ("- Alpha", 72), ("- Beta", 72),
            ("After the list", 72)), infer: true);

        Assert.Equal(new[] { "P", "L", "P" }, tree.RootRoles);
    }

    /// <summary>
    /// A bullet written as a single encoded byte is not detected — a known limit, pinned.
    /// </summary>
    /// <remarks>
    /// <para><b>What the detector reads is not what the producer wrote.</b> The tagger's line text
    /// comes from <c>PdfString.GetText()</c>, which decodes a show operand through PDFDocEncoding
    /// and never consults the font's own <c>/Encoding</c> — documented as best-effort, because
    /// nothing about the tagging depends on it. A <c>/WinAnsiEncoding</c> bullet is byte
    /// <c>0x95</c>, and PDFDocEncoding reads that byte as U+0141, the Polish <c>\u0141</c>.</para>
    ///
    /// <para><b>Matching U+0141 would be guessing at an encoding</b> — it is a real letter, and the
    /// same byte means something different again under MacRomanEncoding — so the detector refuses,
    /// and the most common way of writing a bullet goes undetected. Closing it means decoding the
    /// show string through the font, which is a change to what the tagger <i>records</i>.</para>
    ///
    /// <para>This test exists so that stays a known limit rather than becoming a surprise: it fails
    /// the day the decode improves, which is exactly when someone should come back and delete
    /// it.</para>
    /// </remarks>
    [Fact]
    public void ASingleByteEncodedBulletIsNotDetectedYet()
    {
        // 0x95 is the WinAnsi bullet; the fixture writes the byte, as a producer does.
        Tree tree = Tag(Lines(("\u0095 Alpha", 72), ("\u0095 Beta", 72)), infer: true);

        Assert.Equal(0, tree.Count("L"));
        Assert.Equal(2, tree.Count("P"));
    }

    /// <summary>A bullet that survives the decode is detected.</summary>
    /// <remarks>
    /// The other side of the limit above, and what keeps the bullet table from being dead code: a
    /// U+2022 that reaches the detector as U+2022 — which happens whenever the show string is
    /// written in a two-byte encoding — is an item.
    /// </remarks>
    [Fact]
    public void ABulletThatSurvivesTheDecodeIsDetected()
    {
        Assert.True(Marker("\u2022 Alpha"));
        Assert.False(Marker("\u0141 Alpha"));
    }

    /// <summary>A marker is only a marker when something separates it from the word.</summary>
    /// <remarks>
    /// <b>Without the space rule the first letter of a word is a bullet.</b> "-ish", "*args" and a
    /// hyphenated compound at the start of a line all open with a character the table holds, and a
    /// page of them would be read as a list. The rule is the same one that keeps "3.14" out: what
    /// makes a marker is that it stands alone.
    /// </remarks>
    [Theory]
    [InlineData("- Alpha", true)]
    [InlineData("-Alpha", false)]
    [InlineData("\u2022Alpha", false)]
    [InlineData("*Alpha", false)]
    [InlineData("* Alpha", true)]
    public void AMarkerMustBeSeparatedFromTheTextItMarks(string line, bool expected)
        => Assert.Equal(expected, Marker(line));

    private static bool Marker(string line)
        => Tetitulz.Pdf.Accessibility.Internal.ListLineDetector.HasMarker(line);

    /// <summary>Each nested element names its own parent, not <c>/Document</c>.</summary>
    /// <remarks>
    /// <b>Containment and parentage are two claims and a tree can get one right while the other is
    /// wrong.</b> <c>/K</c> says what an element contains; <c>/P</c> says what contains it, and
    /// §14.7.2 requires them to agree. A tree whose <c>/LI</c> is reachable only through its
    /// <c>/L</c> but whose <c>/P</c> points at <c>/Document</c> passes every containment assertion
    /// in this file and is malformed — so the parent pointers are read back separately.
    /// </remarks>
    [Fact]
    public void EachNestedElementNamesItsRealParent()
    {
        using var input = new MemoryStream(Page(Lines(("- Alpha", 72), ("- Beta", 72))));
        using var output = new MemoryStream();

        PdfAutoTagger.AutoTag(input, output, new AutoTagOptions { InferLists = true });

        using var ms = new MemoryStream(output.ToArray());
        using PdfDocument doc = PdfDocument.Open(ms);

        var root = doc.ResolveObject(doc.Catalog.Dictionary.Get(PdfName.StructTreeRoot)) as PdfDictionary;
        Assert.True(root != null, "no structure tree was written");

        var byNumber = new Dictionary<int, PdfDictionary>();
        Collect(doc, root!, byNumber, 0);

        int checkedPairs = 0;
        foreach (KeyValuePair<int, PdfDictionary> entry in byNumber)
        {
            string role = (doc.ResolveObject(entry.Value.Get(PdfName.S)) as PdfName)?.Value;
            if (role != "LI" && role != "LBody") continue;

            var parent = entry.Value.Get(PdfName.P) as PdfReference;
            Assert.True(parent != null, $"/{role} has no /P");

            string parentRole = byNumber.TryGetValue(parent!.ReferenceObjectNumber, out PdfDictionary p)
                ? (doc.ResolveObject(p.Get(PdfName.S)) as PdfName)?.Value
                : "(not in the tree)";

            Assert.Equal(role == "LI" ? "L" : "LI", parentRole);
            checkedPairs++;
        }

        Assert.Equal(4, checkedPairs);   // two LI and two LBody
    }

    /// <summary>Every structure element in the tree, by object number.</summary>
    /// <remarks>
    /// <b><c>/K</c> is read raw and each kid resolved separately.</b> Resolving <c>/K</c> itself
    /// first collapses the single-child case: an element with one kid writes <c>/K</c> as a bare
    /// reference, so the resolve hands back the child <i>dictionary</i> and a test looking for a
    /// <see cref="PdfReference"/> walks past every such element — which is exactly the
    /// <c>/LI</c> → <c>/LBody</c> edge this is here to check.
    /// </remarks>
    private static void Collect(
        PdfDocument doc, PdfDictionary node, Dictionary<int, PdfDictionary> into, int depth)
    {
        if (depth > 40) return;

        PdfObject k = node.Get(PdfName.K);
        var kids = doc.ResolveObject(k) is PdfArray array
            ? array.ToList()
            : k == null ? new List<PdfObject>() : new List<PdfObject> { k };

        foreach (PdfObject kid in kids)
        {
            if (doc.ResolveObject(kid) is not PdfDictionary child) continue;
            if (child.Get(PdfName.S) == null) continue;

            int number = kid is PdfReference reference
                ? reference.ReferenceObjectNumber
                : child.ObjectNumber;

            into[number] = child;
            Collect(doc, child, into, depth + 1);
        }
    }

    /// <summary>A run is not carried across a page boundary.</summary>
    /// <remarks>
    /// One <c>/L</c> spanning two pages is legal and is not something a heuristic reading each page
    /// independently has evidence for: the items after the break may continue the list or start a
    /// new one, and the tree must not claim to know which. Two lists is the honest answer, and the
    /// only fixture that can tell it from one is a two-page document.
    /// </remarks>
    [Fact]
    public void ARunIsNotCarriedAcrossAPageBoundary()
    {
        using var input = new MemoryStream(TwoPages());
        using var output = new MemoryStream();

        AutoTagResult result = PdfAutoTagger.AutoTag(
            input, output, new AutoTagOptions { InferLists = true });

        Assert.True(result.ContentMarked, "the tagger marked nothing");
        Assert.Equal(2, result.ListsInferred);
        Assert.Equal(2, new Tree(output.ToArray()).Count("L"));
    }

    /// <summary>The result reports how many lists it inferred.</summary>
    /// <remarks>
    /// The number a caller checks before trusting the option on their corpus: a run that is
    /// inferred is one a screen reader will announce as a list, and comparing the count against the
    /// document is the only way to see the setting doing what it should.
    /// </remarks>
    [Fact]
    public void TheResultReportsTheNumberOfListsInferred()
    {
        Assert.Equal(1, Result(Lines(("- Alpha", 72), ("- Beta", 72)), infer: true).ListsInferred);
        Assert.Equal(0, Result(Lines(("- Alpha", 72), ("- Beta", 72)), infer: false).ListsInferred);
    }

    /// <summary>The default is off, so no existing caller's output changes.</summary>
    [Fact]
    public void TheDefaultIsOff() => Assert.False(new AutoTagOptions().InferLists);

    // ==================================================================== helpers

    private static AutoTagResult Result((string Text, float X)[] lines, bool infer)
    {
        using var input = new MemoryStream(Page(lines));
        using var output = new MemoryStream();

        return PdfAutoTagger.AutoTag(input, output, new AutoTagOptions { InferLists = infer });
    }

    private static Tree Tag((string Text, float X)[] lines, bool infer)
    {
        using var input = new MemoryStream(Page(lines));
        using var output = new MemoryStream();

        AutoTagResult result = PdfAutoTagger.AutoTag(input, output, new AutoTagOptions { InferLists = infer });
        Assert.True(result.ContentMarked,
            "the tagger marked nothing, so the tree under test was never built");

        return new Tree(output.ToArray());
    }

    /// <summary>One page, each line at its own X on a descending baseline.</summary>
    /// <remarks>
    /// Baselines are 30 points apart — comfortably more than the paragraph-gap threshold would
    /// need, and identical for every line, so the grouping under test is the marker and the left
    /// edge rather than an accident of spacing.
    /// </remarks>
    private static byte[] Page((string Text, float X)[] lines)
    {
        var content = new StringBuilder();
        for (int i = 0; i < lines.Length; i++)
            content.Append("BT /F1 12 Tf ")
                   .Append(lines[i].X.ToString("0.##", CultureInfo.InvariantCulture))
                   .Append(' ').Append((700 - i * 30).ToString(CultureInfo.InvariantCulture))
                   .Append(" Td (").Append(Escape(lines[i].Text)).Append(") Tj ET\n");

        // The page declares /WinAnsiEncoding, so each character is written as its WinAnsi BYTE and
        // the bytes are laid down through Latin-1, which maps every byte to the char of the same
        // value. Encoding.GetEncoding("windows-1252") is not available on .NET without the
        // CodePages provider, and registering one in a test would make the fixture depend on a
        // package the library does not use.
        byte[] bytes = Encoding.Latin1.GetBytes(ToWinAnsi(content.ToString()));

        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();

        void Obj(string body)
        {
            offsets.Add(sb.Length);
            sb.Append(offsets.Count).Append(" 0 obj\n").Append(body).Append("\nendobj\n");
        }

        Obj("<< /Type /Catalog /Pages 2 0 R >>");
        Obj("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        Obj("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R"
            + " /Resources << /Font << /F1 5 0 R >> >> >>");
        Obj("<< /Length " + bytes.Length.ToString(CultureInfo.InvariantCulture)
            + " >>\nstream\n" + Encoding.Latin1.GetString(bytes) + "\nendstream");
        Obj("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");

        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(offsets.Count + 1).Append("\n0000000000 65535 f \n");
        foreach (int offset in offsets)
            sb.AppendFormat(CultureInfo.InvariantCulture, "{0:D10} 00000 n \n", offset);

        sb.Append("trailer\n<< /Size ").Append(offsets.Count + 1)
          .Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");

        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    /// <summary>
    /// The characters a fixture is written with, as the WinAnsi code points that encode them.
    /// </summary>
    /// <remarks>
    /// Only the bullet needs it: U+2022 is byte <c>0x95</c> in WinAnsi, and the tagger decodes that
    /// byte back to U+2022 through the font's <c>/WinAnsiEncoding</c> — so the detector sees
    /// the character the test wrote, having gone through the same encoding a real document uses.
    /// </remarks>
    private static string ToWinAnsi(string text) => text.Replace('\u2022', '\u0095');

    /// <summary>Two pages, each carrying a two-item run at the same left edge.</summary>
    /// <remarks>
    /// The runs are identical on purpose: nothing but the page boundary separates them, so a
    /// grouping that ignored it would join all four items into one list and the count would be 1.
    /// </remarks>
    private static byte[] TwoPages()
    {
        string content = "BT /F1 12 Tf 72 700 Td (- Alpha) Tj ET\n"
                       + "BT /F1 12 Tf 72 670 Td (- Beta) Tj ET\n";
        int length = content.Length;

        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();

        void Obj(string body)
        {
            offsets.Add(sb.Length);
            sb.Append(offsets.Count).Append(" 0 obj\n").Append(body).Append("\nendobj\n");
        }

        Obj("<< /Type /Catalog /Pages 2 0 R >>");
        Obj("<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>");
        Obj("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 5 0 R"
            + " /Resources << /Font << /F1 7 0 R >> >> >>");
        Obj("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 6 0 R"
            + " /Resources << /Font << /F1 7 0 R >> >> >>");
        Obj("<< /Length " + length.ToString(CultureInfo.InvariantCulture)
            + " >>\nstream\n" + content + "\nendstream");
        Obj("<< /Length " + length.ToString(CultureInfo.InvariantCulture)
            + " >>\nstream\n" + content + "\nendstream");
        Obj("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");

        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(offsets.Count + 1).Append("\n0000000000 65535 f \n");
        foreach (int offset in offsets)
            sb.AppendFormat(CultureInfo.InvariantCulture, "{0:D10} 00000 n \n", offset);

        sb.Append("trailer\n<< /Size ").Append(offsets.Count + 1)
          .Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");

        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    private static string Escape(string text)
        => text.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)");

    private static (string Text, float X)[] Lines(params (string Text, float X)[] lines) => lines;

    /// <summary>The structure tree of a tagged document, read back.</summary>
    private sealed class Tree
    {
        private readonly List<string> _roles = new List<string>();
        private readonly Dictionary<string, List<string>> _firstChildren =
            new Dictionary<string, List<string>>(StringComparer.Ordinal);

        internal List<string> RootRoles { get; } = new List<string>();

        internal Tree(byte[] pdf)
        {
            using var ms = new MemoryStream(pdf);
            using PdfDocument doc = PdfDocument.Open(ms);

            var root = doc.ResolveObject(doc.Catalog.Dictionary.Get(PdfName.StructTreeRoot))
                       as PdfDictionary;
            if (root == null) return;

            string RoleOf(PdfObject node)
                => doc.ResolveObject(node) is PdfDictionary d
                   && doc.ResolveObject(d.Get(PdfName.S)) is PdfName s ? s.Value : null;

            List<PdfObject> KidsOf(PdfDictionary dict)
            {
                PdfObject k = doc.ResolveObject(dict.Get(PdfName.K));
                if (k is PdfArray array) return array.ToList();
                return k == null ? new List<PdfObject>() : new List<PdfObject> { k };
            }

            void Visit(PdfObject node, int depth)
            {
                if (depth > 40) return;
                if (doc.ResolveObject(node) is not PdfDictionary dict) return;

                string role = RoleOf(node);
                List<PdfObject> kids = KidsOf(dict);

                if (role != null)
                {
                    _roles.Add(role);
                    if (!_firstChildren.ContainsKey(role))
                        _firstChildren[role] = kids.Select(RoleOf).Where(r => r != null).ToList();
                }

                foreach (PdfObject kid in kids) Visit(kid, depth + 1);
            }

            // The /Document element is the single child of /StructTreeRoot; its kids are the roots.
            foreach (PdfObject documentElement in KidsOf(root))
            {
                if (doc.ResolveObject(documentElement) is not PdfDictionary document) continue;

                foreach (PdfObject kid in KidsOf(document))
                {
                    string role = RoleOf(kid);
                    if (role != null) RootRoles.Add(role);
                    Visit(kid, 0);
                }
            }
        }

        internal int Count(string role) => _roles.Count(r => r == role);

        internal List<string> ChildRolesOfFirst(string role)
        {
            Assert.True(_firstChildren.ContainsKey(role), $"no /{role} element in the tree");
            return _firstChildren[role];
        }
    }
}
