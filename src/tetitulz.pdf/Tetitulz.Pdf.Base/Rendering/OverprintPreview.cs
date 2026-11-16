// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Threading;
using SkiaSharp;
using Tetitulz.Pdf.Base.Images;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Functions;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Util;

namespace Tetitulz.Pdf.Base.Rendering
{
    /// <summary>
    /// Overprint simulation as a press shows it: the page's separation plates, recombined into sRGB
    /// — what an overprint preview shows. Reached from <c>PdfPageRenderer.RenderPageToBitmap</c>
    /// when <see cref="PdfRenderOptions.SimulateOverprint"/> is on and the page overprints.
    /// </summary>
    /// <remarks>
    /// <para><b>Why plates.</b> The composite renderer simulates overprint one paint at a time and
    /// only for solid DeviceCMYK path fills; shadings, patterns, images, spot inks, soft masks and
    /// non-rectangular clips were declined and reported. A separation plate composites every one of
    /// those per colorant already (<see cref="PdfSeparationRenderer"/>), so recombining the plates
    /// closes them all at once.</para>
    ///
    /// <para><b>The recombination.</b> The process plates' inks through the composite's own CMYK
    /// conversion (<see cref="PdfColor.CmykToRgb"/>), multiplied by each spot ink's appearance at its
    /// tint — its colour space's tint transform with that ink alone at the tint, through the
    /// alternate space, as a solid paint in that space renders. Inks mix by multiplying, which is
    /// how they darken one another on paper.</para>
    ///
    /// <para><b>Through a press.</b> With a CMYK <see cref="PdfRenderOptions.ProofProfile"/> the plates are that
    /// press's and the process inks come back through its own <c>A2B</c> table (<see cref="PressProof"/>) — the
    /// preview is the proof. Without one, the recombination above, then any proof of the result.</para>
    ///
    /// <para><b>What it changes beyond overprint, said plainly.</b> Only pages that overprint take
    /// this route. On them transparency composites per ink, as in a CMYK blending space, rather than
    /// in sRGB; colour without CMYK components reaches the plates by the inverse of the naive
    /// conversion and comes back by the conversion, so it round-trips to within the plates' 8-bit
    /// quantisation; and the page is opaque paper, whatever background the options name. The SVG
    /// writer has no plates and keeps its per-paint simulation.</para>
    /// </remarks>
    internal static class OverprintPreview
    {
        /// <summary>How many dictionaries <see cref="PageOverprints"/> will open before it answers yes, to be safe: a hostile resource graph costs no more than this.</summary>
        private const int MaxResourceDictionaries = 4096;

        /// <summary>
        /// Whether anything on the page can overprint: an ExtGState setting <c>/OP</c> or
        /// <c>/op</c> true in the page's resources, a form's, a pattern's, a Type 3 font's, a soft
        /// mask group's or an annotation appearance's — the only way overprint is turned on (§8.4.5). Errs towards yes
        /// when the graph is too large to finish, which costs a preview and changes nothing for a
        /// page that turns out not to overprint.
        /// </summary>
        internal static bool PageOverprints(PdfPage page, PdfDocument doc)
        {
            var pending = new Stack<PdfDictionary>();
            var seen = new HashSet<object>(PdfReferenceEquality.Instance);
            void Push(PdfObject obj)
            {
                if (PdfResolve.Dict(doc, obj) is PdfDictionary d && seen.Add(d)) pending.Push(d);
            }

            Push(page.Resources);
            if (PdfResolve.Array(doc, page.Dictionary, PdfName.Annots) is PdfArray annots)
                foreach (PdfObject a in annots)
                {
                    PdfDictionary ap = PdfResolve.Dict(doc, PdfResolve.Dict(doc, a) is PdfDictionary annot ? annot[PdfName.AP] : null);
                    PdfObject normal = PdfResolve.Value(doc, ap?[PdfName.N]);
                    if (normal is PdfStream appearance) Push(appearance[PdfName.Resources]);
                    else if (normal is PdfDictionary states)
                        foreach (var state in states)
                            if (PdfResolve.Value(doc, state.Value) is PdfStream s) Push(s[PdfName.Resources]);
                }

            int opened = 0;
            while (pending.Count > 0)
            {
                if (++opened > MaxResourceDictionaries) return true;
                PdfDictionary resources = pending.Pop();

                if (PdfResolve.Dict(doc, resources, PdfName.ExtGState) is PdfDictionary states)
                    foreach (var entry in states)
                    {
                        PdfDictionary extGState = PdfResolve.Dict(doc, entry.Value);
                        if (SetsOverprint(doc, extGState)) return true;
                        // A soft mask's group is content with resources of its own.
                        if (PdfResolve.Dict(doc, extGState, PdfName.SMask) is PdfDictionary mask
                            && PdfResolve.Value(doc, mask[PdfName.G]) is PdfStream group)
                            Push(group[PdfName.Resources]);
                    }

                if (PdfResolve.Dict(doc, resources, PdfName.XObject) is PdfDictionary xobjects)
                    foreach (var entry in xobjects)
                        if (PdfResolve.Value(doc, entry.Value) is PdfStream form
                            && PdfResolve.Name(doc, form, PdfName.Subtype)?.Value == "Form")
                            Push(form[PdfName.Resources]);

                if (PdfResolve.Dict(doc, resources, PdfName.Pattern) is PdfDictionary patterns)
                    foreach (var entry in patterns)
                    {
                        if (PdfResolve.Dict(doc, entry.Value) is not PdfDictionary pattern) continue;
                        Push(pattern[PdfName.Resources]);                                 // a tiling pattern's cell
                        if (SetsOverprint(doc, PdfResolve.Dict(doc, pattern, PdfName.ExtGState))) return true;   // a shading pattern's
                    }

                if (PdfResolve.Dict(doc, resources, PdfName.Font) is PdfDictionary fonts)
                    foreach (var entry in fonts)
                        if (PdfResolve.Dict(doc, entry.Value) is PdfDictionary font
                            && PdfResolve.Name(doc, font, PdfName.Subtype)?.Value == "Type3")
                            Push(font[PdfName.Resources]);
            }
            return false;
        }

        private static bool SetsOverprint(PdfDocument doc, PdfDictionary extGState) =>
            extGState != null
            && (PdfResolve.Bool(doc, extGState, PdfName.OP)?.Value == true || PdfResolve.Bool(doc, extGState, PdfName.op)?.Value == true);

        /// <summary>The page as its plates recombine — an opaque sRGB bitmap of the size an ordinary render has.</summary>
        internal static SKBitmap Render(PdfPage page, PdfDocument doc, float dpi, PdfRenderOptions options, CancellationToken cancellationToken,
            out bool proofed)
            => Preview(plate => PdfPageRenderer.RenderPageToBitmap(page, doc, dpi, options, null, cancellationToken, plate),
                       dpi, doc, options, out proofed);

        /// <summary>
        /// The preview from <paramref name="pass"/>'s plates — a page's or a tile's. With a CMYK
        /// <see cref="PdfRenderOptions.ProofProfile"/> the plates are that press's (<see cref="PressProof"/>) and
        /// <paramref name="proofed"/> says so: the preview is then the proof, and is not proofed again.
        /// </summary>
        internal static SKBitmap Preview(Func<SeparationPlate, SKBitmap> pass, float dpi, PdfDocument doc, PdfRenderOptions options,
            out bool proofed)
        {
            PressProof press = PressProof.For(options);
            PdfSeparations plates = PdfSeparationRenderer.RenderPlates(pass, dpi, out SpotColorantList spots, press?.Output);
            proofed = press != null;
            return Recombine(plates, spots, doc, press);
        }

        /// <summary>
        /// The plates as sRGB: the process inks' CMYK through the press's own table when <paramref name="press"/>
        /// names one, else the composite's conversion, times each spot ink's appearance at its tint.
        /// </summary>
        internal static SKBitmap Recombine(PdfSeparations plates, SpotColorantList spots, PdfDocument doc, PressProof press = null)
        {
            int w = plates.Width, h = plates.Height;
            byte[] c = plates["Cyan"].Tints, m = plates["Magenta"].Tints, y = plates["Yellow"].Tints, k = plates["Black"].Tints;
            var spotPlates = new List<(byte[] Tints, SKColor[] Appearance)>();
            foreach (string name in spots.Names)
                spotPlates.Add((plates[name].Tints, Appearance(doc, spots.SpaceOf(name), name)));

            Func<byte, byte, byte, byte, SKColor> pressInks = press?.Session();
            var bitmap = new SKBitmap(w, h, SKColorType.Rgba8888, SKAlphaType.Premul);
            BitmapPixels.Fill(bitmap, (row, py) =>
            {
                for (int px = 0; px < w; px++)
                {
                    int i = py * w + px;
                    SKColor process = pressInks != null
                        ? pressInks(c[i], m[i], y[i], k[i])
                        : PdfColor.CmykToRgb(c[i] / 255f, m[i] / 255f, y[i] / 255f, k[i] / 255f);
                    float r = process.Red, g = process.Green, b = process.Blue;
                    foreach (var (tints, appearance) in spotPlates)
                    {
                        byte t = tints[i];
                        if (t == 0) continue;
                        SKColor ink = appearance[t];
                        r *= ink.Red / 255f; g *= ink.Green / 255f; b *= ink.Blue / 255f;
                    }
                    row[px] = new SKColor(SaturatingCast.ToByte(Math.Round(r)), SaturatingCast.ToByte(Math.Round(g)), SaturatingCast.ToByte(Math.Round(b)), 255);
                }
            });
            return bitmap;
        }

        /// <summary>
        /// A spot ink's sRGB at each of the 256 tints: its space's tint transform with that ink alone
        /// at the tint (a <c>/DeviceN</c> space's other components at 0), through the alternate
        /// space. Without a space to read — or a transform that will not evaluate — the ink is drawn
        /// as grey <c>1 − tint</c>, and that is reported.
        /// </summary>
        internal static SKColor[] Appearance(PdfDocument doc, PdfArray space, string name)
        {
            var lut = new SKColor[256];
            var ink = Ink(doc, space, name);
            if (ink != null)
            {
                try
                {
                    for (int t = 0; t < 256; t++) lut[t] = ink(t);
                    return lut;
                }
                catch (Exception ex)
                {
                    RenderDiagnostics.Report(RenderDiagnosticKind.OverprintNotSimulated,
                        $"Overprint preview: the tint transform of spot ink {name} did not evaluate; it is drawn as grey.", ex);
                }
            }
            else
            {
                RenderDiagnostics.Report(RenderDiagnosticKind.OverprintNotSimulated,
                    $"Overprint preview: no colour space names spot ink {name} with a tint transform; it is drawn as grey.", null);
            }
            for (int t = 0; t < 256; t++) { byte v = (byte)(255 - t); lut[t] = new SKColor(v, v, v); }
            return lut;
        }

        /// <summary>
        /// A spot ink's sRGB as a function of its tint (0 … 255) — its space's tint transform with that ink alone
        /// at the tint, through the alternate — or null when no space names the ink with a tint transform. The
        /// function throws when the transform will not evaluate.
        /// </summary>
        /// <remarks>
        /// A <c>/DeviceN</c> space whose attributes' <c>/Colorants</c> define the ink as a Separation
        /// answers through that Separation: ISO 32000-1 §8.6.6.5 — its tint transform describes "the
        /// appearance of that colorant alone, whereas those of a DeviceN colour space describe only the
        /// appearance of its colorants in combination". Some renderers slice the DeviceN's transform
        /// instead; the composite, which paints the combination, keeps the DeviceN's.
        /// </remarks>
        internal static Func<int, SKColor> Ink(PdfDocument doc, PdfArray space, string name)
        {
            space = OwnSeparation(doc, space, name) ?? space;
            space = NamedSpotColors.Substitute(doc, space);   // a spot the named-colour profile names
            List<string> names = space == null ? null : SeparationPlate.ColorantNames(space);
            int index = names?.IndexOf(name) ?? -1;
            PdfDictionary tintTransform = space != null && space.Count > 3 ? PdfResolve.Dict(doc, space[3]) : null;
            if (index < 0 || tintTransform == null) return null;
            PdfObject alternate = space[2];
            string family = PdfColor.ResolveSpace(alternate, doc).DeviceFamily;
            var comps = new float[names.Count];
            return t =>
            {
                comps[index] = t / 255f;
                float[] alt = PdfFunctionEvaluator.Evaluate(tintTransform, comps, doc.ResolveObject);
                return IccColorTransform.ResolveAlternateColor(doc, alternate, alt, family);
            };
        }

        /// <summary>
        /// The Separation a <c>/DeviceN</c> space's attributes define for <paramref name="name"/> in their
        /// <c>/Colorants</c> (Table 71), or null when the space is no DeviceN or defines none for it.
        /// </summary>
        private static PdfArray OwnSeparation(PdfDocument doc, PdfArray space, string name)
        {
            if (space == null || space.Count < 5 || !(PdfResolve.Value(doc, space[0]) is PdfName family)
                || family.Value != PdfColorSpaceNames.DeviceN)
                return null;
            PdfDictionary attributes = PdfResolve.Dict(doc, space[4]);
            PdfDictionary colorants = attributes != null ? PdfResolve.Dict(doc, attributes, PdfName.Colorants) : null;
            if (colorants == null || !(PdfResolve.Value(doc, colorants[new PdfName(name)]) is PdfArray separation)) return null;
            return separation.Count > 3 && PdfResolve.Value(doc, separation[0]) is PdfName s && s.Value == PdfColorSpaceNames.Separation
                   && PdfResolve.Value(doc, separation[1]) is PdfName own && own.Value == name
                ? separation
                : null;
        }
    }
}
