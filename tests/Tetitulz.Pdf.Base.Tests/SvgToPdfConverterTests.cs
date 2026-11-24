// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Linq;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Document;
using System.Text;
using Tetitulz.Pdf.Base.Operations;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests
{
    public class SvgToPdfConverterTests
    {
        // Minimal valid SVG wrapper
        private static string Svg(string body) =>
            $@"<svg xmlns=""http://www.w3.org/2000/svg"" width=""200"" height=""200"">{body}</svg>";

        // ------------------------------------------------------------------
        //  Content-stream assertions
        //
        //  These replace a block of `Record.Exception(...); Assert.Null(ex);` tests that asserted
        //  nothing about the conversion: every one of them passed with the converter emitting an
        //  empty page. Asserting on the operators the converter actually wrote is what makes the
        //  name of each test checkable.
        // ------------------------------------------------------------------

        /// <summary>The first page's decoded content stream.</summary>
        private static string ContentOf(byte[] pdf)
        {
            using var doc = PdfDocument.Open(new MemoryStream(pdf));
            Assert.Single(doc.Pages);
            return Encoding.Latin1.GetString(doc.Pages[0].GetContentStreamData());
        }

        /// <summary>Content with parenthesised strings blanked, so text never looks like an operator.</summary>
        private static string WithoutStrings(string content) =>
            System.Text.RegularExpressions.Regex.Replace(content, @"\([^)]*\)", "()");

        /// <summary>Occurrences of a standalone PDF operator token.</summary>
        private static int CountOperator(string content, string op) =>
            System.Text.RegularExpressions.Regex.Matches(
                WithoutStrings(content), $@"(?<![\w.]){System.Text.RegularExpressions.Regex.Escape(op)}(?![\w.])").Count;

        private static float Num(string s) =>
            float.Parse(s, System.Globalization.CultureInfo.InvariantCulture);

        private static void AssertColourOperator(string content, string op, float r, float g, float b)
        {
            var m = System.Text.RegularExpressions.Regex.Match(
                content, $@"([-\d.]+)\s+([-\d.]+)\s+([-\d.]+)\s+{op}(?![\w.])");
            Assert.True(m.Success, $"no `{op}` colour operator was emitted");
            Assert.Equal(r, Num(m.Groups[1].Value), 2);
            Assert.Equal(g, Num(m.Groups[2].Value), 2);
            Assert.Equal(b, Num(m.Groups[3].Value), 2);
        }

        private static void AssertFillColour(string content, float r, float g, float b) =>
            AssertColourOperator(content, "rg", r, g, b);

        private static void AssertStrokeColour(string content, float r, float g, float b) =>
            AssertColourOperator(content, "RG", r, g, b);

        /// <summary>The four operands of the first <c>re</c>, as written — for comparing placements.</summary>
        private static string RectOperands(string content)
        {
            var m = System.Text.RegularExpressions.Regex.Match(
                content, @"([-\d.]+\s+[-\d.]+\s+[-\d.]+\s+[-\d.]+)\s+re(?![\w.])");
            Assert.True(m.Success, "no rectangle was emitted");
            return m.Groups[1].Value;
        }

        /// <summary>
        /// X-extent of every Bézier control point — the width of a curved shape, independent of
        /// where the page flip puts it.
        /// </summary>
        private static float HorizontalSpan(string content)
        {
            float min = float.MaxValue, max = float.MinValue;
            foreach (System.Text.RegularExpressions.Match m in
                     System.Text.RegularExpressions.Regex.Matches(
                         content, @"((?:[-\d.]+\s+){6})c(?![\w.])"))
            {
                var nums = m.Groups[1].Value.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                for (int i = 0; i < 6; i += 2)
                {
                    float x = Num(nums[i]);
                    if (x < min) min = x;
                    if (x > max) max = x;
                }
            }
            Assert.True(max > min, "no curve operators were emitted, so there is no span to measure");
            return max - min;
        }

        // ------------------------------------------------------------------
        //  Hostile input: group nesting depth
        // ------------------------------------------------------------------

        /// <summary>
        /// <c>ProcessNode</c> → <c>EmitGroup</c> → <c>ProcessNode</c> recursed once per nested
        /// <c>&lt;g&gt;</c> with no counter, so roughly 200 KB of open tags overflowed the stack —
        /// and a .NET <c>StackOverflowException</c> cannot be caught, so the process is torn down
        /// with every other request in a server host. The guard's own evidence is asserted, not a
        /// wall clock: the walk stops, says so on <c>LastError</c>, and the over-deep content is
        /// absent from the page.
        /// </summary>
        [Fact]
        public void Convert_AdversarialGroupNestingDepth_IsRefusedAndReported()
        {
            var sb = new StringBuilder(@"<svg xmlns=""http://www.w3.org/2000/svg"" width=""200"" height=""200"">");
            const int levels = 20_000;
            for (int i = 0; i < levels; i++) sb.Append("<g>");
            sb.Append(@"<rect x=""10"" y=""10"" width=""50"" height=""50"" fill=""#ff0000""/>");
            for (int i = 0; i < levels; i++) sb.Append("</g>");
            sb.Append("</svg>");

            byte[] pdf = SvgToPdfConverter.Convert(sb.ToString());

            Assert.NotNull(SvgToPdfConverter.LastError);
            Assert.Contains("nesting", SvgToPdfConverter.LastError);

            // The rectangle sat below the cap, so it must NOT have been drawn — which is what
            // distinguishes a guard that fired from one that silently let the walk through.
            Assert.Equal(0, CountOperator(ContentOf(pdf), "re"));
        }

        /// <summary>
        /// The control for the guard above: ordinary nesting still converts, and
        /// <see cref="SvgToPdfConverter.LastError"/> is cleared per call rather than left holding a
        /// previous document's complaint.
        /// </summary>
        [Fact]
        public void Convert_OrdinaryGroupNesting_DrawsTheContentAndClearsLastError()
        {
            var sb = new StringBuilder(@"<svg xmlns=""http://www.w3.org/2000/svg"" width=""200"" height=""200"">");
            for (int i = 0; i < 20; i++) sb.Append("<g>");
            sb.Append(@"<rect x=""10"" y=""10"" width=""50"" height=""50"" fill=""#ff0000""/>");
            for (int i = 0; i < 20; i++) sb.Append("</g>");
            sb.Append("</svg>");

            byte[] pdf = SvgToPdfConverter.Convert(sb.ToString());

            Assert.Null(SvgToPdfConverter.LastError);
            Assert.Equal(1, CountOperator(ContentOf(pdf), "re"));
        }

        [Fact]
        /// <summary>
        /// An SVG with no elements must still produce a parseable one-page PDF — and that page must
        /// be <b>empty</b>. Both halves matter: "some bytes came back" is equally true of a
        /// truncated file, and of one that drew something the SVG never asked for.
        ///
        /// <para>Note the page size: A4, <b>not</b> the SVG's declared 200x200. Page geometry comes
        /// from <c>SvgConversionOptions</c>, not from the document — worth pinning, because it is
        /// the kind of default a reader would guess wrong.</para>
        /// </summary>
        public void Convert_EmptySvg_ProducesAnEmptyOnePageDocument()
        {
            string svg = @"<svg xmlns=""http://www.w3.org/2000/svg"" width=""200"" height=""200""></svg>";

            byte[] pdf = SvgToPdfConverter.Convert(svg);

            using var doc = PdfDocument.Open(new MemoryStream(pdf));
            Assert.Single(doc.Pages);

            var box = doc.Pages[0].MediaBox;
            Assert.Equal(595.28, ((PdfNumber)box[2]).DoubleValue, 2);
            Assert.Equal(841.89, ((PdfNumber)box[3]).DoubleValue, 2);

            // Nothing was drawn — and the control below proves that check can fail.
            Assert.DoesNotContain(" re", Encoding.Latin1.GetString(pdf));

            byte[] withRect = SvgToPdfConverter.Convert(
                Svg(@"<rect x=""0"" y=""0"" width=""10"" height=""10""/>"));
            Assert.Contains(" re", Encoding.Latin1.GetString(withRect));
        }

        [Fact]
        public void Convert_RectElement_EmitsAFilledRectangle()
        {
            string content = ContentOf(SvgToPdfConverter.Convert(
                Svg(@"<rect x=""10"" y=""10"" width=""100"" height=""50"" fill=""blue""/>")));

            Assert.Contains(" re", content);
            AssertFillColour(content, 0f, 0f, 1f);
            // The rectangle's own dimensions reach the operator — not merely "a rectangle".
            // This is the assertion that caught the converter emitting `0 841.89 0 0 re` for
            // every shape (see Transform.Identity in SvgToPdfConverter).
            var ops = RectOperands(content).Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal(10f,  Num(ops[0]), 2);
            Assert.Equal(100f, Num(ops[2]), 2);
            Assert.Equal(50f,  Num(ops[3]), 2);
        }

        [Fact]
        public void Convert_CircleElement_EmitsBezierCurvesInTheFillColour()
        {
            string content = ContentOf(SvgToPdfConverter.Convert(
                Svg(@"<circle cx=""50"" cy=""50"" r=""30"" fill=""red""/>")));

            // A circle has no PDF primitive; it is four Béziers.
            Assert.True(CountOperator(content, "c") >= 4,
                $"expected at least 4 curve operators, got {CountOperator(content, "c")}");
            AssertFillColour(content, 1f, 0f, 0f);
        }

        [Fact]
        public void Convert_LineElement_EmitsAStrokedSegment()
        {
            string content = ContentOf(SvgToPdfConverter.Convert(
                Svg(@"<line x1=""0"" y1=""0"" x2=""100"" y2=""100"" stroke=""black""/>")));

            Assert.Equal(1, CountOperator(content, "m"));
            Assert.Equal(1, CountOperator(content, "l"));
            Assert.True(CountOperator(content, "S") >= 1, "the line was never stroked");
        }

        [Fact]
        public void Convert_TextElement_EmitsTheStringInATextObject()
        {
            string content = ContentOf(SvgToPdfConverter.Convert(
                Svg(@"<text x=""10"" y=""30"" font-size=""14"">Hello PDF</text>")));

            Assert.Contains("BT", content);
            Assert.Contains("ET", content);
            Assert.Contains("Hello PDF", content);
            Assert.Contains("14", content);   // the font size reaches Tf
        }

        [Fact]
        public void Convert_PathElement_EmitsMoveLineCloseAndFill()
        {
            string content = ContentOf(SvgToPdfConverter.Convert(
                Svg(@"<path d=""M 10 10 L 100 10 L 100 100 Z"" fill=""green""/>")));

            Assert.Equal(1, CountOperator(content, "m"));
            Assert.Equal(2, CountOperator(content, "l"));
            Assert.True(CountOperator(content, "h") >= 1, "the Z (closepath) was dropped");
            // SVG "green" is #008000, not #00FF00 — a converter guessing full green fails here.
            AssertFillColour(content, 0f, 0.502f, 0f);
        }

        [Fact]
        public void Convert_GroupElement_AppliesTheTranslateToItsChild()
        {
            string translated = ContentOf(SvgToPdfConverter.Convert(
                Svg(@"<g transform=""translate(10,20)""><rect x=""0"" y=""0"" width=""50"" height=""50""/></g>")));
            string plain = ContentOf(SvgToPdfConverter.Convert(
                Svg(@"<rect x=""0"" y=""0"" width=""50"" height=""50""/>")));

            // Same rectangle, different placement: the group transform must move it.
            Assert.Contains(" re", translated);
            Assert.NotEqual(RectOperands(plain), RectOperands(translated));
        }

        [Fact]
        public void Convert_NullStream_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => SvgToPdfConverter.Convert((Stream)null));
        }

        [Fact]
        public void Convert_OutputStartsWithPdf()
        {
            string svg = Svg(@"<rect x=""0"" y=""0"" width=""10"" height=""10""/>");
            byte[] pdf = SvgToPdfConverter.Convert(svg);
            string header = Encoding.ASCII.GetString(pdf, 0, Math.Min(8, pdf.Length));
            Assert.StartsWith("%PDF-", header);
        }

        [Fact]
        public void Convert_WithViewBox_ScalesTheContentToThePage()
        {
            string scaled = ContentOf(SvgToPdfConverter.Convert(
                @"<svg xmlns=""http://www.w3.org/2000/svg"" viewBox=""0 0 100 100"">
                    <rect x=""10"" y=""10"" width=""80"" height=""80""/>
                  </svg>"));
            string unscaled = ContentOf(SvgToPdfConverter.Convert(
                @"<svg xmlns=""http://www.w3.org/2000/svg"" width=""100"" height=""100"">
                    <rect x=""10"" y=""10"" width=""80"" height=""80""/>
                  </svg>"));

            Assert.Contains(" re", scaled);
            // A viewBox maps the user coordinate system onto the page, so the same rectangle
            // cannot land on the same operands as one with no viewBox at all.
            Assert.NotEqual(RectOperands(unscaled), RectOperands(scaled));
        }

        [Fact]
        public void Convert_RgbColor_ParsesTheComponents()
        {
            string content = ContentOf(SvgToPdfConverter.Convert(
                Svg(@"<rect x=""0"" y=""0"" width=""50"" height=""50"" fill=""rgb(255,128,0)""/>")));

            AssertFillColour(content, 1f, 128f / 255f, 0f);
        }

        [Fact]
        public void Convert_NamedColor_Red_ResolvesToTheRightTriple()
        {
            string content = ContentOf(SvgToPdfConverter.Convert(
                Svg(@"<circle cx=""50"" cy=""50"" r=""25"" fill=""red"" stroke=""black""/>")));

            AssertFillColour(content, 1f, 0f, 0f);
            AssertStrokeColour(content, 0f, 0f, 0f);
        }

        [Fact]
        public void Convert_EllipseElement_EmitsCurvesInTheHexFillColour()
        {
            string content = ContentOf(SvgToPdfConverter.Convert(
                Svg(@"<ellipse cx=""100"" cy=""100"" rx=""60"" ry=""30"" fill=""#FF00FF""/>")));

            Assert.True(CountOperator(content, "c") >= 4, "the ellipse emitted no curves");
            AssertFillColour(content, 1f, 0f, 1f);
        }

        [Fact]
        public void Convert_PolylineElement_IsStrokedAndNotFilled()
        {
            string content = ContentOf(SvgToPdfConverter.Convert(
                Svg(@"<polyline points=""0,0 50,50 100,0"" stroke=""blue"" fill=""none""/>")));

            Assert.Equal(1, CountOperator(content, "m"));
            Assert.Equal(2, CountOperator(content, "l"));
            Assert.True(CountOperator(content, "S") >= 1, "the polyline was never stroked");
            // fill="none" is the point of the element — a polyline that fills is wrong.
            Assert.Equal(0, CountOperator(content, "f"));
        }

        [Fact]
        public void Convert_PolygonElement_IsClosedAndFilled()
        {
            string content = ContentOf(SvgToPdfConverter.Convert(
                Svg(@"<polygon points=""50,0 100,100 0,100"" fill=""yellow""/>")));

            Assert.Equal(1, CountOperator(content, "m"));
            Assert.Equal(2, CountOperator(content, "l"));
            // A polygon is filled and a polyline is stroked-open; `f` closes the subpath
            // implicitly (§8.5.3.3), so an explicit `h` is not required for the fill to be right.
            Assert.True(CountOperator(content, "f") >= 1, "the polygon was never filled");
            Assert.Equal(0, CountOperator(content, "S"));
            AssertFillColour(content, 1f, 1f, 0f);

            // The three vertices must be three DISTINCT points.
            var xs = System.Text.RegularExpressions.Regex.Matches(content, @"([-\d.]+)\s+[-\d.]+\s+[ml](?![\w.])")
                .Select(m => Num(m.Groups[1].Value)).ToList();
            Assert.Equal(3, xs.Count);
            Assert.Equal(3, xs.Distinct().Count());
        }

        [Fact]
        /// <summary>
        /// The name promises the two overloads produce the <b>same</b> PDF, so compare them
        /// byte for byte. The predecessor checked only that each was non-empty — which would hold
        /// if the stream overload silently converted a different document, or an empty one.
        /// </summary>
        public void Convert_StreamOverload_ProducesSamePdf()
        {
            string svg = Svg(@"<rect x=""0"" y=""0"" width=""10"" height=""10""/>");
            byte[] fromString = SvgToPdfConverter.Convert(svg);

            using var ms = new MemoryStream(Encoding.UTF8.GetBytes(svg));
            byte[] fromStream = SvgToPdfConverter.Convert(ms);

            Assert.Equal(fromString, fromStream);

            // ...and the shared output really contains the converted rectangle, so equality is not
            // agreement on emptiness.
            Assert.Contains(" re", Encoding.Latin1.GetString(fromString));
        }

        [Fact]
        public void Convert_CustomPageSize_ReflectedInOutput()
        {
            string svg = Svg(@"<rect x=""0"" y=""0"" width=""10"" height=""10""/>");
            var opts = new SvgConversionOptions { PageWidth = 400f, PageHeight = 600f };
            byte[] pdf = SvgToPdfConverter.Convert(svg, opts);
            string text = Encoding.Latin1.GetString(pdf);
            Assert.Contains("400", text);
            Assert.Contains("600", text);
        }

        [Fact]
        public void Convert_GroupWithScale_EnlargesItsChild()
        {
            string scaled = ContentOf(SvgToPdfConverter.Convert(
                Svg(@"<g transform=""scale(2,2)""><circle cx=""25"" cy=""25"" r=""10""/></g>")));
            string plain = ContentOf(SvgToPdfConverter.Convert(
                Svg(@"<circle cx=""25"" cy=""25"" r=""10""/>")));

            // The scaled circle must span twice the extent of the unscaled one.
            float scaledSpan = HorizontalSpan(scaled);
            float plainSpan  = HorizontalSpan(plain);
            Assert.True(scaledSpan > plainSpan * 1.8f,
                $"scale(2,2) did not enlarge the circle: {plainSpan} -> {scaledSpan}");
        }

        [Fact]
        public void Convert_HashRgbColor_ExpandsTheThreeDigitForm()
        {
            string content = ContentOf(SvgToPdfConverter.Convert(
                Svg(@"<rect x=""0"" y=""0"" width=""50"" height=""50"" fill=""#F0A""/>")));

            // #F0A is #FF00AA — each digit doubled, not zero-padded.
            AssertFillColour(content, 1f, 0f, 0xAA / 255f);
        }

        [Fact]
        /// <summary>
        /// <c>PdfOperations.ConvertSvgToPdf(string)</c> takes a FILE PATH, and must produce what the
        /// direct converter produces from the same markup.
        ///
        /// <para><b>A file on disk, not markup.</b> <c>SvgToPdfConverter</c> has both a
        /// <c>Convert(Stream)</c> and a <c>Convert(string svgXml)</c>, so a path bound to the markup
        /// overload would hand the parser the path text and never open a file. Passing markup to the
        /// path parameter would let "the facade matches the direct converter" pass while missing that,
        /// because both sides would be given the same markup.</para>
        /// </summary>
        public void PdfOperations_ConvertSvgToPdf_ReadsTheFileAtThePathItIsGiven()
        {
            string svg  = Svg(@"<rect x=""5"" y=""5"" width=""50"" height=""50""/>");
            string path = Path.Combine(Path.GetTempPath(), $"tetitulz-svg-{Guid.NewGuid():N}.svg");
            File.WriteAllText(path, svg);
            try
            {
                byte[] viaFacade = PdfOperations.ConvertSvgToPdf(path);
                byte[] viaDirect = SvgToPdfConverter.Convert(svg);

                Assert.Equal(viaDirect, viaFacade);
                Assert.Contains(" re", Encoding.Latin1.GetString(viaFacade));
            }
            finally { if (File.Exists(path)) File.Delete(path); }
        }

        [Fact]
        /// <summary>
        /// A missing file reports itself as a missing file. It used to reach the XML parser and come
        /// back as <c>XmlException: Data at the root level is invalid</c>, which blames the caller's
        /// SVG for a file that was never opened.
        /// </summary>
        public void PdfOperations_ConvertSvgToPdf_MissingFile_ThrowsFileNotFound()
        {
            string missing = Path.Combine(Path.GetTempPath(), $"tetitulz-absent-{Guid.NewGuid():N}.svg");

            Assert.Throws<FileNotFoundException>(() => PdfOperations.ConvertSvgToPdf(missing));
        }

        // ==================================================================
        //  Assembly-scan findings — MEDIUM and LOW
        // ==================================================================

        /// <summary>The four operands of the first <c>re</c>, parsed.</summary>
        private static float[] RectNumbers(string content) =>
            RectOperands(content).Split((char[])null, StringSplitOptions.RemoveEmptyEntries)
                .Select(Num).ToArray();

        [Fact]
        /// <summary>
        /// An outer <c>scale(s)</c> around an inner <c>translate(b)</c> must place a point at
        /// <c>s·(b + x)</c>. The four-field <c>Tx/Ty/Sx/Sy</c> transform composed by
        /// <c>tx += parent.Tx; sx *= parent.Sx</c> and produced <c>s·x + b</c> instead, so the
        /// rectangle below landed at x = 10 where every SVG renderer puts it at x = 20.
        ///
        /// <para>The exact coordinate is the assertion on purpose: "the two differ" would also hold
        /// for the wrong answer.</para>
        /// </summary>
        public void Convert_ScaleAroundNestedTranslate_ScalesTheChildTranslateToo()
        {
            string content = ContentOf(SvgToPdfConverter.Convert(
                Svg(@"<g transform=""scale(2)""><g transform=""translate(10,0)"">
                        <rect x=""0"" y=""0"" width=""5"" height=""5""/></g></g>")));

            float[] ops = RectNumbers(content);
            Assert.Equal(20f, ops[0], 2);   // 2 x (10 + 0), not 2 x 0 + 10
            Assert.Equal(10f, ops[2], 2);   // and the scale still reaches the width
        }

        [Fact]
        /// <summary>
        /// A <c>transform</c> list is applied left to right, so <c>translate</c> then <c>scale</c>
        /// is not the same placement as <c>scale</c> then <c>translate</c>. The predecessor
        /// searched the whole attribute for the first <c>translate(</c> and the first
        /// <c>scale(</c> and merged them component-wise, which made the two identical.
        /// </summary>
        public void Convert_TransformListOrder_ChangesThePlacement()
        {
            string translateFirst = ContentOf(SvgToPdfConverter.Convert(
                Svg(@"<g transform=""translate(10,0) scale(2)"">
                        <rect x=""1"" y=""0"" width=""5"" height=""5""/></g>")));
            string scaleFirst = ContentOf(SvgToPdfConverter.Convert(
                Svg(@"<g transform=""scale(2) translate(10,0)"">
                        <rect x=""1"" y=""0"" width=""5"" height=""5""/></g>")));

            Assert.Equal(12f, RectNumbers(translateFirst)[0], 2);   // 10 + 2*1
            Assert.Equal(22f, RectNumbers(scaleFirst)[0], 2);       // 2*(10 + 1)
        }

        /// <summary>
        /// Path data whose final coordinate pair is truncated by the input must not index past the
        /// token list. <c>d="M 10"</c> threw <c>ArgumentOutOfRangeException</c> out of the public
        /// <c>Convert</c> entry points, from a parser whose <c>default:</c> branch already tolerates
        /// junk. The guard's own evidence is asserted: a parseable PDF plus the reported reason.
        /// </summary>
        [Theory]
        [InlineData("M 10")]
        [InlineData("L 5")]
        [InlineData("M 0 0 L 1")]
        public void Convert_MalformedPathWithDanglingCoordinate_ReportsInsteadOfThrowing(string d)
        {
            byte[] pdf = SvgToPdfConverter.Convert(Svg($@"<path d=""{d}""/>"));

            using var doc = PdfDocument.Open(new MemoryStream(pdf));
            Assert.Single(doc.Pages);

            Assert.NotNull(SvgToPdfConverter.LastError);
            Assert.Contains("half-written coordinate pair", SvgToPdfConverter.LastError);
        }

        [Fact]
        /// <summary>
        /// A well-formed path is unaffected by the pair guard and reports nothing — without this
        /// the theory above would pass for a converter that reported on every path.
        /// </summary>
        public void Convert_WellFormedPath_ReportsNothing()
        {
            ContentOf(SvgToPdfConverter.Convert(Svg(@"<path d=""M 10 10 L 20 20 Z"" fill=""blue""/>")));

            Assert.Null(SvgToPdfConverter.LastError);
        }

        /// <summary>
        /// <c>System.Convert.ToByte(s, 16)</c> throws <c>FormatException</c> on a non-hex digit, so
        /// <c>fill='#gggggg'</c> escaped as an exception out of a public API whose every other
        /// colour branch falls back to black.
        /// </summary>
        [Theory]
        [InlineData("#gggggg")]
        [InlineData("#xyz")]
        public void Convert_MalformedHexColour_FallsBackToBlackAndReports(string fill)
        {
            string content = ContentOf(SvgToPdfConverter.Convert(
                Svg($@"<rect x=""0"" y=""0"" width=""10"" height=""10"" fill=""{fill}""/>")));

            AssertFillColour(content, 0f, 0f, 0f);
            Assert.NotNull(SvgToPdfConverter.LastError);
            Assert.Contains(fill, SvgToPdfConverter.LastError);
        }

        [Fact]
        /// <summary>An 8-digit <c>#RRGGBBAA</c> is valid CSS/SVG 2; the alpha is dropped, the
        /// colour is not. It used to fall through every branch to the black fallback.</summary>
        public void Convert_EightDigitHexColour_KeepsTheRgbAndDropsTheAlpha()
        {
            string content = ContentOf(SvgToPdfConverter.Convert(
                Svg(@"<rect x=""0"" y=""0"" width=""10"" height=""10"" fill=""#20C08040""/>")));

            AssertFillColour(content, 0x20 / 255f, 0xC0 / 255f, 0x80 / 255f);
        }

        [Fact]
        /// <summary>
        /// Text above U+00FF used to reach the file as <c>?</c> with the conversion reporting
        /// success — at the viewer indistinguishable from a font-substitution problem. The
        /// substitution still happens (the only font offered is Helvetica); what changed is that
        /// it is now <b>reported</b>, naming the characters.
        /// </summary>
        public void Convert_TextOutsideWinAnsi_ReportsTheCharactersItReplaced()
        {
            const string hebrew = "שלום";
            string content = ContentOf(SvgToPdfConverter.Convert(
                Svg($@"<text x=""10"" y=""20"" font-size=""12"">{hebrew}</text>")));

            Assert.Contains("(????) Tj", content);
            Assert.NotNull(SvgToPdfConverter.LastError);
            Assert.Contains("WinAnsiEncoding cannot", SvgToPdfConverter.LastError);
            Assert.Contains("ש", SvgToPdfConverter.LastError);
        }

        [Fact]
        /// <summary>
        /// The characters WinAnsi <i>can</i> carry must reach the file as WinAnsi codes, and the
        /// font must declare that encoding — otherwise code 0x97 resolves through the font's
        /// built-in StandardEncoding to a different glyph than the em dash that was written.
        /// </summary>
        public void Convert_WinAnsiPunctuation_IsEncodedAndDeclared()
        {
            byte[] pdf = SvgToPdfConverter.Convert(
                Svg("<text x=\"10\" y=\"20\" font-size=\"12\">a—b€c</text>"));

            using var doc = PdfDocument.Open(new MemoryStream(pdf));
            string content = Encoding.Latin1.GetString(doc.Pages[0].GetContentStreamData());

            // The WinAnsi CODES, not '?' and not UTF-8: em dash -> 0x97, euro -> 0x80.
            string expected = "(a" + (char)0x97 + "b" + (char)0x80 + "c) Tj";
            Assert.Contains(expected, content);
            Assert.DoesNotContain("?", content);
            Assert.Null(SvgToPdfConverter.LastError);

            var fonts = (PdfDictionary)doc.ResolveObject(doc.Pages[0].Resources[PdfName.Font]);
            var font = fonts.Select(kv => doc.ResolveObject(kv.Value)).OfType<PdfDictionary>().Single();
            Assert.Equal("WinAnsiEncoding", ((PdfName)font[PdfName.Encoding]).Value);
        }

        [Fact]
        /// <summary>
        /// SVG renders <c>stroke="none"</c> as nothing. <c>EmitLine</c> appended <c>S</c>
        /// unconditionally while <c>SetStrokeColor</c> emitted no <c>RG</c>, so the line was drawn
        /// in whatever colour the previous element left in the graphics state — black at the start
        /// of a page.
        /// </summary>
        public void Convert_LineWithStrokeNone_IsNotStroked()
        {
            string content = ContentOf(SvgToPdfConverter.Convert(
                Svg(@"<line x1=""0"" y1=""0"" x2=""100"" y2=""100"" stroke=""none""/>")));

            Assert.Equal(0, CountOperator(content, "S"));
            Assert.Equal(0, CountOperator(content, "f"));
            Assert.Equal(1, CountOperator(content, "n"));   // the no-op painter (§8.5.3.1)
        }

        [Fact]
        /// <summary>
        /// SVG's initial values are <c>fill:black; stroke:none</c>, so a <c>&lt;polyline&gt;</c>
        /// with no attributes is filled and not stroked. This converter stroked every open
        /// polyline.
        /// </summary>
        public void Convert_PolylineWithNoStrokeAttribute_IsFilledNotStroked()
        {
            string content = ContentOf(SvgToPdfConverter.Convert(
                Svg(@"<polyline points=""0,0 50,50 100,0""/>")));

            Assert.Equal(0, CountOperator(content, "S"));
            Assert.True(CountOperator(content, "f") >= 1, "the polyline was not filled");
        }

        [Fact]
        /// <summary>
        /// Without a <c>viewBox</c> nothing maps the SVG coordinate system onto the page — the user
        /// unit is a point and the graphic is drawn at its own size in the bottom-left corner. That
        /// is right for a page sized to the graphic and wrong-looking otherwise, and it used to
        /// happen with no warning: the parsed <c>width</c>/<c>height</c> were stored on the context
        /// and read by nothing.
        /// </summary>
        public void Convert_OversizedSvgWithNoViewBox_ReportsThatItOverflowsThePage()
        {
            ContentOf(SvgToPdfConverter.Convert(
                @"<svg xmlns=""http://www.w3.org/2000/svg"" width=""2000"" height=""2000"">
                    <rect x=""0"" y=""0"" width=""10"" height=""10""/></svg>"));

            Assert.NotNull(SvgToPdfConverter.LastError);
            Assert.Contains("overflows", SvgToPdfConverter.LastError);

            // Control: a viewBox maps the same drawing onto the page, so there is nothing to report.
            ContentOf(SvgToPdfConverter.Convert(
                @"<svg xmlns=""http://www.w3.org/2000/svg"" width=""2000"" height=""2000""
                       viewBox=""0 0 2000 2000""><rect x=""0"" y=""0"" width=""10"" height=""10""/></svg>"));
            Assert.Null(SvgToPdfConverter.LastError);
        }

        [Fact]
        /// <summary>
        /// A percentage resolves against a containing block this converter does not model, so it is
        /// not a length. <c>TryParseAttrF</c> trimmed the <c>%</c> and yielded the number 100 for
        /// <c>width="100%"</c> — a confidently wrong viewport rather than an absent one.
        /// </summary>
        public void Convert_PercentageViewport_IsNotReadAsANumber()
        {
            ContentOf(SvgToPdfConverter.Convert(
                @"<svg xmlns=""http://www.w3.org/2000/svg"" width=""100%"" height=""100%"">
                    <rect x=""0"" y=""0"" width=""10"" height=""10""/></svg>"));

            Assert.NotNull(SvgToPdfConverter.LastError);
            Assert.Contains("percentage", SvgToPdfConverter.LastError);
        }

        [Fact]
        /// <summary>
        /// The diagnostic slot is reset at the start of every conversion, on every path — a slot a
        /// caller reads after a call must never hand back the previous document's answer.
        /// </summary>
        public void LastError_IsResetByTheNextConversion()
        {
            SvgToPdfConverter.Convert(Svg(@"<path d=""M 10""/>"));
            Assert.NotNull(SvgToPdfConverter.LastError);

            SvgToPdfConverter.Convert(Svg(@"<rect x=""0"" y=""0"" width=""5"" height=""5""/>"));
            Assert.Null(SvgToPdfConverter.LastError);
        }
    }
}
