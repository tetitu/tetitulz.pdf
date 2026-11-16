// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

namespace Tetitulz.Pdf.Base.Rendering
{
    /// <summary>
    /// Identifies the rendering surface a page is being rasterised for.  Flows
    /// into <see cref="PdfPageRenderer"/> where it picks which page-box to
    /// clip to, consulting the document's /ViewerPreferences when set.
    /// </summary>
    public enum RenderIntent
    {
        /// <summary>Screen preview — honour /ViewerPreferences /ViewArea (default: CropBox).</summary>
        Screen,
        /// <summary>Print output — honour /ViewerPreferences /PrintArea (default: CropBox).</summary>
        Print
    }

    /// <summary>Which of a page's annotations are drawn — what a print dialog's "comments and forms" chooses.</summary>
    public enum AnnotationRendering
    {
        /// <summary>Every annotation the intent's flags show (§12.5.3).</summary>
        All = 0,
        /// <summary>Form fields' widgets only: the filled form without comments or markup.</summary>
        FormFieldsOnly = 1,
        /// <summary>No annotation: the page's content alone.</summary>
        None = 2,
    }

    /// <summary>
    /// Image resampling filter applied when a bitmap is scaled onto the page.
    /// A render-time choice between Mitchell-Netravali, bicubic,
    /// and nearest-neighbour interpolation.  <see cref="PdfSpecInterpolate"/>
    /// honours the /Interpolate flag in the image dict — treated as "caller
    /// wants good interpolation" — and picks <see cref="Mitchell"/> when the
    /// flag is true, <see cref="Nearest"/> otherwise.
    /// </summary>
    public enum ImageResampler
    {
        /// <summary>Honour the image's /Interpolate flag (current behaviour).</summary>
        PdfSpecInterpolate,
        /// <summary>Nearest-neighbour — sharpest, pixel-accurate rendering of scanned docs.</summary>
        Nearest,
        /// <summary>Bicubic with Catmull-Rom weights — sharper than Mitchell, mild ringing.</summary>
        CatmullRom,
        /// <summary>Bicubic with Mitchell-Netravali weights — softer.</summary>
        Mitchell,
    }

    /// <summary>
    /// How hard the PNG encoder works. <b>PNG is lossless at every setting</b>, so this trades
    /// encode time against file size and never changes a single pixel.
    /// </summary>
    /// <remarks>
    /// <para>This exists because measurement found PNG encoding to be about <b>97%</b> of a
    /// single-page render: a blank 612x792 page at 150 dpi costs ~82 ms, of which ~79 ms is
    /// <c>SKImage.Encode(Png)</c> and ~2 ms is allocating and clearing the surface. Every public
    /// raster entry point returns PNG bytes, so every caller paid it before any PDF content was
    /// even considered.</para>
    /// <para>Measured on a 1275x1650 surface: <see cref="Fast"/> ~12.8 ms, <see cref="Smallest"/>
    /// ~80.4 ms. The size penalty depends heavily on the page — on a synthetic
    /// high-contrast fixture it was ~3.5x, and on a typical text page it is far less.</para>
    /// <para>A caller that is going to draw the pixels rather than store them should not encode
    /// at all — see <c>PdfPageRenderer.RenderPageToPixels</c>, which returns a
    /// <see cref="RenderedPage"/> of raw RGBA8888 premultiplied pixels. It deliberately exposes no
    /// SkiaSharp type, so there is no <c>RenderPageToSKBitmap</c>.</para>
    /// </remarks>
    public enum PngCompression
    {
        /// <summary>
        /// zlib level 3 with the Sub row filter. Roughly 6x faster to encode than
        /// <see cref="Smallest"/>. <b>The default</b>, because for the overwhelmingly common
        /// case — render a page, show it or hand it on — encode time dominated everything else
        /// the renderer did.
        /// </summary>
        Fast,

        /// <summary>
        /// SkiaSharp's default encoder settings: zlib level 6, every row filter tried. Smallest
        /// output, and what every render produced before <see cref="Fast"/> became the default.
        /// Choose this when the bytes are being stored or transmitted repeatedly.
        /// </summary>
        Smallest,
    }

    /// <summary>
    /// Which encoded raster format a render is written as.
    /// </summary>
    /// <remarks>
    /// <para><b>The measured trade on a rendered page is not the one the format names suggest.</b>
    /// On a 1275x1650 coloured-text page (150 dpi):</para>
    /// <list type="table">
    ///   <item><description><see cref="Png"/> at <see cref="PngCompression.Fast"/> — 280 KB, ~20 ms, lossless</description></item>
    ///   <item><description><see cref="WebpLossless"/> — <b>32 KB</b>, ~39 ms, lossless</description></item>
    ///   <item><description><see cref="Jpeg"/> at quality 85 — 399 KB, ~38 ms, <b>lossy</b></description></item>
    ///   <item><description><see cref="WebpLossy"/> at quality 85 — ~191 KB, <b>~161 ms</b>, lossy</description></item>
    /// </list>
    /// <para>So for the output this renderer actually produces — text and vector art on white —
    /// JPEG is both <i>larger</i> than PNG and lossy, and <see cref="WebpLossless"/> is roughly
    /// <b>9x smaller than PNG</b> for twice its encode time. JPEG earns its place on
    /// scanned/photographic pages, which is the case its defaults were chosen for.
    /// <see cref="WebpLossy"/> is offered for completeness and is the slowest option measured;
    /// do not reach for it by analogy with JPEG.</para>
    /// </remarks>
    public enum PdfImageFormat
    {
        /// <summary>PNG — lossless, effort controlled by <see cref="PdfRenderOptions.PngCompression"/>.</summary>
        Png,
        /// <summary>JPEG — lossy, quality supplied per call; see <see cref="PdfRenderOptions.JpegSubsampling"/>.</summary>
        Jpeg,
        /// <summary>WebP in lossless mode — smallest output measured for rendered pages.</summary>
        WebpLossless,
        /// <summary>WebP in lossy mode, quality supplied per call. The slowest encoder here.</summary>
        WebpLossy,
    }

    /// <summary>
    /// Chroma subsampling for JPEG output (JFIF/ITU-T T.81 component sampling factors).
    /// </summary>
    /// <remarks>
    /// <para><b>Why this is exposed.</b> The encoder's default is 4:2:0, which stores one chroma
    /// sample per 2x2 block of luma. That is right for photographs and wrong for the coloured text
    /// and thin rules a page render is mostly made of. Measured against the source pixels of a
    /// coloured-text page at quality 85: 4:2:0 gives <b>RMSE 10.69 and a worst-case channel error
    /// of 190</b>; <see cref="Chroma444"/> gives <b>3.93 and 68</b>, for ~40% more bytes.</para>
    /// <para><b><see cref="Default"/> stays the default deliberately.</b> Changing it would move
    /// the output of every existing caller, and this library does not move pixels silently — the
    /// same rule that keeps <see cref="PdfRenderOptions.DownsampleImagesToTarget"/> opt-in. Choose
    /// <see cref="Chroma444"/> for document pages.</para>
    /// </remarks>
    public enum JpegSubsampling
    {
        /// <summary>Whatever the encoder chooses — 4:2:0 in practice. Byte-identical to the historical output.</summary>
        Default,
        /// <summary>4:2:0 — chroma at half resolution on both axes. Smallest, worst on text edges.</summary>
        Chroma420,
        /// <summary>4:2:2 — chroma at half horizontal resolution.</summary>
        Chroma422,
        /// <summary>4:4:4 — full-resolution chroma. Best for text and line art.</summary>
        Chroma444,
    }

    /// <summary>
    /// The pixel format an encoded render is written in.
    /// </summary>
    public enum RenderPixelFormat
    {
        /// <summary>8-bit RGBA. The historical behaviour.</summary>
        Rgba,

        /// <summary>
        /// 8-bit grey. Measured on a 1275x1650 page: PNG encode <b>6.2 ms and 167 KB</b> against
        /// <b>20.5 ms and 280 KB</b> for RGBA — 3.3x faster and 1.7x smaller.
        /// </summary>
        /// <remarks>
        /// The page is still <i>rendered</i> in colour and converted before encoding, so anti-aliasing
        /// and blending are unchanged; only the encoded output differs. Colour is converted by the
        /// imaging library's own luminance transform, so this is not a substitute for a DeviceGray
        /// rendering intent. Grey has no alpha channel, so a translucent
        /// <see cref="PdfRenderOptions.BackgroundArgb"/> is flattened onto white and reported.
        /// </remarks>
        Grayscale,
    }

    /// <summary>
    /// Optional rendering parameters threaded through the public
    /// <see cref="PdfPageRenderer"/> entry points.  All existing overloads
    /// keep their historical signatures and default to
    /// <see cref="Default"/>, so no caller breaks.
    /// </summary>
    public sealed class PdfRenderOptions
    {
        /// <summary>Screen vs print.  Default Screen.</summary>
        public RenderIntent Intent { get; init; } = RenderIntent.Screen;

        private readonly AnnotationRendering _annotations = AnnotationRendering.All;

        /// <summary>
        /// Which annotations are drawn, within what <see cref="Intent"/>'s flags already allow — both
        /// renderers, through <c>AnnotationVisibility</c>. Default <see cref="AnnotationRendering.All"/>.
        /// </summary>
        /// <exception cref="System.ArgumentOutOfRangeException">The value is not a member of the enum.</exception>
        public AnnotationRendering Annotations
        {
            get => _annotations;
            init
            {
                if (value != AnnotationRendering.All && value != AnnotationRendering.FormFieldsOnly && value != AnnotationRendering.None)
                    throw new System.ArgumentOutOfRangeException(nameof(Annotations), value, "Not an annotation scope.");
                _annotations = value;
            }
        }

        /// <summary>
        /// Render the page's content stream alone, leaving its annotations out — the transparency
        /// flattener's raster, which replaces the content and keeps the annotations as annotations,
        /// so drawing them into the image as well would draw them twice.
        /// </summary>
        internal bool ContentOnly { get; init; }

        /// <summary>
        /// When true the renderer clips to the page's /TrimBox (the final
        /// trimmed dimensions of the printed page per spec §14.11.2).
        /// Takes precedence over <see cref="Intent"/>.
        /// </summary>
        public bool UseTrimBox { get; init; }

        /// <summary>
        /// When true the renderer clips to the page's /BleedBox (the region
        /// intended to be printed-to-bleed).  Takes precedence over
        /// <see cref="Intent"/>.
        /// </summary>
        public bool UseBleedBox { get; init; }

        /// <summary>
        /// When true the renderer clips to the page's /ArtBox (the meaningful
        /// content extent).  Takes precedence over <see cref="Intent"/>.
        /// </summary>
        public bool UseArtBox { get; init; }

        /// <summary>Which resampler to use when drawing scaled bitmaps.</summary>
        public ImageResampler ImageResampler { get; init; } = ImageResampler.PdfSpecInterpolate;

        /// <summary>
        /// Password used to open an encrypted document. <c>null</c> (the default) and the empty
        /// string both mean "the empty user password", which is what an owner-password-only
        /// document uses.
        /// </summary>
        /// <remarks>
        /// Decryption needs a registered <c>IPdfDecryptor</c> (referencing
        /// <c>Tetitulz.Pdf.Encryption</c> registers one). With no decryptor present the bytes are
        /// passed through unchanged and the page renders from still-encrypted content streams —
        /// the pre-existing behaviour, unchanged by this option.
        /// </remarks>
        public string Password { get; init; }

        /// <summary>
        /// Simulates overprint (<c>/OP</c>, <c>/op</c> with <c>/OPM</c> 1) for path fills and
        /// strokes whose colour was set in DeviceCMYK, by compositing per colorant against the
        /// page. Default <c>false</c>.
        /// </summary>
        /// <remarks>
        /// Off by default deliberately: overprint is a press behaviour and viewers expose it as an
        /// "overprint preview" toggle, so enabling it silently would change screen output for every
        /// prepress PDF. What is and is not covered — and what gets reported instead — is
        /// documented on <see cref="OverprintCompositor"/>.
        /// </remarks>
        public bool SimulateOverprint { get; init; }

        /// <summary>
        /// Soft proofing: an output ICC profile (a press or printer profile with a <c>B2A</c> table)
        /// the rendered page is proofed through — every pixel converted into the profile's device
        /// values by the relative colorimetric table and back to sRGB by the profile's own
        /// <c>A2B</c> table, so the raster shows what that device can reproduce (its gamut, its
        /// paper white, its black). <c>null</c> (the default) renders the page as it is.
        /// </summary>
        /// <remarks>
        /// <para>The conversion into the profile
        /// is <see cref="PdfSeparationOptions.OutputIccProfile"/>'s (the plates' transform, matched
        /// within a level of an independent colour engine through RSWOP) and the conversion back is the one an
        /// <c>/ICCBased</c> colour takes into the render. The proof is applied to the finished page
        /// surface — content, annotations, the background — whole pages, tiles and bands alike, so a
        /// mosaic and the page agree; a transparent background keeps its alpha. The SVG writer's
        /// colours and images are proofed the same way (masks and clips left alone). The overprint
        /// preview (<see cref="SimulateOverprint"/>) through a CMYK profile is the press's own: its
        /// plates separated by the profile and recombined through its <c>A2B</c> table. The
        /// separation plates themselves are ink, and are not proofed.</para>
        /// <para>A profile the proof cannot be built from is reported through
        /// <see cref="RenderDiagnosticKind.ProofProfileUnsupported"/> and the page rendered
        /// unproofed. Colours are proofed through a 33×33×33 grid of the transform, interpolated,
        /// so a colour between grid nodes may differ from the exact transform by a level or two.</para>
        /// </remarks>
        public byte[] ProofProfile { get; init; }

        /// <summary>
        /// Whether the proof's conversion into the profile compensates for the destination's black
        /// point (Adobe's method, on by default as is usual): sRGB black lands on
        /// the device's darkest colour rather than being clipped. Default <c>true</c>. Read only
        /// with <see cref="ProofProfile"/>.
        /// </summary>
        public bool ProofBlackPointCompensation { get; init; } = true;

        /// <summary>
        /// The device the raster is for: an RGB or grey ICC profile — a display's (Display P3, Adobe RGB, a
        /// monitor's own), a printer's RGB, a grey device's — the finished page is converted into, after any
        /// <see cref="ProofProfile"/>, so its pixels are that device's values. <c>null</c> (the default) leaves
        /// the render in sRGB.
        /// </summary>
        /// <remarks>
        /// <para>A matrix/TRC or grey-curve profile is inverted exactly; a profile with a relative colorimetric
        /// <c>B2A</c> table, or a device link from RGB, goes through it on a 33-node grid, as the proof does.
        /// The encoders embed the profile — PNG's <c>iCCP</c>, JPEG's <c>APP2</c>, WebP's <c>ICCP</c> — so a
        /// reader knows what the values mean; a grey destination writes grey PNG and JPEG. Raw pixels
        /// (<c>RenderPageToPixels</c>) are the destination's values, untagged.</para>
        /// <para><b>The working space stays sRGB</b>: every colour reaches the page as sRGB and is converted
        /// at the end, so a destination wider than sRGB holds sRGB's gamut. The separation plates and the SVG
        /// writer are not converted. A profile this cannot convert into (a CMYK one among them — separations
        /// are that destination, <see cref="PdfSeparationOptions.OutputIccProfile"/>) is reported through
        /// <see cref="RenderDiagnosticKind.DestinationProfileUnsupported"/> and the page left in sRGB.</para>
        /// </remarks>
        public byte[] DestinationProfile { get; init; }

        /// <summary>
        /// Whether the conversion through a destination's <c>B2A</c> table compensates for its black point, as
        /// <see cref="ProofBlackPointCompensation"/> does for the proof. Default <c>true</c>. A matrix/TRC
        /// display profile's black is sRGB's, so it changes nothing there.
        /// </summary>
        public bool DestinationBlackPointCompensation { get; init; } = true;

        /// <summary>
        /// An ICC named-colour profile (class <c>nmcl</c> — a spot-colour library) whose colours the page's spot
        /// inks are drawn in: a <c>/Separation</c> whose colorant the profile names in full (<c>SPOT 485 C</c>)
        /// is painted in the profile's colour rather than through the file's own alternate space and tint
        /// transform. <c>null</c> (the default) draws every spot as the file describes it.
        /// </summary>
        /// <remarks>
        /// <para>A tint is the straight line in CIE L*a*b* from paper white to the solid — the rule two independent
        /// named-colour implementations both follow (checked against an independent renderer's
        /// named-profile output). Fills, images, shadings, patterns and an <c>/Indexed</c> base alike, in the
        /// raster and SVG renderers; the overprint preview's spot inks and a PSD's spot channels show it too.</para>
        /// <para>A <c>/DeviceN</c> space, a colorant the profile does not name, process colorants,
        /// <c>/All</c> and <c>/None</c> keep the file's own transform. A profile that is not a readable
        /// named-colour profile is reported through <see cref="RenderDiagnosticKind.NamedColorProfileUnsupported"/>
        /// and every spot drawn as the file describes it. The separation plates hold ink, and are unchanged.</para>
        /// </remarks>
        public byte[] NamedColorProfile { get; init; }

        /// <summary>
        /// How hard the PNG encoder works. Defaults to <see cref="PngCompression.Fast"/>.
        /// Lossless either way — see <see cref="PngCompression"/> for why the default is speed.
        /// Ignored by the JPEG entry points, which take an explicit quality instead.
        /// </summary>
        public PngCompression PngCompression { get; init; } = PngCompression.Fast;

        /// <summary>
        /// Decodes an encoded image (JPEG, JPEG 2000) at a reduced size when it is drawn into an
        /// area far smaller than its native resolution. Default <c>false</c>.
        /// </summary>
        /// <remarks>
        /// <para>Measured on an 8.4 MP source drawn as a 24 pt thumbnail at 150 dpi: the full
        /// decode cost <b>85.9 ms and 57 MB</b> against a 14.9 ms floor. For thumbnails, previews
        /// and contact sheets of scanned documents that is nearly all waste.</para>
        /// <para><b>Off by default because it changes output pixels.</b> A scaled decode followed
        /// by the resampler is not bit-identical to a full decode followed by the same resampler,
        /// and this library's rule is that output does not move silently. When it does fire it
        /// reports <see cref="RenderDiagnosticKind.ImageDownsampled"/> naming both sizes.</para>
        /// <para>Only <b>encoded</b> images are affected — those decoded by a codec. Raw sample
        /// data is untouched, because subsampling it correctly means touching every bit-depth and
        /// colour-space branch of the decoder, which is a much larger surface than this switch
        /// implies.</para>
        /// <para>The decode is kept at twice the device target, so the final resample still has an
        /// octave to work from, and nothing below 256 px is reduced at all.</para>
        /// </remarks>
        public bool DownsampleImagesToTarget { get; init; }

        /// <summary>
        /// The resolution, in pixels per inch of the drawn size, that an encoded image (JPEG, JPEG
        /// 2000) embedded in <b>SVG</b> output is decoded at when
        /// <see cref="DownsampleImagesToTarget"/> is on. Default <c>0</c>: embed at the image's own
        /// size. Ignored by the raster renderer, whose target is the device it draws into.
        /// </summary>
        /// <remarks>
        /// <para>SVG has no device resolution — it scales — so the raster path's target, the device
        /// pixels an image covers, does not exist there, and the SVG writer embedded every image at
        /// its native size: a 40-megapixel scan drawn as a thumbnail went into the file whole. This
        /// names the target instead. An image drawn 2 inches wide at 150 here is decoded for 300
        /// pixels, under the same rule the raster path follows (twice the target kept, nothing
        /// below 256 pixels reduced), and reported as <see cref="RenderDiagnosticKind.ImageDownsampled"/>.</para>
        /// <para>While it is in effect an image drawn several times is embedded once per draw, at
        /// each draw's size, rather than defined once and referenced — the raster path likewise
        /// does not cache an image it reduces.</para>
        /// </remarks>
        public float SvgImageDpi { get; init; }

        /// <summary>
        /// Whether the SVG writer reduces encoded images, and so the size it must work one out for.
        /// </summary>
        internal bool SvgDownsamplesImages => DownsampleImagesToTarget && SvgImageDpi > 0 && !float.IsInfinity(SvgImageDpi);

        /// <summary>
        /// The colour the page surface is cleared to before any content is drawn, as
        /// <c>0xAARRGGBB</c>. Defaults to <see cref="OpaqueWhite"/>.
        /// </summary>
        /// <remarks>
        /// <para>An alpha of 0 gives a <b>transparent</b> page, which is what a caller compositing
        /// the render over something else needs.</para>
        /// <para><b>Formats without an alpha channel flatten it.</b> JPEG has none, and
        /// <see cref="RenderPixelFormat.Grayscale"/> output has none either, so a translucent
        /// background is composited onto white for those and reported through
        /// <see cref="RenderDiagnosticKind.RenderParameterIgnored"/> — "the background I asked for
        /// was dropped" must not look the same as "this format has no alpha".</para>
        /// <para>It is a <c>uint</c> rather than a colour type on purpose: an <c>SKColor</c> here
        /// would put a specific SkiaSharp version in this library's public API, the same reason
        /// <see cref="RenderedPage"/> hands back a <c>byte[]</c>.</para>
        /// </remarks>
        public uint BackgroundArgb { get; init; } = OpaqueWhite;

        /// <summary>Opaque white — the historical page background and the default.</summary>
        public const uint OpaqueWhite = 0xFFFFFFFFu;

        /// <summary>Fully transparent, for a render that will be composited over something else.</summary>
        public const uint Transparent = 0x00000000u;

        /// <summary>
        /// Chroma subsampling for JPEG output. Defaults to <see cref="JpegSubsampling.Default"/>,
        /// which is byte-identical to the historical output. Read
        /// <see cref="Rendering.JpegSubsampling"/> before leaving it there for a page of text.
        /// </summary>
        public JpegSubsampling JpegSubsampling { get; init; } = JpegSubsampling.Default;

        /// <summary>
        /// The pixel format encoded output is written in. Defaults to
        /// <see cref="RenderPixelFormat.Rgba"/>. Ignored by
        /// <c>PdfPageRenderer.RenderPageToPixels</c>, which by contract returns the renderer's
        /// native RGBA8888 surface.
        /// </summary>
        public RenderPixelFormat PixelFormat { get; init; } = RenderPixelFormat.Rgba;

        /// <summary>
        /// Upper bound on how many pages a <c>RenderAllPages*</c> call renders concurrently.
        /// <c>0</c> (the default) means <see cref="System.Environment.ProcessorCount"/>.
        /// </summary>
        /// <remarks>
        /// <para>The batch paths were capped at four workers regardless of the machine, which
        /// measured 301 ms against 174 ms unrestricted on a 12-core box for the same 50-page
        /// document. The cap is now the caller's, and it has two real uses beyond speed.</para>
        /// <para><b>Set it to 1</b> to render on the calling thread with a single shared document
        /// and cache set — the deterministic, thread-affine profile the stream overloads used to
        /// have. That is what a host wants when it reads <see cref="RenderDiagnostics"/> or a
        /// <c>Last*</c> diagnostic slot after the call, since those are <c>[ThreadStatic]</c>.</para>
        /// <para><b>Set it low</b> to bound memory: each worker retains a parsed document, its
        /// resolved typefaces and up to <see cref="ContentOperationCache.MaxBytes"/> of parsed
        /// operations for the length of the batch - and, dominating all three, a <b>full-page
        /// bitmap</b> for the duration of each render, plus a second one of the same size while a
        /// <c>/Rotate</c> page is being rotated. A letter page at 300 dpi is 2550x3300x4 = 33.7 MB,
        /// so a rotated one peaks at ~67 MB <i>per worker</i>: on a 64-core machine the default
        /// degree alone is ~4.3 GB of bitmaps. That figure is what this dial is for.</para>
        /// <para>A negative value is rejected with <see cref="System.ArgumentOutOfRangeException"/>
        /// rather than silently reinterpreted as the default. 0 means "choose automatically".</para>
        /// </remarks>
        public int MaxRenderThreads { get; init; }

        /// <summary>
        /// Whether these options ask for anything an <see cref="Core.Plugins.IRendererPlugin"/>
        /// cannot be told about.
        /// </summary>
        /// <remarks>
        /// <para><c>IRendererPlugin.RenderPage(page, document, dpi)</c> takes no options and cannot
        /// take any: it lives in <c>Core</c>, and <see cref="PdfRenderOptions"/> lives in
        /// <c>Base</c>, so the contract may not name it. A registered plugin therefore renders with
        /// <b>defaults for everything</b> — page box, background, intent, overprint, image
        /// downsampling — and without this predicate, registering one would silently discard
        /// whatever the caller had asked for: a caller who set <c>UseTrimBox</c> would get the
        /// CropBox, with no error and no diagnostic.</para>
        /// <para>The dispatch site refuses to hand a non-integral DPI to a plugin, on the
        /// reasoning that being a pixel off the requested box "silently breaks the one guarantee
        /// that entry point makes". This is the same trade, so it gets the same answer: when the
        /// caller asked for something the plugin cannot honour, the built-in renderer runs and the
        /// substitution is reported rather than made invisible.</para>
        /// <para><b>Output-encoding members are deliberately excluded</b> — <c>PngCompression</c>,
        /// <c>JpegSubsampling</c>, <c>PixelFormat</c>, <c>MaxRenderThreads</c> — because they
        /// describe what happens to the bytes after a page is drawn, not what is drawn.
        /// <c>Password</c> is excluded too: the plugin is handed an already-open
        /// <see cref="Core.Document.PdfDocument"/>, so decryption has happened by then.</para>
        /// </remarks>
        /// <remarks>
        /// <b><see cref="PixelFormat"/> belongs in this set.</b>
        /// For a plugin there is no "after the page is drawn": <c>plugin.RenderPage</c> returns
        /// finished PNG bytes that are returned verbatim, bypassing <c>PdfImageEncoder</c>
        /// entirely - so <see cref="RenderPixelFormat.Grayscale"/>, which changes the encoded
        /// pixels and not merely their size, would be silently lost, with no diagnostic, since the
        /// bypass report fires only when this predicate returns true; removing a plugin reference
        /// would change the output format of an unchanged call.
        /// </remarks>
        internal bool RequestsAnythingAPluginCannotHonour()
            => Intent      != RenderIntent.Screen
            || Annotations != AnnotationRendering.All
            || UseTrimBox  || UseBleedBox || UseArtBox
            || SimulateOverprint
            || ProofProfile != null || DestinationProfile != null || NamedColorProfile != null
            || DownsampleImagesToTarget
            || PixelFormat != RenderPixelFormat.Rgba
            || BackgroundArgb != OpaqueWhite;

        /// <summary>Shared default instance — Screen intent, no box override, PDF-spec interpolation.</summary>
        public static PdfRenderOptions Default { get; } = new PdfRenderOptions();
    }
}
