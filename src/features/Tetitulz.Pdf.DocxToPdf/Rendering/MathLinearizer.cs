// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Linq;
using System.Text;
using DocumentFormat.OpenXml;

namespace Tetitulz.Pdf.DocxToPdf.Rendering
{
    /// <summary>
    /// Converts an Office Math (OMML, <c>m:oMath</c> / <c>m:oMathPara</c>) element into a readable
    /// linear string so equations are preserved as text instead of being dropped. This is a
    /// content-preserving fallback, not a 2-D math typesetter: fractions become <c>(a)/(b)</c>,
    /// superscripts <c>x^(2)</c>, subscripts <c>x_(i)</c>, radicals <c>√(x)</c>, etc. The actual
    /// math symbols and identifiers (∑, ∫, Greek letters, …) come straight from the <c>m:t</c> text,
    /// so they still render (via the renderer's Unicode glyph path). Elements are matched by name so
    /// this works regardless of which OMML wrapper classes the SDK models.
    /// </summary>
    internal static class MathLinearizer
    {
        internal static string Linearize(OpenXmlElement math) => Lin(math, 0).Trim();

        private static string Lin(OpenXmlElement el, int depth)
        {
            if (el == null || depth > 64) return "";
            switch (el.LocalName)
            {
                case "t":   // m:t — leaf text
                    return el.InnerText;
                case "f":   // fraction
                    return "(" + Child(el, "num", depth) + ")/(" + Child(el, "den", depth) + ")";
                case "sSup":    // base + superscript
                    return Child(el, "e", depth) + "^(" + Child(el, "sup", depth) + ")";
                case "sSub":    // base + subscript
                    return Child(el, "e", depth) + "_(" + Child(el, "sub", depth) + ")";
                case "sSubSup": // base with both
                    return Child(el, "e", depth) + "_(" + Child(el, "sub", depth) + ")^(" + Child(el, "sup", depth) + ")";
                case "rad":     // radical (degree omitted — square-root form)
                    return "√(" + Child(el, "e", depth) + ")";
                case "d":       // delimiter (parentheses by default)
                {
                    string beg = PrAttr(el, "dPr", "begChr") ?? "(";
                    string end = PrAttr(el, "dPr", "endChr") ?? ")";
                    var sb = new StringBuilder(beg);
                    bool first = true;
                    foreach (var c in el.ChildElements)
                        if (c.LocalName == "e") { if (!first) sb.Append(", "); sb.Append(Lin(c, depth + 1)); first = false; }
                    sb.Append(end);
                    return sb.ToString();
                }
                case "nary":    // n-ary operator (∑, ∫, ∏, …) with optional limits
                {
                    string op = PrAttr(el, "naryPr", "chr") ?? "∑";
                    string sub = Child(el, "sub", depth), sup = Child(el, "sup", depth);
                    var sb = new StringBuilder(op);
                    if (sub.Length > 0) sb.Append("_(").Append(sub).Append(')');
                    if (sup.Length > 0) sb.Append("^(").Append(sup).Append(')');
                    sb.Append(' ').Append(Child(el, "e", depth));
                    return sb.ToString();
                }
                case "func":    // named function: name(arg)
                    return Child(el, "fName", depth) + "(" + Child(el, "e", depth) + ")";
                default:        // wrappers (oMath, e, num, den, r, …) and unknowns: concat children
                {
                    var sb = new StringBuilder();
                    foreach (var c in el.ChildElements) sb.Append(Lin(c, depth + 1));
                    return sb.ToString();
                }
            }
        }

        private static string Child(OpenXmlElement el, string localName, int depth)
        {
            var c = el.ChildElements.FirstOrDefault(x => x.LocalName == localName);
            return c == null ? "" : Lin(c, depth + 1);
        }

        // Reads a control attribute, e.g. m:dPr/m:begChr/@m:val (the delimiter's opening char).
        private static string PrAttr(OpenXmlElement el, string propName, string childName)
        {
            var pr = el.ChildElements.FirstOrDefault(x => x.LocalName == propName);
            var ch = pr?.ChildElements.FirstOrDefault(x => x.LocalName == childName);
            var v = OoxmlAttribute.Value(ch, "val");
            return string.IsNullOrEmpty(v) ? null : v;
        }
    }
}
