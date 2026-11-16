// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using M = Tetitulz.Pdf.Builder.Model;
using Xunit;

using static Tetitulz.Pdf.HtmlToPdf.Tests.ManagedTestSupport;

namespace Tetitulz.Pdf.HtmlToPdf.Tests
{
    /// <summary>
    /// An imported <c>&lt;ul&gt;</c> or <c>&lt;ol&gt;</c> becomes a real list in the document model,
    /// so the tagged output carries <c>/L</c> → <c>/LI</c> → <c>/Lbl</c> + <c>/LBody</c>.
    /// </summary>
    /// <remarks>
    /// <para><b>It used to flatten.</b> Every <c>&lt;li&gt;</c> went into the section as a sibling
    /// carrying an <c>int Level</c>, and a nested list's items followed as more siblings one level
    /// deeper — so nothing in the model recorded where a sublist began or ended. Tagged output was
    /// a run of <c>/P</c> elements with the bullet drawn as literal text: it looked like a list on
    /// the page and was not one to a screen reader, and a copy picked the markers up as
    /// characters. PDF/UA-1 §7.6 is exactly this shape, and this repository's own deep checker
    /// validates it.</para>
    ///
    /// <para><b>The structure tree is the right place to assert.</b> The mapper's entry point
    /// returns bytes, not a model, so there is no seam to inspect the container through — and the
    /// tree is the better evidence anyway: it is what the flattening actually cost, and a test that
    /// reached into the model could pass while the exporter still emitted <c>/P</c>.</para>
    ///
    /// <para>Roles are read by walking the real tree, never by searching the bytes: <c>"/S /L"</c>
    /// is a prefix of <c>"/S /LI"</c>, so a substring assertion for the list is satisfied by the
    /// items it is supposed to be the parent of.</para>
    /// </remarks>
    public class HtmlImportedListStructureTests
    {
        /// <summary>A flat unordered list is one <c>/L</c> whose items carry a label and a body.</summary>
        [Fact]
        public void AnUnorderedListIsTaggedAsAListRatherThanAsParagraphs()
        {
            List<string> roles = Roles(Tagged("<ul><li>alpha</li><li>beta</li></ul>"));

            Assert.Equal(1, roles.Count(r => r == "L"));
            Assert.Equal(2, roles.Count(r => r == "LI"));
            Assert.Equal(2, roles.Count(r => r == "Lbl"));
            Assert.Equal(2, roles.Count(r => r == "LBody"));
        }

        /// <summary>The items are children of the list, not siblings of it.</summary>
        /// <remarks>
        /// <b>Counting roles cannot see this, and it is the whole difference.</b> A tree with one
        /// <c>/L</c> and two <c>/LI</c> hanging off <c>/Document</c> beside it has exactly the same
        /// role census as a correct one, and is what the flat model would have produced if it had
        /// merely learned to emit the names. UA11 is about the containment.
        /// </remarks>
        [Fact]
        public void TheItemsAreChildrenOfTheList()
        {
            byte[] pdf = Tagged("<ul><li>alpha</li><li>beta</li></ul>");

            Assert.Equal(new[] { "LI", "LI" }, ChildRolesOfFirst(pdf, "L"));
        }

        /// <summary>
        /// A list cut round a positioned item — the item and its positioned block in a box of their own
        /// (2026-10-19) — is still one <c>/L</c> of its three items, the first item's sublist its own
        /// <c>/L</c> inside that item, not the one the parts after it go on; the control, the same list
        /// with a second list after it, is two.
        /// </summary>
        [Fact]
        public void AListCutRoundAPositionedItem_IsOneList()
        {
            byte[] pdf = Tagged("<ul><li>alpha<ul><li>inner</li></ul></li><li style='position:relative'>beta<span style='position:absolute;left:90pt;top:0'>badge</span></li><li>gamma</li></ul>");
            Assert.Equal(2, Roles(pdf).Count(r => r == "L"));
            Assert.Equal(new[] { "LI", "LI", "LI" }, ChildRolesOfFirst(pdf, "L"));

            Assert.Equal(2, Roles(Tagged("<ul><li>alpha</li></ul><ul><li>beta</li></ul>")).Count(r => r == "L"));
        }

        /// <summary>
        /// A sublist taken out of its item because it, or an item of it, is the containing block of a
        /// positioned block (2026-10-20) is still tagged inside its item: each <c>/L</c> of two
        /// <c>/LI</c>s, each nested <c>/L</c> a child of the item before it — the outer list's first item
        /// holding a label, a body and the sublist — and the outer list's later item in the outer
        /// <c>/L</c>. Shapes: a positioned sublist; a positioned item two levels down; two sublists of one
        /// item, the first positioned, one <c>/L</c> as an item's sublists are.
        /// </summary>
        [Theory]
        [InlineData("<ul><li>alpha<ul style='position:relative'><li>inner<span style='position:absolute;left:90pt;top:0'>badge</span></li><li>innertwo</li></ul></li><li>beta</li></ul>", 2)]
        [InlineData("<ul><li>alpha<ul style='position:relative'><li>inner<span style='position:absolute;left:90pt;top:0'>badge</span></li></ul><ul><li>innertwo</li></ul></li><li>beta</li></ul>", 2)]
        [InlineData("<ul><li>alpha<ul><li>inner<ul><li style='position:relative'>deep<span style='position:absolute;left:90pt;top:0'>badge</span></li><li>deeptwo</li></ul></li><li>innertwo</li></ul></li><li>beta</li></ul>", 3)]
        public void ASublistTakenOutOfItsItem_IsTaggedInIt(string html, int lists)
        {
            byte[] pdf = Tagged(html);
            var found = new List<(List<string> Kids, int Depth)>();
            Walk(pdf, (role, kids, depth) => { if (role == "L") found.Add((kids, depth)); });

            Assert.Equal(lists, found.Count);
            foreach (var (kids, _) in found) Assert.Equal(new[] { "LI", "LI" }, kids);
            // L, LI, L: each list two levels below the one it is nested in.
            for (int k = 1; k < found.Count; k++) Assert.Equal(found[k - 1].Depth + 2, found[k].Depth);
            Assert.Equal(new[] { "Lbl", "LBody", "L" }, ChildRolesOfFirst(pdf, "LI"));
        }

        /// <summary>Each item's children are its label and its body, in that order.</summary>
        [Fact]
        public void EachItemCarriesALabelThenABody()
        {
            byte[] pdf = Tagged("<ul><li>alpha</li></ul>");

            Assert.Equal(new[] { "Lbl", "LBody" }, ChildRolesOfFirst(pdf, "LI"));
        }

        /// <summary>A nested list sits inside the item that contains it.</summary>
        /// <remarks>
        /// The relationship the flat model could not express at all: with only an <c>int Level</c>
        /// beside each item, nothing said which parent a deeper item belonged to, so no exporter
        /// could place it — and a nested list came out as more siblings with a bigger indent.
        /// </remarks>
        [Fact]
        public void ANestedListSitsInsideItsParentItem()
        {
            byte[] pdf = Tagged("<ul><li>outer<ul><li>inner</li></ul></li></ul>");

            Assert.Equal(2, Roles(pdf).Count(r => r == "L"));

            // The inner /L is reachable from the outer item, which is what "nested" means. Two /L
            // elements side by side under /Document would satisfy the count above.
            Assert.Contains("L", ChildRolesOfFirst(pdf, "LI"));
        }

        /// <summary>An ordered list's markers are ordinals, not the same number repeated.</summary>
        /// <remarks>
        /// <b>This is the defect the container was built for.</b> With a flat model the marker had
        /// to be derived from the item's <i>level</i>, because a level is the only thing an item
        /// carried — so three siblings all rendered "1.". The mapper resolves HTML's own numbering
        /// (which is richer: <c>type</c>, and a per-item <c>value</c>) and writes it to the item,
        /// and the model's positional numbering agrees with it for the ordinary case.
        /// </remarks>
        [Fact]
        public void AnOrderedListNumbersItsItemsInSequence()
        {
            byte[] pdf = Tagged("<ol><li>one</li><li>two</li><li>three</li></ol>");

            IReadOnlyList<string> labels = LabelTexts(pdf);

            Assert.Equal(3, labels.Count);
            Assert.Contains("1.", labels[0], StringComparison.Ordinal);
            Assert.Contains("2.", labels[1], StringComparison.Ordinal);
            Assert.Contains("3.", labels[2], StringComparison.Ordinal);
        }

        /// <summary><c>&lt;ol start&gt;</c> moves the whole sequence.</summary>
        /// <remarks>
        /// The acceptance control for the ordinal above: "the first label says 1." is equally
        /// satisfied by a hard-coded 1, and a list continued after an interruption is exactly where
        /// that would show.
        /// </remarks>
        [Fact]
        public void AnOrderedListStartsWhereItsStartAttributeSays()
        {
            IReadOnlyList<string> labels = LabelTexts(Tagged("<ol start=\"7\"><li>a</li><li>b</li></ol>"));

            Assert.Equal(2, labels.Count);
            Assert.Contains("7.", labels[0], StringComparison.Ordinal);
            Assert.Contains("8.", labels[1], StringComparison.Ordinal);
        }

        /// <summary>A list whose every item is hidden produces no list at all.</summary>
        /// <remarks>
        /// An empty <c>/L</c> is a UA11 finding, so emitting one would make the converter
        /// manufacture the violation its own checker reports — the failure this codebase treats as
        /// the cardinal sin in remediation, and no better here.
        /// </remarks>
        [Fact]
        public void AListWithNoVisibleItemProducesNoListElement()
        {
            List<string> roles = Roles(Tagged(
                "<p>text</p><ul><li style=\"display:none\">gone</li></ul>"));

            Assert.DoesNotContain("L", roles);
            Assert.DoesNotContain("LI", roles);
            Assert.Contains("P", roles);   // the fixture did reach the tagger
        }

        /// <summary>A plain decimal list carries no per-item marker — the container numbers it.</summary>
        /// <remarks>
        /// <para><b>The container's numbering has to be the thing that runs, or it is decoration.</b>
        /// The mapper's first shape resolved a marker string for every item, and with one present
        /// <c>ModelRules.ListMarker</c> returns it ahead of its own numbering — so
        /// <c>IsOrdered</c> and <c>Start</c> were never consulted for an imported list and deleting
        /// either changed no output. The structure was real and the numbering was still literal
        /// strings, which is half the defect the container exists to remove and the half a tag tree
        /// cannot show.</para>
        ///
        /// <para>Asserted through <c>&lt;ol start&gt;</c> moving the whole sequence, which only a
        /// container that is doing the counting can produce: with a marker on every item the start
        /// would be baked into the strings, and this test would pass either way. So it is paired
        /// with the type/value cases below, where a marker <i>is</i> written, to pin the boundary
        /// between them.</para>
        /// </remarks>
        [Fact]
        public void ADecimalListIsNumberedByTheContainerRatherThanByPerItemMarkers()
        {
            // The label is drawn by ModelRules.ListMarker from Start + position. If the mapper had
            // written a marker instead, the count below would be the same and the numbering would
            // come from a different place — which is why the marker-carrying cases are tested
            // separately rather than folded in here.
            IReadOnlyList<string> labels = LabelTexts(Tagged("<ol start=\"3\"><li>a</li><li>b</li></ol>"));

            Assert.Equal(2, labels.Count);
            Assert.Contains("3.", labels[0], StringComparison.Ordinal);
            Assert.Contains("4.", labels[1], StringComparison.Ordinal);
        }

        /// <summary>A non-decimal <c>type</c> is numbering the container cannot produce.</summary>
        /// <remarks>
        /// The model numbers in decimals only, so roman and alphabetic sequences are exactly what
        /// <c>ListItem.Marker</c> is for. Without the per-item marker these would silently become
        /// "1." and "2." — a plausible-looking list that says something the source did not.
        /// </remarks>
        [Theory]
        [InlineData("i", "i.", "ii.")]
        [InlineData("I", "I.", "II.")]
        [InlineData("a", "a.", "b.")]
        [InlineData("A", "A.", "B.")]
        public void ANonDecimalOrderedListKeepsItsOwnNumbering(
            string type, string first, string second)
        {
            IReadOnlyList<string> labels =
                LabelTexts(Tagged($"<ol type=\"{type}\"><li>a</li><li>b</li></ol>"));

            Assert.Equal(2, labels.Count);
            Assert.Contains(first, labels[0], StringComparison.Ordinal);
            Assert.Contains(second, labels[1], StringComparison.Ordinal);
        }

        /// <summary>A <c>value</c> that moves the sequence is carried on the item.</summary>
        /// <remarks>
        /// <c>ListBlock</c> holds one <c>Start</c>, so it can express where a list begins and not
        /// where it jumps. An item whose <c>value</c> disagrees with its position therefore needs a
        /// marker of its own, and every item after it inherits the new sequence.
        /// </remarks>
        [Fact]
        public void AnItemValueThatMovesTheSequenceIsHonoured()
        {
            IReadOnlyList<string> labels = LabelTexts(Tagged(
                "<ol><li>a</li><li value=\"9\">b</li><li>c</li></ol>"));

            Assert.Equal(3, labels.Count);
            Assert.Contains("1.", labels[0], StringComparison.Ordinal);
            Assert.Contains("9.", labels[1], StringComparison.Ordinal);
            Assert.Contains("10.", labels[2], StringComparison.Ordinal);
        }

        /// <summary>A <c>list-style-type</c> bullet is carried rather than re-derived from depth.</summary>
        /// <remarks>
        /// The model cycles disc/circle/square by nesting depth the way a browser defaults to, so
        /// a top-level list asking for <c>square</c> would otherwise come out as a disc. The
        /// acceptance control is the depth default itself, which must not change.
        /// </remarks>
        [Fact]
        public void ABulletNamedByTheStyleIsCarriedAndTheDefaultIsNot()
        {
            Assert.Contains("\u25aa",
                LabelTexts(Tagged("<ul style=\"list-style-type:square\"><li>a</li></ul>"))[0]);

            Assert.Contains("\u2022", LabelTexts(Tagged("<ul><li>a</li></ul>"))[0]);
        }

        /// <summary>
        /// A plain decimal list leaves its items markerless, so the container does the counting.
        /// </summary>
        /// <remarks>
        /// <para><b>The exported page cannot show this.</b> A list numbered by its container and
        /// one numbered by a literal string on every item draw the same labels, so every assertion
        /// about the PDF passes either way — measured: resolving a marker for every ordered
        /// item survived the whole tag-tree suite. The difference is real all the same: only a
        /// markerless list renumbers when an item is inserted, only it exports as an
        /// <c>&lt;ol start&gt;</c> rather than as literal text, and only it lets a caller reading
        /// the model see a sequence rather than strings.</para>
        ///
        /// <para>So this reads the model. CLAUDE.md's second blind spot exactly: where two paths
        /// agree on the answer, the liveness has to be asserted separately.</para>
        /// </remarks>
        [Fact]
        public void ADecimalListLeavesItsItemsMarkerlessSoTheContainerNumbersThem()
        {
            M.ListBlock list = FirstList("<ol start=\"3\"><li>a</li><li>b</li></ol>");

            Assert.True(list.IsOrdered);
            Assert.Equal(3, list.Start);
            Assert.All(list.Items, li => Assert.Null(li.Marker));
        }

        /// <summary>An unordered list records that it is unordered.</summary>
        [Fact]
        public void AnUnorderedListIsNotMarkedOrdered()
            => Assert.False(FirstList("<ul><li>a</li></ul>").IsOrdered);

        /// <summary>Numbering the container cannot express is carried on the item.</summary>
        /// <remarks>
        /// The other side of the boundary, and the acceptance control for the markerless case: if
        /// nothing ever carried a marker, "a decimal list has none" would be trivially true.
        /// </remarks>
        [Theory]
        [InlineData("<ol type=\"i\"><li>a</li></ol>", "i. ")]        // roman: not decimal
        [InlineData("<ol><li value=\"9\">a</li></ol>", "9. ")]       // a start the container cannot state
        [InlineData("<ul><li>a</li></ul>", "\u2022 ")]                // a bullet is always explicit
        public void NumberingTheContainerCannotExpressIsCarriedOnTheItem(string html, string marker)
            => Assert.Equal(marker, FirstList(html).Items[0].Marker);

        /// <summary>
        /// An ordered list's <c>list-style-type</c> numbers it, and outranks the <c>type</c> attribute
        /// as a browser's cascade does — <c>hebrew</c> by CSS Counter Styles 3 §6.1 (15 is טו).
        /// Until 2026-10-21 only the attribute was read, so every one of these was decimal.
        /// </summary>
        [Theory]
        [InlineData("<ol style=\"list-style-type:lower-roman\">", "i. ", "ii. ")]
        [InlineData("<ol style=\"list-style-type:upper-alpha\">", "A. ", "B. ")]
        [InlineData("<ol style=\"list-style-type:hebrew\">", "\u05D0. ", "\u05D1. ")]
        [InlineData("<ol type=\"a\" style=\"list-style-type:upper-roman\">", "I. ", "II. ")]  // the style wins
        [InlineData("<ol type=\"a\">", "a. ", "b. ")]                                             // control: the attribute alone
        public void AnOrderedListIsNumberedByItsListStyleTypeBeforeItsTypeAttribute(
            string open, string first, string second)
        {
            M.ListBlock list = FirstList(open + "<li>a</li><li>b</li></ol>");

            Assert.Equal(first, list.Items[0].Marker);
            Assert.Equal(second, list.Items[1].Marker);
        }

        /// <summary>The fifteenth item of a <c>hebrew</c> list is טו, not יה — the rule that sets Hebrew numbering apart from plain addition.</summary>
        [Fact]
        public void AHebrewListWritesFifteenAsTetVav()
        {
            string items = string.Concat(Enumerable.Repeat("<li>a</li>", 16));
            M.ListBlock list = FirstList("<ol style=\"list-style-type:hebrew\">" + items + "</ol>");

            Assert.Equal("\u05D8\u05D5. ", list.Items[14].Marker);
            Assert.Equal("\u05D8\u05D6. ", list.Items[15].Marker);
        }

        /// <summary>A sublist hangs off its item in the model, not beside it in the section.</summary>
        [Fact]
        public void ASublistIsAttachedToItsItemInTheModel()
        {
            M.ListBlock outer = FirstList("<ul><li>outer<ul><li>inner</li></ul></li></ul>");

            Assert.Single(outer.Items);
            Assert.NotNull(outer.Items[0].Sublist);
            Assert.Single(outer.Items[0].Sublist!.Items);
        }

        /// <summary>The first list in the mapped model.</summary>
        private static M.ListBlock FirstList(string html)
        {
            var options = Managed();
            options.Tagged = true;

            M.ListBlock found = HtmlToBuilderMapper.MapToModel(html, options)
                .Sections.SelectMany(s => s.Children)
                .OfType<M.ListBlock>()
                .FirstOrDefault();

            Assert.True(found != null, "the mapper produced no list container at all");
            return found!;
        }

        // ==================================================================== helpers

        private static byte[] Tagged(string html)
        {
            var o = Managed();
            o.Tagged = true;
            return ToPdf(html, o);
        }

        private static List<string> Roles(byte[] pdf)
        {
            var roles = new List<string>();
            Walk(pdf, (role, _, __) => roles.Add(role));
            return roles;
        }

        /// <summary>The roles of the direct children of the first element with the given role.</summary>
        private static List<string> ChildRolesOfFirst(byte[] pdf, string role)
        {
            List<string> children = null;

            Walk(pdf, (r, kids, _) =>
            {
                if (children == null && r == role) children = kids;
            });

            Assert.True(children != null, $"no /{role} element in the tree");
            return children;
        }

        /// <summary>The <c>/ActualText</c>-free text of every <c>/Lbl</c>, in tree order.</summary>
        /// <remarks>
        /// The label's characters are in the content stream, not in the element, so the marker is
        /// read from the <c>/Lbl</c>'s own marked-content run through the tagged-text extractor —
        /// the one reader in the tree that binds an element to the glyphs it owns.
        /// </remarks>
        private static IReadOnlyList<string> LabelTexts(byte[] pdf)
        {
            using var ms = new MemoryStream(pdf);
            var texts = new List<string>();

            foreach (var element in Tetitulz.Pdf.Base.PdfOperations.ExtractTaggedText(ms))
                if (element.StructureType == "Lbl")
                    texts.Add(element.Text ?? string.Empty);

            return texts;
        }

        /// <summary>
        /// Visits every structure element, handing the visitor its role, its children's roles and
        /// its depth.
        /// </summary>
        private static void Walk(byte[] pdf, Action<string, List<string>, int> visit)
        {
            using var ms = new MemoryStream(pdf);
            using var doc = PdfDocument.Open(ms);

            var root = doc.ResolveObject(doc.Catalog.Dictionary.Get(PdfName.StructTreeRoot))
                       as PdfDictionary;
            if (root == null) return;

            string RoleOf(PdfObject node)
                => doc.ResolveObject(node) is PdfDictionary d
                   && doc.ResolveObject(d.Get(PdfName.S)) is PdfName s ? s.Value : null;

            IEnumerable<PdfObject> KidsOf(PdfDictionary dict)
            {
                PdfObject k = doc.ResolveObject(dict.Get(PdfName.K));
                if (k is PdfArray arr) return arr;
                return k == null ? Array.Empty<PdfObject>() : new[] { k };
            }

            void Visit(PdfObject node, int depth)
            {
                if (depth > 40) return;
                if (doc.ResolveObject(node) is not PdfDictionary dict) return;

                string role = RoleOf(node);
                var kids = KidsOf(dict).ToList();

                if (role != null)
                    visit(role, kids.Select(RoleOf).Where(r => r != null).ToList(), depth);

                foreach (PdfObject kid in kids) Visit(kid, depth + 1);
            }

            foreach (PdfObject kid in KidsOf(root)) Visit(kid, 0);
        }
    }
}
