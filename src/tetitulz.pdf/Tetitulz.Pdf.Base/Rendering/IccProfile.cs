// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using SkiaSharp;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Util;

namespace Tetitulz.Pdf.Base.Rendering
{
    /// <summary>
    /// The evaluation model a parsed <see cref="IccProfile"/> maps onto.
    ///
    /// <para>This is deliberately a very short list. It is not a colour-management module: it
    /// covers the two profile shapes that are <i>self-contained arithmetic</i> — a 3×3 matrix
    /// behind three tone curves, and a single tone curve — and nothing else. Everything a real
    /// CMM does that this does not (multi-dimensional luts, CMYK separations, gamut mapping,
    /// rendering intents, black-point compensation) resolves to
    /// <see cref="Unsupported"/> <b>with a reason</b>, and the caller keeps the device-space
    /// fallback that shipped before ICC was consulted at all.</para>
    /// </summary>
    internal enum IccModel
    {
        /// <summary>
        /// Nothing here can be evaluated. <see cref="IccProfile.UnsupportedReason"/> says why, and
        /// it is reported through <see cref="RenderDiagnostics"/> rather than dropped.
        /// </summary>
        Unsupported = 0,

        /// <summary>
        /// ICC.1 §8.3 three-component matrix/TRC: <c>rTRC</c>/<c>gTRC</c>/<c>bTRC</c> linearise the
        /// components, then the <c>rXYZ</c>/<c>gXYZ</c>/<c>bXYZ</c> columns take them to PCS XYZ.
        /// </summary>
        MatrixTrc,

        /// <summary>
        /// One-component grey: <c>kTRC</c> yields the luminance <c>Y</c> relative to the PCS white,
        /// and the PCS XYZ is that luminance times the white point.
        /// </summary>
        GrayTrc,

        /// <summary>
        /// ICC.1 §10.10 <c>lut8Type</c>/<c>lut16Type</c> <c>A2B0</c>: input curves, an
        /// <i>n</i>-dimensional CLUT, output curves. This is the only form a <b>CMYK</b> profile
        /// can take — four inks have no matrix — and it is also what an RGB profile carrying no
        /// <c>rXYZ</c>/<c>gXYZ</c>/<c>bXYZ</c> matrix uses. See <see cref="IccLut"/>.
        /// </summary>
        LutAToB,
    }

    /// <summary>
    /// One ICC tone-reproduction curve — a <c>curveType</c> (<c>'curv'</c>: identity, a single
    /// <c>u8Fixed8</c> gamma, or a sampled table) or a <c>parametricCurveType</c> (<c>'para'</c>,
    /// function types 0–4 of ICC.1:2010 Table 65).
    /// </summary>
    /// <remarks>
    /// The input is clamped to [0,1] before evaluation. That is not a defensive convenience: an
    /// <c>/ICCBased</c> space's components are bounded by its <c>/Range</c>, whose default is
    /// <c>[0 1]</c> per ISO 32000-1 Table 66, and every parametric form below is defined only on
    /// that interval.
    /// </remarks>
    internal sealed class IccCurve
    {
        private enum Shape { Identity, Gamma, Table, Parametric }

        private readonly Shape _shape;
        private readonly double _gamma;
        private readonly double[] _table;
        private readonly int _paraType;
        private readonly double[] _p;

        private IccCurve(Shape shape, double gamma, double[] table, int paraType, double[] p)
        {
            _shape = shape; _gamma = gamma; _table = table; _paraType = paraType; _p = p;
        }

        /// <summary>A <c>curveType</c> with a zero count: the identity, per ICC.1 §10.6.</summary>
        internal static readonly IccCurve Identity = new IccCurve(Shape.Identity, 1.0, null, -1, null);

        /// <summary>A <c>curveType</c> with a count of one: the single value is a gamma.</summary>
        internal static IccCurve FromGamma(double gamma) =>
            new IccCurve(Shape.Gamma, gamma, null, -1, null);

        /// <summary>A sampled <c>curveType</c>, evaluated by linear interpolation.</summary>
        internal static IccCurve FromTable(double[] table) =>
            table == null || table.Length == 0 ? Identity : new IccCurve(Shape.Table, 1.0, table, -1, null);

        /// <summary>A <c>parametricCurveType</c> of the given ICC function type.</summary>
        internal static IccCurve FromParametric(int type, double[] parameters) =>
            new IccCurve(Shape.Parametric, 1.0, null, type, parameters);

        /// <summary>Number of <c>s15Fixed16</c> parameters an ICC parametric function type carries,
        /// or −1 when the type is not one ICC.1 defines.</summary>
        internal static int ParametricParameterCount(int functionType) => functionType switch
        {
            0 => 1,   // Y = X^g
            1 => 3,   // g a b
            2 => 4,   // g a b c
            3 => 5,   // g a b c d
            4 => 7,   // g a b c d e f
            _ => -1,
        };

        internal double Evaluate(double x)
        {
            if (!(x > 0)) x = 0;          // also catches NaN
            else if (x > 1) x = 1;

            switch (_shape)
            {
                case Shape.Identity:   return x;
                case Shape.Gamma:      return Pow(x, _gamma);
                case Shape.Table:      return Interpolate(x);
                default:               return Parametric(x);
            }
        }

        private double Interpolate(double x)
        {
            int n = _table.Length;
            if (n == 1) return _table[0];
            double pos = x * (n - 1);
            int i = SaturatingCast.ToInt(pos);
            if (i >= n - 1) return _table[n - 1];
            return _table[i] + (_table[i + 1] - _table[i]) * (pos - i);
        }

        /// <summary>ICC.1:2010 Table 65 — the five defined parametric functions.</summary>
        private double Parametric(double x)
        {
            double g = _p[0];
            switch (_paraType)
            {
                case 0:
                    return Pow(x, g);
                case 1:
                {
                    double a = _p[1], b = _p[2];
                    return x >= Break(a, b) ? Pow(a * x + b, g) : 0.0;
                }
                case 2:
                {
                    double a = _p[1], b = _p[2], c = _p[3];
                    return x >= Break(a, b) ? Pow(a * x + b, g) + c : c;
                }
                case 3:
                {
                    double a = _p[1], b = _p[2], c = _p[3], d = _p[4];
                    return x >= d ? Pow(a * x + b, g) : c * x;
                }
                default:   // 4
                {
                    double a = _p[1], b = _p[2], c = _p[3], d = _p[4], e = _p[5], f = _p[6];
                    return x >= d ? Pow(a * x + b, g) + e : c * x + f;
                }
            }
        }

        /// <summary>The X below which types 1 and 2 are constant — the root of <c>aX + b</c>.</summary>
        private static double Break(double a, double b) => a != 0 ? -b / a : 0.0;

        /// <summary><c>Math.Pow</c> with a non-negative base: a negative base under a fractional
        /// exponent is NaN, and a file chooses both the base and the exponent here.</summary>
        private static double Pow(double v, double g) => v <= 0 ? 0.0 : Math.Pow(v, g);
    }

    /// <summary>
    /// A minimal, pure-managed reader for the parts of an ICC profile this renderer can evaluate:
    /// the matrix/TRC model (ICC.1 §8.3) for three-component profiles and the <c>kTRC</c> model for
    /// grey ones. Everything else is <b>detected and named</b> — see <see cref="IccModel"/>.
    ///
    /// <para><b>The whole file is untrusted.</b> Every offset and length below comes out of the
    /// profile itself: the declared profile size, the tag count, each tag's offset and size, and a
    /// sampled curve's point count. Each is bounded against the buffer and against
    /// <see cref="PdfLimits"/> before it is used, and a value that does not check out makes the
    /// profile <see cref="IccModel.Unsupported"/> with a reason rather than throwing — the caller
    /// is a render path that must keep going.</para>
    /// </summary>
    internal sealed class IccProfile
    {
        // ICC.1 layout: a fixed 128-byte header, then a uInt32 tag count, then 12 bytes per tag.
        private const int HeaderBytes    = 128;
        private const int TagCountOffset = 128;
        private const int TagTableOffset = 132;
        private const int TagEntryBytes  = 12;
        private const int MinProfileBytes = TagTableOffset;

        // Header field offsets that matter here.
        private const int VersionOffset        = 8;
        private const int DeviceClassOffset    = 12;
        private const int DataColorSpaceOffset = 16;
        private const int PcsOffset            = 20;
        private const int FileSignatureOffset  = 36;

        /// <summary>
        /// The header fields every reader of a profile starts from, behind the one gate that says
        /// whether these bytes are an ICC header at all — the 132-byte floor, the size cap and the
        /// <c>'acsp'</c> file signature. Shared by <see cref="Parse"/> and the public
        /// <see cref="IccProfileComponents"/>, so a conformance check and the evaluator cannot
        /// disagree about what is and is not a profile.
        /// </summary>
        /// <param name="data">The raw profile bytes.</param>
        /// <param name="header">The device class, data colour space, PCS and major version.</param>
        /// <param name="reason">Why the bytes are not an ICC header, when this returns false.</param>
        internal static bool TryReadHeader(byte[] data, out IccHeader header, out string reason)
        {
            header = default;
            if (data == null || data.Length < MinProfileBytes)
            {
                reason = "profile is shorter than an ICC header plus tag count";
                return false;
            }
            if (data.Length > PdfLimits.MaxIccProfileBytes)
            {
                reason = $"profile is {data.Length} bytes, over the {PdfLimits.MaxIccProfileBytes}-byte cap";
                return false;
            }
            if (!IsSignature(data, FileSignatureOffset, "acsp"))
            {
                reason = "no 'acsp' file signature at byte 36 — not an ICC profile";
                return false;
            }

            // ICC.1 §7.2.4: bytes 8–11 are the version, byte 8 the major number as a plain integer
            // (2 for a v2 profile, 4 for v4); byte 9 holds the minor and bug-fix nibbles.
            header = new IccHeader(
                ReadSignature(data, DeviceClassOffset),
                ReadSignature(data, DataColorSpaceOffset),
                ReadSignature(data, PcsOffset),
                data[VersionOffset]);
            reason = null;
            return true;
        }

        /// <summary>
        /// The ICC.1 header fields read before any tag: device class (offset 12), data colour space
        /// (16), PCS (20) and the version's major number (byte 8). What <see cref="TryReadHeader"/>
        /// hands back.
        /// </summary>
        internal readonly struct IccHeader
        {
            internal IccHeader(string deviceClass, string dataColorSpace, string pcs, int majorVersion)
            {
                DeviceClass = deviceClass;
                DataColorSpace = dataColorSpace;
                Pcs = pcs;
                MajorVersion = majorVersion;
            }

            internal string DeviceClass { get; }
            internal string DataColorSpace { get; }
            internal string Pcs { get; }
            internal int MajorVersion { get; }
        }

        private readonly double[] _matrix;      // rX rY rZ gX gY gZ bX bY bZ
        private readonly IccCurve _curveA, _curveB, _curveC;
        private readonly IccLut _lut;

        /// <summary>
        /// The <c>A2B1</c> and <c>A2B2</c> tables when the profile carries them, indexed by
        /// <see cref="IccRenderingIntent"/>; <c>[0]</c> is unused because <see cref="_lut"/> is
        /// <c>A2B0</c>. Null entries mean the profile has no table for that intent.
        /// </summary>
        /// <remarks>
        /// <para><b>Parsed eagerly, and it has to be.</b> The obvious alternative — keep the tag
        /// offsets and parse on demand — would mean holding the profile's byte array, and
        /// <see cref="IccColorTransform"/>'s cache is a <c>ConditionalWeakTable</c> keyed by exactly
        /// that array. A value holding a strong reference to its own key keeps the entry alive for
        /// the life of the process, which is the opposite of what that cache is for.</para>
        /// <para>The cost is bounded by the same <c>PdfLimits</c> caps one table is, and in practice
        /// it is one extra parse: of the thirteen host profiles surveyed that carry these tags,
        /// every one points <c>A2B2</c> at <c>A2B0</c>'s own offset — the saturation intent is
        /// <i>aliased</i> to perceptual rather than being a third table — which
        /// <see cref="Parse"/> detects by offset and shares.</para>
        /// </remarks>
        private readonly IccLut[] _intentLuts;

        /// <summary>
        /// The source black point's L* per intent (indexed by <see cref="IccRenderingIntent"/>), for
        /// black-point compensation into sRGB — whose own black is zero — or null when there is nothing
        /// to compensate. Found at parse time (<see cref="IccBlackPoint.Source"/>) because the profile's
        /// bytes are not kept (see <see cref="_intentLuts"/>).
        /// </summary>
        private double[] _sourceBlackL;

        /// <summary>
        /// The media white over D50, per XYZ channel — what absolute colorimetric scales the PCS
        /// colour by — or null when it is the identity (<see cref="MediaWhiteScale"/>).
        /// </summary>
        private double[] _mediaWhiteScale;

        private IccProfile(IccModel model, string dataColorSpace, string unsupportedReason,
            double[] matrix, IccCurve a, IccCurve b, IccCurve c, IccLut lut = null,
            IccLut[] intentLuts = null)
        {
            Model = model;
            DataColorSpace = dataColorSpace;
            UnsupportedReason = unsupportedReason;
            _matrix = matrix; _curveA = a; _curveB = b; _curveC = c; _lut = lut;
            _intentLuts = intentLuts;
        }

        /// <summary>
        /// True when this profile carries a distinct table for <paramref name="intent"/>, so
        /// selecting it changes the colour. False for every profile with one <c>A2B</c> table, and
        /// for the matrix/TRC and grey forms, which have no per-intent variants at all.
        /// </summary>
        /// <remarks>
        /// Exposed because "the intent was honoured" and "the intent had nothing to select" are
        /// different facts about a render, and only the second is worth reporting — a channel that
        /// fired on every intent-setting document, most of which carry a single table, is one a
        /// caller learns to ignore.
        /// </remarks>
        internal bool HasTableFor(IccRenderingIntent intent)
        {
            if (Model != IccModel.LutAToB) return false;
            int index = IccRenderingIntents.TagIndex(intent, out _);
            return index != 0 && _intentLuts != null && _intentLuts[index] != null;
        }

        /// <summary>The table to evaluate for <paramref name="intent"/>, falling back to <c>A2B0</c>.</summary>
        internal IccLut LutFor(IccRenderingIntent intent)
        {
            int index = IccRenderingIntents.TagIndex(intent, out _);
            if (index == 0 || _intentLuts == null) return _lut;
            return _intentLuts[index] ?? _lut;
        }

        internal IccModel Model { get; }

        /// <summary>
        /// A matrix/TRC profile's colorant columns, <c>rX rY rZ gX gY gZ bX bY bZ</c> (a copy), or null for any
        /// other model — what a destination inverts (<see cref="RenderDestination"/>).
        /// </summary>
        internal double[] Colorants() => Model == IccModel.MatrixTrc ? (double[])_matrix.Clone() : null;

        /// <summary>
        /// The tone curve of component <paramref name="index"/> — a matrix/TRC profile's <c>rTRC</c>, <c>gTRC</c>,
        /// <c>bTRC</c>, a grey profile's <c>kTRC</c> as 0 — or null.
        /// </summary>
        internal IccCurve Curve(int index) => Model switch
        {
            IccModel.MatrixTrc => index switch { 0 => _curveA, 1 => _curveB, 2 => _curveC, _ => null },
            IccModel.GrayTrc => index == 0 ? _curveA : null,
            _ => null,
        };

        /// <summary>The profile's data colour space signature (<c>"RGB "</c>, <c>"GRAY"</c>,
        /// <c>"CMYK"</c>, …), or <c>null</c> when the header could not be read at all.</summary>
        internal string DataColorSpace { get; }

        /// <summary>Why this profile is not colour-managed, or <c>null</c> when it is.</summary>
        internal string UnsupportedReason { get; }

        /// <summary>Components one colour carries in this profile's data space; 0 when unsupported.</summary>
        internal int ComponentCount => Model switch
        {
            IccModel.MatrixTrc => 3,
            IccModel.GrayTrc   => 1,
            // The LUT's own input-channel count, not the data space's nominal one. They agree in
            // every well-formed profile; where they do not, the table is the thing about to be
            // indexed, so it is the one that decides how many operands are needed.
            IccModel.LutAToB   => _lut.InputChannels,
            _                  => 0,
        };

        internal static IccProfile Unsupported(string reason, string dataColorSpace = null) =>
            new IccProfile(IccModel.Unsupported, dataColorSpace, reason, null, null, null, null);

        /// <summary>
        /// Converts one colour, expressed in this profile's data colour space with components in
        /// [0,1], to sRGB. Returns <c>false</c> — leaving the caller's fallback in place — for an
        /// unsupported profile or too few components.
        /// </summary>
        /// <remarks>
        /// Too <i>many</i> components are ignored here rather than refused, because that is a
        /// disagreement between the PDF's <c>/N</c> and the profile, not a property of the profile.
        /// <see cref="IccColorTransform.TryTransform(byte[], float[], out SKColor)"/> is where that
        /// is detected and reported, so the reason names both numbers.
        /// </remarks>
        internal bool TryEvaluate(float[] components, out SKColor color)
            => TryEvaluate(components, IccRenderingIntents.Default, out color);

        /// <summary>
        /// As <see cref="TryEvaluate(float[], out SKColor)"/>, reading the <c>A2B</c> table
        /// <paramref name="intent"/> selects.
        /// </summary>
        /// <remarks>
        /// <para><b>Only the lut form has per-intent tables.</b> A matrix/TRC or grey/TRC profile
        /// describes one colorimetric transform and ICC gives it no intent variants, so the
        /// parameter is accepted and ignored there — which is correct rather than a gap, and is why
        /// an sRGB document renders identically whatever <c>/RI</c> it sets.</para>
        /// </remarks>
        internal bool TryEvaluate(float[] components, IccRenderingIntent intent, out SKColor color)
        {
            color = default;
            if (components == null) return false;

            switch (Model)
            {
                case IccModel.MatrixTrc:
                {
                    if (components.Length < 3) return false;
                    double lr = _curveA.Evaluate(components[0]);
                    double lg = _curveB.Evaluate(components[1]);
                    double lb = _curveC.Evaluate(components[2]);
                    // Columns, not rows: rXYZ is red's contribution to X, Y and Z.
                    double x = _matrix[0] * lr + _matrix[3] * lg + _matrix[6] * lb;
                    double y = _matrix[1] * lr + _matrix[4] * lg + _matrix[7] * lb;
                    double z = _matrix[2] * lr + _matrix[5] * lg + _matrix[8] * lb;
                    color = ToSrgb(x, y, z, AbsoluteScale(intent));
                    return true;
                }
                case IccModel.GrayTrc:
                {
                    if (components.Length < 1) return false;
                    // ICC.1 §10.18: the grey TRC yields Y relative to the PCS white, so the PCS
                    // colour is that luminance along the PCS white's chromaticity.
                    //
                    // The white is PdfColor's D50 and NOT the profile's own `wtpt`, deliberately.
                    // The PCS white is D50 by definition, and PdfColor.XyzD50ToSRgb adapts FROM
                    // that same constant — scaling along a `wtpt` that differs from it in the
                    // fourth decimal (real profiles write Z = 0.8249) leaves a neutral grey with a
                    // one-count colour cast, which is a defect, not extra fidelity.
                    double luminance = _curveA.Evaluate(components[0]);
                    color = ToSrgb(luminance * PdfColor.D50X, luminance * PdfColor.D50Y, luminance * PdfColor.D50Z,
                                   AbsoluteScale(intent));
                    return true;
                }
                case IccModel.LutAToB:
                {
                    IccLut lut = LutFor(intent);
                    double black = _sourceBlackL != null && IccRenderingIntents.Compensates(intent)
                        ? _sourceBlackL[(int)IccRenderingIntents.Table(intent)] : 0;
                    double[] absolute = AbsoluteScale(intent);
                    if ((black > 0 || absolute != null) && TryEvaluateThroughPcs(lut, components, black, absolute, out color)) return true;
                    return lut.TryEvaluate(components, out color);
                }

                default:
                    return false;
            }
        }

        /// <summary>The media-white scale when <paramref name="intent"/> is absolute colorimetric; null otherwise or when it is the identity.</summary>
        private double[] AbsoluteScale(IccRenderingIntent intent) =>
            IccRenderingIntents.Table(intent) == IccRenderingIntent.AbsoluteColorimetric ? _mediaWhiteScale : null;

        /// <summary>A D50 PCS colour into sRGB, scaled per channel by <paramref name="scale"/> first when there is one.</summary>
        private static SKColor ToSrgb(double x, double y, double z, double[] scale) =>
            scale == null
                ? PdfColor.XyzD50ToSRgb(x, y, z)
                : PdfColor.XyzD50ToSRgb(x * scale[0], y * scale[1], z * scale[2]);

        /// <summary>
        /// Absolute colorimetric's scaling for a profile: its media white (<c>wtpt</c>) over D50 per
        /// XYZ channel — at full adaptation, into sRGB, whose
        /// white is D50 as a version 2 display profile's is. Null — no scaling — when the profile
        /// has no <c>wtpt</c>, when it is a version 2 display profile (a colour engine's
        /// media white answers D50 for those: their <c>wtpt</c> is the
        /// illuminant, not a medium), or when the white is D50 to within the tag's precision.
        /// </summary>
        /// <remarks>
        /// <para><b>An independent colour engine is the oracle</b> (<c>IccAbsoluteColorimetricTests</c>):
        /// RSWOP's paper renders 246/243/243 under absolute colorimetric where relative gives
        /// white. The relative table is the one read — absolute is relative times the media white —
        /// and black-point compensation never applies.</para>
        /// <para>The 1e-3 tolerance is the PCS white written to four decimals: a version 4 profile's
        /// <c>wtpt</c> must be D50 and is often written 0.9642/1/0.8249, whose ratio to the exact
        /// constant would otherwise give a neutral grey a one-count cast.</para>
        /// </remarks>
        private static double[] MediaWhiteScale(byte[] data, TagTable table, IccHeader header)
        {
            if (!table.TryFind("wtpt", out int off, out int size)) return null;
            if (header.MajorVersion < 4 && header.DeviceClass == "mntr") return null;
            if (!TryReadXyz(data, off, size, out double x, out double y, out double z) || !(x > 0 && y > 0 && z > 0)) return null;
            var scale = new[] { x / PdfColor.D50X, y / PdfColor.D50Y, z / PdfColor.D50Z };
            const double Tolerance = 1e-3;
            return Math.Abs(scale[0] - 1) < Tolerance && Math.Abs(scale[1] - 1) < Tolerance && Math.Abs(scale[2] - 1) < Tolerance
                ? null : scale;
        }

        /// <summary>
        /// A profile's tag table, bounded — the checks every reader of the table applies: the
        /// header's declared size bounds every read when it is smaller than the buffer (a larger one
        /// means a truncated stream, and the buffer is the bound; neither number is trusted alone),
        /// and the table itself must declare at least one tag, no more than the cap, and fit.
        /// </summary>
        private static bool TryOpenTagTable(byte[] data, out TagTable table, out string reason)
        {
            table = default;
            uint declared = ReadU32(data, 0);
            int limit = declared >= MinProfileBytes && declared < (uint)data.Length ? (int)declared : data.Length;
            uint tagCount = ReadU32(data, TagCountOffset);
            reason = tagCount == 0 ? "profile declares no tags"
                : tagCount > PdfLimits.MaxIccTagCount ? $"tag table declares {tagCount} tags, over the {PdfLimits.MaxIccTagCount} cap"
                : TagTableOffset + (long)tagCount * TagEntryBytes > limit ? "tag table runs past the end of the profile"
                : null;
            if (reason != null) return false;
            table = new TagTable(data, limit, (int)tagCount);
            return true;
        }

        /// <summary>
        /// <see cref="MediaWhiteScale"/> for a profile's bytes — what absolute colorimetric divides by
        /// on the way into an output profile (<see cref="IccOutputTransform"/>); null for none.
        /// </summary>
        internal static double[] MediaWhiteScaleOf(byte[] data) =>
            TryReadHeader(data, out IccHeader header, out _) && TryOpenTagTable(data, out TagTable table, out _)
                ? MediaWhiteScale(data, table, header)
                : null;

        /// <summary>The source black point's L* for every intent; null when none is above zero.</summary>
        private static double[] SourceBlackPoints(byte[] data, string deviceClass, string dataSpace, IccProfile profile)
        {
            var black = new double[4];
            bool any = false;
            for (int i = 0; i < 3; i++)
            {
                black[i] = IccBlackPoint.Source(data, deviceClass, dataSpace, profile.LutFor, (IccRenderingIntent)i);
                any |= black[i] > 0;
            }
            return any ? black : null;
        }

        /// <summary>
        /// A lut colour into sRGB with black-point compensation: the PCS colour scaled per XYZ channel
        /// so the source black point <paramref name="blackL"/> (L*, neutral) lands on sRGB's zero and
        /// the D50 white stays put — <c>X′ = (X − BPₓ)·D50ₓ / (D50ₓ − BPₓ)</c>, the compensation
        /// with a zero destination black. False when the table cannot be evaluated raw (a v4
        /// <c>mAB </c>), and the caller converts uncompensated.
        /// </summary>
        /// <remarks>
        /// <para><b>On by default, as is usual.</b> An independent renderer draws an
        /// <c>/ICCBased</c> RSWOP colour to PNG with compensation unless told otherwise, and this
        /// matches both settings within a level (<c>IccSourceBlackPointTests</c>): relative
        /// colorimetric's 100 % CMYK, sRGB 24/23/23 uncompensated, is black. Absolute colorimetric is
        /// never compensated; perceptual is, though RSWOP's perceptual black is L* 0 and nothing moves.</para>
        /// </remarks>
        /// <param name="lut">The table to evaluate.</param>
        /// <param name="components">The colour.</param>
        /// <param name="blackL">The source black point's L*, or 0 for no compensation.</param>
        /// <param name="absolute">Absolute colorimetric's media-white scale, applied after, or null.</param>
        /// <param name="color">The sRGB colour.</param>
        private static bool TryEvaluateThroughPcs(IccLut lut, float[] components, double blackL, double[] absolute, out SKColor color)
        {
            color = default;
            var pcs = new double[Math.Max(3, lut.OutputChannels)];
            if (!lut.TryEvaluateRaw(components, pcs)) return false;

            double x, y, z;
            if (lut.IsLabPcs)
            {
                lut.DecodeLab(pcs[0], pcs[1], pcs[2], out double l, out double a, out double b);
                PdfColor.LabToXyzD50(l, a, b, out x, out y, out z);
            }
            else
            {
                const double Scale = 65535.0 / 32768.0;   // u1Fixed15
                x = pcs[0] * Scale; y = pcs[1] * Scale; z = pcs[2] * Scale;
            }

            if (blackL > 0)
            {
                PdfColor.LabToXyzD50(blackL, 0, 0, out double bx, out double by, out double bz);
                x = (x - bx) * PdfColor.D50X / (PdfColor.D50X - bx);
                y = (y - by) * PdfColor.D50Y / (PdfColor.D50Y - by);
                z = (z - bz) * PdfColor.D50Z / (PdfColor.D50Z - bz);
            }
            color = ToSrgb(Math.Max(0, x), Math.Max(0, y), Math.Max(0, z), absolute);
            return true;
        }

        // ==============================================================
        //  Parsing
        // ==============================================================

        /// <summary>
        /// Reads a profile. Never throws and never returns <c>null</c>: a profile it cannot use
        /// comes back as <see cref="IccModel.Unsupported"/> carrying the reason.
        /// </summary>
        internal static IccProfile Parse(byte[] data)
        {
            if (!TryReadHeader(data, out IccHeader header, out string headerProblem))
                return Unsupported(headerProblem);

            string deviceClass = header.DeviceClass;
            string dataSpace   = header.DataColorSpace;
            string pcs         = header.Pcs;

            if (deviceClass == "link")
                return Unsupported("device-link profile ('link'): it maps device to device and has no PCS side, so "
                                   + "it cannot describe a colour space; a link is a conversion's destination", dataSpace);
            if (deviceClass == "nmcl")
                return Unsupported("named-colour profile ('nmcl'): needs a full CMM", dataSpace);
            if (!TryOpenTagTable(data, out TagTable table, out string tableProblem))
                return Unsupported(tableProblem, dataSpace);
            double[] mediaWhite = MediaWhiteScale(data, table, header);

            // Only two PCS encodings exist, and both are decodable — 'Lab ' arrives through
            // the lut path (IccLut.DecodeLab) and 'XYZ ' through either. Anything else is not a
            // PCS at all.
            //
            // A non-XYZ PCS is not refused — that would refuse essentially every CMYK profile, which
            // almost all declare Lab — because the lut IS read: the branch chooses.
            bool labPcs = pcs == "Lab ";
            if (pcs != "XYZ " && !labPcs)
                return Unsupported($"PCS is '{pcs}', which is neither 'XYZ ' nor 'Lab '", dataSpace);

            // Matrix/TRC first where the profile offers it. It is exact, allocates nothing, and
            // costs three curve reads; an RGB profile commonly carries BOTH forms, and the lut is
            // then the perceptual rendering of a transform the matrix expresses directly. Falling
            // through to the lut when the matrix form is incomplete is what makes a lut-only RGB
            // profile work.
            IccProfile matrixForm = null;
            if (!labPcs && dataSpace == "RGB ")  matrixForm = ParseMatrixTrc(data, table, dataSpace);
            if (!labPcs && dataSpace == "GRAY")  matrixForm = ParseGrayTrc(data, table, dataSpace);
            if (matrixForm != null && matrixForm.Model != IccModel.Unsupported)
            {
                matrixForm._mediaWhiteScale = mediaWhite;
                return matrixForm;
            }

            if (table.TryFind("A2B0", out int lutOff, out int lutSize))
            {
                IccLut lut = IccLut.Parse(data, lutOff, lutSize, labPcs, out string lutReason);
                if (lut != null)
                {
                    var profile = new IccProfile(IccModel.LutAToB, dataSpace, null, null, null, null, null, lut,
                                                 ParseIntentLuts(data, table, labPcs, lutOff, lut));
                    profile._sourceBlackL = SourceBlackPoints(data, deviceClass, dataSpace, profile);
                    profile._mediaWhiteScale = mediaWhite;
                    return profile;
                }

                return Unsupported(lutReason ?? "A2B0 lut could not be read", dataSpace);
            }

            // No lut to fall through TO, so the matrix/TRC attempt's own reason is the answer.
            // Returning the generic message here instead was a real loss and a test caught it: a
            // profile whose rTRC declares an over-cap point count reported "has neither a
            // matrix/TRC form nor an A2B0 lut", which is true, useless, and hides the fact that the
            // profile is malformed rather than merely of an unsupported kind.
            if (matrixForm != null) return matrixForm;

            return Unsupported(
                dataSpace == "CMYK"
                    ? "CMYK profile carries no A2B0 lut, and four inks have no matrix/TRC form"
                    : $"data colour space '{dataSpace}' has neither a matrix/TRC form nor an A2B0 lut",
                dataSpace);
        }

        /// <summary>
        /// Reads the <c>A2B1</c> and <c>A2B2</c> tables a profile carries beside its <c>A2B0</c>,
        /// or <see langword="null"/> when it carries neither.
        /// </summary>
        /// <param name="data">The profile's bytes.</param>
        /// <param name="table">Its tag table, already bounds-checked.</param>
        /// <param name="labPcs">Whether the PCS is <c>'Lab '</c>, as <c>A2B0</c> was read with.</param>
        /// <param name="perceptualOffset">
        /// Where <c>A2B0</c> was found, so a tag pointing at the same bytes can share its already
        /// parsed table instead of building a second identical one.
        /// </param>
        /// <param name="perceptual">The parsed <c>A2B0</c> table, for that sharing.</param>
        /// <remarks>
        /// <para><b>Aliasing is the common case, not an edge case.</b> Every one of the thirteen
        /// host profiles surveyed that carries these tags points <c>A2B2</c> at <c>A2B0</c>'s
        /// offset — the saturation intent is the perceptual table under another name — while
        /// <c>A2B1</c> is genuinely a second table. Detecting that by offset turns the usual cost of
        /// this method from two extra CLUT parses into one.</para>
        /// <para><b>A table that will not parse is dropped, not fatal.</b> The profile still has its
        /// <c>A2B0</c>, and refusing the whole profile because a table the document may never ask
        /// for is malformed would lose colour management the file could otherwise have had. The
        /// selection falls back to <c>A2B0</c>, which is exactly what a profile without the tag
        /// does.</para>
        /// </remarks>
        private static IccLut[] ParseIntentLuts(byte[] data, TagTable table, bool labPcs,
                                                int perceptualOffset, IccLut perceptual)
        {
            IccLut[] luts = null;

            for (int index = 1; index <= 2; index++)
            {
                if (!table.TryFind(IccRenderingIntents.AToBTag(index), out int off, out int size))
                    continue;

                IccLut lut = off == perceptualOffset
                    ? perceptual
                    : IccLut.Parse(data, off, size, labPcs, out _);
                if (lut == null) continue;

                (luts ??= new IccLut[3])[index] = lut;
            }

            return luts;
        }

        /// <summary>
        /// The destination profile's <c>B2A</c> table for <paramref name="intent"/> and the number
        /// of device channels it produces, or <see langword="null"/> and a reason.
        /// </summary>
        /// <param name="data">The profile.</param>
        /// <param name="channels">The device channels the table produces.</param>
        /// <param name="reason">Why there is no table.</param>
        /// <param name="intent">
        /// Which table: <c>B2A0</c> perceptual (the default here, what every caller read before the
        /// parameter), <c>B2A1</c> relative colorimetric, <c>B2A2</c> saturation — through
        /// <see cref="IccRenderingIntents.TagIndex"/>, so absolute colorimetric reads <c>B2A1</c> as
        /// the source side does. A profile without that table falls back to <c>B2A0</c>, as ICC.1
        /// requires every output profile to carry it.
        /// </param>
        internal static IccLut ParseB2A(byte[] data, out int channels, out string reason,
            IccRenderingIntent intent = IccRenderingIntent.Perceptual)
        {
            channels = 0;

            // The same gate as Parse — one answer to "is this an ICC header at all".
            if (!TryReadHeader(data, out IccHeader header, out reason))
                return null;

            string dataSpace = header.DataColorSpace;
            string pcs = header.Pcs;

            channels = ChannelsOf(dataSpace);
            if (channels == 0)
            {
                reason = $"destination colour space '{dataSpace}' is not one this converts into";
                return null;
            }

            bool labPcs = pcs == "Lab ";
            if (pcs != "XYZ " && !labPcs)
            {
                reason = $"PCS is '{pcs}', which is neither 'XYZ ' nor 'Lab '";
                return null;
            }

            if (!TryOpenTagTable(data, out TagTable table, out reason))
                return null;
            string wanted = "B2A" + (char)('0' + IccRenderingIntents.TagIndex(intent, out _));
            if (!table.TryFind(wanted, out int off, out int size) && !table.TryFind("B2A0", out off, out size))
            {
                reason = "profile carries no B2A0 table, so it cannot be a conversion destination";
                return null;
            }

            IccLut lut = IccLut.Parse(data, off, size, labPcs, out reason, bToA: true);
            if (lut == null) return null;

            if (lut.OutputChannels != channels)
            {
                reason = $"B2A0 produces {lut.OutputChannels} channels but the profile's data space "
                       + $"'{dataSpace}' has {channels}";
                return null;
            }

            reason = null;
            return lut;
        }

        /// <summary>Where the tag <paramref name="signature"/> sits in the profile, or false and why not.</summary>
        internal static bool TryFindTag(byte[] data, string signature, out int offset, out int size, out string reason)
        {
            offset = size = 0;
            if (!TryOpenTagTable(data, out TagTable table, out reason)) return false;
            if (table.TryFind(signature, out offset, out size)) return true;
            reason = $"the profile carries no '{signature}' tag";
            return false;
        }

        /// <summary>
        /// A device link's <c>A2B0</c> table (ICC.1 §8.6: a link carries exactly that one transform),
        /// device channels in and out, or <see langword="null"/> and a reason.
        /// </summary>
        internal static IccLut ParseDeviceLink(byte[] data, out string reason)
        {
            if (!TryReadHeader(data, out IccHeader header, out reason)) return null;
            if (header.DeviceClass != "link")
            {
                reason = $"device class is '{header.DeviceClass}', not a device link ('link')";
                return null;
            }
            if (!TryOpenTagTable(data, out TagTable table, out reason)) return null;
            if (!table.TryFind("A2B0", out int off, out int size))
            {
                reason = "device link carries no A2B0 table, which ICC.1 §8.6 requires";
                return null;
            }
            return IccLut.Parse(data, off, size, labPcs: false, out reason, bToA: false, deviceLink: true);
        }

        /// <summary>Device channels an ICC data-space signature implies, or 0 for one not handled.</summary>
        internal static int ChannelsOf(string dataSpace) => dataSpace switch
        {
            "CMYK" => 4,
            "RGB " => 3,
            "GRAY" => 1,
            _ => 0,
        };

        private static IccProfile ParseMatrixTrc(byte[] data, TagTable table, string dataSpace)
        {
            if (!table.TryFind("rXYZ", out int rOff, out int rSize) ||
                !table.TryFind("gXYZ", out int gOff, out int gSize) ||
                !table.TryFind("bXYZ", out int bOff, out int bSize))
            {
                return Unsupported(
                    table.TryFind("A2B0", out _, out _)
                        ? "RGB profile carries no rXYZ/gXYZ/bXYZ matrix; its A2B0 lut needs a full CMM"
                        : "RGB profile is missing one of the rXYZ/gXYZ/bXYZ tags",
                    dataSpace);
            }

            var matrix = new double[9];
            if (!TryReadXyz(data, rOff, rSize, out matrix[0], out matrix[1], out matrix[2]) ||
                !TryReadXyz(data, gOff, gSize, out matrix[3], out matrix[4], out matrix[5]) ||
                !TryReadXyz(data, bOff, bSize, out matrix[6], out matrix[7], out matrix[8]))
                return Unsupported("an rXYZ/gXYZ/bXYZ tag is not a well-formed XYZType", dataSpace);

            IccCurve rTrc = ReadCurve(data, table, "rTRC", out string why);
            if (rTrc == null) return Unsupported("rTRC: " + why, dataSpace);
            IccCurve gTrc = ReadCurve(data, table, "gTRC", out why);
            if (gTrc == null) return Unsupported("gTRC: " + why, dataSpace);
            IccCurve bTrc = ReadCurve(data, table, "bTRC", out why);
            if (bTrc == null) return Unsupported("bTRC: " + why, dataSpace);

            return new IccProfile(IccModel.MatrixTrc, dataSpace, null, matrix, rTrc, gTrc, bTrc);
        }

        private static IccProfile ParseGrayTrc(byte[] data, TagTable table, string dataSpace)
        {
            IccCurve kTrc = ReadCurve(data, table, "kTRC", out string why);
            if (kTrc == null) return Unsupported("kTRC: " + why, dataSpace);

            return new IccProfile(IccModel.GrayTrc, dataSpace, null, null, kTrc, null, null);
        }

        private static IccCurve ReadCurve(byte[] data, TagTable table, string signature, out string reason)
        {
            if (!table.TryFind(signature, out int off, out int size))
            {
                reason = "tag is absent, or its offset/size falls outside the profile";
                return null;
            }
            return ReadCurveTag(data, off, size, out reason);
        }

        /// <summary>
        /// Reads one curve element of a v4 <c>'mAB '</c> pipeline and reports the bytes it occupies,
        /// <b>including the padding to the next four-byte boundary</b> (ICC.1 §7.2.5).
        /// </summary>
        /// <remarks>
        /// The stride is not the element's own length, and the elements carry no length of their
        /// own — so a caller walking a run of them has to be told where the next one starts. That
        /// is the only reason this exists beside <see cref="ReadCurveTag"/>: the type dispatch and
        /// the caps stay in one place.
        /// </remarks>
        internal static IccCurve ReadPipelineCurve(
            byte[] data, int off, int available, out int consumed, out string reason)
        {
            consumed = 0;

            IccCurve curve = ReadCurveTag(data, off, available, out reason);
            if (curve == null) return null;

            int length;
            if (IsSignature(data, off, "curv"))
            {
                // Bounded by ReadCurveTag against PdfLimits.MaxIccCurvePoints and by `available`,
                // so this cannot overflow.
                length = 12 + ((int)ReadU32(data, off + 8) * 2);
            }
            else
            {
                length = 12 + (IccCurve.ParametricParameterCount(ReadU16(data, off + 8)) * 4);
            }

            consumed = (length + 3) & ~3;
            if (consumed > available)
            {
                reason = "curve element and its padding run past the end of the tag";
                return null;
            }
            return curve;
        }

        private static IccCurve ReadCurveTag(byte[] data, int off, int size, out string reason)
        {
            reason = null;
            if (size < 12)
            {
                reason = "tone-curve tag is shorter than its own header";
                return null;
            }

            if (IsSignature(data, off, "curv"))
            {
                uint count = ReadU32(data, off + 8);
                if (count == 0) return IccCurve.Identity;
                if (count > PdfLimits.MaxIccCurvePoints)
                {
                    reason = $"sampled curve declares {count} points, over the " +
                             $"{PdfLimits.MaxIccCurvePoints} cap";
                    return null;
                }
                if (12L + (long)count * 2L > size)
                {
                    reason = "sampled curve runs past the end of its tag";
                    return null;
                }
                if (count == 1)
                    return IccCurve.FromGamma(ReadU16(data, off + 12) / 256.0);   // u8Fixed8

                var samples = new double[count];
                for (int i = 0; i < samples.Length; i++)
                    samples[i] = ReadU16(data, off + 12 + i * 2) / 65535.0;
                return IccCurve.FromTable(samples);
            }

            if (IsSignature(data, off, "para"))
            {
                int functionType = ReadU16(data, off + 8);
                int paramCount = IccCurve.ParametricParameterCount(functionType);
                if (paramCount < 0)
                {
                    reason = $"parametricCurveType function type {functionType} is not defined by ICC.1";
                    return null;
                }
                if (12L + paramCount * 4L > size)
                {
                    reason = "parametric curve runs past the end of its tag";
                    return null;
                }
                var parameters = new double[paramCount];
                for (int i = 0; i < paramCount; i++)
                    parameters[i] = ReadS15Fixed16(data, off + 12 + i * 4);
                return IccCurve.FromParametric(functionType, parameters);
            }

            reason = $"tone curve has type '{ReadSignature(data, off)}'; " +
                     "only 'curv' and 'para' are evaluated";
            return null;
        }

        private static bool TryReadXyz(byte[] data, int off, int size,
            out double x, out double y, out double z)
        {
            x = y = z = 0;
            if (size < 20 || !IsSignature(data, off, "XYZ ")) return false;
            x = ReadS15Fixed16(data, off + 8);
            y = ReadS15Fixed16(data, off + 12);
            z = ReadS15Fixed16(data, off + 16);
            return true;
        }

        /// <summary>
        /// The profile's tag table, and the single place a tag's file-supplied offset and size are
        /// checked against the profile's bounds. A tag that fails the check reads as <b>absent</b>,
        /// which is what makes a hostile table produce an honest "unsupported" verdict instead of
        /// an out-of-range read.
        /// </summary>
        private readonly struct TagTable
        {
            private readonly byte[] _data;
            private readonly int _limit;
            private readonly int _count;

            internal TagTable(byte[] data, int limit, int count)
            {
                _data = data; _limit = limit; _count = count;
            }

            internal bool TryFind(string signature, out int offset, out int size)
            {
                offset = 0; size = 0;
                for (int i = 0; i < _count; i++)
                {
                    int entry = TagTableOffset + i * TagEntryBytes;
                    if (!IsSignature(_data, entry, signature)) continue;

                    long off = ReadU32(_data, entry + 4);
                    long len = ReadU32(_data, entry + 8);
                    if (off < HeaderBytes || len <= 0 || off + len > _limit) return false;

                    offset = (int)off; size = (int)len;
                    return true;
                }
                return false;
            }
        }

        // ---- Big-endian primitives -------------------------------------

        private static uint ReadU32(byte[] d, int o) => Core.IO.BigEndian.U32(d, o);

        private static int ReadU16(byte[] d, int o) => Core.IO.BigEndian.U16(d, o);

        /// <summary>ICC <c>s15Fixed16Number</c> — a signed 16.16 fixed-point value.</summary>
        private static double ReadS15Fixed16(byte[] d, int o) => unchecked((int)ReadU32(d, o)) / 65536.0;

        private static bool IsSignature(byte[] d, int o, string signature)
        {
            if (o < 0 || o + 4 > d.Length) return false;
            return d[o] == signature[0] && d[o + 1] == signature[1]
                && d[o + 2] == signature[2] && d[o + 3] == signature[3];
        }

        private static string ReadSignature(byte[] d, int o)
        {
            if (o < 0 || o + 4 > d.Length) return string.Empty;
            var chars = new char[4];
            for (int i = 0; i < 4; i++)
            {
                byte b = d[o + i];
                chars[i] = b >= 0x20 && b < 0x7F ? (char)b : '?';
            }
            return new string(chars);
        }
    }
}
