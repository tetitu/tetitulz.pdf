// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Util;

namespace Tetitulz.Pdf.Plugins.PostScript.Graphics
{
    /// <summary>The device colour spaces this interpreter supports.</summary>
    internal enum PsColorSpace : byte { Gray, Rgb, Cmyk }

    /// <summary>
    /// A colour in one of the three device spaces.
    ///
    /// <para><b>The space is carried, not normalised to RGB.</b> A CMYK fill has to emit <c>k</c>
    /// rather than a converted <c>rg</c>: prepress workflows care about the difference, and the
    /// round trip through sRGB is lossy in a way that is visible in rich blacks.</para>
    /// </summary>
    internal readonly struct PsColor : IEquatable<PsColor>
    {
        public readonly PsColorSpace Space;
        public readonly double C0, C1, C2, C3;

        /// <summary>
        /// The <c>/Pattern</c> resource this colour paints with (<c>setpattern</c>), or null for a
        /// plain colour. The device components stay what an uncoloured pattern is painted in — black
        /// for a coloured one — and what <c>currentgray</c> and its kin read back.
        /// </summary>
        public readonly string PatternName;

        /// <summary>
        /// For an uncoloured pattern, the <c>/ColorSpace</c> resource naming <c>[/Pattern base]</c>
        /// over <see cref="Space"/>; null for a coloured one, which <c>/Pattern</c> itself names.
        /// </summary>
        public readonly string PatternSpaceName;

        /// <summary>
        /// The colour space this colour is painted in when it is not a device one — an
        /// <c>/ICCBased</c>, <c>/CalGray</c>, <c>/CalRGB</c> or <c>/Lab</c> space as PDF writes it — or
        /// null. The device components stay what <c>currentgray</c> and its kin read back, and what is
        /// painted where the space cannot be named.
        /// </summary>
        public readonly PdfObject NamedSpace;

        /// <summary>The colour's components in <see cref="NamedSpace"/>, held to its ranges; null for a device colour.</summary>
        public readonly double[] NamedComponents;

        private PsColor(PsColorSpace space, double c0, double c1, double c2, double c3,
                        string patternName = null, string patternSpaceName = null,
                        PdfObject namedSpace = null, double[] namedComponents = null)
        {
            Space = space; C0 = c0; C1 = c1; C2 = c2; C3 = c3;
            PatternName = patternName; PatternSpaceName = patternSpaceName;
            NamedSpace = namedSpace; NamedComponents = namedComponents;
        }

        /// <summary>This colour painting through a pattern resource; see <see cref="PatternName"/>.</summary>
        public PsColor WithPattern(string patternName, string patternSpaceName) =>
            new(Space, C0, C1, C2, C3, patternName, patternSpaceName, NamedSpace, NamedComponents);

        /// <summary>This colour without its pattern: its device components, and its named space if it has one.</summary>
        public PsColor WithoutPattern => new(Space, C0, C1, C2, C3, namedSpace: NamedSpace, namedComponents: NamedComponents);

        /// <summary>
        /// This device colour standing for <paramref name="components"/> in <paramref name="space"/>,
        /// which is what is painted; see <see cref="NamedSpace"/>.
        /// </summary>
        public PsColor InSpace(PdfObject space, double[] components) =>
            new(Space, C0, C1, C2, C3, PatternName, PatternSpaceName, space, components);

        /// <summary>Black, the PostScript initial colour.</summary>
        public static readonly PsColor Black = new(PsColorSpace.Gray, 0, 0, 0, 0);

        public static PsColor Gray(double g) => new(PsColorSpace.Gray, Clamp(g), 0, 0, 0);

        public static PsColor Rgb(double r, double g, double b) =>
            new(PsColorSpace.Rgb, Clamp(r), Clamp(g), Clamp(b), 0);

        public static PsColor Cmyk(double c, double m, double y, double k) =>
            new(PsColorSpace.Cmyk, Clamp(c), Clamp(m), Clamp(y), Clamp(k));

        /// <summary>
        /// <c>sethsbcolor</c>. Converted here rather than through
        /// <c>Base.Rendering.PdfColor</c>, which is across the ring boundary and unreachable from a
        /// plugin — the one small piece of colour maths this assembly has to own.
        /// </summary>
        public static PsColor Hsb(double h, double s, double b)
        {
            h = Clamp(h); s = Clamp(s); b = Clamp(b);
            if (s <= 0) return Rgb(b, b, b);

            double sector = (h - Math.Floor(h)) * 6.0;
            int i = SaturatingCast.ToInt(Math.Floor(sector));
            double f = sector - i;

            double p = b * (1 - s);
            double q = b * (1 - s * f);
            double t = b * (1 - s * (1 - f));

            return i switch
            {
                0 => Rgb(b, t, p),
                1 => Rgb(q, b, p),
                2 => Rgb(p, b, t),
                3 => Rgb(p, q, b),
                4 => Rgb(t, p, b),
                _ => Rgb(b, p, q)
            };
        }

        /// <summary>
        /// The equivalent grey, for <c>currentgray</c> against a non-grey colour.
        ///
        /// <para><b>PLRM §4.8.3, not PDF §8.6.4.4, and the difference is the whole point of this
        /// method.</b> Nothing here describes how a fill is rendered — a CMYK fill is emitted as
        /// <c>k</c> and the reader does its own conversion. These two methods exist only for what
        /// the <i>PostScript program</i> reads back through <c>currentgray</c> /
        /// <c>currentrgbcolor</c> / <c>currenthsbcolor</c>, and there a conforming interpreter uses
        /// the <b>additive</b> rule. Under the PDF rule <c>1 0 0 0.5 setcmykcolor currentgray</c>
        /// answers 0.3505 where PLRM says 0.2 — and an EPS prologue deriving a tint from the
        /// current colour then paints a visibly different colour that nothing downstream can
        /// detect, because the emitted stream is valid and the wrong colour is a real colour.</para>
        /// </summary>
        /// <remarks>The CMYK weights are PLRM's own 0.3/0.59/0.11 — <b>not</b> BT.601's
        /// 0.299/0.587/0.114, which is why <see cref="Luma"/> is not reused for that arm. The RGB
        /// arm keeps BT.601 from Core.Constants (the double set; <c>(double)0.299f != 0.299</c>,
        /// see GAPS.md §2b).</remarks>
        public double ToGray() => Space switch
        {
            PsColorSpace.Gray => C0,
            PsColorSpace.Rgb  => Luma(C0, C1, C2),
            _                 => 1 - Math.Min(1, PlrmGrayRed   * C0
                                                + PlrmGrayGreen * C1
                                                + PlrmGrayBlue  * C2
                                                + C3)
        };

        // PLRM §4.8.3's CMYK-to-gray weights, stated to two decimals by the spec itself. Deliberately
        // not PdfColorSpaceNames.Luma*, which are BT.601's three-decimal values: substituting them
        // changes every currentgray readback of a CMYK colour.
        private const double PlrmGrayRed   = 0.3;
        private const double PlrmGrayGreen = 0.59;
        private const double PlrmGrayBlue  = 0.11;

        private static double Luma(double r, double g, double b)
            => PdfColorSpaceNames.LumaRed   * r
             + PdfColorSpaceNames.LumaGreen * g
             + PdfColorSpaceNames.LumaBlue  * b;

        /// <summary>
        /// The equivalent RGB, for <c>currentrgbcolor</c> / <c>currenthsbcolor</c>.
        ///
        /// <para>PLRM §4.8.3's additive rule — <c>red = 1 − min(1, cyan + black)</c> — not PDF's
        /// multiplicative <c>(1−c)(1−k)</c>. See <see cref="ToGray"/>: <c>1 0 0 0.5 setcmykcolor
        /// currentrgbcolor</c> must push <c>(0, 0.5, 0.5)</c>, where the PDF rule gives
        /// <c>(0.25, 0.5, 0.5)</c>.</para>
        /// </summary>
        public (double R, double G, double B) ToRgb() => Space switch
        {
            PsColorSpace.Gray => (C0, C0, C0),
            PsColorSpace.Rgb  => (C0, C1, C2),
            _                 => (1 - Math.Min(1, C0 + C3),
                                  1 - Math.Min(1, C1 + C3),
                                  1 - Math.Min(1, C2 + C3))
        };

        private static double Clamp(double v) => v < 0 ? 0 : v > 1 ? 1 : v;

        public bool Equals(PsColor o) =>
            Space == o.Space && C0 == o.C0 && C1 == o.C1 && C2 == o.C2 && C3 == o.C3
            && string.Equals(PatternName, o.PatternName, StringComparison.Ordinal)
            && string.Equals(PatternSpaceName, o.PatternSpaceName, StringComparison.Ordinal)
            && ReferenceEquals(NamedSpace, o.NamedSpace) && SameComponents(NamedComponents, o.NamedComponents);

        private static bool SameComponents(double[] a, double[] b)
        {
            if (ReferenceEquals(a, b)) return true;
            if (a == null || b == null || a.Length != b.Length) return false;
            for (int k = 0; k < a.Length; k++)
                if (a[k] != b[k]) return false;
            return true;
        }

        public override bool Equals(object obj) => obj is PsColor c && Equals(c);
        // PsHash rather than HashCode.Combine: the latter is .NET Core 2.1+ and this assembly also
        // builds for netstandard2.0. The pattern's name is enough beside the five numbers: two
        // colours with one name and different spaces are rare and still compare unequal.
        public override int GetHashCode() =>
            PsHash.Combine((int)Space, C0.GetHashCode(), C1.GetHashCode(),
                           C2.GetHashCode(), C3.GetHashCode(),
                           PatternName == null ? 0 : StringComparer.Ordinal.GetHashCode(PatternName));
    }

    /// <summary>
    /// The PostScript graphics state (PLRM §4.2), or the part of it this interpreter models.
    ///
    /// <para>A class rather than a struct because <c>gsave</c>/<c>grestore</c> push and pop whole
    /// copies and <see cref="Clone"/> makes that explicit at the call site.</para>
    /// </summary>
    internal sealed class PsGraphicsState
    {
        public PsMatrix Ctm = PsMatrix.Identity;
        public PsColor Color = PsColor.Black;

        /// <summary>
        /// How many numeric operands <c>setcolor</c> consumes in the current colour space, or -1
        /// when <c>setcolorspace</c> named one this interpreter cannot size.
        ///
        /// <para><b>Part of the graphics state, because PLRM §4.8 makes the current colour space
        /// part of it.</b> It lived on the interpreter, where <c>gsave</c>/<c>grestore</c> could not
        /// reach it: <c>gsave /DeviceCMYK setcolorspace … grestore 0.2 setcolor</c> left the count
        /// at 4 after the restore had put the space back to DeviceGray, so <c>setcolor</c> popped
        /// four operands — <c>0.2</c> plus three unrelated values, or <c>stackunderflow</c>. That is
        /// the exact desynchronisation <c>PsSystemDict.SetColor</c>'s own doc says the arity table
        /// exists to prevent, reached the other way round.</para>
        ///
        /// <para>Starts at 1: PostScript's initial colour space is DeviceGray.</para>
        /// </summary>
        public int ColorComponents = 1;

        /// <summary>
        /// The space <c>setcolorspace</c> established, for a Level 2 <c>image</c> to carry, or null
        /// when the program has not called <c>setcolorspace</c>.
        ///
        /// <para><b>Null means "no <c>setcolorspace</c> has run", not "DeviceGray".</b> The two must
        /// stay distinguishable: PLRM makes <c>image</c> unconditionally DeviceGray in Level 1 and
        /// current-colour-space-driven in Level 2, and this interpreter has no way to know which
        /// language a file is written in. Treating null as DeviceGray at the point of use is
        /// correct; treating a <i>failed</i> resolution as DeviceGray is the silent-corruption case
        /// <see cref="PsImageColorSpace.CarryFailure"/> exists to prevent, so never write a
        /// <c>?? DeviceGray</c> that cannot tell the two nulls apart.</para>
        ///
        /// <para><b>Set only by <c>setcolorspace</c> — never by <c>setgray</c> /
        /// <c>setrgbcolor</c> / <c>setcmykcolor</c>.</b> Those do establish a colour space in
        /// Level 2, and <c>PsGraphicsOps.SetColor</c> already moves
        /// <see cref="ColorComponents"/> to 3 or 4 for them — so driving an image's sample arity
        /// from that would make <c>1 0 0 setrgbcolor … &lt;4 bytes&gt; image</c> demand twelve bytes,
        /// pad eight zeros and emit DeviceRGB, breaking every Level 1 greyscale image that sets a
        /// colour first. <c>setcolorspace</c> is a Level 2-only operator, so a program using it is
        /// the only evidence available that Level 2 image semantics were intended.</para>
        /// </summary>
        public PsImageColorSpace ImageSpace;

        public double LineWidth = 1.0;
        public int LineCap;          // 0 butt, 1 round, 2 square — same encoding as PDF
        public int LineJoin;         // 0 miter, 1 round, 2 bevel
        public double MiterLimit = 10.0;

        public double[] DashArray = Array.Empty<double>();
        public double DashPhase;

        /// <summary>
        /// The clipping region — the clips this state has taken, the last first — or null for the
        /// page's own. See <see cref="PsClip"/> for why the history is kept.
        /// </summary>
        public PsClip Clip;

        /// <summary>The clipping path stack <c>clipsave</c> pushes onto (PLRM §8.2), or null when it is empty.</summary>
        public PsClipSave ClipStack;

        /// <summary>
        /// The transfer function as PDF's <c>/TR</c> writes it — a sampled function, or an array of four
        /// — or null for the identity (PLRM §7.3, part of the graphics state).
        /// </summary>
        public PdfObject Transfer;

        /// <summary>The procedures <c>settransfer</c> or <c>setcolortransfer</c> set — red, green, blue, grey — for <c>currenttransfer</c>; null for none.</summary>
        public Interpreter.PsObject[] TransferProcedures;

        /// <summary>Overprint, black generation, undercolour removal, flatness, smoothness and stroke adjustment, as the <c>gs</c> the emitter writes carries them.</summary>
        public PsDeviceParameters Device = PsDeviceParameters.Initial;

        /// <summary>
        /// The procedures <c>setblackgeneration</c> and <c>setundercolorremoval</c> set, for their
        /// <c>current*</c> forms — black generation first — or null for neither set.
        /// </summary>
        public Interpreter.PsObject[] BlackGenerationProcedures;

        /// <summary>
        /// The colour space as <c>currentcolorspace</c> answers it — the array <c>setcolorspace</c>
        /// was given, or a one-element array around the family name it was given — or null for the
        /// device space of <see cref="Color"/>, which <c>setgray</c> and its kin select.
        /// </summary>
        public Interpreter.PsObject[] ColorSpaceOperand;

        /// <summary>
        /// The current colour as <c>currentcolor</c> answers it — the components <c>setcolor</c> or a
        /// space's initial colour gave, an uncoloured pattern's components and then the pattern — or
        /// null for the components of <see cref="Color"/> in its device space.
        /// </summary>
        public Interpreter.PsObject[] ColorOperands;

        /// <summary>
        /// The emitted <c>q</c> depth this state's level was opened at: every <c>q</c> above it is the
        /// level's — its own, a clip's bracket, one a widening re-opened — so <c>grestore</c> closes
        /// down to it. 0 for the bottom level.
        /// </summary>
        public int EmittedFloor;

        /// <summary>
        /// The current clipping path as four corner points in <b>device space</b>
        /// (<c>x0 y0 x1 y1 x2 y2 x3 y3</c>), or null when nothing has clipped yet — which is what
        /// <c>clippath</c> reads.
        ///
        /// <para><b>Device space, because the CTM moves and the clip does not.</b> A clip taken
        /// under one transform and read back under another has to come back through the inverse of
        /// the <i>current</i> CTM; storing user-space coordinates would return the clip in whichever
        /// space happened to be in force when it was set.</para>
        ///
        /// <para><b>Four corners rather than the whole path</b>, with <see cref="ClipIsExact"/>
        /// saying when that is lossless. A single rectangular clip — overwhelmingly the common case,
        /// and the one <c>clippath pathbbox</c> is written for — is stored exactly, rotation
        /// included, because a quadrilateral survives any affine map. Anything else (a curve, more
        /// than four points, or a second clip intersecting the first) degrades to the device-space
        /// bounding box, which is an <i>over</i>-estimate: it can only ever report the clip as
        /// larger than it is, never smaller, so a program sizing content to it stays inside.</para>
        /// </summary>
        public double[] ClipQuadDevice => Clip?.Quad;

        /// <summary>
        /// True when <see cref="ClipQuadDevice"/> is the clip itself rather than a box around it.
        /// <c>clippath</c> reports the approximation when this is false.
        /// </summary>
        public bool ClipIsExact => Clip?.Exact == true;

        /// <summary>
        /// Font resource name (S5); null until a font is set. The rest of the font — its matrix,
        /// encoding, program — is <see cref="Font"/>.
        /// </summary>
        public string FontResourceName;

        /// <summary>
        /// The current font (PLRM §4.2: part of the graphics state), which the interpreter's
        /// <c>CurrentFont*</c> properties read and write — so <c>grestore</c> puts back the font a
        /// <c>gsave</c> kept. A font surviving <c>grestore</c> would draw every line of an
        /// interpreter's alphabet.ps, which sets a 50-point font inside <c>gsave … grestore</c> to
        /// measure a letter, at 50 points.
        /// </summary>
        public PsFontState Font = PsFontState.Initial;

        /// <summary>
        /// The current path when this state was saved by <c>gsave</c> or <c>save</c> — PLRM §4.2
        /// makes the path part of the graphics state, and the engine keeps the one live path outside
        /// the stack — put back when this state becomes current again. Null for a state never saved.
        /// In the user space of this state's CTM, which is the one in force when it is put back.
        /// </summary>
        public PsPath.Snapshotted? SavedPath;

        public PsGraphicsState Clone() => new()
        {
            Ctm              = Ctm,
            Color            = Color,
            ColorComponents  = ColorComponents,
            ImageSpace       = ImageSpace,
            LineWidth        = LineWidth,
            LineCap          = LineCap,
            LineJoin         = LineJoin,
            MiterLimit       = MiterLimit,
            DashArray        = DashArray.Length == 0 ? Array.Empty<double>() : (double[])DashArray.Clone(),
            DashPhase        = DashPhase,
            Clip             = Clip,
            ClipStack        = ClipStack,
            Transfer         = Transfer,
            TransferProcedures = TransferProcedures,
            Device           = Device,
            BlackGenerationProcedures = BlackGenerationProcedures,
            ColorSpaceOperand = ColorSpaceOperand,
            ColorOperands    = ColorOperands,
            EmittedFloor     = EmittedFloor,
            FontResourceName = FontResourceName,
            Font             = Font
        };
    }
}
