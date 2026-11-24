// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using Tetitulz.Pdf.Core.Constants;

namespace Tetitulz.Pdf.Base.Rendering
{
    /// <summary>
    /// The SVG-syntax primitives shared by <see cref="PdfSvgRenderer"/> and
    /// <see cref="SvgContentWriter"/>: number formatting, XML escaping, and the two stroke
    /// constants.
    ///
    /// <para><b>Why a separate type.</b> The writer is a top-level type, and the alternative to a
    /// leaf —
    /// having one of the two call the other's statics — is the sideways-peer dependency that
    /// once made the Base test host crash mid-run under parallel execution, reporting "Passed!"
    /// while silently dropping ~190 tests. Sharing downward into a leaf is the shape that does
    /// not have that failure mode.</para>
    ///
    /// <para>This type is a leaf <b>by construction, not by assertion</b>: it has no static
    /// fields at all. The two members that look like state are <c>const</c>, which the compiler
    /// inlines, so the type has no initializer to run and cannot participate in a
    /// type-initialization cycle.</para>
    /// </summary>
    internal static class SvgSyntax
    {
        /// <summary>
        /// Escapes text for inclusion in XML character data or a quoted attribute value.
        /// Escapes the apostrophe as well as the four mandatory characters, so the result is
        /// safe inside single- as well as double-quoted attributes.
        /// </summary>
        /// <remarks>
        /// <b>One implementation, in Core.</b> This was the third byte-identical copy of the
        /// five-entity chain; the other two were in the conformance converters, which are in a
        /// plugin and so could never have called this one — it is <c>internal</c> to Base, and
        /// Core's <c>InternalsVisibleTo</c> names no plugin. That is what forced the shared
        /// rule down into Core rather than sideways.
        /// <para>The forward is safe for the reason stated on this type: <c>XmlEscape</c> has no
        /// static fields either, so calling it introduces no type initializer to run.</para>
        /// </remarks>
        internal static string XmlEsc(string s) => Core.Text.XmlEscape.EscapeText(s);

        /// <summary>Formats a coordinate/length for SVG output.</summary>
        internal static string F(float v) => v.ToString("G6", System.Globalization.CultureInfo.InvariantCulture);

        /// <summary>
        /// The PDF graphics state's initial miter limit (§8.4.3.5).
        /// </summary>
        /// <remarks>
        /// <para><b>This is NOT the value at which <c>stroke-miterlimit</c> may be omitted.</b> "PDF
        /// and SVG both default to 10" is true of PDF and false of SVG: SVG's initial <c>stroke-miterlimit</c> is
        /// <see cref="SvgDefaultMiterLimit"/> — <b>4</b>. Omitting the attribute at 10 would export
        /// the *default graphics state* — the commonest state there is, reached by any
        /// file that never writes an <c>M</c> operator — with an effective limit of 4, while the
        /// raster path strokes it at 10: any join sharper than about 29° would come out as a spike
        /// in the render and a bevel in the export.</para>
        /// <para>So the attribute is emitted whenever it differs from <i>SVG's</i> default.</para>
        /// </remarks>
        internal const float DefaultMiterLimit = 10f;

        /// <summary>
        /// SVG's initial <c>stroke-miterlimit</c> (SVG 1.1 §11.4) — the value the attribute may be
        /// omitted at, and the one an omitted attribute means.
        /// </summary>
        internal const float SvgDefaultMiterLimit = 4f;

        /// <summary>
        /// Width substituted for a line width of 0, in SVG user units.
        ///
        /// <para>ISO 32000-1 §8.4.3.2 gives 0 a meaning of its own: "the thinnest line that can be
        /// rendered at device resolution". The raster path gets that for free — Skia treats a stroke
        /// width of 0 as a hairline — but SVG defines <c>stroke-width="0"</c> as painting
        /// <em>nothing</em>, so emitting the value verbatim made every zero-width stroke disappear
        /// from the export while the same file rendered a visible line.</para>
        ///
        /// <para><b>Why 0.75.</b> This writer's user unit is one PDF point: the root
        /// <c>viewBox</c> is the page box measured in points. A CSS pixel is 1/96 inch, so
        /// <see cref="PdfUnits.PxToPoint"/> points (0.75) is exactly <em>one device pixel</em> at the
        /// 96 dpi reference resolution an SVG is rasterised at by default — the same one-pixel line
        /// the raster hairline draws, expressed in the only units SVG offers here.</para>
        ///
        /// <para>It is a fixed width, not a true hairline: SVG cannot say "one device pixel whatever
        /// the scale", so this line thickens with zoom where a Skia hairline would not. Substituting a
        /// visible minimum is still strictly closer to the spec than drawing nothing, which is the
        /// only other option.</para>
        /// </summary>
        internal const float HairlineStrokeWidth = (float)PdfUnits.PxToPoint;

        /// <summary>
        /// Line width to emit as <c>stroke-width</c>, in PAGE units: the graphics state's value
        /// scaled by <paramref name="ctmScale"/> (§8.4.3.2 — the line width is in user space and the
        /// CTM applies to it), or <see cref="HairlineStrokeWidth"/> when the result is not positive.
        ///
        /// <para><b>The scale matters.</b> Path coordinates already carry the CTM (they are emitted
        /// through <c>TxPt</c>), so a width left in user units would stroke
        /// <c>0.1 0 0 0.1 0 0 cm 10 w</c> ten times too thick. The raster path multiplies by the geometric mean of the CTM's two axis lengths
        /// (<c>PdfRenderContext.BuildStrokePaint</c>), which is what
        /// <see cref="SvgContentWriter.CtmScale"/> computes.</para>
        ///
        /// <para>A width of 0, a negative width (invalid in PDF and rejected by SVG) and a CTM that
        /// collapses the width to nothing all take the hairline substitution, which is a PAGE-unit
        /// length and therefore deliberately not scaled.</para>
        /// </summary>
        internal static float StrokeWidthOrHairline(float lineWidth, float ctmScale)
        {
            float scaled = lineWidth > 0f ? lineWidth * ctmScale : 0f;
            return scaled > 0f ? scaled : HairlineStrokeWidth;
        }

        /// <summary>
        /// A <c>data:</c> URI for <paramref name="bytes"/> — the one place this assembly builds
        /// one, for the four sites that embed a raster in an SVG.
        /// </summary>
        /// <remarks>
        /// <para><b>This is a consolidation, not an optimisation, and the difference was measured
        /// rather than assumed.</b> The four sites each wrote
        /// <c>"data:image/png;base64," + Convert.ToBase64String(bytes)</c>, which looks like two
        /// full copies of a payload that is <b>2.67x the encoded image</b> in memory — base64 is
        /// 4 chars per 3 bytes and a <c>char</c> is two bytes, so a 1200-square RGB page export
        /// carries 3.66 MB of PNG as 9.75 MB of UTF-16. Replacing it with
        /// <c>ToBase64CharArray</c> into a buffer that also holds the prefix moved
        /// <b>nothing</b>: <c>SvgExportBenchmarks</c> reported 44.89 MB before and 44.89 MB
        /// after, to the same two decimals. Both shapes allocate one large intermediate and one
        /// large result; swapping which of the two is a <c>char[]</c> changes no total.</para>
        ///
        /// <para><b>The same measurement refused the companion change</b>, so do not make it
        /// either: appending the URI through <c>_sb.Append($"…{dataUri}…")</c> does <i>not</i>
        /// build an intermediate string on net10.0. Since C# 10 that binds to
        /// <c>StringBuilder.Append(ref AppendInterpolatedStringHandler)</c>, which writes the
        /// pieces straight into the builder. Rewriting those interpolations as chained
        /// <c>Append</c> calls cost readability and saved zero bytes.</para>
        ///
        /// <para>What is left is worth having on its own terms: one implementation instead of
        /// four, and one place to change if the encoding ever needs to stream. Streaming is the
        /// only shape that would actually help — appending base64 into the output builder in
        /// chunks, never materialising the payload — and it needs these methods to stop returning
        /// a <c>string</c>, which is a larger change than the numbers currently justify.</para>
        ///
        /// <para><c>ToBase64CharArray</c> rather than the span-based <c>Convert.TryToBase64Chars</c>
        /// because Base still targets netstandard2.0, where the latter does not exist. Output is
        /// character-for-character what the concatenation produced, verified by hashing four
        /// exported SVGs — including the <c>&lt;defs&gt;</c>/<c>&lt;use&gt;</c> dedup branch —
        /// either side of the change.</para>
        /// </remarks>
        /// <param name="mediaType">The MIME type, e.g. <c>image/png</c>.</param>
        /// <param name="bytes">The payload to encode.</param>
        internal static string DataUri(string mediaType, byte[] bytes)
        {
            if (bytes == null) return null;

            string prefix = "data:" + mediaType + ";base64,";
            // 4 characters per 3 bytes, rounded up to the padded quantum.
            long b64 = ((long)bytes.Length + 2) / 3 * 4;
            if (b64 > int.MaxValue - prefix.Length)
            {
                // Beyond what one string can hold. Let the caller's existing null handling refuse
                // the image rather than throwing out of a rendering path.
                return null;
            }

            char[] buf = new char[prefix.Length + (int)b64];
            prefix.CopyTo(0, buf, 0, prefix.Length);
            int written = Convert.ToBase64CharArray(bytes, 0, bytes.Length, buf, prefix.Length);
            return new string(buf, 0, prefix.Length + written);
        }
    }
}
