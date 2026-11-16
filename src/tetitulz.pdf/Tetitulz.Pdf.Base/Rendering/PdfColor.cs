// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Globalization;
using SkiaSharp;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Text;
using Tetitulz.Pdf.Core.Util;

namespace Tetitulz.Pdf.Base.Rendering
{
    /// <summary>
    /// Single source of truth for PDF colour-space → sRGB conversion, shared by
    /// the SkiaSharp interpreter and the SVG renderer, so both have the full colour
    /// science (CMYK, CIE L*a*b*, CalRGB) and agree.
    /// </summary>
    internal static class PdfColor
    {
        /// <summary>
        /// Clamps to [0,1]. <b>Internal:</b> both renderers had their own byte-identical
        /// <c>Clamp</c>, so the range rule had three definitions. GAPS.md §2b.
        /// </summary>
        internal static float Clamp01(float v) => v < 0 ? 0 : v > 1 ? 1 : v;

        // ================================================================
        //  Colour-space resolution — the single resolver
        // ================================================================

        /// <summary>
        /// One PDF colour space, resolved from the object that declares it.
        ///
        /// <para><b>Why one type.</b> The raster path grew <b>five</b> resolvers answering
        /// overlapping questions — <c>ResolveColorSpaceName</c>, <c>GetShadingColorSpace</c>,
        /// <c>ImageComponentCount</c>, <c>GetColorComponents</c> and <c>ResolveAlternateCsName</c> —
        /// and they disagreed at the edges: only one knew about <c>/ImageMask</c>, only two resolved
        /// an indirect <c>/DeviceN</c> name array, and two returned a different family for the same
        /// <c>[/Indexed [/CalRGB …] …]</c>. Every question they answered is a projection of this
        /// one struct, so a new call site cannot introduce a sixth answer.</para>
        /// </summary>
        internal readonly struct ResolvedColorSpace
        {
            /// <summary>
            /// The colour-space family, with inline-image abbreviations expanded, or <c>null</c>
            /// when the object declares no colour space (absent, or not a name/array).
            /// </summary>
            public string Family { get; }

            /// <summary>The array form — the carrier of every parameter a space has (an
            /// <c>/Indexed</c> palette, a tint transform, a <c>/Gamma</c> dictionary, an ICC
            /// stream) — or <c>null</c> when the space was written as a bare name.</summary>
            public PdfArray Array { get; }

            /// <summary>
            /// Components one sample or one <c>sc</c> operand group carries <b>in this space</b>:
            /// <c>/Indexed</c> is 1 (a palette index) and <c>/Separation</c> is 1 (a tint),
            /// whatever their base or alternate space's arity.
            /// </summary>
            public int Components { get; }

            internal ResolvedColorSpace(string family, PdfArray array, int components)
            {
                Family = family; Array = array; Components = components;
            }

            /// <summary>
            /// The name to hand <see cref="ComponentsToColor"/> for values expressed directly in
            /// this space: an <c>/ICCBased</c> space collapses to the device space its <c>/N</c>
            /// stands for, and a space that declared nothing at all is DeviceRGB.
            /// </summary>
            public string DeviceFamily =>
                Family == null                     ? PdfColorSpaceNames.DeviceRGB
                : Family == PdfColorSpaceNames.ICCBased ? DeviceSpaceForComponentCount(Components)
                : Family;
        }

        /// <summary>
        /// An <c>/ICCBased</c> profile stream's <c>/N</c> — the only statement of the space's arity —
        /// following an indirect reference to the stream and to <c>/N</c>; 3 when either is unreadable or out of range.
        /// </summary>
        /// <param name="profile">The array's second element, as stored.</param>
        /// <param name="doc">Document used to follow indirect references; may be null.</param>
        /// <remarks>
        /// <para><b>Three copies of this read, all raw.</b> <see cref="ResolveSpace"/>,
        /// <c>IndexedPalette</c> and the SVG writer's <c>GetIccComponentCount</c> each tested
        /// <c>icc[PdfName.N] is PdfNumber</c>, so a one-channel profile written <c>/N 7 0 R</c> was
        /// taken for RGB: a grey image drew nothing, a palette over it read three bytes an entry,
        /// and a CMYK fill exported as RGB. One read, here, in the leaf both renderers share.</para>
        /// <para>A count outside 1 to <see cref="PdfLimits.MaxColorantCount"/> is read as absent:
        /// a palette multiplies it by its entry index, and <c>(int)LongValue</c> read 4 294 967 297
        /// as a one-channel profile.</para>
        /// </remarks>
        internal static int IccComponentCount(PdfObject profile, PdfDocument doc)
            => PdfResolve.Number(doc, PdfResolve.Dict(doc, profile), PdfName.N) is PdfNumber n
               && n.IsInteger && n.LongValue >= 1 && n.LongValue <= PdfLimits.MaxColorantCount
                ? (int)n.LongValue
                : PdfColorSpaceNames.RgbComponents;

        /// <summary>
        /// Resolves a <c>/ColorSpace</c> entry — a name, an array, or an indirect reference to
        /// either — into its family, parameter array and component count.
        /// </summary>
        /// <param name="csObj">The declaring object; may be null.</param>
        /// <param name="doc">Document used to follow indirect references; may be null.</param>
        public static ResolvedColorSpace ResolveSpace(PdfObject csObj, PdfDocument doc)
        {
            if (doc != null) csObj = doc.ResolveObject(csObj) ?? csObj;

            if (csObj is PdfName n)
            {
                string family = PdfColorSpaceNames.Expand(n.Value);
                return new ResolvedColorSpace(family, null,
                    PdfColorSpaceNames.ComponentCount(family, PdfColorSpaceNames.RgbComponents));
            }

            if (csObj is PdfArray a && a.Count > 0 && a[0] is PdfName familyName)
            {
                string family = PdfColorSpaceNames.Expand(familyName.Value);
                int components;
                switch (family)
                {
                    // The ICC profile stream's /N is the only statement of its arity.
                    case PdfColorSpaceNames.ICCBased:
                        components = a.Count > 1
                            ? IccComponentCount(a[1], doc)
                            : PdfColorSpaceNames.RgbComponents;
                        break;
                    // The colorant-name array's length is the arity; the family name cannot
                    // supply it. Resolved through an indirect reference — two of the five old
                    // resolvers did not, and read a 6-ink DeviceN as 3-component RGB.
                    case PdfColorSpaceNames.DeviceN:
                        components = a.Count > 1
                            && (doc?.ResolveObject(a[1]) ?? a[1]) is PdfArray names && names.Count > 0
                                ? names.Count
                                : PdfColorSpaceNames.RgbComponents;
                        break;
                    default:
                        components = PdfColorSpaceNames.ComponentCount(
                            family, PdfColorSpaceNames.RgbComponents);
                        break;
                }
                return new ResolvedColorSpace(family, a, components);
            }

            return new ResolvedColorSpace(null, csObj as PdfArray, PdfColorSpaceNames.RgbComponents);
        }

        /// <summary>
        /// §8.6.8 Table 74 — the operand vector <c>cs</c>/<c>CS</c> must set the current colour to,
        /// or <c>null</c> for <c>/Pattern</c>, which has no initial colour.
        /// </summary>
        /// <remarks>
        /// <b>A value rule, so it gets one implementation reached from both renderers.</b> It was
        /// two private <c>SetInitialColorForSpace</c> methods, one per renderer, agreeing on all
        /// four decisions — the /Pattern early return, the arity, the Separation/DeviceN 1.0 rule
        /// and the all-zeros-except-CMYK-K rule — and the SVG copy's own doc comment ended "The
        /// raster path's SetInitialColorForSpace is the reference", which is the prose CLAUDE.md
        /// calls a defect marker rather than documentation.
        ///
        /// <para>The caller pushes the result through its own <c>sc</c> handler rather than
        /// converting here, so a Separation's tint transform, an Indexed palette lookup and a
        /// CalGray <c>/Gamma</c> all apply exactly as they do for an explicit <c>sc</c>. That is
        /// why this returns operands and not a colour: one colour-setting path per renderer, not a
        /// second one free to drift from it.</para>
        ///
        /// <para><b>The family must already be device-mapped.</b> <c>/ICCBased</c> is not a
        /// paintable family — its <c>/N</c> names which device space's black is meant — so pass
        /// <see cref="ResolvedColorSpace.DeviceFamily"/>, never the raw first array element.</para>
        /// </remarks>
        /// <param name="deviceFamily">The device-mapped family; see the remarks.</param>
        /// <param name="csArray">The space's parameter array, or null for a bare device name.</param>
        /// <param name="doc">Document used to follow indirect references; may be null.</param>
        internal static PdfObject[] InitialColorOperands(string deviceFamily, PdfArray csArray, PdfDocument doc)
        {
            // "No initial colour": nothing is painted until a pattern is selected. The caller
            // clears its own paint side's pattern name, which is what makes that observable.
            if (deviceFamily == PdfColorSpaceNames.Pattern) return null;

            int n = csArray != null
                ? ResolveSpace(csArray, doc).Components
                : PdfColorSpaceNames.ComponentCount(deviceFamily, PdfColorSpaceNames.RgbComponents);
            n = Math.Max(1, Math.Min(PdfLimits.MaxColorantCount, n));

            var operands = new PdfObject[n];
            if (deviceFamily == PdfColorSpaceNames.Separation || deviceFamily == PdfColorSpaceNames.DeviceN)
            {
                // Table 74: "1.0 in each component" — full tint, which for a spot ink is normally
                // a saturated colour rather than anything resembling black.
                for (int i = 0; i < n; i++) operands[i] = new PdfNumber(1.0);
                return operands;
            }

            // All components 0 — black in DeviceGray/DeviceRGB/CalGray/CalRGB/Lab, and index 0 in
            // /Indexed. DeviceCMYK is the exception: its black is 0,0,0,1.
            for (int i = 0; i < n; i++) operands[i] = new PdfNumber(0.0);
            if (deviceFamily == PdfColorSpaceNames.DeviceCMYK && n >= PdfColorSpaceNames.CmykComponents)
                operands[PdfColorSpaceNames.CmykComponents - 1] = new PdfNumber(1.0);
            return operands;
        }

        /// <summary>
        /// The <c>/CS</c> entry of <paramref name="owner"/>'s transparency group (<c>/Group</c>
        /// with <c>/S /Transparency</c>, or no <c>/S</c> — Table 96 defines one value), resolved;
        /// <c>null</c> when the owner has no transparency group or the group names no space. For
        /// the page's own group (§11.6.6: honoured whatever its <c>/I</c>) and a form's alike.
        /// </summary>
        /// <param name="owner">The page or form dictionary.</param>
        /// <param name="doc">Document used to follow indirect references; may be null.</param>
        internal static PdfObject TransparencyGroupColorSpace(PdfDictionary owner, PdfDocument doc)
        {
            var group = PdfResolve.Dict(doc, owner, PdfName.Group);
            if (group == null) return null;
            if ((PdfResolve.Name(doc, group, PdfName.S)?.Value ?? PdfName.Transparency.Value) != PdfName.Transparency.Value)
                return null;
            return PdfResolve.Value(doc, group[PdfName.CS]);
        }

        /// <summary>
        /// Which blending space a <b>transparency group</b>'s <c>/CS</c> (§11.6.6, Table 147)
        /// names: <see cref="GroupBlendSpace.Rgb"/>, <see cref="GroupBlendSpace.Gray"/> or
        /// <see cref="GroupBlendSpace.Cmyk"/>, <c>null</c> for an absent entry (inherit the parent's,
        /// §11.6.6), and <c>false</c> — with the shared sentence — for a space §11.3.4 does not admit,
        /// which the group then inherits as well.
        /// </summary>
        /// <param name="csObj">The group dictionary's <c>/CS</c> entry, or <c>null</c> when absent.</param>
        /// <param name="doc">Document used to follow indirect references; may be null.</param>
        /// <param name="space">The blending space, or <c>null</c> to inherit.</param>
        /// <param name="unsupportedDetail">The diagnostic detail when the result is <c>false</c>;
        /// <c>null</c> otherwise.</param>
        /// <remarks>
        /// <para><b>An absent <c>/CS</c> is supported, not unsupported.</b> §11.6.6 makes it
        /// optional for a non-isolated group, where the group simply inherits the space of the
        /// group it composites into. Reporting an absent key would fire on the majority of real
        /// transparency groups and say nothing about any of them.</para>
        /// <para><b>It decides on the FAMILY, never on the component count.</b> <c>/Lab</c> is a
        /// three-component space and is emphatically not an additive RGB one — a
        /// <c>Components == 3</c> test lets it straight through, and both renderers would then
        /// composite Lab values as if they were sRGB. <c>ICCBased</c> is the one family whose arity
        /// has to be consulted, because <c>/N</c> is the only statement of what it stands for.</para>
        /// <para>It lives here, and not in either renderer, because the same question is asked at
        /// the raster <c>Do</c> and the SVG <c>Do</c>. Every "silently wrong output" defect this
        /// repository has found in rendering was one of the two lagging the other, and a predicate
        /// duplicated in both files is that defect waiting to happen. The two answer it differently
        /// by design: the raster path composites a grey or CMYK group in its space
        /// (<see cref="GroupBlenders"/>); the SVG export, which composites in sRGB, reports it.</para>
        /// </remarks>
        internal static bool TryGetGroupBlendSpace(PdfObject csObj, PdfDocument doc,
                                                   out GroupBlendSpace? space, out string unsupportedDetail)
        {
            unsupportedDetail = null;
            space = null;

            var resolved = doc?.ResolveObject(csObj) ?? csObj;
            if (resolved == null || resolved is PdfNull) return true;   // absent → inherit

            var cs = ResolveSpace(resolved, doc);
            space = cs.Family switch
            {
                PdfColorSpaceNames.DeviceRGB  => GroupBlendSpace.Rgb,
                PdfColorSpaceNames.CalRGB     => GroupBlendSpace.Rgb,
                PdfColorSpaceNames.DeviceGray => GroupBlendSpace.Gray,
                PdfColorSpaceNames.CalGray    => GroupBlendSpace.Gray,
                PdfColorSpaceNames.DeviceCMYK => GroupBlendSpace.Cmyk,
                PdfColorSpaceNames.ICCBased   => cs.Components switch
                {
                    PdfColorSpaceNames.RgbComponents  => GroupBlendSpace.Rgb,
                    PdfColorSpaceNames.GrayComponents => GroupBlendSpace.Gray,
                    PdfColorSpaceNames.CmykComponents => GroupBlendSpace.Cmyk,
                    _ => (GroupBlendSpace?)null,
                },
                _ => null,
            };
            if (space.HasValue) return true;

            unsupportedDetail =
                "transparency group /CS " + DescribeGroupSpace(cs) + " (§11.6.6): not a blending " +
                "colour space (§11.3.4 admits DeviceGray, DeviceRGB, DeviceCMYK, CalGray, CalRGB and " +
                "an ICCBased grey, RGB or CMYK space), so the group is composited in its parent's " +
                "space instead.";
            return false;
        }

        /// <summary>
        /// A luminosity soft mask's <c>/BC</c> backdrop colour (§11.6.5.2), or <c>null</c> when the
        /// entry is absent or empty — which the callers read as the spec default, black.
        /// </summary>
        /// <remarks>
        /// <para><b>The two renderers disagreed here, and on the commonest CMYK value they
        /// disagreed by inverting each other.</b> The raster path read <c>/BC</c> as raw RGB
        /// components whatever its length, so a four-component backdrop lost <c>k</c> entirely:
        /// <c>/BC [0 0 0 0]</c> — CMYK <i>white</i>, the backdrop a producer writes to mean "do not
        /// mask" — became RGB black, luma 0, mask fully out. The SVG writer switched on the
        /// component count and got that one right, while its own comment admitted the arity is a
        /// guess.</para>
        ///
        /// <para><b>Neither had to guess.</b> §11.6.5.2 says <c>/BC</c> is expressed in the colour
        /// space of the mask's own transparency group, so the space is written down two
        /// dereferences away at <c>/G /Group /CS</c>. That is what is read here; the component
        /// count is the fallback only when the group names no space at all, which §11.6.6 permits.
        /// <b>Not</b> gated on <see cref="TryGetGroupBlendSpace"/>: that predicate is about
        /// what space the group is <i>composited</i> in, and a backdrop still has to be read in the
        /// space its numbers were written in — gating on it would make the <c>/CS</c> read
        /// unreachable for every space whose arity does not already give the same answer.</para>
        /// </remarks>
        internal static SKColor? SoftMaskBackdrop(PdfDictionary smaskDict, PdfDocument doc)
        {
            if (smaskDict == null) return null;

            var bcObj = doc?.ResolveObject(smaskDict[PdfName.BC]) ?? smaskDict[PdfName.BC];
            if (bcObj is not PdfArray bc || bc.Count == 0) return null;

            var comps = new float[bc.Count];
            for (int i = 0; i < bc.Count; i++) comps[i] = PdfResolve.ArrayFloat(doc, bc, i);

            var gObj  = doc?.ResolveObject(smaskDict[PdfName.G]) ?? smaskDict[PdfName.G];
            var grpObj = gObj is PdfDictionary g
                ? doc?.ResolveObject(g[PdfName.Group]) ?? g[PdfName.Group]
                : null;
            var csObj = grpObj is PdfDictionary grp ? grp[PdfName.CS] : null;

            var space = ResolveSpace(csObj, doc);
            string family = space.Family != null
                ? space.DeviceFamily
                : DeviceSpaceForComponentCount(bc.Count);

            return ComponentsToColor(comps, family);
        }

        /// <summary>
        /// What the raster renderer approximates when it composites a group in the blending space
        /// <paramref name="csObj"/> names, or <c>null</c> when it composites the space as written.
        /// </summary>
        /// <remarks>
        /// <para><see cref="TryGetGroupBlendSpace"/> admits an <c>ICCBased</c> grey or CMYK space by
        /// its arity, and <see cref="GroupBlenders"/> then composites in the <i>device</i> family's
        /// arithmetic — <see cref="CmykToRgb"/> and the BT.601 grey — never through the profile.
        /// For a paint inside the group that is what §11.3.6's formula is run on, so a translucent
        /// or blended paint interpolates in device ink or device grey where the profile would have
        /// placed the components elsewhere. The profile is still applied to what is <i>painted</i>
        /// into the group (<c>IccColorTransform</c>); the compositing is what approximates, and it
        /// is reported.</para>
        /// <para>Raster-only by design: the SVG export composites every group in sRGB and already
        /// reports each non-RGB space, device or profiled alike, so a second report there would say
        /// the same thing twice.</para>
        /// </remarks>
        internal static string GroupBlendSpaceApproximation(PdfObject csObj, PdfDocument doc)
        {
            var resolved = doc?.ResolveObject(csObj) ?? csObj;
            if (resolved == null || resolved is PdfNull) return null;

            var cs = ResolveSpace(resolved, doc);
            if (cs.Family != PdfColorSpaceNames.ICCBased) return null;
            string family = cs.Components switch
            {
                PdfColorSpaceNames.GrayComponents => "grey",
                PdfColorSpaceNames.CmykComponents => "CMYK",
                _ => null,
            };
            if (family == null) return null;

            return "transparency group /CS " + DescribeGroupSpace(cs) + " (§11.6.6): the group is " +
                   "composited in device " + family + ", not through its ICC profile — a translucent " +
                   "or blended paint inside it interpolates in the device model's components; the " +
                   "profile is applied to what is painted into the group, not to the compositing.";
        }

        /// <summary>
        /// Names a group colour space for a diagnostic — the family, plus the ICC arity, which is
        /// the only thing distinguishing a supported <c>/N 3</c> profile from an unsupported one.
        /// </summary>
        private static string DescribeGroupSpace(ResolvedColorSpace cs)
            => cs.Family == null
                ? "is not a colour space"
                : cs.Family == PdfColorSpaceNames.ICCBased
                    ? $"/{cs.Family} (/N {cs.Components})"
                    : $"/{cs.Family}";

        /// <summary>
        /// Maps an ICCBased profile's <c>/N</c> component count onto the device space it
        /// stands in for: 1 = gray, 4 = CMYK, anything else (including a missing /N) = RGB.
        /// </summary>
        public static string DeviceSpaceForComponentCount(int n)
            => n switch
            {
                PdfColorSpaceNames.GrayComponents => PdfColorSpaceNames.DeviceGray,
                PdfColorSpaceNames.CmykComponents => PdfColorSpaceNames.DeviceCMYK,
                _                                 => PdfColorSpaceNames.DeviceRGB
            };

        /// <summary>
        /// sRGB transfer function (gamma encoding) for a linear-light component, per
        /// IEC 61966-2-1. Shared by <see cref="LabToSRgb(float,float,float)"/> and <see cref="CalRgbToSRgb(float[],PdfDictionary,PdfDocument)"/>,
        /// rather than private copies of each.
        /// </summary>
        private static double SrgbGamma(double c) => Core.Imaging.SrgbTransfer.Encode(c);

        /// <summary>
        /// Quantises a <c>double</c> [0,1] component to an 8-bit channel value — the
        /// <c>double</c> counterpart of <see cref="Channel"/>, and the same rule.
        /// </summary>
        /// <remarks>
        /// <b>Internal rather than private</b>, for the same reason <see cref="Channel"/> is:
        /// <c>PdfColorSpaceConverter</c> works in <c>double</c> and uses this expression. A caller holding a <c>float</c>
        /// wants <see cref="Channel"/>; one holding a <c>double</c> wants this. Which of the two a
        /// site needs is a fact about its arithmetic, not a licence to write a third.
        /// </remarks>
        internal static byte Clamp255(double v) => (byte)Math.Max(0, Math.Min(255, SaturatingCast.ToInt(v * 255 + 0.5)));

        /// <summary>
        /// Quantises a [0,1] value to an 8-bit channel, clamping and <b>rounding to nearest</b> —
        /// the <c>float</c> counterpart of <see cref="Clamp255"/>, and <b>the single quantisation
        /// rule for the render path</b>.
        ///
        /// <para>Device and CIE spaces round alike: truncating the device spaces would make DeviceGray
        /// 0.5 a level darker than the equivalent CalGray, and bias every channel down by half a
        /// level.</para>
        ///
        /// <para><b>Internal, not private, deliberately.</b> The image <c>/Decode</c> paths in both
        /// renderers quantise too, and would drift apart the same way for the same reason —
        /// raster truncating, SVG rounding, so <c>/Decode [0 0.5]</c> over sample 255 rendered 127
        /// and exported 128. Any new site that turns a [0,1] value into a channel byte belongs
        /// here rather than writing its own cast.</para>
        /// </summary>
        internal static byte Channel(float v) => ClampChannel(v * 255f);

        /// <summary>
        /// The same rule as <see cref="Channel"/> for a value already scaled to 0–255: clamp, then
        /// round to nearest.
        ///
        /// <para>Exists because both renderers carried a byte-identical <c>ClampByte</c> — a third
        /// spelling of one quantisation rule, alongside <see cref="Channel"/> and
        /// <see cref="Clamp255"/>. Whether the caller holds a [0,1] value or a 0–255 one is a
        /// detail of where it is in the pipeline, not a reason for a second rounding convention;
        /// that is exactly how the tree ended up with three. GAPS.md §2b.</para>
        /// </summary>
        internal static byte ClampChannel(float scaled)
            => SaturatingCast.ToByte(Math.Max(0f, Math.Min(255f, scaled + 0.5f)));

        private const int Max8 = 255, Max16 = 65535;

        /// <summary>
        /// One 16-bit sample down-converted to 8 bits, rounding to nearest.
        /// </summary>
        /// <remarks>
        /// <para>Lives here, with the other quantisation rules, because both renderers need it and
        /// each had its own transcription — a pair that had already diverged once, by a systematic
        /// ±1 on every pixel of every 16-bpc image.</para>
        /// <para><b>It is deliberately not reached by one renderer calling the other.</b> That was
        /// tried and reverted the same day: <c>PdfSvgRenderer</c> calling
        /// <c>ContentStreamInterpreter.Scale16To8</c> made the SVG path trigger that type's static
        /// initializer for the first time, and the Base test host began crashing part-way through a
        /// parallel run — reporting "Passed!" for the tests that had completed while silently
        /// dropping the rest, the exact signature GAPS.md §8a records. A shared leaf helper has no
        /// initializer to trigger. <b>Share downward into a leaf, never sideways between peers.</b></para>
        /// </remarks>
        internal static byte Scale16To8(int v16)
            => (byte)Core.Imaging.RasterSample.ScaleToByte(v16, 16);

        /// <summary>Formats an <see cref="SKColor"/> as an SVG <c>rgb(r,g,b)</c> string.</summary>
        public static string ToCssRgb(SKColor c) => $"rgb({c.Red},{c.Green},{c.Blue})";

        /// <summary>
        /// The inverse of <see cref="ToCssRgb"/>, plus the <c>#RGB</c> / <c>#RRGGBB</c> forms.
        /// Returns <c>false</c> — leaving <paramref name="color"/> untouched — for anything that is
        /// not a solid colour, which for this writer means <c>url(#…)</c> pattern and gradient
        /// references and <c>none</c>.
        /// </summary>
        /// <remarks>
        /// <para><b>It parses a string this assembly itself wrote</b>, and lives next to the writer
        /// for that reason: <see cref="ToCssRgb"/> is the only producer of SVG paint values, so the
        /// round trip is exact — but the two have to move together, and a second CSS syntax added
        /// on the writing side would arrive here as an unparseable value.</para>
        /// <para>The hex branch is defensive rather than reachable today. It is kept because the
        /// SVG image-mask path already had it, and losing it would silently paint a stencil black
        /// rather than fail visibly.</para>
        /// </remarks>
        internal static bool TryParseCssColor(string css, out SKColor color)
        {
            color = default;
            if (string.IsNullOrEmpty(css)) return false;

            if (css[0] == '#')
            {
                string hex = css.Substring(1);
                if (hex.Length == 3)
                    hex = new string(new[] { hex[0], hex[0], hex[1], hex[1], hex[2], hex[2] });
                if (hex.Length != 6) return false;
                if (!byte.TryParse(hex.Substring(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte hr) ||
                    !byte.TryParse(hex.Substring(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte hg) ||
                    !byte.TryParse(hex.Substring(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte hb))
                    return false;
                color = new SKColor(hr, hg, hb);
                return true;
            }

            if (!css.StartsWith("rgb(", StringComparison.Ordinal) || css[css.Length - 1] != ')')
                return false;

            string[] parts = css.Substring(4, css.Length - 5).Split(',');
            if (parts.Length != 3) return false;
            if (!byte.TryParse(parts[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out byte r) ||
                !byte.TryParse(parts[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out byte g) ||
                !byte.TryParse(parts[2].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out byte b))
                return false;

            color = new SKColor(r, g, b);
            return true;
        }

        /// <summary>
        /// Unpacks a <c>0xAARRGGBB</c> page background — the single reading of
        /// <see cref="PdfRenderOptions.BackgroundArgb"/>, shared by both renderers.
        /// </summary>
        /// <remarks>
        /// The raster path unpacked this inline and the SVG path did not read the option at all,
        /// emitting a hardcoded opaque white <c>&lt;rect&gt;</c>: a transparent render and a
        /// transparent export disagreed, and nothing reported the discarded value. Both now come
        /// through here, in a leaf with no static initializer, per the sharing rule above.
        /// </remarks>
        internal static void UnpackArgb(uint argb, out byte a, out byte r, out byte g, out byte b)
        {
            a = (byte)((argb >> 24) & 0xFF);
            r = (byte)((argb >> 16) & 0xFF);
            g = (byte)((argb >> 8)  & 0xFF);
            b = (byte)(argb & 0xFF);
        }

        /// <summary>
        /// Converts component values in a named device/CIE colour space to sRGB — <b>the single
        /// component→sRGB converter</b>, shared by both renderers and by every colour operator,
        /// image decoder and palette builder in the raster path.
        ///
        /// <para>CalRGB is treated as DeviceRGB here; the precise gamma+matrix CalRGB transform
        /// lives in <see cref="CalRgbToSRgb(float[],PdfDictionary,PdfDocument)"/> and is used where the colour-space parameter
        /// dictionary is available. CalGray is likewise treated as DeviceGray, with the /Gamma-aware
        /// transform in <see cref="CalGrayToSRgb"/>.</para>
        ///
        /// <para><b>Dispatch rule.</b> A <i>known</i> family decides the interpretation, whatever
        /// the operand count — <c>/DeviceRGB</c> with one component is a malformed file, not a grey.
        /// Only when <paramref name="cs"/> names a family this converter does not know (an
        /// unresolved <c>/ICCBased</c>, a <c>/Separation</c> whose tint transform was not applied,
        /// a name that is not a colour space at all) does the <i>count</i> decide: 1 → grey,
        /// ≥ 4 → CMYK, otherwise RGB — here, not in a second converter applying it to <b>every</b>
        /// space including DeviceRGB, which would convert the same Separation colour two different
        /// ways depending on which a caller reached.</para>
        /// </summary>
        public static SKColor ComponentsToColor(float[] comps, string cs)
        {
            if (comps == null || comps.Length == 0) return SKColors.Black;
            float At(int i) => comps.Length > i ? comps[i] : 0f;
            SKColor Gray() => new SKColor(Channel(comps[0]), Channel(comps[0]), Channel(comps[0]));
            SKColor Rgb() => new SKColor(Channel(At(0)), Channel(At(1)), Channel(At(2)));
            // Delegates rather than repeating (1−c)(1−k): this and CmykToRgb were two
            // implementations in one file that disagreed on clamping. GAPS.md §2b.
            SKColor Cmyk() => CmykToRgb(At(0), At(1), At(2), At(3));

            switch (cs)
            {
                case PdfColorSpaceNames.DeviceGray: case PdfColorSpaceNames.CalGray:
                case PdfColorSpaceNames.DeviceGrayAbbrev:
                    return Gray();

                case PdfColorSpaceNames.DeviceRGB: case PdfColorSpaceNames.CalRGB:
                case PdfColorSpaceNames.DeviceRGBAbbrev:
                    return Rgb();

                case PdfColorSpaceNames.DeviceCMYK: case PdfColorSpaceNames.DeviceCMYKAbbrev:
                    return Cmyk();

                case PdfColorSpaceNames.Lab:
                {
                    // CIE L*a*b* → sRGB via D50 XYZ. Uses default D50 white point.
                    float L = comps.Length > 0 ? comps[0] : 0f;   // 0–100
                    float a = comps.Length > 1 ? comps[1] : 0f;   // –128..127
                    float b2 = comps.Length > 2 ? comps[2] : 0f;  // –128..127
                    return LabToSRgb(L, a, b2);
                }

                case PdfColorSpaceNames.Pattern:
                    // A Pattern space carries no colour components of its own (§8.6.8), so there is
                    // nothing meaningful to paint. Return a transparent pixel rather than opaque
                    // black so it cannot contaminate composite output — operand-path callers should
                    // have set FillIsPattern via `scn`; this branch is reached only when an
                    // intermediate path (shading colour, mesh interpolation, a malformed Indexed
                    // base) evaluates a Pattern space as if it held components.
                    RenderDiagnostics.Report(RenderDiagnosticKind.PatternOperandNoName,
                        "Pattern colour space evaluated without a pattern name; using transparent.");
                    return SKColors.Transparent;

                default:
                    // Unknown family — the component count is the only evidence left. Matches what
                    // most viewers do for an unresolved ICCBased alternate.
                    if (comps.Length == PdfColorSpaceNames.GrayComponents) return Gray();
                    if (comps.Length >= PdfColorSpaceNames.CmykComponents) return Cmyk();
                    return Rgb();
            }
        }

        /// <summary>
        /// Simple CMYK → sRGB (naïve subtractive model, no ICC) — <b>the single implementation
        /// of <c>r = (1−c)(1−k)</c> in the tree</b>, reached from <see cref="ComponentsToColor"/>,
        /// both renderers' image paths, both annotation-colour readers, the ICC 4-component
        /// fallback, <c>ImageSampleLayout</c> and <c>OverprintCompositor</c>.
        ///
        /// <para><b>Do not re-inline this formula at a call site</b>: copies drift into different
        /// rounding conventions.</para>
        ///
        /// <para>Operands are clamped <i>before</i> conversion, not only after. Clamping the result
        /// alone is not enough: <c>c = k = 2</c> gives <c>(1−2)(1−2) = 1</c>, so an out-of-range
        /// pair would paint white where it must paint black.</para>
        /// </summary>
        public static SKColor CmykToRgb(float c, float m, float y, float k)
        {
            float ik = 1f - Clamp01(k);
            return new SKColor(
                Channel((1f - Clamp01(c)) * ik),
                Channel((1f - Clamp01(m)) * ik),
                Channel((1f - Clamp01(y)) * ik));
        }

        /// <summary>
        /// Reads <paramref name="key"/> from a CIE parameter dictionary, following an indirect
        /// reference.
        /// </summary>
        /// <remarks>
        /// <para><b>These dictionaries are where an unresolved read is least visible.</b> Every
        /// entry has a spec default that is also a perfectly ordinary value — <c>/Gamma</c> 1,
        /// <c>/Matrix</c> identity, a D65 <c>/WhitePoint</c>, <c>/Range</c> ±100 — so an indirect
        /// one does not fail, it quietly renders a different colour. A <c>/CalGray</c> space with
        /// an indirect <c>/Gamma</c> becomes literally <c>DeviceGray</c>.</para>
        ///
        /// <para><c>doc</c> is optional because several callers legitimately have none — an
        /// <c>/Indexed</c> palette built from a already-resolved parameter dictionary, for
        /// instance. Where it is null the behaviour is exactly what it was.</para>
        /// </remarks>
        private static PdfObject Res(PdfDocument doc, PdfDictionary paramDict, PdfName key)
        {
            var raw = paramDict?[key];
            return raw is PdfReference && doc != null ? doc.ResolveObject(raw) : raw;
        }

        /// <summary>
        /// CIE L*a*b* → sRGB honouring the colour space's own parameter dictionary: the components
        /// are first clamped to <c>/Range</c> (default <c>[-100 100 -100 100]</c>, ISO 32000-1
        /// §8.6.5.4 Table 65) and the conversion then runs against its <c>/WhitePoint</c> instead of
        /// the assumed D50.
        ///
        /// <para>With no <c>/WhitePoint</c> — it is a required entry, so this only happens on a
        /// malformed space — the D50 <see cref="LabToSRgb(float,float,float)"/> path is used
        /// unchanged.</para>
        /// </summary>
        public static SKColor LabToSRgb(float L, float a, float b, PdfDictionary paramDict, PdfDocument doc = null)
            => LabToSRgb(L, a, b, CieParams.ForLab(paramDict, doc));

        // ================================================================
        //  Pre-parsed CIE parameters — the same conversion, read once
        // ================================================================

        /// <summary>
        /// A <c>/CalRGB</c> or <c>/Lab</c> parameter dictionary, already resolved and converted.
        ///
        /// <para><b>Why this exists.</b> <see cref="CalRgbToSRgb(float[],PdfDictionary,PdfDocument)"/>
        /// and <see cref="LabToSRgb(float,float,float,PdfDictionary,PdfDocument)"/> read
        /// <c>/Gamma</c>, <c>/WhitePoint</c>, <c>/Matrix</c> and <c>/Range</c> out of the dictionary
        /// on <b>every call</b> — three indirect-reference resolutions and up to fifteen
        /// <c>PdfNumber</c> casts. That is right for an <c>sc</c> fill, which happens once per
        /// operator. It is wrong for an <b>image</b>, where the call is per pixel and every one of
        /// those reads returns the same value: a 2000×2000 CalRGB image paid 12 million dictionary
        /// lookups to compute nine constants four million times.</para>
        ///
        /// <para>The two <c>CalGray</c> and <c>Separation</c> branches beside them in the same
        /// <c>switch</c> already hoist their work into a LUT; these two did not, and the asymmetry
        /// is the whole finding.</para>
        ///
        /// <para><b>A struct, and a leaf.</b> No static fields, nothing to initialise, and it is
        /// passed by <c>in</c> so the per-pixel call copies nothing.</para>
        /// </summary>
        internal readonly struct CieParams
        {
            internal readonly double GammaR, GammaG, GammaB;
            internal readonly double Xw, Yw, Zw;
            internal readonly bool HasWhitePoint;
            internal readonly double M0, M1, M2, M3, M4, M5, M6, M7, M8;
            internal readonly double AMin, AMax, BMin, BMax;

            private CieParams(
                double gr, double gg, double gb,
                double xw, double yw, double zw, bool hasWp,
                double m0, double m1, double m2, double m3, double m4,
                double m5, double m6, double m7, double m8,
                double amin, double amax, double bmin, double bmax)
            {
                GammaR = gr; GammaG = gg; GammaB = gb;
                Xw = xw; Yw = yw; Zw = zw; HasWhitePoint = hasWp;
                M0 = m0; M1 = m1; M2 = m2; M3 = m3; M4 = m4; M5 = m5; M6 = m6; M7 = m7; M8 = m8;
                AMin = amin; AMax = amax; BMin = bmin; BMax = bmax;
            }

            /// <summary>CalRGB: absent white-point components default to <b>D65</b>.</summary>
            internal static CieParams ForCalRgb(PdfDictionary paramDict, PdfDocument doc)
                => From(paramDict, doc, 0.9505, 1.0000, 1.0890);

            /// <summary>
            /// Lab: absent white-point components default to <b>D50</b>.
            ///
            /// <para>The two spaces genuinely differ here, and collapsing them into one factory
            /// would have changed Lab's answer for a malformed <c>/WhitePoint</c> such as
            /// <c>[null 1 0.8251]</c> - silently, and only on input nobody tests. Two factories
            /// rather than one shared default is the whole reason this is not a single method.</para>
            /// </summary>
            internal static CieParams ForLab(PdfDictionary paramDict, PdfDocument doc)
                => From(paramDict, doc, D50X, D50Y, D50Z);

            /// <summary>
            /// Reads <paramref name="paramDict"/> once. Every default and every fallback is exactly
            /// what the per-call readers used, so a converted call site cannot change a pixel.
            /// </summary>
            private static CieParams From(
                PdfDictionary paramDict, PdfDocument doc,
                double defXw, double defYw, double defZw)
            {
                double gr = 1.0, gg = 1.0, gb = 1.0;
                double xw = defXw, yw = defYw, zw = defZw;
                bool hasWp = false;
                double m0 = 1, m1 = 0, m2 = 0, m3 = 0, m4 = 1, m5 = 0, m6 = 0, m7 = 0, m8 = 1;
                double amin = -100, amax = 100, bmin = -100, bmax = 100;

                if (paramDict != null)
                {
                    // Each element may be a reference too (§7.3.10), read through PdfResolve.
                    if (Res(doc, paramDict, PdfName.Gamma) is PdfArray gamma && gamma.Count >= 3)
                    {
                        gr = PdfResolve.ArrayDouble(doc, gamma, 0, 1.0);
                        gg = PdfResolve.ArrayDouble(doc, gamma, 1, 1.0);
                        gb = PdfResolve.ArrayDouble(doc, gamma, 2, 1.0);
                    }
                    if (Res(doc, paramDict, PdfName.WhitePoint) is PdfArray wp && wp.Count >= 3)
                    {
                        xw = PdfResolve.ArrayDouble(doc, wp, 0, defXw);
                        yw = PdfResolve.ArrayDouble(doc, wp, 1, defYw);
                        zw = PdfResolve.ArrayDouble(doc, wp, 2, defZw);
                        hasWp = true;
                    }
                    if (Res(doc, paramDict, PdfName.Matrix) is PdfArray mat && mat.Count >= 9)
                    {
                        m0 = PdfResolve.ArrayDouble(doc, mat, 0, 1);
                        m1 = PdfResolve.ArrayDouble(doc, mat, 1, 0);
                        m2 = PdfResolve.ArrayDouble(doc, mat, 2, 0);
                        m3 = PdfResolve.ArrayDouble(doc, mat, 3, 0);
                        m4 = PdfResolve.ArrayDouble(doc, mat, 4, 1);
                        m5 = PdfResolve.ArrayDouble(doc, mat, 5, 0);
                        m6 = PdfResolve.ArrayDouble(doc, mat, 6, 0);
                        m7 = PdfResolve.ArrayDouble(doc, mat, 7, 0);
                        m8 = PdfResolve.ArrayDouble(doc, mat, 8, 1);
                    }
                    if (Res(doc, paramDict, PdfName.Range) is PdfArray range && range.Count >= 4)
                    {
                        amin = PdfResolve.ArrayDouble(doc, range, 0, amin); amax = PdfResolve.ArrayDouble(doc, range, 1, amax);
                        bmin = PdfResolve.ArrayDouble(doc, range, 2, bmin); bmax = PdfResolve.ArrayDouble(doc, range, 3, bmax);
                    }
                }

                return new CieParams(gr, gg, gb, xw, yw, zw, hasWp,
                                     m0, m1, m2, m3, m4, m5, m6, m7, m8,
                                     amin, amax, bmin, bmax);
            }
        }

        /// <summary>
        /// <see cref="CalRgbToSRgb(float[],PdfDictionary,PdfDocument)"/> over parameters that were
        /// read once. Identical arithmetic; the only difference is where the nine constants came
        /// from.
        /// </summary>
        internal static SKColor CalRgbToSRgb(float[] comps, in CieParams p)
        {
            double r = comps.Length > 0 ? comps[0] : 0.0;
            double g = comps.Length > 1 ? comps[1] : 0.0;
            double b = comps.Length > 2 ? comps[2] : 0.0;

            double A = Math.Pow(Math.Max(0, r), p.GammaR);
            double B = Math.Pow(Math.Max(0, g), p.GammaG);
            double C = Math.Pow(Math.Max(0, b), p.GammaB);

            double X = p.M0 * A + p.M3 * B + p.M6 * C;
            double Y = p.M1 * A + p.M4 * B + p.M7 * C;
            double Z = p.M2 * A + p.M5 * B + p.M8 * C;

            AdaptToD65(ref X, ref Y, ref Z, p.Xw, p.Yw, p.Zw);
            return XyzD65ToSRgb(X, Y, Z);
        }

        /// <summary>
        /// <see cref="LabToSRgb(float,float,float,PdfDictionary,PdfDocument)"/> over parameters that
        /// were read once. The <c>HasWhitePoint</c> flag preserves the per-call version's behaviour
        /// exactly: <c>/WhitePoint</c> is a required entry, so its absence means a malformed space
        /// and the D50 path is used unchanged.
        /// </summary>
        internal static SKColor LabToSRgb(float L, float a, float b, in CieParams p)
        {
            double Ld = L < 0 ? 0 : L > 100 ? 100 : L;
            double ad = a < p.AMin ? p.AMin : a > p.AMax ? p.AMax : a;
            double bd = b < p.BMin ? p.BMin : b > p.BMax ? p.BMax : b;

            if (!p.HasWhitePoint || !(p.Xw > 0) || !(p.Yw > 0) || !(p.Zw > 0))
                return LabToSRgb((float)Ld, (float)ad, (float)bd);

            LabToXyz(Ld, ad, bd, p.Xw, p.Yw, p.Zw, out double X, out double Y, out double Z);
            AdaptToD65(ref X, ref Y, ref Z, p.Xw, p.Yw, p.Zw);
            return XyzD65ToSRgb(X, Y, Z);
        }

        /// <summary>
        /// CalGray (ISO 32000-2 §8.6.5.2) → sRGB: <c>A</c> raised to the space's <c>/Gamma</c>
        /// (default 1) is the CIE luminance <c>Y</c> — "X = L = X_W × A^G, Y = M = Y_W × A^G,
        /// Z = N = Z_W × A^G" — and a luminance along the white point's axis is a neutral grey,
        /// encoded through the sRGB transfer function.
        ///
        /// <para><b>Not the encoded grey level itself</b>: CalGray with the default gamma is not
        /// "equivalent to DeviceGray" — the clause defines XYZ — and <c>[/CalGray
        /// &lt;&lt;/Gamma 1&gt;&gt;]</c> renders at 0.5 as sRGB 187/188, not 128. A CalGray value is
        /// as light as its luminance says.</para>
        /// </summary>
        public static SKColor CalGrayToSRgb(float a, PdfDictionary paramDict, PdfDocument doc = null)
        {
            double gamma = Res(doc, paramDict, PdfName.Gamma) is PdfNumber gn ? gn.DoubleValue : 1.0;
            double y = Math.Pow(Math.Max(0.0, Math.Min(1.0, (double)a)), gamma);
            return XyzD65ToSRgb(D65X * y, D65Y * y, D65Z * y);
        }

        // D50 — the white point CIE L*a*b* is conventionally referenced to, and the one the
        // three-argument LabToSRgb below is fused to.
        internal const double D50X = 0.9642, D50Y = 1.0000, D50Z = 0.8251;

        // D65 — the white point of sRGB, the destination of every conversion here.
        private const double D65X = 0.95047, D65Y = 1.00000, D65Z = 1.08883;

        /// <summary>CIE L*a*b* → XYZ against D50, the inverse of <see cref="XyzD50ToLab"/>.</summary>
        internal static void LabToXyzD50(double L, double a, double b, out double X, out double Y, out double Z)
            => LabToXyz(L, a, b, D50X, D50Y, D50Z, out X, out Y, out Z);

        /// <summary>CIE L*a*b* → CIE XYZ relative to an arbitrary white point.</summary>
        private static void LabToXyz(double L, double a, double b,
            double xw, double yw, double zw, out double X, out double Y, out double Z)
        {
            double fy = (L + 16.0) / 116.0;
            double fx = a / 500.0 + fy;
            double fz = fy - b / 200.0;
            const double delta = 6.0 / 29.0;
            X = xw * (fx > delta ? fx * fx * fx : (fx - 16.0 / 116.0) * 3 * delta * delta);
            Y = yw * (fy > delta ? fy * fy * fy : (fy - 16.0 / 116.0) * 3 * delta * delta);
            Z = zw * (fz > delta ? fz * fz * fz : (fz - 16.0 / 116.0) * 3 * delta * delta);
        }

        /// <summary>
        /// Bradford chromatic adaptation from an arbitrary white point to D65, in place.
        /// Shared by <see cref="CalRgbToSRgb(float[],PdfDictionary,PdfDocument)"/> and the white-point-aware Lab path — the two
        /// would otherwise carry the same twelve coefficients twice.
        /// </summary>
        /// <remarks>
        /// M_A (XYZ → LMS), its inverse, and the diagonal scale between the two whites make the
        /// full transform:
        /// <code>
        /// M_A:     [ 0.8951  0.2664 -0.1614]   M_A^-1: [ 0.9869929 -0.1470543  0.1599627]
        ///          [-0.7502  1.7135  0.0367]           [ 0.4323053  0.5183603  0.0492912]
        ///          [ 0.0389 -0.0685  1.0296]           [-0.0085287  0.0400428  0.9684867]
        /// </code>
        /// </remarks>
        internal static void AdaptToD65(ref double X, ref double Y, ref double Z,
            double xw, double yw, double zw)
        {
            // Source white → LMS
            double sLs =  0.8951 * xw + 0.2664 * yw - 0.1614 * zw;
            double sMs = -0.7502 * xw + 1.7135 * yw + 0.0367 * zw;
            double sSs =  0.0389 * xw - 0.0685 * yw + 1.0296 * zw;
            // D65 → LMS
            double dLs =  0.8951 * D65X + 0.2664 * D65Y - 0.1614 * D65Z;
            double dMs = -0.7502 * D65X + 1.7135 * D65Y + 0.0367 * D65Z;
            double dSs =  0.0389 * D65X - 0.0685 * D65Y + 1.0296 * D65Z;
            // Sample → LMS, scale, back to XYZ
            double lms_L = (sLs > 0 ? dLs / sLs : 1) * ( 0.8951 * X + 0.2664 * Y - 0.1614 * Z);
            double lms_M = (sMs > 0 ? dMs / sMs : 1) * (-0.7502 * X + 1.7135 * Y + 0.0367 * Z);
            double lms_S = (sSs > 0 ? dSs / sSs : 1) * ( 0.0389 * X - 0.0685 * Y + 1.0296 * Z);
            X =  0.9869929 * lms_L - 0.1470543 * lms_M + 0.1599627 * lms_S;
            Y =  0.4323053 * lms_L + 0.5183603 * lms_M + 0.0492912 * lms_S;
            Z = -0.0085287 * lms_L + 0.0400428 * lms_M + 0.9684867 * lms_S;
        }

        /// <summary>
        /// ICC PCS XYZ → sRGB. The PCS is defined against D50 (ICC.1 §7.2.16), so this is exactly
        /// <see cref="AdaptToD65"/> followed by <see cref="XyzD65ToSRgb"/> — composed here rather
        /// than re-derived in <see cref="IccProfile"/>, which is the only caller, so the ICC path
        /// and the CIE-based spaces cannot drift apart on the same twelve Bradford coefficients.
        /// </summary>
        internal static SKColor XyzD50ToSRgb(double X, double Y, double Z)
        {
            AdaptToD65(ref X, ref Y, ref Z, D50X, D50Y, D50Z);
            return XyzD65ToSRgb(X, Y, Z);
        }

        /// <summary>D65 CIE XYZ → sRGB (IEC 61966-2-1 matrix + transfer function).</summary>
        internal static SKColor XyzD65ToSRgb(double X, double Y, double Z)
        {
            double lr =  3.2404542 * X - 1.5371385 * Y - 0.4985314 * Z;
            double lg = -0.9692660 * X + 1.8760108 * Y + 0.0415560 * Z;
            double lb =  0.0556434 * X - 0.2040259 * Y + 1.0572252 * Z;
            return new SKColor(Clamp255(SrgbGamma(lr)), Clamp255(SrgbGamma(lg)), Clamp255(SrgbGamma(lb)));
        }

        /// <summary>
        /// CIE L*a*b* (D50 white point) → sRGB. Reference: IEC 61966-2-1.
        ///
        /// <para>Kept as a single fused D50 → sRGB matrix rather than routed through
        /// <see cref="AdaptToD65"/>: this is the path every caller that has no colour-space
        /// parameter dictionary takes, and the fused matrix is the value its tests pin.</para>
        /// </summary>
        public static SKColor LabToSRgb(float L, float a, float b)
        {
            // Lab → XYZ against the D50 white point Lab is conventionally referenced to.
            LabToXyz(L, a, b, D50X, D50Y, D50Z, out double X, out double Y, out double Z);

            // XYZ (D50) → linear sRGB (Bradford-adapted D50→D65 + sRGB matrix combined)
            double r  = XyzD50ToLinearSRgb[0] * X + XyzD50ToLinearSRgb[1] * Y + XyzD50ToLinearSRgb[2] * Z;
            double g  = XyzD50ToLinearSRgb[3] * X + XyzD50ToLinearSRgb[4] * Y + XyzD50ToLinearSRgb[5] * Z;
            double bv = XyzD50ToLinearSRgb[6] * X + XyzD50ToLinearSRgb[7] * Y + XyzD50ToLinearSRgb[8] * Z;

            // Gamma-encode to sRGB
            return new SKColor(
                Clamp255(SrgbGamma(r)),
                Clamp255(SrgbGamma(g)),
                Clamp255(SrgbGamma(bv)));
        }

        /// <summary>
        /// The fused D50 XYZ → linear sRGB matrix, row-major, that <see cref="LabToSRgb(float,float,float)"/>
        /// applies (Bradford D50→D65 combined with the IEC 61966-2-1 primaries).
        /// </summary>
        /// <remarks>
        /// Named rather than written inline so <see cref="LinearSRgbToXyzD50"/> can be its
        /// arithmetic inverse. Two hand-written matrices would be two things to keep in step, and
        /// the failure would be a colour that survives a round trip through neither.
        /// </remarks>
        private static readonly double[] XyzD50ToLinearSRgb =
        {
             3.1338561, -1.6168667, -0.4906146,
            -0.9787684,  1.9161415,  0.0334540,
             0.0719453, -0.2289914,  1.4052427,
        };

        /// <summary>
        /// The inverse of <see cref="XyzD50ToLinearSRgb"/>, <b>computed</b> from it rather than
        /// transcribed.
        /// </summary>
        /// <remarks>
        /// <para><b>Inverted in code on purpose.</b> The obvious alternative is to write the nine
        /// coefficients of the reverse matrix down, which means recalling or copying published
        /// numbers — the transcription hazard this repository has been bitten by, and one that
        /// cannot be caught by any round-trip test, because a consistently wrong pair still round
        /// trips. Deriving it guarantees the two are exact inverses of each other whatever the
        /// forward numbers are, and leaves only the forward matrix to be right.</para>
        ///
        /// <para>A 3×3 inverse by cofactors: nine multiplies and a determinant, computed once at
        /// type initialisation.</para>
        /// </remarks>
        private static readonly double[] LinearSRgbToXyzD50 = Invert3x3(XyzD50ToLinearSRgb);

        /// <summary>Linear sRGB → D50 XYZ, row-major — a copy of the matrix <see cref="SRgbToXyzD50"/> applies.</summary>
        internal static double[] LinearSRgbToXyzD50Matrix() => (double[])LinearSRgbToXyzD50.Clone();

        /// <summary>sRGB's transfer function inverted, for one 8-bit channel: an encoded level → linear.</summary>
        internal static double SRgbLevelToLinear(byte level) => SrgbLinear(level / 255.0);

        /// <summary>The inverse of a row-major 3×3 matrix, by cofactor expansion.</summary>
        /// <remarks>
        /// Throws on a singular matrix rather than returning something usable: the type's own input is a
        /// colour-space matrix defined at compile time, so a zero determinant means the constant
        /// above was edited into nonsense, and failing at type initialisation says that far more
        /// clearly than every colour coming out black. A caller inverting a matrix a file supplied
        /// checks <see cref="Determinant3x3"/> first.
        /// </remarks>
        internal static double[] Invert3x3(double[] m)
        {
            double a = m[0], b = m[1], c = m[2];
            double d = m[3], e = m[4], f = m[5];
            double g = m[6], h = m[7], i = m[8];

            double det = Determinant3x3(m);

            if (Math.Abs(det) < 1e-12)
                throw new InvalidOperationException("PdfColor's XYZ→sRGB matrix is singular.");

            return new[]
            {
                ((e * i) - (f * h)) / det, ((c * h) - (b * i)) / det, ((b * f) - (c * e)) / det,
                ((f * g) - (d * i)) / det, ((a * i) - (c * g)) / det, ((c * d) - (a * f)) / det,
                ((d * h) - (e * g)) / det, ((b * g) - (a * h)) / det, ((a * e) - (b * d)) / det,
            };
        }

        /// <summary>A row-major 3×3 matrix's determinant.</summary>
        internal static double Determinant3x3(double[] m)
            => (m[0] * ((m[4] * m[8]) - (m[5] * m[7])))
             - (m[1] * ((m[3] * m[8]) - (m[5] * m[6])))
             + (m[2] * ((m[3] * m[7]) - (m[4] * m[6])));

        /// <summary>sRGB's transfer function inverted: an encoded channel [0,1] → linear.</summary>
        /// <remarks>
        /// The exact inverse of <see cref="SrgbGamma"/>, including its breakpoint: the linear
        /// segment's threshold is 0.0031308 going one way and 12.92 × that going the other, which
        /// is where a hand-written 0.04045 comes from.
        /// </remarks>
        private static double SrgbLinear(double c) => Core.Imaging.SrgbTransfer.Decode(c);

        /// <summary>
        /// sRGB → the ICC PCS's D50 CIE XYZ — the inverse of the fused matrix
        /// <see cref="LabToSRgb(float,float,float)"/> applies.
        /// </summary>
        /// <remarks>
        /// <b>What a <c>B2A</c> transform needs.</b> Every other conversion here runs device → PCS →
        /// sRGB; converting *into* a destination profile runs
        /// the other way, and its input is the PCS.
        /// </remarks>
        internal static void SRgbToXyzD50(SKColor color, out double X, out double Y, out double Z)
        {
            double r = SrgbLinear(color.Red / 255.0);
            double g = SrgbLinear(color.Green / 255.0);
            double b = SrgbLinear(color.Blue / 255.0);

            X = LinearSRgbToXyzD50[0] * r + LinearSRgbToXyzD50[1] * g + LinearSRgbToXyzD50[2] * b;
            Y = LinearSRgbToXyzD50[3] * r + LinearSRgbToXyzD50[4] * g + LinearSRgbToXyzD50[5] * b;
            Z = LinearSRgbToXyzD50[6] * r + LinearSRgbToXyzD50[7] * g + LinearSRgbToXyzD50[8] * b;
        }

        /// <summary>sRGB → CIE L*a*b* against the D50 white point, the ICC PCS's reference.</summary>
        /// <remarks>
        /// The inverse of <see cref="LabToSRgb(float,float,float)"/>, and deliberately routed
        /// through the same white point constants so the pair cannot disagree about which D50 they
        /// mean.
        /// </remarks>
        internal static void SRgbToLab(SKColor color, out double L, out double a, out double b)
        {
            SRgbToXyzD50(color, out double X, out double Y, out double Z);
            XyzD50ToLab(X, Y, Z, out L, out a, out b);
        }

        /// <summary>CIE XYZ (D50) → L*a*b* against the same D50 the rest of this type uses.</summary>
        internal static void XyzD50ToLab(double X, double Y, double Z, out double L, out double a, out double b)
        {
            double fx = LabF(X / D50X);
            double fy = LabF(Y / D50Y);
            double fz = LabF(Z / D50Z);

            L = (116.0 * fy) - 16.0;
            a = 500.0 * (fx - fy);
            b = 200.0 * (fy - fz);
        }

        /// <summary>CIE 1976's cube-root companding, with its linear segment near black.</summary>
        private static double LabF(double t)
        {
            const double Epsilon = 216.0 / 24389.0;   // (6/29)^3
            const double Kappa = 24389.0 / 27.0;      // (29/3)^3
            return t > Epsilon ? Math.Pow(t, 1.0 / 3.0) : ((Kappa * t) + 16.0) / 116.0;
        }

        /// <summary>
        /// CalRGB color space → sRGB (PDF §10.6.4.3).
        /// Applies per-component /Gamma, then the 3×3 /Matrix to yield XYZ, then adapts to
        /// D65 via diagonal XYZ scaling, and finally
        /// applies the standard D65 XYZ → sRGB matrix + sRGB transfer function.
        /// </summary>
        public static SKColor CalRgbToSRgb(float[] comps, PdfDictionary paramDict, PdfDocument doc = null)
            => CalRgbToSRgb(comps, CieParams.ForCalRgb(paramDict, doc));

        /// <summary>
        /// An image's <c>/Decode</c> array as floats (§8.9.5.2), or <c>null</c> when it is absent,
        /// too short, or carries an entry that is not a number.
        /// </summary>
        /// <remarks>
        /// <para><b>Every element is resolved.</b> §7.3.10 lets any of them be an indirect
        /// reference, and a plain <c>as PdfNumber</c> cast answers <c>null</c> for <c>6 0 R</c> —
        /// which both renderers then fell through to a per-copy default.</para>
        ///
        /// <para><b>A malformed entry makes the whole array absent, which is neither copy's old
        /// answer and is better than both.</b> The raster reader substituted <c>0.0</c>, so an
        /// unreadable upper bound collapsed every sample onto the lower one; the SVG reader
        /// substituted <c>i % 2 == 0 ? 0 : 1</c>, the identity for a 0..1 space but wrong for an
        /// <c>/Indexed</c> image, whose default upper bound is 2^bpc − 1. Returning <c>null</c>
        /// hands the decision back to the caller, which already knows the right identity for its
        /// own colour space — that is exactly what each does when the array is genuinely absent.</para>
        /// </remarks>
        internal static float[] DecodeArray(PdfDocument doc, PdfArray arr)
        {
            if (arr == null || arr.Count < 2) return null;

            var result = new float[arr.Count];
            for (int i = 0; i < arr.Count; i++)
            {
                var n = PdfResolve.Number(doc, arr[i]);
                if (n == null) return null;
                result[i] = (float)n.DoubleValue;
            }
            return result;
        }

        /// <summary>
        /// Maps an already-normalised sample through component <paramref name="comp"/> of a
        /// <c>/Decode</c> array (§8.9.5.2), clamped to [0,1]. The identity — the sample itself —
        /// when the array is absent or carries no pair for this component.
        /// </summary>
        /// <remarks>
        /// <para><b>The mapper beside the already-shared reader.</b> <see cref="DecodeArray"/> was
        /// consolidated in an earlier round and this was left copied, with the SVG side's own
        /// summary asserting that it matched <c>ContentStreamInterpreter.ApplyDecodeF01</c> — the
        /// prose-agreement claim this project treats as a defect marker rather than documentation.
        /// The two really did agree, apart from the guard below; nothing enforced it.</para>
        ///
        /// <para><b>The <c>comp &lt; 0</c> guard is the SVG side's, kept as the safer of the two.</b>
        /// Without it a negative component index makes <c>idx + 1 &gt;= decode.Length</c> false and
        /// then indexes the array negatively. No caller passes one — every call site uses a literal
        /// or a loop counter — so this is defensive.</para>
        ///
        /// <para>Taking the sample already normalised rather than as a byte is what lets the 16-bpc
        /// paths share the mapping with the 8-bpc ones; the byte form is one division at the call
        /// site.</para>
        /// </remarks>
        internal static float ApplyDecode(float normalised, float[] decode, int comp)
        {
            if (decode == null || comp < 0) return Clamp01(normalised);
            int idx = comp * 2;
            if (idx + 1 >= decode.Length) return Clamp01(normalised);
            return Clamp01(decode[idx] + normalised * (decode[idx + 1] - decode[idx]));
        }
    }
}
