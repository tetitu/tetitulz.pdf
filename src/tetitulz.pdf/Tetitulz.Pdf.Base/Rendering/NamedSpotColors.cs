// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Runtime.CompilerServices;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Base.Rendering
{
    /// <summary>
    /// Spot colours rendered through an ICC named-colour profile (<see cref="PdfRenderOptions.NamedColorProfile"/>): a <c>/Separation</c> whose colorant the profile names is drawn in the profile's colour
    /// rather than through its own alternate space and tint transform.
    /// </summary>
    /// <remarks>
    /// <para><b>The tint.</b> A named-colour profile gives the colour at full tint only, and neither ISO 32000 nor
    /// ICC.1 says how a partial tint looks. Two independent implementations agree on the rule used here: a tint
    /// <c>t</c> is the straight line in CIE L*a*b* (D50) from paper white — L* 100, a* and b* 0 — to the solid,
    /// <c>white + t × (solid − white)</c>. One blends so from its D50 white; the other (ICC.2 named colours) interpolates
    /// linearly between the tint-zero colour and the solid in the profile's PCS, which for a Lab PCS over white
    /// paper is the same line. Checked against an independent renderer's named-profile output.</para>
    /// <para><b>One rule, every consumer.</b> The rule is a colour space PDF already has: <c>[/Separation name
    /// [/Lab &lt;&lt;/WhitePoint D50&gt;&gt;] &lt;&lt;/FunctionType 2 /C0 [100 0 0] /C1 [L a b] /N 1&gt;&gt;]</c>.
    /// Every site that reads a Separation's alternate and tint transform — fills, images, shadings, an
    /// <c>/Indexed</c> base, a pattern's underlying space, the overprint preview's ink and a PSD's spot channel,
    /// in both renderers — asks <see cref="Substitute"/> first and reads that space instead.</para>
    /// <para><b>Which names.</b> The colorant's full name as the profile writes it (prefix, root, suffix),
    /// exactly or ignoring case — never the root alone, which would let a spot named <c>Black</c> take a
    /// process colorant's place. Process colorants, <c>/All</c> and <c>/None</c> are never substituted; a
    /// <c>/DeviceN</c> space keeps its tint transform (neither implementation defines how several named inks
    /// combine). A colorant the profile does not name keeps its own transform.</para>
    /// <para><b>How the profile reaches the sites.</b> Attached to the open document for the render
    /// (<see cref="Attach"/>), which every site already holds — a document is opened per call, and a
    /// <c>PdfRenderSession</c> renders one document under one set of options.</para>
    /// </remarks>
    internal static class NamedSpotColors
    {
        private static readonly ConditionalWeakTable<PdfDocument, Attached> ByDocument = new ConditionalWeakTable<PdfDocument, Attached>();
        private static readonly ConditionalWeakTable<PdfRenderOptions, Attached> ByOptions = new ConditionalWeakTable<PdfRenderOptions, Attached>();

        private sealed class Attached
        {
            internal readonly IccNamedColors Colours;
            internal readonly ConditionalWeakTable<PdfArray, PdfArray> Substituted = new ConditionalWeakTable<PdfArray, PdfArray>();
            internal Attached(IccNamedColors colours) { Colours = colours; }
        }

        /// <summary>
        /// Makes <paramref name="options"/>' named-colour profile the one <paramref name="doc"/>'s spots are drawn
        /// through for this render — or none, when the options name none or it cannot be read (reported).
        /// </summary>
        internal static void Attach(PdfDocument doc, PdfRenderOptions options)
        {
            if (doc == null) return;
            Attached attached = null;
            if (options?.NamedColorProfile != null && !ByOptions.TryGetValue(options, out attached))
            {
                IccNamedColors colours = IccNamedColors.TryParse(options.NamedColorProfile, out string reason);
                if (colours == null)
                {
                    RenderDiagnostics.Report(RenderDiagnosticKind.NamedColorProfileUnsupported,
                        "spot colours were drawn through their own tint transforms: " + reason);
                    return;
                }
                attached = new Attached(colours);
                try { ByOptions.Add(options, attached); }
                catch (ArgumentException) { ByOptions.TryGetValue(options, out attached); }
            }

            ByDocument.Remove(doc);
            if (attached != null) ByDocument.Add(doc, attached);
        }

        /// <summary>
        /// <paramref name="space"/>, or — when it is a <c>/Separation</c> whose colorant the document's attached
        /// profile names — the same colorant drawn in the profile's colour (see the remarks).
        /// </summary>
        internal static PdfArray Substitute(PdfDocument doc, PdfArray space)
        {
            if (doc == null || space == null || space.Count < 4 || !ByDocument.TryGetValue(doc, out Attached attached)) return space;
            if (attached.Substituted.TryGetValue(space, out PdfArray done)) return done;

            PdfArray result = space;
            if ((PdfResolve.Name(doc, space[0])?.Value) == PdfColorSpaceNames.Separation
                && PdfResolve.Name(doc, space[1]) is PdfName colorant
                && !SeparationPlate.IsProcess(colorant.Value)
                && colorant.Value != PdfColorSpaceNames.AllColorant && colorant.Value != PdfColorSpaceNames.NoneColorant
                && attached.Colours.FindFullName(colorant.Value) is IccNamedColor named)
                result = SpaceFor(colorant, named);

            try { attached.Substituted.Add(space, result); }
            catch (ArgumentException) { }
            return result;
        }

        /// <summary>The named colour as a Separation space: a Lab alternate (D50) and the straight line from white.</summary>
        internal static PdfArray SpaceFor(PdfName colorant, IccNamedColor named)
        {
            var lab = new PdfDictionary();
            lab.Set(PdfName.WhitePoint, Numbers(PdfColor.D50X, PdfColor.D50Y, PdfColor.D50Z));
            lab.Set(PdfName.Range, Numbers(-128, 127, -128, 127));
            var alternate = new PdfArray();
            alternate.Add(new PdfName(PdfColorSpaceNames.Lab));
            alternate.Add(lab);

            var tint = new PdfDictionary();
            tint.Set(PdfName.FunctionType, new PdfNumber(2));
            tint.Set(PdfName.Domain, Numbers(0, 1));
            tint.Set(PdfName.C0, Numbers(100, 0, 0));
            tint.Set(PdfName.C1, Numbers(named.L, named.A, named.B));
            tint.Set(PdfName.N, new PdfNumber(1));

            var space = new PdfArray();
            space.Add(new PdfName(PdfColorSpaceNames.Separation));
            space.Add(colorant);
            space.Add(alternate);
            space.Add(tint);
            return space;
        }

        private static PdfArray Numbers(params double[] values) => PdfArray.OfNumbers(values);
    }
}
