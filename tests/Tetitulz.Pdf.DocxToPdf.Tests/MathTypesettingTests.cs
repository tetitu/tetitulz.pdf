// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Tetitulz.Pdf.DocxToPdf.Rendering;
using Xunit;

namespace Tetitulz.Pdf.DocxToPdf.Tests
{
    /// <summary>
    /// 2-D math typesetting: the geometry, not the characters.
    ///
    /// <para>
    /// A linearised equation and a typeset one contain the same letters, so a text assertion cannot
    /// tell them apart. These read the emitted operators — where the fraction rule is relative to
    /// its operands, what size a script is set at, that a vinculum spans the radicand — which is
    /// the only way to distinguish "stacked" from "printed with a slash".
    /// </para>
    /// </summary>
    [Collection(DiagnosticsCollection.Name)]
    public class MathTypesettingTests
    {
        private const string Ns =
            "xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\" " +
            "xmlns:m=\"http://schemas.openxmlformats.org/officeDocument/2006/math\"";

        private static string MathPara(string omathInner, string element = "oMath")
            => $@"<w:p {Ns}><m:{element}>{omathInner}</m:{element}></w:p>";

        private static string R(string text) => $"<m:r><m:t>{text}</m:t></m:r>";

        private static string Convert(string paragraphXml)
        {
            using var ms = new MemoryStream();
            using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
            {
                var main = doc.AddMainDocumentPart();
                main.Document = new Document(new Body(new Paragraph(paragraphXml)));
                main.Document.Save();
            }
            return Encoding.Latin1.GetString(DocxToPdfConverter.ConvertToBytes(
                ms.ToArray(), new DocxToPdfOptions { CompressStreams = false }));
        }

        /// <summary>
        /// Every drawn string with the text state in force when it was shown: (text, x, y, size).
        ///
        /// <para>
        /// Scanned sequentially rather than pattern-matched, because the two paths order their
        /// operators differently — body text sets <c>Td</c> once per line and then <c>Tf</c> per
        /// segment, while a math box sets <c>Tf</c> then its own <c>Td</c>. A regex tuned to one
        /// silently sees none of the other.
        /// </para>
        ///
        /// <para>
        /// For body text several segments share one <c>Td</c>, so <c>x</c> is the line origin;
        /// every math box emits its own, so <c>x</c> is exact there.
        /// </para>
        /// </summary>
        private static List<(string text, double x, double y, double size)> Glyphs(string pdf)
        {
            var result = new List<(string, double, double, double)>();
            double x = 0, y = 0, size = 0;

            // The literal-string pattern has to respect PDF escaping: a drawn "(" is written as
            // "\(", and a naive [^)]* both mis-terminates and reports the backslash as content.
            foreach (Match m in Regex.Matches(pdf,
                @"/F\d+ ([\d.]+) Tf|(-?[\d.]+) (-?[\d.]+) Td|\(((?:\\.|[^\\()])*)\) Tj"))
            {
                if (m.Groups[1].Success)
                    size = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                else if (m.Groups[2].Success)
                {
                    x = double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
                    y = double.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
                }
                else
                    result.Add((Unescape(m.Groups[4].Value), x, y, size));
            }
            return result;
        }

        private static string Unescape(string s)
        {
            var sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] == '\\' && i + 1 < s.Length) { sb.Append(s[++i]); continue; }
                sb.Append(s[i]);
            }
            return sb.ToString();
        }

        private static (double x, double y, double size) Find(string pdf, string text)
        {
            var hit = Glyphs(pdf).FirstOrDefault(g => g.text == text);
            Assert.True(hit.text == text, $"'{text}' was never drawn — the fixture did not reach the feature");
            return (hit.x, hit.y, hit.size);
        }

        /// <summary>Every filled rectangle: (x, y, w, h). Fraction bars and vincula are these.</summary>
        private static List<double[]> Rules(string pdf)
            => Regex.Matches(pdf, @"(-?[\d.]+) (-?[\d.]+) (-?[\d.]+) (-?[\d.]+) re\nf")
                .Select(m => Enumerable.Range(1, 4)
                    .Select(g => double.Parse(m.Groups[g].Value, CultureInfo.InvariantCulture))
                    .ToArray())
                .ToList();

        // ------------------------------------------------------------------ fractions

        [Fact]
        public void Fraction_DrawsARuleBetweenTheNumeratorAndDenominatorBaselines()
        {
            string pdf = Convert(MathPara($"<m:f><m:num>{R("a")}</m:num><m:den>{R("b")}</m:den></m:f>"));

            var num = Find(pdf, "a");
            var den = Find(pdf, "b");
            var rules = Rules(pdf);

            Assert.Single(rules);
            double barY = rules[0][1];

            Assert.True(den.y < barY && barY < num.y,
                $"the bar ({barY}) must lie between the denominator ({den.y}) and numerator ({num.y})");
            Assert.True(num.y - den.y > 6.0, "the operands must actually be stacked apart");
        }

        [Fact]
        public void InlineFraction_SetsItsOperandsAtScriptSize()
        {
            string pdf = Convert(MathPara($"<m:f><m:num>{R("a")}</m:num><m:den>{R("b")}</m:den></m:f>"));

            var num = Find(pdf, "a");
            // 70% of the body size — a fraction printed inline at full size is the tell that the
            // style cascade is not running.
            Assert.True(num.size < 11.0 * 0.99,
                $"expected script size, saw {num.size}");
        }

        [Fact]
        public void DisplayFraction_SetsItsOperandsAtFullSize()
        {
            string pdf = Convert(MathPara(
                $"<m:f><m:num>{R("a")}</m:num><m:den>{R("b")}</m:den></m:f>", element: "oMathPara"));

            var num = Find(pdf, "a");
            var inline = Find(Convert(MathPara(
                $"<m:f><m:num>{R("a")}</m:num><m:den>{R("b")}</m:den></m:f>")), "a");

            Assert.True(num.size > inline.size,
                $"display style must set larger than inline: {num.size} vs {inline.size}");
        }

        [Fact]
        public void FractionBar_SpansTheWiderOperand()
        {
            string pdf = Convert(MathPara(
                $"<m:f><m:num>{R("wwww")}</m:num><m:den>{R("i")}</m:den></m:f>"));

            var rules = Rules(pdf);
            Assert.Single(rules);
            var wide = Find(pdf, "wwww");

            // The rule must cover the wide numerator, not the narrow denominator.
            Assert.True(rules[0][2] >= (wide.x - rules[0][0]) + 1,
                "the bar must span the wider operand");
        }

        // ------------------------------------------------------------------ scripts

        [Fact]
        public void Superscript_IsRaisedAboveAndSetSmallerThanItsBase()
        {
            string pdf = Convert(MathPara($"<m:sSup><m:e>{R("x")}</m:e><m:sup>{R("2")}</m:sup></m:sSup>"));

            var b = Find(pdf, "x");
            var sup = Find(pdf, "2");

            Assert.True(sup.y > b.y + 2.0, $"the superscript must sit above the base: {sup.y} vs {b.y}");
            Assert.True(sup.size < b.size, $"and be smaller: {sup.size} vs {b.size}");
            Assert.True(sup.x > b.x, "and follow it horizontally");
        }

        [Fact]
        public void Subscript_IsLoweredBelowItsBase()
        {
            string pdf = Convert(MathPara($"<m:sSub><m:e>{R("x")}</m:e><m:sub>{R("i")}</m:sub></m:sSub>"));

            var b = Find(pdf, "x");
            var sub = Find(pdf, "i");

            Assert.True(sub.y < b.y - 0.5, $"the subscript must sit below the base: {sub.y} vs {b.y}");
            Assert.True(sub.size < b.size);
        }

        [Fact]
        public void SubSup_PutsBothScriptsAtTheSameHorizontalPosition()
        {
            string pdf = Convert(MathPara(
                $"<m:sSubSup><m:e>{R("x")}</m:e><m:sub>{R("i")}</m:sub><m:sup>{R("2")}</m:sup></m:sSubSup>"));

            var b = Find(pdf, "x");
            var sub = Find(pdf, "i");
            var sup = Find(pdf, "2");

            // Stacked, not side by side — the difference between x_i^2 and x_i 2.
            Assert.True(Math.Abs(sup.x - sub.x) < 1.0,
                $"sub and sup must share an x: {sub.x} vs {sup.x}");
            Assert.True(sup.x > b.x);
            Assert.True(sup.y > sub.y);
        }

        // ------------------------------------------------------------------ radicals

        [Fact]
        public void Radical_DrawsAVinculumOverTheRadicand()
        {
            string pdf = Convert(MathPara($"<m:rad><m:e>{R("x")}</m:e></m:rad>"));

            var radicand = Find(pdf, "x");
            var rules = Rules(pdf);

            Assert.NotEmpty(rules);
            var vinculum = rules.OrderByDescending(r => r[1]).First();
            Assert.True(vinculum[1] > radicand.y + 2.0,
                "the vinculum must sit above the radicand");
            Assert.True(vinculum[0] <= radicand.x + 0.5,
                "and start at or before it");
        }

        [Fact]
        public void Radical_WithADegree_DrawsTheDegreeAboveAndLeftOfTheRadicand()
        {
            // Guaranteed red before this work: MathLinearizer discarded m:deg unconditionally, so
            // a cube root printed as a square root — a wrong statement, not a missing flourish.
            string pdf = Convert(MathPara(
                $"<m:rad><m:deg>{R("3")}</m:deg><m:e>{R("x")}</m:e></m:rad>"));

            var radicand = Find(pdf, "x");
            var degree = Find(pdf, "3");

            Assert.True(degree.x < radicand.x, "the degree sits left of the radicand");
            Assert.True(degree.y > radicand.y, "and above it");
            Assert.True(degree.size < radicand.size, "at script-script size");
        }

        // ------------------------------------------------------------------ accents

        [Fact]
        public void Accent_DrawsItsAccentCharacterAboveTheBase()
        {
            // Also guaranteed red before this work: m:accPr/m:chr was never read, so every accent
            // was silently lost and x̄ became x.
            string pdf = Convert(MathPara(
                $"<m:acc><m:accPr><m:chr m:val=\"¯\"/></m:accPr><m:e>{R("x")}</m:e></m:acc>"));

            var b = Find(pdf, "x");
            var accent = Find(pdf, "¯");

            Assert.True(accent.y > b.y, "the accent must sit above the base");
        }

        [Fact]
        public void Bar_DrawsARuleOverItsBase()
        {
            string pdf = Convert(MathPara($"<m:bar><m:e>{R("z")}</m:e></m:bar>"));

            var b = Find(pdf, "z");
            var rules = Rules(pdf);

            Assert.NotEmpty(rules);
            Assert.True(rules.Any(r => r[1] > b.y), "the bar must sit above its base");
        }

        // ------------------------------------------------------------------ the seam

        [Fact]
        public void MathFreeParagraph_KeepsItsExactBaseline()
        {
            // The invariant the whole seam rests on: a paragraph with no equation emits exactly the
            // geometry it always did. 792 − 72 margin − 12×1.2 line = 705.6, which is the value the
            // renderer produced before Ascent/Descent existed.
            string pdf = Convert($@"<w:p {Ns}><w:r><w:t>plain</w:t></w:r></w:p>");

            var text = Find(pdf, "plain");
            Assert.Equal(705.6, text.y, 1);
        }

        [Fact]
        public void InlineEquation_SitsOnTheSurroundingTextBaseline()
        {
            string pdf = Convert($@"<w:p {Ns}><w:r><w:t>Let </w:t></w:r>
<m:oMath><m:sSup><m:e>{R("x")}</m:e><m:sup>{R("2")}</m:sup></m:sSup></m:oMath>
<w:r><w:t> be</w:t></w:r></w:p>");

            var before = Find(pdf, "Let ");
            var baseGlyph = Find(pdf, "x");

            Assert.Equal(before.y, baseGlyph.y, 1);
        }

        [Fact]
        public void InlineEquation_AdvancesTheTextAfterItByTheEquationsWidth()
        {
            string withMath = Convert($@"<w:p {Ns}><w:r><w:t>Let </w:t></w:r>
<m:oMath><m:sSup><m:e>{R("x")}</m:e><m:sup>{R("2")}</m:sup></m:sSup></m:oMath>
<w:r><w:t> be</w:t></w:r></w:p>");
            string without = Convert($@"<w:p {Ns}><w:r><w:t>Let </w:t></w:r><w:r><w:t> be</w:t></w:r></w:p>");

            // " be" is drawn inside the same text object, so its own Td is not emitted; instead the
            // equation's slot is stepped over with a TJ. The check that matters is that the step
            // exists and is positive.
            Assert.Matches(@"\[-[\d.]+\] TJ", withMath);
            Assert.DoesNotMatch(@"\[-[\d.]+\] TJ", without);
        }

        [Fact]
        public void FractionsDenominator_ClaimsRoomBelowTheBaseline()
        {
            // The descent half of the seam. A fraction's denominator hangs below the baseline, so
            // the line has to claim extra room *under* it — which is the second AdvanceCursor, and
            // is zero for every ordinary paragraph. Two documents differing only in that one
            // paragraph isolate the contribution.
            string withMath = ConvertBody(
                MathPara($"<m:f><m:num>{R("a")}</m:num><m:den>{R("b")}</m:den></m:f>")
                + $@"<w:p {Ns}><w:r><w:t>AFTER</w:t></w:r></w:p>");

            string withoutMath = ConvertBody(
                $@"<w:p {Ns}><w:r><w:t>x</w:t></w:r></w:p>"
                + $@"<w:p {Ns}><w:r><w:t>AFTER</w:t></w:r></w:p>");

            double after = Find(withMath, "AFTER").y;
            double baseline = Find(withoutMath, "AFTER").y;

            Assert.True(baseline - after > 2.0,
                $"the denominator must push the following line down: {baseline} → {after}");
        }

        private static string ConvertBody(string paragraphsXml)
        {
            using var ms = new MemoryStream();
            using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
            {
                var main = doc.AddMainDocumentPart();
                var body = new Body();
                foreach (var p in SplitParagraphs(paragraphsXml)) body.Append(new Paragraph(p));
                main.Document = new Document(body);
                main.Document.Save();
            }
            return Encoding.Latin1.GetString(DocxToPdfConverter.ConvertToBytes(
                ms.ToArray(), new DocxToPdfOptions { CompressStreams = false }));
        }

        private static IEnumerable<string> SplitParagraphs(string xml)
        {
            int i = 0;
            while (true)
            {
                int start = xml.IndexOf("<w:p ", i, StringComparison.Ordinal);
                if (start < 0) yield break;
                int end = xml.IndexOf("</w:p>", start, StringComparison.Ordinal);
                if (end < 0) yield break;
                yield return xml.Substring(start, end - start + 6);
                i = end + 6;
            }
        }

        // ------------------------------------------------------------------ delimiters, n-ary

        [Fact]
        public void Delimiters_BracketTheirContent()
        {
            string pdf = Convert(MathPara($"<m:d><m:e>{R("x")}</m:e></m:d>"));

            var open = Find(pdf, "(");
            var content = Find(pdf, "x");
            var close = Find(pdf, ")");

            Assert.True(open.x < content.x, "the opening fence precedes the content");
            Assert.True(close.x > content.x, "and the closing one follows it");
        }

        [Fact]
        public void Delimiters_HonourTheirStatedCharacters()
        {
            string pdf = Convert(MathPara(
                $"<m:d><m:dPr><m:begChr m:val=\"[\"/><m:endChr m:val=\"]\"/></m:dPr><m:e>{R("x")}</m:e></m:d>"));

            var drawn = Glyphs(pdf).Select(g => g.text).ToList();
            Assert.Contains("[", drawn);
            Assert.Contains("]", drawn);
        }

        [Fact]
        public void DelimitersAroundATallFraction_AreStretchedWithATextMatrix()
        {
            string pdf = Convert(MathPara(
                $"<m:d><m:e><m:f><m:num>{R("a")}</m:num><m:den>{R("b")}</m:den></m:f></m:e></m:d>"));

            // A grown fence needs a text matrix with d > 1; Tz would only scale it horizontally.
            var matrices = Regex.Matches(pdf, @"1 0 0 ([\d.]+) (-?[\d.]+) (-?[\d.]+) Tm")
                .Select(m => double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture))
                .ToList();

            Assert.Contains(matrices, d => d > 1.05);
        }

        [Fact]
        public void DelimitersAroundShortContent_AreNotStretched()
        {
            // The control: a fence that already fits must stay at its natural size, so an
            // implementation that always stretched could not pass both this and the test above.
            string pdf = Convert(MathPara($"<m:d><m:e>{R("x")}</m:e></m:d>"));

            Assert.DoesNotMatch(@"1 0 0 [\d.]+ -?[\d.]+ -?[\d.]+ Tm", pdf);
        }

        [Fact]
        public void DisplayNary_PutsItsLimitsAboveAndBelowTheOperator()
        {
            string pdf = Convert(MathPara(
                $"<m:nary><m:naryPr><m:chr m:val=\"S\"/></m:naryPr>" +
                $"<m:sub>{R("i")}</m:sub><m:sup>{R("n")}</m:sup><m:e>{R("x")}</m:e></m:nary>",
                element: "oMathPara"));

            var op = Find(pdf, "S");
            var lower = Find(pdf, "i");
            var upper = Find(pdf, "n");

            Assert.True(upper.y > op.y, "the upper limit sits above the operator");
            Assert.True(lower.y < op.y, "and the lower one below it");
            Assert.True(Math.Abs(upper.x - lower.x) < op.x + 20, "both are centred on the operator");
        }

        [Fact]
        public void InlineNary_PutsItsLimitsBesideTheOperator()
        {
            string pdf = Convert(MathPara(
                $"<m:nary><m:naryPr><m:chr m:val=\"S\"/></m:naryPr>" +
                $"<m:sub>{R("i")}</m:sub><m:sup>{R("n")}</m:sup><m:e>{R("x")}</m:e></m:nary>"));

            var op = Find(pdf, "S");
            var lower = Find(pdf, "i");

            // Inline, the limits ride as scripts to the right rather than stacking — which is what
            // keeps an inline formula on its own line.
            Assert.True(lower.x > op.x, "an inline n-ary sets its limits beside the operator");
        }

        [Fact]
        public void LimLow_PutsItsConditionUnderTheBase()
        {
            string pdf = Convert(MathPara(
                $"<m:limLow><m:e>{R("lim")}</m:e><m:lim>{R("k")}</m:lim></m:limLow>"));

            var b = Find(pdf, "lim");
            var condition = Find(pdf, "k");

            Assert.True(condition.y < b.y, "the condition sits below 'lim'");
        }

        // ------------------------------------------------------------------ display blocks

        [Fact]
        public void DisplayEquation_IsCentredInTheColumn()
        {
            string pdf = Convert(MathPara(
                $"<m:f><m:num>{R("a")}</m:num><m:den>{R("b")}</m:den></m:f>", element: "oMathPara"));

            // Default page 612 wide with 72pt margins → the column centre is 306.
            var rules = Rules(pdf);
            Assert.Single(rules);
            double barCentre = rules[0][0] + rules[0][2] / 2;

            Assert.Equal(306.0, barCentre, 0);
        }

        [Fact]
        public void DisplayEquation_HonoursLeftJustification()
        {
            string centred = Convert(MathPara(
                $"<m:f><m:num>{R("a")}</m:num><m:den>{R("b")}</m:den></m:f>", element: "oMathPara"));

            string left = Convert($@"<w:p {Ns}><m:oMathPara><m:oMathParaPr><m:jc m:val=""left""/></m:oMathParaPr>
<m:f><m:num>{R("a")}</m:num><m:den>{R("b")}</m:den></m:f></m:oMathPara></w:p>");

            double centredX = Rules(centred).Single()[0];
            double leftX = Rules(left).Single()[0];

            Assert.True(leftX < centredX - 100,
                $"left-justified must sit at the margin, not the centre: {leftX} vs {centredX}");
            // The bar starts a hair inside the box: a fraction carries a deliberate side bearing
            // (ThinSpaceEm/2) so it never touches what precedes it.
            Assert.InRange(leftX, 72.0, 74.0);
        }

        [Fact]
        public void DisplayEquation_HonoursRightJustification()
        {
            string pdf = Convert($@"<w:p {Ns}><m:oMathPara><m:oMathParaPr><m:jc m:val=""right""/></m:oMathParaPr>
<m:f><m:num>{R("a")}</m:num><m:den>{R("b")}</m:den></m:f></m:oMathPara></w:p>");

            var bar = Rules(pdf).Single();
            // Right margin is 540; the bar stops just short of it by the fraction's side bearing.
            Assert.InRange(bar[0] + bar[2], 538.0, 540.0);
        }

        [Fact]
        public void DisplayEquation_WiderThanTheColumn_IsScaledDownToFit()
        {
            string wide = new string('w', 300);
            string pdf = Convert(MathPara(
                $"<m:f><m:num>{R(wide)}</m:num><m:den>{R("b")}</m:den></m:f>", element: "oMathPara"));

            var scales = Regex.Matches(pdf, @"([\d.]+) 0 0 ([\d.]+) (-?[\d.]+) (-?[\d.]+) cm")
                .Select(m => double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture))
                .ToList();

            Assert.Contains(scales, s => s < 1.0 && s >= 0.5);
        }

        [Fact]
        public void DisplayEquation_ThatFits_IsNotScaled()
        {
            // The control: a formula inside the column must be drawn at natural size, so an
            // implementation that always scaled could not pass both this and the test above.
            string pdf = Convert(MathPara(
                $"<m:f><m:num>{R("a")}</m:num><m:den>{R("b")}</m:den></m:f>", element: "oMathPara"));

            Assert.DoesNotMatch(@"[\d.]+ 0 0 [\d.]+ -?[\d.]+ -?[\d.]+ cm", pdf);
        }

        [Fact]
        public void DisplayEquation_FarBeyondTheScaleFloor_IsDrawnAtTheFloorAndReports()
        {
            string enormous = new string('w', 3000);
            var reports = new List<string>();
            int collectingThread = System.Environment.CurrentManagedThreadId;
            void Collect(string m) { if (System.Environment.CurrentManagedThreadId == collectingThread) reports.Add(m); }

            DocxToPdfConverter.DiagnosticReported += Collect;
            string pdf;
            try
            {
                pdf = Convert(MathPara(
                    $"<m:f><m:num>{R(enormous)}</m:num><m:den>{R("b")}</m:den></m:f>",
                    element: "oMathPara"));
            }
            finally { DocxToPdfConverter.DiagnosticReported -= Collect; }

            var scales = Regex.Matches(pdf, @"([\d.]+) 0 0 ([\d.]+) (-?[\d.]+) (-?[\d.]+) cm")
                .Select(m => double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture))
                .ToList();

            Assert.Contains(scales, s => Math.Abs(s - 0.5) < 0.01);
            Assert.Contains(reports, m => m.Contains("overflows"));
        }

        // ------------------------------------------------------------------ table cells

        [Fact]
        public void FractionInATableCell_IsStackedNotLinearised()
        {
            using var ms = new MemoryStream();
            using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
            {
                var main = doc.AddMainDocumentPart();
                var table = new Table(
                    new TableGrid(new GridColumn { Width = "9000" }),
                    new TableRow(new TableCell(new Paragraph(MathPara(
                        $"<m:f><m:num>{R("p")}</m:num><m:den>{R("q")}</m:den></m:f>")))));
                main.Document = new Document(new Body(table));
                main.Document.Save();
            }
            string pdf = Encoding.Latin1.GetString(DocxToPdfConverter.ConvertToBytes(
                ms.ToArray(), new DocxToPdfOptions { CompressStreams = false }));

            var num = Find(pdf, "p");
            var den = Find(pdf, "q");
            var rules = Rules(pdf);

            // An equation must not render one way in a paragraph and another inside a cell.
            Assert.True(num.y > den.y, "the operands must be stacked inside the cell too");
            Assert.Contains(rules, r => r[1] > den.y && r[1] < num.y);
        }

        // ------------------------------------------------------------------ matrices and arrays

        private static string MatrixRow(params string[] cells)
            => "<m:mr>" + string.Concat(cells.Select(c => $"<m:e>{R(c)}</m:e>")) + "</m:mr>";

        [Fact]
        public void Matrix_StacksItsRowsAndLinesUpItsColumns()
        {
            string pdf = Convert(MathPara(
                "<m:m>" + MatrixRow("a", "b") + MatrixRow("c", "d") + "</m:m>"));

            var a = Find(pdf, "a");
            var b = Find(pdf, "b");
            var c = Find(pdf, "c");
            var d = Find(pdf, "d");

            // Rows stack: the second row sits below the first.
            Assert.True(c.y < a.y, $"row 2 must sit below row 1: {c.y} vs {a.y}");
            Assert.Equal(a.y, b.y, 1);
            Assert.Equal(c.y, d.y, 1);

            // Cells are centred in their column (DrawingML's default), so two single glyphs of
            // slightly different advance widths sit half that difference apart — near-identical,
            // not identical. Exact column alignment is asserted in the next test, where one cell is
            // far wider than the other.
            Assert.True(Math.Abs(a.x - c.x) < 1.5, $"column 1 must line up: {a.x} vs {c.x}");
            Assert.True(Math.Abs(b.x - d.x) < 1.5, $"column 2 must line up: {b.x} vs {d.x}");
            Assert.True(b.x > a.x + 4, "column 2 must sit clearly right of column 1");
        }

        [Fact]
        public void Matrix_SizesEachColumnToItsWidestCell()
        {
            // A wide cell in column 1 must push column 2 right for *every* row, not just its own.
            string pdf = Convert(MathPara(
                "<m:m>" + MatrixRow("wwwwww", "x") + MatrixRow("i", "y") + "</m:m>"));

            var topRight = Find(pdf, "x");
            var bottomRight = Find(pdf, "y");

            Assert.Equal(topRight.x, bottomRight.x, 1);
        }

        [Fact]
        public void Matrix_IsCentredOnTheMathsAxis_NotRestingOnTheBaseline()
        {
            string pdf = Convert($@"<w:p {Ns}><w:r><w:t>Let </w:t></w:r>
<m:oMath><m:m>{MatrixRow("a")}{MatrixRow("c")}</m:m></m:oMath></w:p>");

            var text = Find(pdf, "Let ");
            var top = Find(pdf, "a");
            var bottom = Find(pdf, "c");

            // A block resting on the baseline would put every row at or above the text; centring
            // on the axis straddles it.
            Assert.True(top.y > text.y, "the top row sits above the text baseline");
            Assert.True(bottom.y < text.y, "and the bottom row below it");
        }

        [Fact]
        public void EquationArray_StacksItsEquationsLeftAligned()
        {
            string pdf = Convert(MathPara(
                $"<m:eqArr><m:e>{R("aaa")}</m:e><m:e>{R("b")}</m:e></m:eqArr>"));

            var first = Find(pdf, "aaa");
            var second = Find(pdf, "b");

            Assert.True(second.y < first.y, "the second equation sits below the first");
            Assert.Equal(first.x, second.x, 1);   // left-aligned, not centred
        }

        [Fact]
        public void MatrixWithAbsurdlyManyCells_IsBoundedAndReported()
        {
            var rows = new StringBuilder();
            for (int r = 0; r < 60; r++)
                rows.Append(MatrixRow(Enumerable.Repeat("x", 20).ToArray()));

            var reports = new List<string>();
            int collectingThread = System.Environment.CurrentManagedThreadId;
            void Collect(string m) { if (System.Environment.CurrentManagedThreadId == collectingThread) reports.Add(m); }
            DocxToPdfConverter.DiagnosticReported += Collect;
            string pdf;
            try { pdf = Convert(MathPara("<m:m>" + rows + "</m:m>")); }
            finally { DocxToPdfConverter.DiagnosticReported -= Collect; }

            Assert.Contains(reports, m => m.Contains("cells"));
            // The cap bounds the emitted geometry, not a stopwatch.
            Assert.True(Glyphs(pdf).Count(g => g.text == "x")
                        <= Rendering.MathTypesetting.MathLayoutMetrics.MaxMatrixCells,
                "the cell cap must bound what a crafted matrix can emit");
        }

        // ------------------------------------------------------------------ honest fallback

        [Fact]
        public void UnsupportedConstruct_IsLinearisedAndReported()
        {
            var reports = new List<string>();
            int collectingThread = System.Environment.CurrentManagedThreadId;
            void Collect(string m) { if (System.Environment.CurrentManagedThreadId == collectingThread) reports.Add(m); }

            DocxToPdfConverter.DiagnosticReported += Collect;
            string pdf;
            try
            {
                // An element name OMML does not define. This test has now had its construct
                // taken away TWICE -- it was m:eqArr until equation arrays gained a 2-D form, then
                // m:groupChr until group characters did -- and its own comment predicted both
                // times. There is no third candidate: every construct in the OMML schema is
                // typeset now, so the only way to reach the fallback branch is an element the
                // schema does not have.
                //
                // That is not a weaker fixture. The branch exists precisely for input this
                // renderer does not know about -- a future OMML revision, or a producer emitting
                // something non-standard -- and an unknown element is exactly that input, rather
                // than a stand-in for it.
                pdf = Convert(MathPara($"<m:zzNotAConstruct><m:e>{R("a")}</m:e></m:zzNotAConstruct>"));
            }
            finally { DocxToPdfConverter.DiagnosticReported -= Collect; }

            Assert.Contains(reports, m => m.Contains("zzNotAConstruct"));
            // The content still reaches the page through the linear fallback.
            Assert.Contains(Glyphs(pdf), g => g.text.Contains("a"));
        }

        [Fact]
        public void DeeplyNestedFractions_AreBoundedAndReported()
        {
            // A hundred: three times the layout's ceiling of thirty-two, and under the part's depth limit, which refuses
            // a document nested deeper before it is opened (PdfLimits.MaxDocxXmlDepth).
            string inner = R("x");
            for (int i = 0; i < 100; i++)
                inner = $"<m:f><m:num>{inner}</m:num><m:den>{R("2")}</m:den></m:f>";

            var reports = new List<string>();
            int collectingThread = System.Environment.CurrentManagedThreadId;
            void Collect(string m) { if (System.Environment.CurrentManagedThreadId == collectingThread) reports.Add(m); }
            DocxToPdfConverter.DiagnosticReported += Collect;
            try { Convert(MathPara(inner)); }
            finally { DocxToPdfConverter.DiagnosticReported -= Collect; }

            Assert.Contains(reports, m => m.Contains("ceiling"));
        }

        // ------------------------------------------------------------------ the last five
        //
        // m:sPre, m:groupChr, m:box, m:borderBox and m:phant -- the constructs that had no 2-D
        // form and were linearised.
        //
        // These live HERE rather than in a file of their own, and that is the point rather than
        // filing convenience: they need exactly the Glyphs / Rules / Find probes above, and a
        // second copy of those had already gone wrong once while being written -- it read `Td` as
        // relative where the math boxes emit it absolute, which quietly reported every script at
        // the wrong height. Two probes disagreeing about what the output says is the same defect
        // class this repository keeps finding in its renderers.
        //
        // The fallback branch in MathBoxBuilder used to name the unhandled constructs as
        // "matrices, equation arrays, boxes, group characters, limits" -- five things, every one
        // of which the dispatch above it already handled. The list had rotted one construct at a
        // time while reading as authoritative, and is now deleted rather than corrected: the
        // dispatch is the list.

        /// <summary>
        /// <c>m:sPre</c> puts its scripts <b>before</b> the base. The ordering of x positions is
        /// the whole difference between a pre-script and an ordinary one -- a linearised m:sPre
        /// emits the same characters, so only geometry separates the two.
        /// </summary>
        [Fact]
        public void PreScripts_PlaceTheScriptsBeforeTheBase()
        {
            string pdf = Convert(MathPara(
                $"<m:sPre><m:sub>{R("n")}</m:sub><m:sup>{R("m")}</m:sup><m:e>{R("P")}</m:e></m:sPre>"));

            var nucleus = Find(pdf, "P");
            var sub = Find(pdf, "n");
            var sup = Find(pdf, "m");

            Assert.True(sub.x < nucleus.x, $"the subscript must precede the base ({sub.x} vs {nucleus.x})");
            Assert.True(sup.x < nucleus.x, $"the superscript must precede the base ({sup.x} vs {nucleus.x})");

            // Stacked, not side by side: one above the baseline and one below.
            Assert.True(sup.y > nucleus.y, "the superscript must sit above the baseline");
            Assert.True(sub.y < nucleus.y, "the subscript must sit below the baseline");
        }

        /// <summary>
        /// A pre-script and an ordinary script sit at the <b>same</b> heights. They are one
        /// typographic object on opposite sides of the nucleus, and separate constants for the new
        /// code are exactly how the two would drift apart inside a single equation.
        /// </summary>
        [Fact]
        public void PreScripts_UseTheSameShiftsAsOrdinaryScripts()
        {
            string pre = Convert(MathPara(
                $"<m:sPre><m:sub>{R("n")}</m:sub><m:e>{R("P")}</m:e></m:sPre>"));
            string post = Convert(MathPara(
                $"<m:sSub><m:e>{R("P")}</m:e><m:sub>{R("n")}</m:sub></m:sSub>"));

            double preDrop = Find(pre, "P").y - Find(pre, "n").y;
            double postDrop = Find(post, "P").y - Find(post, "n").y;

            Assert.True(Math.Abs(preDrop - postDrop) < 0.05,
                $"a pre-subscript dropped {preDrop} and an ordinary one {postDrop}");
        }

        /// <summary>
        /// <c>m:groupChr</c> puts its character <b>below</b> the base by default -- the opposite of
        /// <c>m:bar</c>'s default, which is the mistake to make when the two are written from one
        /// another.
        ///
        /// <para><b>The fixture names an ASCII character on purpose.</b> OMML's default is U+23DF
        /// BOTTOM CURLY BRACKET, and on a host with no font covering it the glyph is (correctly)
        /// omitted and reported -- so an assertion on the default character's position would pass
        /// or fail according to what fonts the machine has installed. The default itself is pinned
        /// by the case below, in a way that does not depend on coverage.</para>
        /// </summary>
        [Fact]
        public void GroupCharacter_DefaultsToBelowTheBase()
        {
            string pdf = Convert(MathPara(
                "<m:groupChr><m:groupChrPr><m:chr m:val=\"-\"/></m:groupChrPr>" +
                $"<m:e>{R("x")}</m:e></m:groupChr>"));

            Assert.True(Find(pdf, "-").y < Find(pdf, "x").y,
                "with no m:pos the grouping character must sit BELOW the base");
        }

        /// <summary><c>m:pos top</c> moves it above, so the default above is a real choice.</summary>
        [Fact]
        public void GroupCharacter_HonoursItsPosition()
        {
            string pdf = Convert(MathPara(
                "<m:groupChr><m:groupChrPr><m:chr m:val=\"-\"/><m:pos m:val=\"top\"/>" +
                $"</m:groupChrPr><m:e>{R("x")}</m:e></m:groupChr>"));

            Assert.True(Find(pdf, "-").y > Find(pdf, "x").y,
                "m:pos top must put the grouping character above the base");
        }

        /// <summary>
        /// The default character is U+23DF, asserted without depending on how this host can draw it:
        /// the page the default draws is the page an explicit U+23DF draws, and not the page another
        /// character draws. An implementation defaulting to some other character fails the equality.
        ///
        /// <para>This is the shape a host-dependent font assertion needs -- the alternative was a
        /// test that silently passes on a machine with the glyph and fails on a bare container,
        /// which is how "works on my machine" gets encoded into a suite. The first form of this test
        /// listed the outcomes (drawn, or reported uncovered) and missed a third (drawn to width as a
        /// path), which a host font made the one taken.</para>
        /// </summary>
        [Fact]
        public void GroupCharacter_DefaultsToTheBottomCurlyBracket()
        {
            // The page the default draws is the page an explicit U+23DF draws, whatever this host can draw it
            // with: a glyph, the bracket drawn to the base's width as a path, or space reserved and
            // reported. Asserting one of those outcomes was host-dependent \u2014 the equation path's asking the
            // host's fonts (Wave 22, DocxToPdf-3:8) turned "reported" into "a path" on this one. The
            // controls: an explicit U+23DE (top bracket) and an explicit '_' each draw another page, so the
            // comparison can see a different character.
            string Page(string chrPr)
                => PageContent(Convert(MathPara($"<m:groupChr>{chrPr}<m:e>{R("x")}</m:e></m:groupChr>")));

            string byDefault = Page("");
            Assert.Equal(Page("<m:groupChrPr><m:chr m:val=\"\u23DF\"/></m:groupChrPr>"), byDefault);
            Assert.NotEqual(Page("<m:groupChrPr><m:chr m:val=\"\u23DE\"/></m:groupChrPr>"), byDefault);
            Assert.NotEqual(Page("<m:groupChrPr><m:chr m:val=\"_\"/></m:groupChrPr>"), byDefault);
        }

        /// <summary>The first page's content stream, decoded; the PDF is Latin-1 text of its bytes.</summary>
        private static string PageContent(string pdf)
        {
            using var doc = Tetitulz.Pdf.Core.Document.PdfDocument.Open(new System.IO.MemoryStream(Encoding.Latin1.GetBytes(pdf)));
            return Encoding.Latin1.GetString(doc.Pages[0].GetContentStreamData());
        }

        /// <summary>Every point a stroked or filled path visits: the operands of <c>m</c>, <c>l</c>, and a <c>c</c>'s end point, with whether it was a curve.</summary>
        private static List<(double x, double y, bool curve)> PathPoints(string pdf)
            => Regex.Matches(pdf, @"(?:(-?[\d.]+) (-?[\d.]+) (-?[\d.]+) (-?[\d.]+) )?(-?[\d.]+) (-?[\d.]+) ([mlc])\n")
                .Select(m => (double.Parse(m.Groups[5].Value, CultureInfo.InvariantCulture),
                              double.Parse(m.Groups[6].Value, CultureInfo.InvariantCulture),
                              m.Groups[7].Value == "c"))
                .ToList();

        private const string WideBase = "<m:r><m:t>a</m:t></m:r><m:r><m:t>+wwwwww+</m:t></m:r><m:r><m:t>z</m:t></m:r>";

        /// <summary>
        /// Under a base wider than the glyph, the default under-brace is drawn: curves from the
        /// base's left edge to past its last letter's, below the baseline — not the glyph centred at
        /// its natural width, which is reported. Independent of the host's fonts: an uncovered
        /// glyph measures narrower still.
        /// </summary>
        [Fact]
        public void GroupCharacter_UnderAWideBase_IsADrawnBraceAsWideAsTheBase()
        {
            var reports = new List<string>();
            int collectingThread = System.Environment.CurrentManagedThreadId;
            void Collect(string m) { if (System.Environment.CurrentManagedThreadId == collectingThread) reports.Add(m); }
            DocxToPdfConverter.DiagnosticReported += Collect;
            string pdf;
            try { pdf = Convert(MathPara($"<m:groupChr><m:e>{WideBase}</m:e></m:groupChr>")); }
            finally { DocxToPdfConverter.DiagnosticReported -= Collect; }

            var a = Find(pdf, "a");
            var z = Find(pdf, "z");
            var points = PathPoints(pdf);

            Assert.True(points.Count(p => p.curve) >= 4, "the brace was not drawn as curves: " + points.Count);
            Assert.InRange(points.Min(p => p.x), a.x - 1, a.x + 1);
            Assert.True(points.Max(p => p.x) > z.x + 2, $"the brace ends at {points.Max(p => p.x)}, short of z at {z.x}");
            Assert.True(points.Max(p => p.y) < a.y, "the brace is not below the base");
            Assert.DoesNotContain(Glyphs(pdf), g => g.text.Contains('⏟'));
            Assert.DoesNotContain(reports, m => m.Contains("natural width", StringComparison.Ordinal));

            // It curls: at half its height the path is one curl in from each end, not at the end.
            double minX = points.Min(p => p.x), maxX = points.Max(p => p.x);
            double midY = (points.Min(p => p.y) + points.Max(p => p.y)) / 2;
            var atMid = points.Where(p => Math.Abs(p.y - midY) < 0.5).Select(p => p.x).ToList();
            Assert.Contains(atMid, x => x > minX + 0.5 && x < (minX + maxX) / 2 - 0.5);
            // And points down at its centre: its lowest point is there.
            Assert.Contains(points, p => Math.Abs(p.x - (minX + maxX) / 2) < 0.5 && Math.Abs(p.y - points.Min(q => q.y)) < 0.01);
        }

        /// <summary>
        /// The other drawn forms, each spanning the base on its side of it: the square brackets and
        /// the left and both-way arrows straight, the parentheses curved; nothing reported.
        /// </summary>
        [Theory]
        [InlineData("⎵", false, false)]
        [InlineData("⎴", true, false)]
        [InlineData("⏝", false, true)]
        [InlineData("⏜", true, true)]
        [InlineData("←", true, false)]
        [InlineData("↔", false, false)]
        public void GroupCharacter_OtherDrawnForms_SpanTheBase(string chr, bool top, bool curved)
        {
            var reports = new List<string>();
            int collectingThread = System.Environment.CurrentManagedThreadId;
            void Collect(string m) { if (System.Environment.CurrentManagedThreadId == collectingThread) reports.Add(m); }
            DocxToPdfConverter.DiagnosticReported += Collect;
            string pdf;
            try
            {
                pdf = Convert(MathPara(
                    $"<m:groupChr><m:groupChrPr><m:chr m:val=\"{chr}\"/>{(top ? "<m:pos m:val=\"top\"/>" : "")}</m:groupChrPr>" +
                    $"<m:e>{WideBase}</m:e></m:groupChr>"));
            }
            finally { DocxToPdfConverter.DiagnosticReported -= Collect; }

            var a = Find(pdf, "a");
            var z = Find(pdf, "z");
            var points = PathPoints(pdf);
            Assert.InRange(points.Min(p => p.x), a.x - 1, a.x + 1);
            Assert.True(points.Max(p => p.x) > z.x + 2, "the shape stops short of the base's end");
            Assert.Equal(curved, points.Any(p => p.curve));
            if (curved)
            {
                // A parenthesis bows away from its base: its curve's control points lie beyond its
                // ends, below them under the base and above them over it — not a flat stroke.
                var c = Regex.Match(pdf, @"(-?[\d.]+) (-?[\d.]+) (-?[\d.]+) (-?[\d.]+) (-?[\d.]+) (-?[\d.]+) c\n");
                double control = double.Parse(c.Groups[2].Value, CultureInfo.InvariantCulture);
                double end = double.Parse(c.Groups[6].Value, CultureInfo.InvariantCulture);
                Assert.True(top ? control > end + 2 : control < end - 2, $"the curve does not bow away from the base: control {control}, end {end}");
            }
            if (top) Assert.True(points.Min(p => p.y) > a.y, "not above the base");
            else Assert.True(points.Max(p => p.y) < a.y, "not below the base");
            Assert.DoesNotContain(reports, m => m.Contains("natural width", StringComparison.Ordinal));
        }

        /// <summary>The top curly bracket over its base (<c>m:pos top</c>) is drawn above the base's baseline, its arms ending at its bottom.</summary>
        [Fact]
        public void GroupCharacter_TopBrace_IsDrawnAboveTheBase()
        {
            string pdf = Convert(MathPara(
                "<m:groupChr><m:groupChrPr><m:chr m:val=\"⏞\"/><m:pos m:val=\"top\"/></m:groupChrPr>" +
                $"<m:e>{WideBase}</m:e></m:groupChr>"));

            var a = Find(pdf, "a");
            var points = PathPoints(pdf);
            Assert.True(points.Count(p => p.curve) >= 4, "the brace was not drawn as curves");
            Assert.True(points.Min(p => p.y) > a.y, "the brace is not above the base");
            // Its arms end at its lowest points, at both ends: the first point and the last.
            Assert.Equal(points.Min(p => p.y), points[0].y, 1);
            Assert.Equal(points[0].y, points[points.Count - 1].y, 1);
        }

        /// <summary>A right arrow over a wide base is a shaft as wide as the base with a head at its right end — straight strokes, no curves.</summary>
        [Fact]
        public void GroupCharacter_ArrowOverAWideBase_IsAShaftAsWideAsTheBase()
        {
            string pdf = Convert(MathPara(
                "<m:groupChr><m:groupChrPr><m:chr m:val=\"→\"/><m:pos m:val=\"top\"/></m:groupChrPr>" +
                $"<m:e>{WideBase}</m:e></m:groupChr>"));

            var a = Find(pdf, "a");
            var z = Find(pdf, "z");
            var points = PathPoints(pdf);
            Assert.DoesNotContain(points, p => p.curve);
            Assert.InRange(points.Min(p => p.x), a.x - 1, a.x + 1);
            Assert.True(points.Max(p => p.x) > z.x + 2, "the shaft stops short of the base's end");
            // The head is at the right: the point furthest right is the head's tip, reached by the shaft and by the head.
            double right = points.Max(p => p.x);
            Assert.Equal(2, points.Count(p => Math.Abs(p.x - right) < 0.01));
        }

        /// <summary>The control: a character with no drawn form under a wide base is still the glyph at its natural width, reported.</summary>
        [Fact]
        public void GroupCharacter_WithNoDrawnForm_IsReported()
        {
            var reports = new List<string>();
            int collectingThread = System.Environment.CurrentManagedThreadId;
            void Collect(string m) { if (System.Environment.CurrentManagedThreadId == collectingThread) reports.Add(m); }
            DocxToPdfConverter.DiagnosticReported += Collect;
            string pdf;
            try
            {
                pdf = Convert(MathPara(
                    $"<m:groupChr><m:groupChrPr><m:chr m:val=\"~\"/></m:groupChrPr><m:e>{WideBase}</m:e></m:groupChr>"));
            }
            finally { DocxToPdfConverter.DiagnosticReported -= Collect; }

            Assert.Contains(Glyphs(pdf), g => g.text == "~");
            Assert.Empty(PathPoints(pdf));
            Assert.Contains(reports, m => m.Contains("natural width", StringComparison.Ordinal));
        }

        /// <summary>
        /// <c>m:box</c> has no visual form, so its content is typeset exactly as if the box were
        /// absent -- <b>not</b> linearised, which is what it used to get and which flattens
        /// whatever 2-D structure it contains. A fraction inside draws a rule; a linearised one
        /// draws "a/b" and no rule, so the rule count is precisely the difference.
        /// </summary>
        [Fact]
        public void LogicalBox_TypesetsItsContentRatherThanLinearisingIt()
        {
            string boxed = Convert(MathPara(
                $"<m:box><m:e><m:f><m:num>{R("a")}</m:num><m:den>{R("b")}</m:den></m:f></m:e></m:box>"));
            string bare = Convert(MathPara(
                $"<m:f><m:num>{R("a")}</m:num><m:den>{R("b")}</m:den></m:f>"));

            Assert.True(Rules(bare).Count > 0, "the control drew no rule, so this asserts nothing");
            Assert.Equal(Rules(bare).Count, Rules(boxed).Count);
        }

        /// <summary><c>m:borderBox</c> strokes a rectangle: four rules where a bare run draws none.</summary>
        [Fact]
        public void BorderBox_StrokesItsFourEdges()
        {
            Assert.Empty(Rules(Convert(MathPara(R("x")))));
            Assert.Equal(4, Rules(Convert(MathPara(
                $"<m:borderBox><m:e>{R("x")}</m:e></m:borderBox>"))).Count);
        }

        /// <summary>
        /// The edge flags are <b>hide</b> flags, so a plain <c>&lt;m:borderBox/&gt;</c> -- which
        /// carries no properties at all -- draws every edge. Reading them as "show" flags gives an
        /// empty border on every real document, and the case above would still pass.
        /// </summary>
        [Fact]
        public void BorderBox_HideFlagsSuppressEdgesRatherThanEnablingThem()
        {
            int hidden = Rules(Convert(MathPara(
                "<m:borderBox><m:borderBoxPr><m:hideTop/><m:hideBot/></m:borderBoxPr>" +
                $"<m:e>{R("x")}</m:e></m:borderBox>"))).Count;

            Assert.Equal(2, hidden);
        }

        /// <summary>
        /// <c>m:strikeH</c> adds the cancel bar, asserted apart from the edges so an
        /// implementation drawing a fixed four rules could not satisfy it.
        /// </summary>
        [Fact]
        public void BorderBox_StrikeHAddsARule()
        {
            int plain = Rules(Convert(MathPara(
                $"<m:borderBox><m:e>{R("x")}</m:e></m:borderBox>"))).Count;
            int struck = Rules(Convert(MathPara(
                "<m:borderBox><m:borderBoxPr><m:strikeH/></m:borderBoxPr>" +
                $"<m:e>{R("x")}</m:e></m:borderBox>"))).Count;

            Assert.Equal(plain + 1, struck);
        }

        /// <summary>
        /// <c>m:phant</c> reserves space and draws nothing. Both halves matter: dropping the
        /// element removes exactly the space it exists to reserve, so the alignment it was placed
        /// for silently fails -- and drawing it defeats the point.
        /// </summary>
        [Fact]
        public void Phantom_ReservesSpaceWithoutDrawing()
        {
            string withPhantom = Convert(MathPara(
                $"<m:phant><m:e>{R("MMM")}</m:e></m:phant>{R("x")}"));
            string without = Convert(MathPara(R("x")));

            Assert.DoesNotContain(Glyphs(withPhantom), t => t.text == "MMM");
            Assert.True(Find(withPhantom, "x").x > Find(without, "x").x + 1,
                "the phantom reserved no width, so it cannot align anything");
        }

        /// <summary>
        /// <c>m:zeroWid</c> drops the reserved width -- the flag that lets a phantom overlap its
        /// neighbour. Without this the case above would pass for an implementation ignoring every
        /// flag.
        /// </summary>
        [Fact]
        public void Phantom_ZeroWidthReservesNoWidth()
        {
            string zeroWid = Convert(MathPara(
                "<m:phant><m:phantPr><m:zeroWid/></m:phantPr>" +
                $"<m:e>{R("MMM")}</m:e></m:phant>{R("x")}"));
            string without = Convert(MathPara(R("x")));

            Assert.True(Math.Abs(Find(zeroWid, "x").x - Find(without, "x").x) < 0.05,
                "a zero-width phantom must not move what follows it");
        }

        // ------------------------------------------------------------------ exact geometry
        //
        // The tests above compare positions with each other, so a shift that moves two things by
        // the same amount, or a clearance set to zero, passes them. These pin the builder's
        // arithmetic against the font's own metrics -- MathMeasure.VerticalOf and AxisHeight, the
        // inputs the builder reads, never its outputs -- to the 0.01 pt the operators are written
        // at.

        /// <summary>
        /// The vertical metrics of the faces a fixture drew in, by their <c>/BaseFont</c> — one family's, the
        /// upright face and the italic one the variables are drawn in, which share them. Each glyph is drawn in
        /// the face it was measured in (Wave 22, DocxToPdf-3:9); before, every glyph was drawn upright, and the
        /// fixture held one face.
        /// </summary>
        private static (double h, double d) Vertical(string pdf, double size)
            => SharedByTheFacesDrawn(pdf, f => Rendering.MathTypesetting.MathMeasure.VerticalOf(f, size));

        /// <summary>The maths axis of the faces a fixture drew in, which they share as they share <see cref="Vertical"/>.</summary>
        private static double Axis(string pdf, double size)
            => SharedByTheFacesDrawn(pdf, f => Rendering.MathTypesetting.MathMeasure.AxisHeight(f, size));

        private static T SharedByTheFacesDrawn<T>(string pdf, Func<string, T> metric)
        {
            var fonts = Regex.Matches(pdf, @"/BaseFont /([A-Za-z-]+)").Select(m => m.Groups[1].Value)
                .Distinct().ToList();
            Assert.NotEmpty(fonts);
            var values = fonts.Select(metric).Distinct().ToList();
            Assert.True(values.Count == 1, "the faces drawn do not share the metric: " + string.Join(", ", fonts));
            return values[0];
        }

        private static void AssertPt(double expected, double actual, string what)
            => Assert.True(Math.Abs(expected - actual) <= 0.02,
                $"{what}: expected {expected:F3}, measured {actual:F3}");

        private static List<string> ConvertCollecting(string paragraphXml, out string pdf)
        {
            var reports = new List<string>();
            int collectingThread = System.Environment.CurrentManagedThreadId;
            void Collect(string m) { if (System.Environment.CurrentManagedThreadId == collectingThread) reports.Add(m); }
            DocxToPdfConverter.DiagnosticReported += Collect;
            try { pdf = Convert(paragraphXml); }
            finally { DocxToPdfConverter.DiagnosticReported -= Collect; }
            return reports;
        }

        /// <summary>
        /// The numerator's descent clears the top of the bar by exactly the inline gap, and the bar
        /// clears the denominator's ascent by the same gap -- the clearance TeX calls phi.
        /// </summary>
        [Fact]
        public void Fraction_OperandsClearTheBarByExactlyTheGap()
        {
            string pdf = Convert(MathPara($"<m:f><m:num>{R("a")}</m:num><m:den>{R("b")}</m:den></m:f>"));

            var num = Find(pdf, "a");
            var den = Find(pdf, "b");
            var bar = Rules(pdf).Single();
            double em = num.size / Rendering.MathTypesetting.MathLayoutMetrics.ScriptScaleLevel1;
            double gap = em * Rendering.MathTypesetting.MathLayoutMetrics.FractionGapTextEm;

            var (_, numDepth) = Vertical(pdf, num.size);
            var (denHeight, _) = Vertical(pdf, den.size);

            AssertPt(gap, (num.y - numDepth) - (bar[1] + bar[3]), "numerator descent to bar top");
            AssertPt(gap, bar[1] - (den.y + denHeight), "bar bottom to denominator ascent");
        }

        /// <summary>
        /// A skewed or linear fraction has no stacked form: it is linearised -- no bar -- and the
        /// reason reported. Each type on its own, so neither can ride on the other.
        /// </summary>
        [Theory]
        [InlineData("skw")]
        [InlineData("lin")]
        public void Fraction_SkewedOrLinearType_IsLinearisedAndReported(string type)
        {
            var reports = ConvertCollecting(MathPara(
                $"<m:f><m:fPr><m:type m:val=\"{type}\"/></m:fPr>" +
                $"<m:num>{R("a")}</m:num><m:den>{R("b")}</m:den></m:f>"), out string pdf);

            Assert.Empty(Rules(pdf));
            Assert.Contains(reports, m => m.Contains("skewed or linear"));
            Assert.Contains(Glyphs(pdf), g => g.text.Contains("a"));
        }

        /// <summary>The control: a <c>bar</c> fraction is stacked, with its bar, and nothing reported.</summary>
        [Fact]
        public void Fraction_BarType_IsStackedWithItsBar()
        {
            var reports = ConvertCollecting(MathPara(
                "<m:f><m:fPr><m:type m:val=\"bar\"/></m:fPr>" +
                $"<m:num>{R("a")}</m:num><m:den>{R("b")}</m:den></m:f>"), out string pdf);

            Assert.Single(Rules(pdf));
            Assert.DoesNotContain(reports, m => m.Contains("skewed or linear"));
        }

        /// <summary>
        /// <c>noBar</c> is a stacked fraction without the bar (a binomial coefficient's inside):
        /// the operands still stack, and no rule is drawn.
        /// </summary>
        [Fact]
        public void Fraction_NoBarType_StacksWithoutARule()
        {
            string pdf = Convert(MathPara(
                "<m:f><m:fPr><m:type m:val=\"noBar\"/></m:fPr>" +
                $"<m:num>{R("a")}</m:num><m:den>{R("b")}</m:den></m:f>"));

            Assert.Empty(Rules(pdf));
            Assert.True(Find(pdf, "a").y > Find(pdf, "b").y + 6.0, "the operands must still be stacked");
        }

        /// <summary>
        /// The nesting ceiling is <c>depth &gt; 32</c>. The builder starts at the <c>m:oMath</c>
        /// (depth 0); under <paramref name="wrappers"/> nested <c>m:e</c> elements the run sits at
        /// depth <c>wrappers + 1</c> and its text at <c>wrappers + 2</c>. Thirty wrappers put the
        /// text at exactly 32 -- typeset, nothing reported; thirty-one put it at 33.
        /// </summary>
        [Theory]
        [InlineData(30, false)]
        [InlineData(31, true)]
        public void NestingCeiling_IsExclusiveOfThirtyTwo(int wrappers, bool reported)
        {
            string inner = R("x");
            for (int i = 0; i < wrappers; i++) inner = "<m:e>" + inner + "</m:e>";

            var reports = ConvertCollecting(MathPara(inner), out string pdf);

            Assert.Equal(reported, reports.Any(m => m.Contains("ceiling")));
            Assert.Contains(Glyphs(pdf), g => g.text == "x");
        }

        /// <summary>
        /// The cell cap is <c>cells &gt; 400</c>: a 20 x 20 matrix is drawn whole and nothing is
        /// reported; one cell more is reported, and the cell past the cap is not drawn.
        /// </summary>
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Matrix_OfExactlyTheCellCap_IsDrawnWhole(bool oneMore)
        {
            var rows = new StringBuilder();
            for (int r = 0; r < 20; r++) rows.Append(MatrixRow(Enumerable.Repeat("x", 20).ToArray()));
            if (oneMore) rows.Append(MatrixRow("x"));

            var reports = ConvertCollecting(MathPara("<m:m>" + rows + "</m:m>"), out string pdf);

            Assert.Equal(oneMore, reports.Any(m => m.Contains("cells")));
            Assert.Equal(Rendering.MathTypesetting.MathLayoutMetrics.MaxMatrixCells,
                Glyphs(pdf).Count(g => g.text == "x"));
        }

        /// <summary>
        /// A border box's top edge sits one padding above the content's ascent -- the padding ring
        /// is outside the content, not cut into it.
        /// </summary>
        [Fact]
        public void BorderBox_TopEdgeSitsOnePaddingAboveTheContent()
        {
            string pdf = Convert(MathPara($"<m:borderBox><m:e>{R("x")}</m:e></m:borderBox>"));

            var content = Find(pdf, "x");
            var top = Rules(pdf).Where(r => r[2] > r[3]).OrderByDescending(r => r[1]).First();
            var (height, _) = Vertical(pdf, content.size);
            double pad = content.size * Rendering.MathTypesetting.MathLayoutMetrics.AccentGapEm;

            AssertPt(height + pad, top[1] - content.y, "content baseline to the top edge");
        }

        /// <summary>
        /// A two-row matrix: the rows are one row's height plus depth plus the row gap apart, and
        /// the block they make is centred on the maths axis of the text beside it.
        /// </summary>
        [Fact]
        public void Matrix_RowsAreOneRowGapApart_AndTheBlockIsCentredOnTheAxis()
        {
            string pdf = Convert($@"<w:p {Ns}><w:r><w:t>Let </w:t></w:r>
<m:oMath><m:m>{MatrixRow("a")}{MatrixRow("c")}</m:m></m:oMath></w:p>");

            var text = Find(pdf, "Let ");
            var top = Find(pdf, "a");
            var bottom = Find(pdf, "c");
            double em = top.size;
            var (h, d) = Vertical(pdf, em);
            double gap = em * Rendering.MathTypesetting.MathLayoutMetrics.StackRowGapEm;
            double axis = Axis(pdf, em);

            AssertPt(h + d + gap, top.y - bottom.y, "row pitch");
            AssertPt(axis, ((top.y + h) + (bottom.y - d)) / 2 - text.y, "block centre above the baseline");
        }

        /// <summary>
        /// A fence is stretched only when its content needs more than 5% beyond the glyph's own
        /// height. A base with an empty superscript and an empty subscript reaches the
        /// subscript's shift below the baseline and nothing else -- a few percent more than the
        /// fence -- so the fence stays at its natural size.
        /// </summary>
        [Fact]
        public void DelimiterAroundContentWithinFivePercent_IsNotStretched()
        {
            string pdf = Convert(MathPara(
                $"<m:d><m:e><m:sSubSup><m:e>{R("x")}</m:e><m:sub></m:sub><m:sup></m:sup></m:sSubSup></m:e></m:d>"));

            double em = Find(pdf, "x").size;
            var (h, d) = Vertical(pdf, em);
            double needed = h + Math.Max(d, em * Rendering.MathTypesetting.MathLayoutMetrics.SubSupSubShiftDownEm);
            double ratio = needed / (h + d);
            Assert.InRange(ratio, 1.001, 1.049);   // guards the fixture: inside the window

            Assert.DoesNotMatch(@"1 0 0 [\d.]+ -?[\d.]+ -?[\d.]+ Tm", pdf);
        }

        /// <summary>
        /// The control, and the scale itself: around a subscripted base the fence needs about 9%
        /// more, and is stretched by exactly the content's height over the glyph's.
        /// </summary>
        [Fact]
        public void DelimiterAroundASubscript_IsStretchedByTheRatioOfHeights()
        {
            string pdf = Convert(MathPara(
                $"<m:d><m:e><m:sSub><m:e>{R("x")}</m:e><m:sub>{R("i")}</m:sub></m:sSub></m:e></m:d>"));

            var b = Find(pdf, "x");
            var sub = Find(pdf, "i");
            var (h, d) = Vertical(pdf, b.size);
            var (_, subDepth) = Vertical(pdf, sub.size);
            double down = b.size * Rendering.MathTypesetting.MathLayoutMetrics.SubscriptShiftDownEm;
            double expected = (h + Math.Max(d, down + subDepth)) / (h + d);

            var stretches = Regex.Matches(pdf, @"1 0 0 ([\d.]+) -?[\d.]+ -?[\d.]+ Tm")
                .Select(m => double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)).ToList();

            Assert.Equal(2, stretches.Count);
            foreach (double s in stretches)
                Assert.True(Math.Abs(s - expected) < 0.0005, $"stretch {s} should be {expected:F4}");
        }

        /// <summary>
        /// Content needing more than three times the fence is drawn at the 3x cap, not at its own
        /// ratio and not at natural size, and the cap is reported.
        /// </summary>
        [Fact]
        public void DelimiterNeedingMoreThanTheCap_IsDrawnAtTheCapAndReported()
        {
            var reports = ConvertCollecting(MathPara(
                "<m:d><m:e><m:m>" + MatrixRow("a") + MatrixRow("b") + MatrixRow("c") + MatrixRow("e") +
                "</m:m></m:e></m:d>"), out string pdf);

            var stretches = Regex.Matches(pdf, @"1 0 0 ([\d.]+) -?[\d.]+ -?[\d.]+ Tm")
                .Select(m => m.Groups[1].Value).ToList();

            Assert.Equal(new[] { "3.0000", "3.0000" }, stretches);
            Assert.Contains(reports, m => m.Contains("cap"));
        }

        /// <summary>
        /// An inline n-ary's limits ride as scripts: the upper one exactly the superscript shift
        /// above the operator's baseline, the lower one the with-superscript subscript shift below.
        /// </summary>
        [Fact]
        public void InlineNary_LimitsSitAtTheScriptShifts()
        {
            string pdf = Convert(MathPara(
                $"<m:nary><m:naryPr><m:chr m:val=\"S\"/></m:naryPr>" +
                $"<m:sub>{R("i")}</m:sub><m:sup>{R("n")}</m:sup><m:e>{R("x")}</m:e></m:nary>"));

            var op = Find(pdf, "S");
            double em = op.size;

            AssertPt(em * Rendering.MathTypesetting.MathLayoutMetrics.SuperscriptShiftUpEm,
                Find(pdf, "n").y - op.y, "upper limit above the operator");
            AssertPt(-em * Rendering.MathTypesetting.MathLayoutMetrics.SubSupSubShiftDownEm,
                Find(pdf, "i").y - op.y, "lower limit below the operator");
        }

        /// <summary>
        /// A function name is followed by a thin space before its argument: the argument sits one
        /// thin space further right than it does after the same name set as a plain upright run.
        /// </summary>
        [Fact]
        public void Function_SetsAThinSpaceBeforeItsArgument()
        {
            string function = Convert(MathPara(
                $"<m:func><m:fName>{R("sin")}</m:fName><m:e>{R("x")}</m:e></m:func>"));
            string plain = Convert(MathPara(
                "<m:r><m:rPr><m:sty m:val=\"p\"/></m:rPr><m:t>sin</m:t></m:r>" + R("x")));

            double withSpace = Find(function, "x").x - Find(function, "sin").x;
            double without = Find(plain, "x").x - Find(plain, "sin").x;
            double em = Find(function, "x").size;

            AssertPt(em * Rendering.MathTypesetting.MathLayoutMetrics.ThinSpaceEm, withSpace - without,
                "thin space after the function name");
        }

    }
}

