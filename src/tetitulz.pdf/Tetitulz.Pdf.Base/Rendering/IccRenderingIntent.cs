// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Core.Document;
using SkiaSharp;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Base.Rendering
{
    /// <summary>
    /// A rendering intent, in the numbering ICC gives its own tags — <c>A2B0</c> is perceptual,
    /// <c>A2B1</c> media-relative colorimetric, <c>A2B2</c> saturation.
    /// </summary>
    /// <remarks>
    /// <para><b>The numbering is the tag suffix, and that is the whole mapping.</b> Selecting a
    /// rendering intent in a profile that carries several <c>A2B</c> tables is not arithmetic: the
    /// profile's author built one table per intent, and a CMM picks the one the caller asked for.
    /// That is why this needed no ICC clause text to implement — the tables are the same
    /// <c>lut8Type</c>/<c>lut16Type</c>/<c>lutAToBType</c> structures <see cref="IccLut"/> already
    /// reads, and only which of them is read changes.</para>
    ///
    /// <para><b>ISO 32000-1 §8.6.5.8 names four intents and makes the default
    /// <c>/RelativeColorimetric</c></b>, which is why <see cref="IccRenderingIntents.Default"/> is
    /// that and not perceptual. Honouring the parameter therefore changes what an unset document
    /// renders as, wherever its profile carries a distinct <c>A2B1</c>, rather than reading
    /// <c>A2B0</c> unconditionally.</para>
    /// </remarks>
    internal enum IccRenderingIntent
    {
        /// <summary>ICC <c>A2B0</c>. PDF <c>/Perceptual</c>.</summary>
        Perceptual = 0,

        /// <summary>ICC <c>A2B1</c>. PDF <c>/RelativeColorimetric</c>, and PDF's default.</summary>
        MediaRelativeColorimetric = 1,

        /// <summary>ICC <c>A2B2</c>. PDF <c>/Saturation</c>.</summary>
        Saturation = 2,

        /// <summary>
        /// PDF <c>/AbsoluteColorimetric</c>: the relative table scaled by the media white — see
        /// <see cref="IccRenderingIntents.TagIndex"/>.
        /// </summary>
        AbsoluteColorimetric = 3,

        /// <summary>
        /// <b>A modifier, never an intent of its own</b>: one of the four with this bit set is converted
        /// without black-point compensation — PDF 2.0's <c>/UseBlackPtComp /OFF</c> (ISO 32000-2
        /// §8.6.5.9). Carried in the intent because the intent is what already reaches every
        /// conversion (fills, images, shadings, palettes, plates, both renderers); read it only
        /// through <see cref="IccRenderingIntents.Table"/> and <see cref="IccRenderingIntents.Compensates"/>.
        /// </summary>
        WithoutBlackPointCompensation = 0x10,
    }

    /// <summary>
    /// Reads ISO 32000-1 §8.6.5.8's intent names and maps them onto the table a profile actually
    /// carries.
    /// </summary>
    internal static class IccRenderingIntents
    {
        /// <summary>The public intent a host provider receives for <paramref name="intent"/> (the compensation bit dropped).</summary>
        internal static PdfRenderingIntent ToPublic(IccRenderingIntent intent)
            => (PdfRenderingIntent)(int)Table(intent);

        /// <summary>
        /// Transforms an image bitmap through the registered provider: through the intent-aware
        /// interface when the provider implements it, else through the image seam with no intent,
        /// reported once per registered provider when the file asked for a non-default one.
        /// </summary>
        internal static void TransformImage(byte[] profileBytes, SKBitmap bitmap, IccRenderingIntent intent)
        {
            IIccTransformProvider provider = IccTransform.Current;
            if (!(provider is SkiaIccTransformProvider) && provider is IIccIntentColorTransformProvider withIntent)
            {
                withIntent.TransformToSRgb(profileBytes, bitmap, ToPublic(intent), Compensates(intent));
                return;
            }
            if (!(provider is SkiaIccTransformProvider) && Table(intent) != Default) IccColorTransform.ReportIntentNotForwarded(intent);
            provider.TransformToSRgb(profileBytes, bitmap);
        }
        /// <summary>
        /// The intent that applies when the file names none: <c>/RelativeColorimetric</c>, per
        /// §8.6.5.8's default for <c>/RI</c>.
        /// </summary>
        internal const IccRenderingIntent Default = IccRenderingIntent.MediaRelativeColorimetric;

        /// <summary>
        /// The intent named by a PDF <c>ri</c> operand, <c>/RI</c> entry or image <c>/Intent</c>,
        /// or <see cref="Default"/> for an absent or unrecognised name.
        /// </summary>
        /// <remarks>
        /// §8.6.5.8 says a conforming reader "shall" ignore an intent it does not recognise, so an
        /// unknown name resolves to the default rather than refusing. The four names are compared
        /// with the ordinal comparison every other name dispatch in the renderer uses.
        /// </remarks>
        internal static IccRenderingIntent Parse(string name)
        {
            switch (name)
            {
                case "Perceptual":           return IccRenderingIntent.Perceptual;
                case "RelativeColorimetric": return IccRenderingIntent.MediaRelativeColorimetric;
                case "Saturation":           return IccRenderingIntent.Saturation;
                case "AbsoluteColorimetric": return IccRenderingIntent.AbsoluteColorimetric;
                default:                     return Default;
            }
        }

        /// <summary>
        /// The <c>A2B</c>/<c>B2A</c> tag index to read for <paramref name="intent"/>, and whether
        /// that is the intent the caller asked for.
        /// </summary>
        /// <param name="intent">The intent the file named.</param>
        /// <param name="exact">
        /// <see langword="false"/> when a different table is being substituted, so the caller can
        /// report the substitution rather than let it look like fidelity.
        /// </param>
        /// <remarks>
        /// <para><b>Absolute colorimetric has no table of its own</b>: it is media-relative
        /// colorimetric with the result scaled by the profile's media white over the PCS white, so
        /// the <c>A2B1</c> table is read here and the scaling is <c>IccProfile.MediaWhiteScale</c>'s
        /// (checked against an independent colour engine).</para>
        /// <para>A profile that carries no table for the chosen index falls back to <c>A2B0</c>
        /// separately, in <see cref="IccProfile"/>, because only the profile knows what it has.</para>
        /// </remarks>
        internal static int TagIndex(IccRenderingIntent intent, out bool exact)
        {
            intent = Table(intent);
            if (intent == IccRenderingIntent.AbsoluteColorimetric)
            {
                exact = false;
                return (int)IccRenderingIntent.MediaRelativeColorimetric;
            }

            exact = true;
            return (int)intent;
        }

        /// <summary>The <c>A2B</c> tag signature for a table index, e.g. 1 → <c>"A2B1"</c>.</summary>
        internal static string AToBTag(int index) => "A2B" + (char)('0' + index);

        /// <summary>
        /// The intent an image is drawn under: its own <c>/Intent</c> where it has one, and
        /// <paramref name="graphicsStateIntent"/> otherwise.
        /// </summary>
        /// <remarks>
        /// §8.9.5.2 Table 89 gives an image XObject an <c>/Intent</c> that <b>overrides</b> the
        /// graphics state's for that image alone — an image is the one piece of content that
        /// carries its own. Shared by both renderers so the override cannot exist on one side only,
        /// which is the divergence this pair keeps producing.
        /// </remarks>
        internal static IccRenderingIntent ForImage(PdfDocument doc, PdfDictionary imageDict,
                                                    IccRenderingIntent graphicsStateIntent)
        {
            string named = PdfResolve.Name(doc, imageDict, PdfName.Intent)?.Value;
            // The image names an intent, not a compensation: the graphics state's switch stays.
            return named == null ? graphicsStateIntent
                : Parse(named) | (graphicsStateIntent & IccRenderingIntent.WithoutBlackPointCompensation);
        }

        /// <summary>
        /// The intent a graphics state converts at: its <c>/RI</c> name, with compensation switched
        /// off when its <c>/UseBlackPtComp</c> said <c>/OFF</c>.
        /// </summary>
        internal static IccRenderingIntent Of(string name, bool blackPointCompensation)
            => blackPointCompensation ? Parse(name) : Parse(name) | IccRenderingIntent.WithoutBlackPointCompensation;

        /// <summary>The intent whose table is read — <paramref name="intent"/> without the compensation modifier.</summary>
        internal static IccRenderingIntent Table(IccRenderingIntent intent)
            => intent & ~IccRenderingIntent.WithoutBlackPointCompensation;

        /// <summary>Whether a conversion at <paramref name="intent"/> is black-point compensated (absolute colorimetric never is, whatever this says).</summary>
        internal static bool Compensates(IccRenderingIntent intent)
            => (intent & IccRenderingIntent.WithoutBlackPointCompensation) == 0;

        /// <summary>
        /// What ISO 32000-2 §8.6.5.9's <c>/UseBlackPtComp</c> asks of <paramref name="current"/>:
        /// <c>/OFF</c> switches compensation off; <c>/ON</c> and <c>/Default</c> (the processor's
        /// choice, which here is on, as is usual) switch it on; anything else
        /// is not a value of the key and leaves it as it was.
        /// </summary>
        internal static bool UseBlackPtComp(string name, bool current) => name switch
        {
            "OFF"     => false,
            "ON"      => true,
            "Default" => true,
            _         => current,
        };

        /// <summary>The PDF name for an intent, for a diagnostic's detail text.</summary>
        internal static string Name(IccRenderingIntent intent) => Table(intent) switch
        {
            IccRenderingIntent.Perceptual                => "Perceptual",
            IccRenderingIntent.MediaRelativeColorimetric => "RelativeColorimetric",
            IccRenderingIntent.Saturation                => "Saturation",
            IccRenderingIntent.AbsoluteColorimetric      => "AbsoluteColorimetric",
            _                                            => "unknown",
        };
    }
}
