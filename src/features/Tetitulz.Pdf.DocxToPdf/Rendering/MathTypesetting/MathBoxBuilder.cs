// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using DocumentFormat.OpenXml;

namespace Tetitulz.Pdf.DocxToPdf.Rendering.MathTypesetting
{
    /// <summary>
    /// Turns an OMML element into a <see cref="MathBox"/> tree.
    ///
    /// <para>
    /// Matched by <see cref="OpenXmlElement.LocalName"/>, no namespace check and no SDK wrapper
    /// types — the same choice <c>MathLinearizer</c> documents, and for the same reason: it works
    /// regardless of which OMML classes the SDK models.
    /// </para>
    ///
    /// <para>
    /// Anything not handled here falls back to <c>MathLinearizer</c>'s linear string <b>for that
    /// node only</b>, and reports. That keeps the tiering enforceable in code rather than by
    /// omission: a construct is either typeset or explicitly linearised, never silently dropped.
    /// </para>
    /// </summary>
    internal sealed class MathBoxBuilder
    {
        private readonly DocxRenderContext _context;
        private int _nodes;

        internal MathBoxBuilder(DocxRenderContext context) => _context = context;

        internal MathBox Build(OpenXmlElement element, MathStyle style)
        {
            _nodes = 0;
            return BuildNode(element, style, 0);
        }

        private MathBox BuildNode(OpenXmlElement el, MathStyle style, int depth)
        {
            if (el == null) return MathHBox.Phantom(0, 0, 0);

            if (depth > MathLayoutMetrics.MaxNestingDepth || ++_nodes > MathLayoutMetrics.MaxNodeCount)
            {
                DocxDiagnostics.Report("MathBoxBuilder.Build",
                    "the equation exceeds the nesting or node ceiling; the rest was linearised");
                return Linear(el, style);
            }

            try
            {
                switch (el.LocalName)
                {
                    case "t": return MathMeasure.Glyphs(el.InnerText, style, _context);
                    case "r": return Concat(el, style, depth, RunStyle(el, style));

                    case "oMath":
                    case "oMathPara":
                    case "e":
                    case "num":
                    case "den":
                    case "sub":
                    case "sup":
                    case "fName":
                    case "deg":
                    case "lim":
                        return Concat(el, style, depth, style);

                    case "f": return Fraction(el, style, depth);
                    case "sSup": return Scripts(el, style, depth, superscript: true, subscript: false);
                    case "sSub": return Scripts(el, style, depth, superscript: false, subscript: true);
                    case "sSubSup": return Scripts(el, style, depth, superscript: true, subscript: true);
                    case "rad": return Radical(el, style, depth);
                    case "acc": return Accent(el, style, depth);
                    case "bar": return Bar(el, style, depth);
                    case "func": return Function(el, style, depth);
                    case "d": return Delimiter(el, style, depth);
                    case "nary": return Nary(el, style, depth);
                    case "limLow": return Limit(el, style, depth, above: false);
                    case "limUpp": return Limit(el, style, depth, above: true);
                    case "m": return Matrix(el, style, depth);
                    case "eqArr": return EquationArray(el, style, depth);
                    case "sPre": return PreScripts(el, style, depth);
                    case "groupChr": return GroupCharacter(el, style, depth);
                    case "box": return LogicalBox(el, style, depth);
                    case "borderBox": return BorderBox(el, style, depth);
                    case "phant": return PhantomNode(el, style, depth);

                    default:
                        // A construct with no 2-D form. No list of them is given here, because such a
                        // list goes stale one construct at a time:
                        // the dispatch above IS the list, and a prose copy of it can only rot.
                        DocxDiagnostics.Report("MathBoxBuilder.Build",
                            "'m:" + el.LocalName + "' has no 2-D layout yet and was linearised");
                        return Linear(el, style);
                }
            }
            catch (Exception ex)
            {
                DocxDiagnostics.Report("MathBoxBuilder.Build",
                    "'m:" + el.LocalName + "' could not be laid out and was linearised", ex);
                return Linear(el, style);
            }
        }

        private MathBox Linear(OpenXmlElement el, MathStyle style)
            => MathMeasure.Glyphs(MathLinearizer.Linearize(el), style.WithItalic(false), _context);

        private MathBox Concat(OpenXmlElement el, MathStyle outer, int depth, MathStyle style)
        {
            var box = new MathHBox();
            foreach (var child in el.ChildElements)
            {
                // Property elements carry settings, not content.
                if (child.LocalName.EndsWith("Pr", StringComparison.Ordinal)) continue;
                box.Children.Add(BuildNode(child, style, depth + 1));
            }
            return box.Pack();
        }

        /// <summary>
        /// OMML sets variables in italic by default; a run marked <c>m:nor</c> (normal) or given an
        /// explicit style is upright. Nothing in the linear fallback ever did this, so italics are
        /// new fidelity rather than a port.
        /// </summary>
        private static MathStyle RunStyle(OpenXmlElement run, MathStyle style)
        {
            var rPr = run.ChildElements.FirstOrDefault(e => e.LocalName == "rPr");
            if (rPr == null) return style;

            if (rPr.ChildElements.Any(e => e.LocalName == "nor")) return style.WithItalic(false);

            string sty = Val(rPr.ChildElements.FirstOrDefault(e => e.LocalName == "sty"));
            return sty switch
            {
                "p" => style.WithItalic(false),
                "b" => style.WithItalic(false).WithBold(true),
                "bi" => style.WithItalic(true).WithBold(true),
                _ => style,   // "i" and unspecified
            };
        }

        // ------------------------------------------------------------------ fractions

        private MathBox Fraction(OpenXmlElement el, MathStyle style, int depth)
        {
            string type = PrAttr(el, "fPr", "type");
            if (type == "skw" || type == "lin")
            {
                DocxDiagnostics.Report("MathBoxBuilder.Fraction",
                    "a skewed or linear fraction has no stacked form; it was linearised");
                return Linear(el, style);
            }

            var partStyle = style.ForFractionPart();
            var num = BuildNode(Child(el, "num"), partStyle, depth + 1);
            var den = BuildNode(Child(el, "den"), partStyle, depth + 1);

            double axis = MathMeasure.AxisHeight(
                _context.StyleResolver.MapToPdfFontName(style.FontName, style.Bold, style.Italic),
                style.SizePt);
            double rule = style.RuleThickness;
            double gap = style.Em * (style.Display
                ? MathLayoutMetrics.FractionGapDisplayEm
                : MathLayoutMetrics.FractionGapTextEm);

            // The numerator rises until its depth clears the bar; the denominator drops until its
            // height does. Both measured from the bar, not from the baseline — which is what makes
            // a tall numerator push the bar up rather than collide with it.
            double numShift = axis + rule / 2 + gap + num.Depth;
            double denShift = -(axis - rule / 2 - gap - den.Height);

            var overlay = new MathOverlayBox { Align = MathAlign.Centre };
            overlay.Children.Add(new MathShiftBox { Child = num, Shift = numShift }.Pack());
            overlay.Children.Add(new MathShiftBox { Child = den, Shift = -Math.Abs(denShift) }.Pack());
            overlay.Pack();

            bool noBar = type == "noBar";
            if (!noBar)
            {
                overlay.Children.Add(new MathRuleBox
                {
                    Width = overlay.Width,
                    Thickness = rule,
                    RaiseToBottom = axis - rule / 2,
                    Height = axis + rule / 2,
                    Depth = 0,
                    ColorHex = style.ColorHex,
                });
            }

            // A little side bearing, so a fraction never touches what precedes it.
            double pad = style.Em * MathLayoutMetrics.ThinSpaceEm / 2;
            var wrapper = new MathHBox();
            wrapper.Children.Add(MathHBox.Phantom(pad, 0, 0));
            wrapper.Children.Add(overlay.Pack());
            wrapper.Children.Add(MathHBox.Phantom(pad, 0, 0));
            return wrapper.Pack();
        }

        // ------------------------------------------------------------------ scripts

        private MathBox Scripts(OpenXmlElement el, MathStyle style, int depth,
            bool superscript, bool subscript)
        {
            var nucleus = BuildNode(Child(el, "e"), style, depth + 1);
            var scriptStyle = style.ToScript();

            MathBox sup = superscript ? BuildNode(Child(el, "sup"), scriptStyle, depth + 1) : null;
            MathBox sub = subscript ? BuildNode(Child(el, "sub"), scriptStyle, depth + 1) : null;

            double upShift = style.Em * MathLayoutMetrics.SuperscriptShiftUpEm;
            double downShift = style.Em * (superscript && subscript
                ? MathLayoutMetrics.SubSupSubShiftDownEm
                : MathLayoutMetrics.SubscriptShiftDownEm);

            // Both present: push them apart until the gap is respected, so a deep superscript and a
            // tall subscript cannot overlap.
            if (sup != null && sub != null)
            {
                double gap = style.Em * MathLayoutMetrics.SubSupMinGapEm;
                double actual = (upShift - sup.Depth) - (sub.Height - downShift);
                if (actual < gap)
                {
                    double extra = (gap - actual) / 2;
                    upShift += extra;
                    downShift += extra;
                }
            }

            var result = new MathHBox();
            result.Children.Add(nucleus);

            if (sup != null && sub != null)
            {
                var stack = new MathOverlayBox { Align = MathAlign.Left };
                stack.Children.Add(new MathShiftBox { Child = sup, Shift = upShift }.Pack());
                stack.Children.Add(new MathShiftBox { Child = sub, Shift = -downShift }.Pack());
                result.Children.Add(stack.Pack());
            }
            else if (sup != null)
            {
                result.Children.Add(new MathShiftBox { Child = sup, Shift = upShift }.Pack());
            }
            else if (sub != null)
            {
                result.Children.Add(new MathShiftBox { Child = sub, Shift = -downShift }.Pack());
            }

            return result.Pack();
        }

        // ------------------------------------------------------------------ radicals

        private MathBox Radical(OpenXmlElement el, MathStyle style, int depth)
        {
            var radicand = BuildNode(Child(el, "e"), style, depth + 1);

            double rule = style.RuleThickness;
            double gap = style.Em * MathLayoutMetrics.RadicalVerticalGapEm;
            double extra = style.Em * MathLayoutMetrics.RadicalExtraAscenderEm;
            double hookWidth = style.Em * MathLayoutMetrics.RadicalHookWidthEm;

            double bodyHeight = radicand.Height + gap;
            double vinculumBottom = bodyHeight + extra;
            double totalHeight = vinculumBottom + rule;
            double depthBelow = radicand.Depth;

            // The surd is DRAWN, not set from a glyph: it then stretches to any radicand, and the
            // whole OpenType MATH / extensible-glyph problem disappears with it.
            string colour = style.ColorHex;
            var hook = new MathPathBox
            {
                Width = hookWidth,
                Height = totalHeight,
                Depth = depthBelow,
                Emit = (ops, x, baseline) =>
                {
                    ops.Append("q\n");
                    AppendStroke(ops, colour, rule);
                    ops.AppendFormat(CultureInfo.InvariantCulture,
                        "{0:F2} {1:F2} m\n{2:F2} {3:F2} l\n{4:F2} {5:F2} l\n{6:F2} {7:F2} l\nS\nQ\n",
                        x, baseline + totalHeight * 0.45,
                        x + hookWidth * 0.30, baseline + totalHeight * 0.30,
                        x + hookWidth * 0.62, baseline - depthBelow,
                        x + hookWidth, baseline + vinculumBottom + rule / 2);
                },
            };

            var body = new MathOverlayBox { Align = MathAlign.Left };
            body.Children.Add(radicand);
            body.Children.Add(new MathRuleBox
            {
                Width = radicand.Width,
                Thickness = rule,
                RaiseToBottom = vinculumBottom,
                Height = totalHeight,
                Depth = 0,
                ColorHex = colour,
            });
            body.Pack();
            body.Height = totalHeight;
            body.Depth = depthBelow;

            var result = new MathHBox();

            // m:deg — the index of the root. Discarding it, as the linear fallback does, turns a
            // cube root into a square root: not a fidelity gap but a wrong statement.
            var deg = Child(el, "deg");
            bool degHidden = string.Equals(PrAttr(el, "radPr", "degHide"), "1", StringComparison.Ordinal);
            if (deg != null && deg.ChildElements.Count > 0 && !degHidden)
            {
                var degBox = BuildNode(deg, style.ToScript().ToScript(), depth + 1);
                double raise = totalHeight * MathLayoutMetrics.RadicalDegreeRaiseFactor;
                result.Children.Add(new MathShiftBox { Child = degBox, Shift = raise }.Pack());
            }

            result.Children.Add(hook);
            result.Children.Add(body);
            return result.Pack();
        }

        // ------------------------------------------------------------------ accents and bars

        private MathBox Accent(OpenXmlElement el, MathStyle style, int depth)
        {
            var baseBox = BuildNode(Child(el, "e"), style, depth + 1);

            // The accent character lives in m:accPr/m:chr@m:val and defaults to a combining
            // circumflex; it is read here, or every accent would be silently lost.
            string chr = PrAttr(el, "accPr", "chr") ?? "̂";
            var accentBox = MathMeasure.Glyphs(chr, style.WithItalic(false), _context);

            double gap = style.Em * MathLayoutMetrics.AccentGapEm;
            var overlay = new MathOverlayBox { Align = MathAlign.Centre };
            overlay.Children.Add(baseBox);
            overlay.Children.Add(new MathShiftBox
            {
                Child = accentBox,
                Shift = baseBox.Height + gap,
            }.Pack());
            return overlay.Pack();
        }

        /// <summary>
        /// <c>m:sPre</c> — sub/superscripts placed <b>before</b> the base, as in
        /// <c>&#8202;<sub>n</sub>P<sub>r</sub></c> and in tensor notation.
        /// </summary>
        /// <remarks>
        /// The shifts are <see cref="Scripts"/>'s, deliberately: a pre-script is the same
        /// typographic object on the other side of the nucleus, and giving it its own constants
        /// would let the two drift so that <c>&#8202;<sub>2</sub>X</c> and <c>X<sub>2</sub></c>
        /// sat at different heights in one equation. The script stack is <b>right</b>-aligned so
        /// the longer of the two hugs the base rather than trailing away from it.
        /// </remarks>
        private MathBox PreScripts(OpenXmlElement el, MathStyle style, int depth)
        {
            var nucleus = BuildNode(Child(el, "e"), style, depth + 1);
            var scriptStyle = style.ToScript();

            MathBox sup = Child(el, "sup") != null ? BuildNode(Child(el, "sup"), scriptStyle, depth + 1) : null;
            MathBox sub = Child(el, "sub") != null ? BuildNode(Child(el, "sub"), scriptStyle, depth + 1) : null;
            if (sup == null && sub == null) return nucleus;

            double upShift = style.Em * MathLayoutMetrics.SuperscriptShiftUpEm;
            double downShift = style.Em * (sup != null && sub != null
                ? MathLayoutMetrics.SubSupSubShiftDownEm
                : MathLayoutMetrics.SubscriptShiftDownEm);

            if (sup != null && sub != null)
            {
                double gap = style.Em * MathLayoutMetrics.SubSupMinGapEm;
                double actual = (upShift - sup.Depth) - (sub.Height - downShift);
                if (actual < gap)
                {
                    double extra = (gap - actual) / 2;
                    upShift += extra;
                    downShift += extra;
                }
            }

            var stack = new MathOverlayBox { Align = MathAlign.Right };
            if (sup != null) stack.Children.Add(new MathShiftBox { Child = sup, Shift = upShift }.Pack());
            if (sub != null) stack.Children.Add(new MathShiftBox { Child = sub, Shift = -downShift }.Pack());

            var result = new MathHBox();
            result.Children.Add(stack.Pack());
            result.Children.Add(nucleus);
            return result.Pack();
        }

        /// <summary>
        /// <c>m:groupChr</c> — a character grouping the base from above or below, most often an
        /// under-brace.
        /// </summary>
        /// <remarks>
        /// <para>The character is <c>m:groupChrPr/m:chr</c>, defaulting to U+23DF BOTTOM CURLY
        /// BRACKET, and <c>m:pos</c> puts it under (the default) or over the base.</para>
        /// <para><b>A brace, bracket, parenthesis or arrow is drawn to the base's width</b>, as the
        /// radical's surd is drawn (<see cref="GroupShape"/>): a font's size variants and assembly
        /// pieces live in an OpenType MATH table this renderer does not read, and a path stretches
        /// to any width without one. Any other character, or a base narrower than the glyph, is the
        /// glyph centred — reported when the base is wider, the right character at the wrong width.</para>
        /// </remarks>
        private MathBox GroupCharacter(OpenXmlElement el, MathStyle style, int depth)
        {
            var baseBox = BuildNode(Child(el, "e"), style, depth + 1);

            string chr = PrAttr(el, "groupChrPr", "chr") ?? "\u23DF";
            // Table: OMML's default position for a group character is BELOW the base, which is the
            // opposite of m:bar's default. Reading either one's default onto the other is the easy
            // mistake here.
            bool above = string.Equals(PrAttr(el, "groupChrPr", "pos"), "top", StringComparison.Ordinal);

            MathBox chrBox = MathMeasure.Glyphs(chr, style.WithItalic(false), _context);
            if (chrBox.Width < baseBox.Width - 0.01)
            {
                MathBox drawn = GroupShape(chr, baseBox.Width, style);
                if (drawn != null) chrBox = drawn;
                else
                    DocxDiagnostics.Report("MathBoxBuilder.GroupCharacter",
                        "the grouping character was drawn at its natural width rather than stretched " +
                        "to the group it spans: only a brace, bracket, parenthesis or arrow is drawn to width");
            }

            double gap = style.Em * MathLayoutMetrics.AccentGapEm;
            var overlay = new MathOverlayBox { Align = MathAlign.Centre };
            overlay.Children.Add(baseBox);
            overlay.Children.Add(new MathShiftBox
            {
                Child = chrBox,
                Shift = above ? baseBox.Height + gap : -(baseBox.Depth + gap + chrBox.Height),
            }.Pack());
            return overlay.Pack();
        }

        /// <summary>
        /// A stretchy grouping character drawn as a stroked path <paramref name="width"/> wide and
        /// <see cref="MathLayoutMetrics.GroupShapeHeightEm"/> tall, or <c>null</c> for a character
        /// with no drawn form: the curly brackets U+23DE/U+23DF, the square brackets U+23B4/U+23B5
        /// and the parentheses U+23DC/U+23DD — the bottom ones open upwards, towards a base above
        /// them, the top ones downwards — and the arrows ← → ↔ (U+2190, U+2192, U+2194) and their
        /// long forms (U+27F5–U+27F7).
        /// </summary>
        private static MathBox GroupShape(string chr, double width, MathStyle style)
        {
            if (string.IsNullOrEmpty(chr) || chr.Length != 1) return null;
            char c = chr[0];
            bool opensUp = c == '\u23DF' || c == '\u23B5' || c == '\u23DD';
            int kind = c switch
            {
                '\u23DE' or '\u23DF' => 1,                   // curly bracket
                '\u23B4' or '\u23B5' => 2,                   // square bracket
                '\u23DC' or '\u23DD' => 3,                   // parenthesis
                '\u2192' or '\u27F6' => 4,                   // right arrow
                '\u2190' or '\u27F5' => 5,                   // left arrow
                '\u2194' or '\u27F7' => 6,                   // left right arrow
                _ => 0,
            };
            if (kind == 0) return null;

            double h = style.Em * MathLayoutMetrics.GroupShapeHeightEm;
            double rule = style.RuleThickness;
            string colour = style.ColorHex;
            return new MathPathBox
            {
                Width = width,
                Height = h,
                Depth = 0,
                Emit = (ops, x, baseline) =>
                {
                    // Inset by half the stroke, so the ends stay inside the box.
                    double x0 = x + rule / 2, x1 = x + width - rule / 2;
                    double low = baseline + rule / 2, high = baseline + h - rule / 2;
                    double ends = opensUp ? high : low;     // where the arms end
                    double tip = opensUp ? low : high;      // where the brace points
                    double mid = (low + high) / 2;
                    ops.Append("q\n");
                    AppendStroke(ops, colour, rule);
                    var inv = CultureInfo.InvariantCulture;
                    switch (kind)
                    {
                        case 1:
                        {
                            // Four quarter ellipses joined by two straights: arm, curl, point, curl, arm.
                            const double k = 0.5523;
                            double centre = (x0 + x1) / 2;
                            double a = Math.Min(Math.Abs(high - low), (x1 - x0) / 4);
                            double toMid = mid - ends, toTip = tip - mid;
                            ops.AppendFormat(inv, "{0:F2} {1:F2} m\n", x0, ends);
                            ops.AppendFormat(inv, "{0:F2} {1:F2} {2:F2} {3:F2} {4:F2} {5:F2} c\n", x0, ends + k * toMid, x0 + a - k * a, mid, x0 + a, mid);
                            ops.AppendFormat(inv, "{0:F2} {1:F2} l\n", centre - a, mid);
                            ops.AppendFormat(inv, "{0:F2} {1:F2} {2:F2} {3:F2} {4:F2} {5:F2} c\n", centre - a + k * a, mid, centre, tip - k * toTip, centre, tip);
                            ops.AppendFormat(inv, "{0:F2} {1:F2} {2:F2} {3:F2} {4:F2} {5:F2} c\n", centre, tip - k * toTip, centre + a - k * a, mid, centre + a, mid);
                            ops.AppendFormat(inv, "{0:F2} {1:F2} l\n", x1 - a, mid);
                            ops.AppendFormat(inv, "{0:F2} {1:F2} {2:F2} {3:F2} {4:F2} {5:F2} c\nS\n", x1 - a + k * a, mid, x1, ends + k * toMid, x1, ends);
                            break;
                        }
                        case 2:
                            ops.AppendFormat(inv, "{0:F2} {1:F2} m\n{0:F2} {2:F2} l\n{3:F2} {2:F2} l\n{3:F2} {1:F2} l\nS\n", x0, ends, tip, x1);
                            break;
                        case 3:
                        {
                            // One cubic whose middle reaches the tip: B(½) = ¼·ends + ¾·control.
                            double control = tip - (ends - tip) / 3;
                            double a = (x1 - x0) / 6;
                            ops.AppendFormat(inv, "{0:F2} {1:F2} m\n{2:F2} {3:F2} {4:F2} {3:F2} {5:F2} {1:F2} c\nS\n", x0, ends, x0 + a, control, x1 - a, x1);
                            break;
                        }
                        default:
                        {
                            // A shaft along the middle, a head of two strokes at each pointed end.
                            double head = Math.Min((high - low) / 2, (x1 - x0) / 3);
                            ops.AppendFormat(inv, "{0:F2} {1:F2} m\n{2:F2} {1:F2} l\nS\n", x0, mid, x1);
                            if (kind != 5)
                                ops.AppendFormat(inv, "{0:F2} {1:F2} m\n{2:F2} {3:F2} l\n{0:F2} {4:F2} l\nS\n", x1 - head, mid + head, x1, mid, mid - head);
                            if (kind != 4)
                                ops.AppendFormat(inv, "{0:F2} {1:F2} m\n{2:F2} {3:F2} l\n{0:F2} {4:F2} l\nS\n", x0 + head, mid + head, x0, mid, mid - head);
                            break;
                        }
                    }
                    ops.Append("Q\n");
                },
            };
        }

        /// <summary>
        /// <c>m:box</c> — a logical grouping with <b>no visual form</b>: it marks its content as an
        /// operator emulator, a no-break point or an alignment point, none of which changes a
        /// glyph.
        /// </summary>
        /// <remarks>
        /// So this draws the child and nothing else, and that is exact rather than an
        /// approximation — worth stating, because it is the one construct here where doing nothing
        /// visible is the correct implementation and could otherwise be mistaken for a stub. It was
        /// nonetheless being <i>linearised</i> before, which is not the same as doing nothing: the
        /// contents lost their own 2-D layout on the way through.
        /// </remarks>
        private MathBox LogicalBox(OpenXmlElement el, MathStyle style, int depth)
            => BuildNode(Child(el, "e"), style, depth + 1);

        /// <summary>
        /// <c>m:borderBox</c> — a rectangle around the content, with per-edge suppression and the
        /// optional strike-throughs used to cancel terms.
        /// </summary>
        private MathBox BorderBox(OpenXmlElement el, MathStyle style, int depth)
        {
            var content = BuildNode(Child(el, "e"), style, depth + 1);

            double rule = style.RuleThickness;
            double pad = style.Em * MathLayoutMetrics.AccentGapEm;

            // The border sits outside a padding ring, so the box grows on all four sides.
            double left = 0;
            double width = content.Width + 2 * pad;
            double top = content.Height + pad;
            double bottom = -(content.Depth + pad);

            var overlay = new MathOverlayBox { Align = MathAlign.Left };
            overlay.Children.Add(new MathHBox
            {
                Width = width,
                Height = top + rule,
                Depth = -bottom + rule,
            });
            overlay.Children.Add(new MathShiftBox { Child = content, Shift = 0 }.Pack());

            void Horizontal(double atY)
                => overlay.Children.Add(new MathRuleBox
                {
                    Width = width, Thickness = rule, RaiseToBottom = atY,
                    Height = Math.Max(0, atY + rule), Depth = Math.Max(0, -atY),
                    ColorHex = style.ColorHex,
                });

            // Edges. OMML states them as HIDE flags, so absence means the edge is drawn — reading
            // them as "show" flags would produce an empty border on every real document, since a
            // plain <m:borderBox> carries no properties at all.
            bool hideTop = Flag(el, "borderBoxPr", "hideTop");
            bool hideBot = Flag(el, "borderBoxPr", "hideBot");
            bool hideLeft = Flag(el, "borderBoxPr", "hideLeft");
            bool hideRight = Flag(el, "borderBoxPr", "hideRight");

            if (!hideTop) Horizontal(top);
            if (!hideBot) Horizontal(bottom - rule);
            if (!hideLeft) overlay.Children.Add(VerticalRule(left, bottom, top, rule, style));
            if (!hideRight) overlay.Children.Add(VerticalRule(width - rule, bottom, top, rule, style));

            // Strikes. strikeH / strikeV are the cancel bars; the two diagonals need a stroked path
            // rather than a rectangle, and are reported rather than drawn as an axis-aligned line
            // that would read as a different notation entirely.
            if (Flag(el, "borderBoxPr", "strikeH"))
                Horizontal((top + bottom) / 2);
            if (Flag(el, "borderBoxPr", "strikeV"))
                overlay.Children.Add(VerticalRule((width - rule) / 2, bottom, top, rule, style));
            if (Flag(el, "borderBoxPr", "strikeBLTR") || Flag(el, "borderBoxPr", "strikeTLBR"))
                DocxDiagnostics.Report("MathBoxBuilder.BorderBox",
                    "a diagonal strike-through was omitted; it needs a stroked path, and an " +
                    "axis-aligned substitute would read as a different notation");

            var packed = overlay.Pack();
            // The content is inset by the padding, which an overlay cannot express on its own.
            var shell = new MathHBox();
            shell.Children.Add(MathHBox.Phantom(pad, 0, 0));
            shell.Children.Add(packed);
            return shell.Pack();
        }

        private static MathBox VerticalRule(double atX, double bottom, double top, double rule,
                                            MathStyle style)
        {
            var line = new MathRuleBox
            {
                Width = rule,
                Thickness = (top - bottom) + rule,
                RaiseToBottom = bottom - rule,
                Height = Math.Max(0, top + rule),
                Depth = Math.Max(0, -(bottom - rule)),
                ColorHex = style.ColorHex,
            };
            var row = new MathHBox();
            row.Children.Add(MathHBox.Phantom(atX, 0, 0));
            row.Children.Add(line);
            return row.Pack();
        }

        /// <summary>
        /// <c>m:phant</c> — content that occupies space without being drawn, which is how an author
        /// aligns one equation against another.
        /// </summary>
        /// <remarks>
        /// The flags are all <b>suppressions</b> of what the phantom would otherwise contribute:
        /// <c>m:show</c> makes it visible after all, and <c>zeroWid</c>/<c>zeroAsc</c>/<c>zeroDesc</c>
        /// drop its width, ascent and descent respectively. Dropping the element instead — which is
        /// what linearising it amounted to — removes exactly the space it exists to reserve, so the
        /// alignment it was placed for silently fails.
        /// </remarks>
        private MathBox PhantomNode(OpenXmlElement el, MathStyle style, int depth)
        {
            var content = BuildNode(Child(el, "e"), style, depth + 1);

            bool show = Flag(el, "phantPr", "show");
            double w = Flag(el, "phantPr", "zeroWid") ? 0 : content.Width;
            double h = Flag(el, "phantPr", "zeroAsc") ? 0 : content.Height;
            double d = Flag(el, "phantPr", "zeroDesc") ? 0 : content.Depth;

            if (!show) return MathHBox.Phantom(w, h, d);

            // Visible, but still contributing only the dimensions the flags left it. Wrapping it in
            // a box of the reduced size is what makes a zeroWid phantom overlap its neighbour, which
            // is the whole point of that flag.
            var overlay = new MathOverlayBox { Align = MathAlign.Left };
            overlay.Children.Add(content);
            var packed = overlay.Pack();
            packed.Width = w; packed.Height = h; packed.Depth = d;
            return packed;
        }

        /// <summary>
        /// An OMML boolean property. Absent means <c>false</c>; present with no
        /// <c>m:val</c> means <c>true</c>, which is the XML convention a plain
        /// <c>PrAttr(...) == "1"</c> test gets wrong for <c>&lt;m:hideTop/&gt;</c>.
        /// </summary>
        private static bool Flag(OpenXmlElement el, string prName, string flagName)
        {
            var pr = el.ChildElements.FirstOrDefault(e => e.LocalName == prName);
            return OoxmlOnOff.ElementIsOn(pr?.ChildElements.FirstOrDefault(e => e.LocalName == flagName));
        }

        private MathBox Bar(OpenXmlElement el, MathStyle style, int depth)
        {
            var baseBox = BuildNode(Child(el, "e"), style, depth + 1);
            bool underneath = string.Equals(PrAttr(el, "barPr", "pos"), "bot", StringComparison.Ordinal);

            double rule = style.RuleThickness;
            double gap = style.Em * MathLayoutMetrics.RadicalVerticalGapEm;

            var overlay = new MathOverlayBox { Align = MathAlign.Left };
            overlay.Children.Add(baseBox);
            overlay.Children.Add(new MathRuleBox
            {
                Width = baseBox.Width,
                Thickness = rule,
                RaiseToBottom = underneath ? -(baseBox.Depth + gap + rule) : baseBox.Height + gap,
                Height = underneath ? 0 : baseBox.Height + gap + rule,
                Depth = underneath ? baseBox.Depth + gap + rule : 0,
                ColorHex = style.ColorHex,
            });
            return overlay.Pack();
        }

        // ------------------------------------------------------------------ stacked constructs

        /// <summary>
        /// A matrix: rows of cells on a shared column grid, the whole block centred on the maths
        /// axis so it sits level with the surrounding text.
        ///
        /// <para>
        /// Alignment comes from measuring each column's widest cell — <b>no glue</b>. TeX-style
        /// stretch and shrink would buy alignment for exactly this construct and
        /// <c>m:eqArr</c>, and cost a badness model the renderer would carry forever; column
        /// maxima give the same result for a grid whose contents are fixed.
        /// </para>
        /// </summary>
        private MathBox Matrix(OpenXmlElement el, MathStyle style, int depth)
        {
            var rows = el.ChildElements.Where(e => e.LocalName == "mr").ToList();
            if (rows.Count == 0) return MathHBox.Phantom(0, 0, 0);

            var grid = new List<List<MathBox>>(rows.Count);
            int cells = 0;
            foreach (var row in rows)
            {
                var cellBoxes = new List<MathBox>();
                foreach (var cell in row.ChildElements.Where(e => e.LocalName == "e"))
                {
                    if (++cells > MathLayoutMetrics.MaxMatrixCells)
                    {
                        DocxDiagnostics.Report("MathBoxBuilder.Matrix", string.Format(
                            CultureInfo.InvariantCulture,
                            "the matrix declares more than {0} cells; the remainder were dropped",
                            MathLayoutMetrics.MaxMatrixCells));
                        break;
                    }
                    cellBoxes.Add(BuildNode(cell, style, depth + 1));
                }
                grid.Add(cellBoxes);
                if (cells > MathLayoutMetrics.MaxMatrixCells) break;
            }

            int columns = grid.Max(r => r.Count);
            if (columns == 0) return MathHBox.Phantom(0, 0, 0);

            var columnWidth = new double[columns];
            for (int c = 0; c < columns; c++)
                foreach (var row in grid)
                    if (c < row.Count) columnWidth[c] = Math.Max(columnWidth[c], row[c].Width);

            double gap = style.Em * MathLayoutMetrics.MatrixColumnGapEm;
            var rowBoxes = new List<MathBox>(grid.Count);
            foreach (var row in grid)
            {
                var line = new MathHBox();
                for (int c = 0; c < columns; c++)
                {
                    if (c > 0) line.Children.Add(MathHBox.Phantom(gap, 0, 0));

                    var cell = c < row.Count ? row[c] : MathHBox.Phantom(0, 0, 0);
                    // Cells are centred in their column, which is DrawingML's default (m:mcJc).
                    double slack = columnWidth[c] - cell.Width;
                    if (slack > 0.01)
                    {
                        line.Children.Add(MathHBox.Phantom(slack / 2, 0, 0));
                        line.Children.Add(cell);
                        line.Children.Add(MathHBox.Phantom(slack / 2, 0, 0));
                    }
                    else line.Children.Add(cell);
                }
                rowBoxes.Add(line.Pack());
            }

            return StackRows(rowBoxes, style, MathAlign.Centre);
        }

        /// <summary>
        /// An equation array — several equations stacked, left-aligned by default. Structurally a
        /// one-column matrix, so it shares <see cref="StackRows"/>.
        /// </summary>
        private MathBox EquationArray(OpenXmlElement el, MathStyle style, int depth)
        {
            var rows = el.ChildElements
                .Where(e => e.LocalName == "e")
                .Select(e => BuildNode(e, style, depth + 1))
                .ToList();

            if (rows.Count == 0) return MathHBox.Phantom(0, 0, 0);
            return StackRows(rows, style, MathAlign.Left);
        }

        /// <summary>
        /// Stacks rows vertically and centres the block on the maths axis, so a two-row matrix sits
        /// level with the text beside it rather than resting its bottom row on the baseline.
        /// </summary>
        private MathBox StackRows(List<MathBox> rows, MathStyle style, MathAlign align)
        {
            double gap = style.Em * MathLayoutMetrics.StackRowGapEm;
            double axis = MathMeasure.AxisHeight(
                _context.StyleResolver.MapToPdfFontName(style.FontName, style.Bold, style.Italic),
                style.SizePt);

            // Total extent, then place each row's baseline from the top downward.
            double total = 0;
            for (int i = 0; i < rows.Count; i++)
            {
                total += rows[i].Height + rows[i].Depth;
                if (i > 0) total += gap;
            }

            double top = axis + total / 2;
            var stack = new MathOverlayBox { Align = align };

            double cursor = top;
            foreach (var row in rows)
            {
                double baselineShift = cursor - row.Height;
                stack.Children.Add(new MathShiftBox { Child = row, Shift = baselineShift }.Pack());
                cursor -= row.Height + row.Depth + gap;
            }

            return stack.Pack();
        }

        // ------------------------------------------------------------------ delimiters

        /// <summary>
        /// A bracketed group. The fences are grown to the content with a vertical text scale rather
        /// than assembled from glyph parts — the assembly route needs an OpenType MATH table, which
        /// no reachable font here has.
        /// </summary>
        private MathBox Delimiter(OpenXmlElement el, MathStyle style, int depth)
        {
            string open = PrAttr(el, "dPr", "begChr") ?? "(";
            string close = PrAttr(el, "dPr", "endChr") ?? ")";
            string separator = PrAttr(el, "dPr", "sepChr") ?? "|";
            bool hideOpen = string.Equals(open, "", StringComparison.Ordinal);

            var args = el.ChildElements.Where(e => e.LocalName == "e").ToList();
            var content = new MathHBox();
            for (int i = 0; i < args.Count; i++)
            {
                if (i > 0)
                    content.Children.Add(MathMeasure.Glyphs(separator, style.WithItalic(false), _context));
                content.Children.Add(BuildNode(args[i], style, depth + 1));
            }
            content.Pack();

            double pad = style.Em * MathLayoutMetrics.DelimiterPaddingEm;
            var result = new MathHBox();
            if (!hideOpen) result.Children.Add(Fence(open, content, style));
            result.Children.Add(MathHBox.Phantom(pad, 0, 0));
            result.Children.Add(content);
            result.Children.Add(MathHBox.Phantom(pad, 0, 0));
            if (!string.IsNullOrEmpty(close)) result.Children.Add(Fence(close, content, style));
            return result.Pack();
        }

        private MathBox Fence(string chr, MathBox content, MathStyle style)
        {
            var glyph = MathMeasure.Glyphs(chr, style.WithItalic(false), _context);
            double natural = glyph.Height + glyph.Depth;
            double needed = content.Height + content.Depth;
            if (natural <= 0 || needed <= natural * 1.05) return glyph;

            double stretch = needed / natural;
            if (stretch > MathLayoutMetrics.MaxDelimiterStretch)
            {
                DocxDiagnostics.Report("MathBoxBuilder.Delimiter", string.Format(
                    CultureInfo.InvariantCulture,
                    "a delimiter would need {0:F1}× stretching; it was drawn at the {1:F1}× cap "
                        + "rather than as a hairline caricature",
                    stretch, MathLayoutMetrics.MaxDelimiterStretch));
                stretch = MathLayoutMetrics.MaxDelimiterStretch;
            }

            // Scaled with a text matrix: Tz scales horizontally only, so the vertical growth has to
            // come from the matrix's d term. The glyph box draws itself stretched, in the face, colour
            // and encoding it was measured in (MathGlyphBox.VerticalScale).
            return glyph is MathGlyphBox glyphBox ? glyphBox.Stretched(stretch) : glyph;
        }

        // ------------------------------------------------------------------ n-ary and limits

        /// <summary>
        /// ∑, ∫, ∏ and friends. In display style the limits sit above and below the operator; inline
        /// they sit beside it as ordinary scripts, which is what Word does and what keeps an inline
        /// formula on its line.
        /// </summary>
        private MathBox Nary(OpenXmlElement el, MathStyle style, int depth)
        {
            string chr = PrAttr(el, "naryPr", "chr") ?? "∑";
            bool subHidden = string.Equals(PrAttr(el, "naryPr", "subHide"), "1", StringComparison.Ordinal);
            bool supHidden = string.Equals(PrAttr(el, "naryPr", "supHide"), "1", StringComparison.Ordinal);
            string limLoc = PrAttr(el, "naryPr", "limLoc");

            bool stacked = style.Display
                ? !string.Equals(limLoc, "subSup", StringComparison.Ordinal)
                : string.Equals(limLoc, "undOvr", StringComparison.Ordinal);

            var opStyle = style.Display
                ? style.WithSize(style.SizePt * MathLayoutMetrics.NaryDisplayOperatorScale)
                : style;
            var op = MathMeasure.Glyphs(chr, opStyle.WithItalic(false), _context);

            var scriptStyle = style.ToScript();
            MathBox sub = subHidden ? null : BuildNode(Child(el, "sub"), scriptStyle, depth + 1);
            MathBox sup = supHidden ? null : BuildNode(Child(el, "sup"), scriptStyle, depth + 1);
            var body = BuildNode(Child(el, "e"), style, depth + 1);

            var operatorGroup = stacked
                ? StackLimits(op, sub, sup, style)
                : BesideLimits(op, sub, sup, style);

            var result = new MathHBox();
            result.Children.Add(operatorGroup);
            result.Children.Add(MathHBox.Phantom(style.Em * MathLayoutMetrics.ThinSpaceEm, 0, 0));
            result.Children.Add(body);
            return result.Pack();
        }

        private static MathBox StackLimits(MathBox op, MathBox sub, MathBox sup, MathStyle style)
        {
            double gap = style.Em * MathLayoutMetrics.NaryLimitGapEm;
            var stack = new MathOverlayBox { Align = MathAlign.Centre };
            stack.Children.Add(op);

            if (sup != null && sup.Width > 0)
                stack.Children.Add(new MathShiftBox
                {
                    Child = sup,
                    Shift = op.Height + gap + sup.Depth,
                }.Pack());

            if (sub != null && sub.Width > 0)
                stack.Children.Add(new MathShiftBox
                {
                    Child = sub,
                    Shift = -(op.Depth + gap + sub.Height),
                }.Pack());

            return stack.Pack();
        }

        private static MathBox BesideLimits(MathBox op, MathBox sub, MathBox sup, MathStyle style)
        {
            var group = new MathHBox();
            group.Children.Add(op);

            bool hasSup = sup != null && sup.Width > 0;
            bool hasSub = sub != null && sub.Width > 0;
            if (!hasSup && !hasSub) return group.Pack();

            double up = style.Em * MathLayoutMetrics.SuperscriptShiftUpEm;
            double down = style.Em * (hasSup
                ? MathLayoutMetrics.SubSupSubShiftDownEm
                : MathLayoutMetrics.SubscriptShiftDownEm);

            var stack = new MathOverlayBox { Align = MathAlign.Left };
            if (hasSup) stack.Children.Add(new MathShiftBox { Child = sup, Shift = up }.Pack());
            if (hasSub) stack.Children.Add(new MathShiftBox { Child = sub, Shift = -down }.Pack());
            group.Children.Add(stack.Pack());
            return group.Pack();
        }

        /// <summary>
        /// <c>m:limLow</c> / <c>m:limUpp</c> — <c>lim</c> with its condition underneath, <c>max</c>
        /// with a bound above. Structurally the stacked n-ary case with no body.
        /// </summary>
        private MathBox Limit(OpenXmlElement el, MathStyle style, int depth, bool above)
        {
            var baseBox = BuildNode(Child(el, "e"), style, depth + 1);
            var limitBox = BuildNode(Child(el, "lim"), style.ToScript(), depth + 1);
            double gap = style.Em * MathLayoutMetrics.NaryLimitGapEm;

            var stack = new MathOverlayBox { Align = MathAlign.Centre };
            stack.Children.Add(baseBox);
            stack.Children.Add(new MathShiftBox
            {
                Child = limitBox,
                Shift = above
                    ? baseBox.Height + gap + limitBox.Depth
                    : -(baseBox.Depth + gap + limitBox.Height),
            }.Pack());
            return stack.Pack();
        }

        private MathBox Function(OpenXmlElement el, MathStyle style, int depth)
        {
            var name = BuildNode(Child(el, "fName"), style.WithItalic(false), depth + 1);
            var arg = BuildNode(Child(el, "e"), style, depth + 1);

            var box = new MathHBox();
            box.Children.Add(name);
            box.Children.Add(MathHBox.Phantom(style.Em * MathLayoutMetrics.ThinSpaceEm, 0, 0));
            box.Children.Add(arg);
            return box.Pack();
        }

        // ------------------------------------------------------------------ helpers

        private static void AppendStroke(StringBuilder ops, string hex, double width)
            => RunRenderer.AppendStroke(ops, hex, width);

        private static OpenXmlElement Child(OpenXmlElement el, string localName)
            => el?.ChildElements.FirstOrDefault(e => e.LocalName == localName);

        private static string Val(OpenXmlElement e)
            => OoxmlAttribute.Value(e, "val");

        private static string PrAttr(OpenXmlElement el, string propName, string childName)
            => Val(Child(Child(el, propName), childName));
    }
}


