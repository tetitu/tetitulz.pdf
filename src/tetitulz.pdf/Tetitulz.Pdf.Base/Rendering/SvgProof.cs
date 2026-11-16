// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using SkiaSharp;

namespace Tetitulz.Pdf.Base.Rendering
{
    /// <summary>
    /// Soft proofing the SVG writer's output (<see cref="PdfRenderOptions.ProofProfile"/>): the
    /// finished document's colours put through the same proof the raster surface takes (<see cref="SoftProof"/>).
    /// </summary>
    /// <remarks>
    /// <para><b>On the finished document, not at each paint.</b> Every colour the writer emits is a paint value
    /// <see cref="PdfColor.ToCssRgb"/> wrote (or <c>white</c>/<c>black</c>), and every raster an embedded data
    /// URI; proofing the document once reaches all of them — paths, text, gradient stops, annotations, the
    /// background, images — where proofing at each emission would be a site per paint, each a chance to be
    /// missed. A colour is proofed exactly (the composition the grid samples), an image through the grid, as the
    /// raster proof reads it, and is written back as PNG — a JPEG passed through is decoded and re-encoded
    /// losslessly.</para>
    /// <para><b>What is not a colour is left alone</b>: anything inside a <c>&lt;mask&gt;</c> or
    /// <c>&lt;clipPath&gt;</c> (a mask's luminance is coverage, and a proof would change how much shows), and
    /// every value that is not a solid colour (<c>none</c>, <c>url(#…)</c>, <c>currentColor</c>).</para>
    /// <para>The document is read with DTDs prohibited and no resolver, as every XML reader here is; it is the
    /// writer's own output.</para>
    /// </remarks>
    internal static class SvgProof
    {
        private static readonly string[] ColourAttributes = { "fill", "stroke", "stop-color", "flood-color", "lighting-color", "color" };
        private const string XLink = "http://www.w3.org/1999/xlink";

        /// <summary>
        /// <paramref name="svg"/> proofed through <paramref name="options"/>' proof profile, or unchanged when none
        /// is asked for or it cannot be used (the proof reports that).
        /// </summary>
        internal static string Apply(string svg, PdfRenderOptions options)
        {
            SoftProof proof = SoftProof.For(options);
            if (proof == null || string.IsNullOrEmpty(svg)) return svg;

            XDocument doc;
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
            using (var reader = XmlReader.Create(new StringReader(svg), settings))
                doc = XDocument.Load(reader, LoadOptions.PreserveWhitespace);

            var colours = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (XElement element in doc.Descendants())
            {
                if (element.Ancestors().Any(a => a.Name.LocalName == "mask" || a.Name.LocalName == "clipPath")
                    || element.Name.LocalName == "mask" || element.Name.LocalName == "clipPath")
                    continue;

                foreach (string name in ColourAttributes)
                {
                    XAttribute attribute = element.Attribute(name);
                    if (attribute != null) attribute.Value = Proofed(proof, attribute.Value, colours);
                }
                XAttribute style = element.Attribute("style");
                if (style != null) style.Value = ProofStyle(proof, style.Value, colours);

                if (element.Name.LocalName == "image")
                {
                    XAttribute href = element.Attribute("href") ?? element.Attribute(XName.Get("href", XLink));
                    if (href != null) href.Value = ProofImage(proof, href.Value);
                }
            }

            var text = new StringBuilder(svg.Length);
            if (doc.Declaration != null) text.Append(doc.Declaration).Append('\n');
            text.Append(doc.Root.ToString(SaveOptions.DisableFormatting));
            return text.ToString();
        }

        /// <summary>A paint value proofed: a solid colour through the exact composition, anything else as it was.</summary>
        private static string Proofed(SoftProof proof, string value, Dictionary<string, string> cache)
        {
            string trimmed = value.Trim();
            if (cache.TryGetValue(trimmed, out string done)) return done;
            SKColor colour = trimmed == "white" ? SKColors.White : SKColors.Black;
            bool solid = trimmed == "white" || trimmed == "black" || PdfColor.TryParseCssColor(trimmed, out colour);
            string result = solid && proof.TryProofExactly(colour, out SKColor proofed) ? PdfColor.ToCssRgb(proofed) : value;
            cache[trimmed] = result;
            return result;
        }

        private static readonly Regex StyleProperty = new Regex(
            @"(?<name>fill|stroke|stop-color|flood-color|lighting-color|color)\s*:\s*(?<value>[^;\s][^;]{0,63})",
            RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

        /// <summary>The colour properties of a <c>style</c> attribute proofed.</summary>
        private static string ProofStyle(SoftProof proof, string style, Dictionary<string, string> cache)
        {
            if (style.Length > 4096) return style;   // the writer's styles are a few properties
            return StyleProperty.Replace(style, m => m.Groups["name"].Value + ":" + Proofed(proof, m.Groups["value"].Value, cache));
        }

        /// <summary>An embedded PNG or JPEG proofed through the grid and written back as PNG; anything else as it was.</summary>
        private static string ProofImage(SoftProof proof, string href)
        {
            const string Png = "data:image/png;base64,", Jpeg = "data:image/jpeg;base64,";
            string prefix = href.StartsWith(Png, StringComparison.Ordinal) ? Png : href.StartsWith(Jpeg, StringComparison.Ordinal) ? Jpeg : null;
            if (prefix == null) return href;
            byte[] encoded;
            try { encoded = Convert.FromBase64String(href.Substring(prefix.Length)); }
            catch (FormatException) { return href; }

            using SKBitmap decoded = SKBitmap.Decode(encoded);
            if (decoded == null) return href;
            using SKBitmap bitmap = decoded.Copy(SKColorType.Rgba8888);
            if (bitmap == null) return href;
            proof.Apply(bitmap);
            return Png + Convert.ToBase64String(PdfImageEncoder.EncodePng(bitmap, PngCompression.Fast));
        }
    }
}
