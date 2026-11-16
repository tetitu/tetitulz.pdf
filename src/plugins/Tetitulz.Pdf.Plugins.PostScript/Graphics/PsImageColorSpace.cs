// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System.Runtime.CompilerServices;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Imaging;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Plugins.PostScript.Interpreter;

namespace Tetitulz.Pdf.Plugins.PostScript.Graphics
{
    /// <summary>
    /// The colour space <c>setcolorspace</c> established, resolved once into the form an emitted
    /// PDF image needs: how many samples a pixel has, and the <c>/ColorSpace</c> object to write.
    ///
    /// <para><b>Why the whole space object and not just a component count.</b>
    /// <see cref="PsGraphicsState.ColorComponents"/> is an <c>int</c>, and
    /// <c>[/Indexed /DeviceRGB 1 &lt;…&gt;]</c> and <c>/DeviceGray</c> both have a count of 1 — so a
    /// count cannot tell an indexed image from a grey one, and cannot carry the palette an indexed
    /// image is meaningless without. PLRM §4.8 makes the current colour space part of the graphics
    /// state, which is the same argument <see cref="PsGraphicsState.ColorComponents"/>'s own doc
    /// records for living there rather than on the interpreter.</para>
    ///
    /// <para><b>Resolved eagerly, at <c>setcolorspace</c>.</b> A <c>PsArray</c> aliases its storage
    /// (see <c>PsComposites</c>), so a program can <c>put</c> into the palette after establishing
    /// the space; resolving then would read whatever it had become. Reporting, in contrast, happens
    /// <i>lazily</i> at the image, because <c>PostScriptDiagnostics.Report</c> is neither
    /// deduplicated nor capped and <c>setcolorspace</c> is not bounded per conversion, while images
    /// are (<see cref="PsLimits.MaxImageXObjects"/>).</para>
    /// </summary>
    internal sealed class PsImageColorSpace
    {
        private PsImageColorSpace(string family, int components,
                                  PdfObject pdfColorSpace, string carryFailure,
                                  int setColorArity = int.MinValue, string approximation = null)
        {
            Family        = family;
            Components    = components;
            PdfColorSpace = pdfColorSpace;
            CarryFailure  = carryFailure;
            Approximation = approximation;
            SetColorArity = setColorArity == int.MinValue ? components : setColorArity;
        }

        /// <summary>
        /// Why <see cref="PdfColorSpace"/> is not the space the program named, for a space that is
        /// carried approximately (CalGray and CalRGB travel as their device analogues); null when
        /// it is carried exactly or refused. <c>PsImageOps</c> reports it once per conversion at
        /// the first image.
        /// </summary>
        public string Approximation { get; }

        /// <summary>
        /// How many operands <c>setcolor</c> takes in this space, or -1 when that cannot be
        /// determined from the space object alone (<c>ICCBased</c>, whose <c>/N</c> lives in a
        /// stream this interpreter never built, and <c>Pattern</c>).
        ///
        /// <para><b>Here rather than in a second switch.</b> Two classifiers would disagree: one
        /// knowing <c>CalGray</c>/<c>CalRGB</c>/<c>Lab</c> and the other not would make
        /// <c>/CalRGB setcolorspace … image</c> size <c>setcolor</c> to 3 operands while emitting a
        /// <b>DeviceGray</b> image and reading a third of the samples. One classifier, two
        /// questions.</para>
        /// </summary>
        public int SetColorArity { get; }

        /// <summary>The PostScript family name, for diagnostics.</summary>
        public string Family { get; }

        /// <summary>An <c>/Indexed</c> space's base space; null for any other.</summary>
        public PsImageColorSpace Base { get; private init; }

        /// <summary>An <c>/Indexed</c> space's largest index.</summary>
        public int Hival { get; private init; }

        /// <summary>An <c>/Indexed</c> space's palette, the base space's components a byte each, copied at <c>setcolorspace</c>.</summary>
        public byte[] Lookup { get; private init; }

        /// <summary>A <c>/Separation</c> or <c>/DeviceN</c> space's alternate space; null for any other, or one that is not a space.</summary>
        public PsImageColorSpace Alternate { get; private init; }

        /// <summary>A <c>/Separation</c> or <c>/DeviceN</c> space's tint transform — a procedure, or a Level 3 function dictionary.</summary>
        public PsObject TintTransform { get; private init; }

        /// <summary>A <c>/Separation</c> space's colorant — its name, or a string's Latin-1 text; null for any other space, or one that names none.</summary>
        public string Colorant { get; private init; }

        /// <summary>A <c>/DeviceN</c> space's colorants, an entry null where the program's names array holds no name or string; null for any other space.</summary>
        public string[] Colorants { get; private init; }

        /// <summary>A copy made at <c>setcolorspace</c>: the names array is the program's storage.</summary>
        private static string[] ColorantsOf(PsArray names, PsInterpreter i)
        {
            var colorants = new string[names.Length];
            for (int k = 0; k < names.Length; k++) colorants[k] = ColorantOf(names[k], i);
            return colorants;
        }

        /// <summary>
        /// A <c>/Separation</c> or <c>/DeviceN</c> space carried as <paramref name="pdf"/>
        /// (<see cref="PsSpaceColor.SpecialSpace"/>), its samples as they are, said as
        /// <paramref name="approximation"/>.
        /// </summary>
        internal PsImageColorSpace CarriedAs(PdfObject pdf, string approximation) =>
            new(Family, Components, pdf, null, SetColorArity, approximation)
            {
                Alternate = Alternate, TintTransform = TintTransform, Colorant = Colorant, Colorants = Colorants,
            };

        // A string's text is charged: the space keeps it, and a graphics state keeps the space.
        private static string ColorantOf(PsObject name, PsInterpreter i) =>
            name.Type == PsType.Name ? name.AsName()
            : name.Type == PsType.String ? (i != null ? i.LatinFromOperand(name.AsString(), "setcolorspace") : name.AsString().AsLatin1())
            : null;

        /// <summary>Samples per pixel an image in this space carries.</summary>
        public int Components { get; }

        /// <summary>
        /// For a space a colour is painted in as itself — <c>/ICCBased</c>, or <c>/CalGray</c>,
        /// <c>/CalRGB</c>, <c>/Lab</c> with their dictionary — each component's minimum and maximum,
        /// which a colour is held to; null for any other.
        /// </summary>
        public double[] NamedRange { get; private init; }

        /// <summary>A CIE space's white point (<c>/Lab</c>'s, for what a colour reads back as); null for any other.</summary>
        public double[] WhitePoint { get; private init; }

        /// <summary>
        /// A <c>CIEBased*</c> space as evaluated to XYZ, whose colours and images are carried as Lab
        /// (<see cref="PdfColorSpace"/> is that Lab space); null for any other.
        /// </summary>
        public PsCieSpace Cie { get; private init; }

        /// <summary>
        /// The Lab space a CIE-based space's colours are carried in, as a space of its own — L 0 to
        /// 100, a and b −128 to 127, the CIE space's white point — what an image converted from it
        /// and an indexed palette over it are in.
        /// </summary>
        internal static PsImageColorSpace LabOf(PsCieSpace cie) =>
            new(PdfColorSpaceNames.Lab, 3, cie.LabSpace, null)
            {
                NamedRange = new double[] { 0, 100, -128, 127, -128, 127 }, WhitePoint = cie.WhitePoint,
            };

        /// <summary>
        /// The object to write as the image's <c>/ColorSpace</c>, or null when the space cannot be
        /// carried into PDF by this converter — in which case <see cref="CarryFailure"/> says why.
        /// </summary>
        public PdfObject PdfColorSpace { get; }

        /// <summary>
        /// Why the space could not be carried, or null when it was. <b>Never</b> collapse this into
        /// "assume DeviceGray": a <c>/Separation</c> image emitted as <c>/DeviceGray</c> paints tint
        /// values as grey levels, sizes the sample buffer to a quarter of the real payload, and then
        /// reports only that "the remainder was padded" — a valid PDF containing the wrong picture,
        /// behind a diagnostic that names the wrong cause.
        /// </summary>
        public string CarryFailure { get; }

        public static readonly PsImageColorSpace DeviceGray =
            new(PdfColorSpaceNames.DeviceGray, 1, PdfName.DeviceGray, null);

        public static readonly PsImageColorSpace DeviceRgb =
            new(PdfColorSpaceNames.DeviceRGB, 3, PdfName.DeviceRGB, null);

        public static readonly PsImageColorSpace DeviceCmyk =
            new(PdfColorSpaceNames.DeviceCMYK, 4, PdfName.DeviceCMYK, null);

        /// <summary>The device space of <paramref name="components"/> components — 3 RGB, 4 CMYK, else grey.</summary>
        public static PsImageColorSpace Device(int components) => components switch
        {
            3 => DeviceRgb,
            4 => DeviceCmyk,
            _ => DeviceGray,
        };

        /// <summary>
        /// Resolves a <c>setcolorspace</c> operand, or null when it is not a colour space at all.
        /// <paramref name="i"/>, when there is one, reads an <c>/ICCBased</c> profile a file holds and
        /// is charged for the profile's bytes.
        /// </summary>
        public static PsImageColorSpace Resolve(PsObject space, PsInterpreter i = null) => Resolve(space, i, 0);

        /// <summary>
        /// The deepest a colour space may nest its base, alternate or pattern-base space. A space's array
        /// can reference itself (<c>a 1 a put</c> makes <c>a[1]</c> name <c>a</c>, which no setter checks)
        /// or be built thousands deep by a loop, and <c>Resolve</c> descends it by C# recursion that never
        /// returns to the step loop, so neither the work budget nor a <c>PsErrorException</c> can stop a
        /// <c>StackOverflowException</c> — it takes the host down. A real space nests a few levels.
        /// </summary>
        private const int MaxColorSpaceDepth = 16;

        private static PsImageColorSpace Resolve(PsObject space, PsInterpreter i, int depth)
        {
            if (depth > MaxColorSpaceDepth) return null;

            if (space.Type == PsType.Name) return FromFamilyName(space.AsName(), null, i, depth);

            if (space.Type != PsType.Array) return null;

            var array = space.AsArray();
            if (array.Length == 0 || array[0].Type != PsType.Name) return null;

            return FromFamilyName(array[0].AsName(), array, i, depth);
        }

        /// <summary>
        /// A space resolved from a dictionary, by the dictionary: the same <c>[/ICCBased dict]</c> set
        /// again is the same PDF space — one resource, one copy of the profile, a file read once.
        /// </summary>
        private static readonly ConditionalWeakTable<PsDict, PsImageColorSpace> ByDictionary = new();

        private static PsImageColorSpace FromFamilyName(string family, PsArray array, PsInterpreter i, int depth)
        {
            if (array != null && array.Length > 1 && array[1].Type == PsType.Dictionary
                && family is PdfColorSpaceNames.ICCBased or PdfColorSpaceNames.CalGray or PdfColorSpaceNames.CalRGB or PdfColorSpaceNames.Lab
                    or "CIEBasedA" or "CIEBasedABC" or "CIEBasedDEF" or "CIEBasedDEFG")
            {
                PsDict dict = array[1].AsDict();
                if (ByDictionary.TryGetValue(dict, out PsImageColorSpace known)) return known;
                PsImageColorSpace resolved = family == PdfColorSpaceNames.ICCBased ? ResolveIcc(dict, i, depth)
                    : family.StartsWith("CIEBased", System.StringComparison.Ordinal) ? ResolveCieBased(family, dict, i)
                    : ResolveCie(family, dict);

                // An /Alternate naming this space resolved it, and remembered it, on the way here.
                if (ByDictionary.TryGetValue(dict, out known)) return known;
                if (resolved.PdfColorSpace != null) ByDictionary.Add(dict, resolved);
                return resolved;
            }

            switch (family)
            {
                case PdfColorSpaceNames.DeviceGray:
                case "G":
                    return DeviceGray;

                case PdfColorSpaceNames.DeviceRGB:
                case "RGB":
                    return new PsImageColorSpace(PdfColorSpaceNames.DeviceRGB, 3,
                                                 PdfName.DeviceRGB, null);

                case PdfColorSpaceNames.DeviceCMYK:
                case "CMYK":
                    return new PsImageColorSpace(PdfColorSpaceNames.DeviceCMYK, 4,
                                                 PdfName.DeviceCMYK, null);

                // The CIE-based device analogues. PDF has /CalGray and /CalRGB, but carrying them
                // needs the whitepoint/gamma/matrix parameters this converter does not read, so the
                // samples travel in the device space the family stands for and the approximation is
                // reported at the image. That is exact for the common case — a CalRGB with sRGB-ish
                // parameters — and wrong only in the colours' precise chromaticity.
                case "CalGray":
                    return new PsImageColorSpace("CalGray", 1, PdfName.DeviceGray, null,
                                                 approximation: CalDetail);
                case "CalRGB":
                    return new PsImageColorSpace("CalRGB", 3, PdfName.DeviceRGB, null,
                                                 approximation: CalDetail);

                // Lab is NOT a device space in disguise: its components are not in [0,1] — L is
                // 0..100 and a,b are signed — so treating its samples as DeviceRGB would not be an
                // approximation, it would be a different picture.
                case "Lab":
                    return Uncarried("Lab", 3,
                        "a /Lab image's components are not [0,1] device values (L is 0..100 and " +
                        "a/b are signed), so they cannot travel in a device space");

                case "Indexed":
                case "I":
                    return ResolveIndexed(array, i, depth);

                // Everything below is a real colour space this converter cannot carry. Each keeps
                // its true component count, so the sample arithmetic stays right even though the
                // image will be declined — a wrong count would misreport the payload size as well.
                // [/Separation name alternate tintTransform] and [/DeviceN names alternate tintTransform]:
                // the alternate and the transform are kept for a fill's colour (PsSpaceColor), which
                // reaches the page in the alternate space; an image still needs the transform as a PDF
                // function, which this converter does not build.
                case "Separation":
                    return new PsImageColorSpace("Separation", 1, null,
                        "a /Separation image needs its tint transform emitted as a PDF function, " +
                        "which this converter does not build")
                    {
                        Alternate     = array != null && array.Length > 3 ? Resolve(array[2], i, depth + 1) : null,
                        TintTransform = array != null && array.Length > 3 ? array[3] : PsObject.Null,
                        Colorant      = array != null && array.Length > 3 ? ColorantOf(array[1], i) : null,
                    };

                case "DeviceN":
                    if (array != null && array.Length > 1 && array[1].Type == PsType.Array
                        && array[1].AsArray().Length > PsLimits.MaxDeviceNColorants)
                        return new PsImageColorSpace("DeviceN", 1, null,
                            $"a /DeviceN space names more than {PsLimits.MaxDeviceNColorants} colorants", setColorArity: -1);
                    return new PsImageColorSpace("DeviceN",
                        array != null && array.Length > 1 && array[1].Type == PsType.Array
                            ? array[1].AsArray().Length : 1,
                        null,
                        "a /DeviceN image needs its tint transform emitted as a PDF function, " +
                        "which this converter does not build")
                    {
                        Alternate     = array != null && array.Length > 3 ? Resolve(array[2], i, depth + 1) : null,
                        TintTransform = array != null && array.Length > 3 ? array[3] : PsObject.Null,
                        Colorants     = array != null && array.Length > 3 && array[1].Type == PsType.Array
                                        ? ColorantsOf(array[1].AsArray(), i) : null,
                    };

                case "CIEBasedA":   return Uncarried("CIEBasedA",   1, CieDetail);
                case "CIEBasedABC": return Uncarried("CIEBasedABC", 3, CieDetail);
                case "CIEBasedDEF": return Uncarried("CIEBasedDEF", 3, CieDetail);
                case "CIEBasedDEFG":return Uncarried("CIEBasedDEFG",4, CieDetail);

                // An /ICCBased space with no dictionary (one with one is resolved above): its count
                // is unknown, so setcolor takes what numbers there are.
                case "ICCBased":
                    return new PsImageColorSpace("ICCBased", 3, null,
                        "an /ICCBased space names no dictionary holding its /N and profile", setColorArity: -1);

                case "Pattern":
                    return PatternOver(array != null && array.Length > 1 ? Resolve(array[1], i, depth + 1) : null);

                default:
                    return null;
            }
        }

        private const string CalDetail =
            "its WhitePoint, Gamma and Matrix were not read, so the samples travel in the device " +
            "space the family stands for — exact for sRGB-like parameters, otherwise off in the " +
            "colours' chromaticity";

        private const string CieDetail =
            "a CIEBased space names no dictionary of its white point, ranges and decode procedures";

        private static PsImageColorSpace Uncarried(string family, int components, string why) =>
            new(family, components, null, why);

        /// <summary>
        /// <c>[/CIEBasedA dict]</c> and its kin evaluated to XYZ (<see cref="PsCieSpace"/>) and carried
        /// as Lab; refused, with the count kept, where the dictionary cannot be evaluated — or with no
        /// interpreter to run its procedures in.
        /// </summary>
        private static PsImageColorSpace ResolveCieBased(string family, PsDict dict, PsInterpreter i)
        {
            int n = family switch { "CIEBasedA" => 1, "CIEBasedDEFG" => 4, _ => 3 };
            if (i == null) return Uncarried(family, n, CieDetail);

            // A decode procedure runs in a child interpreter, in whose setcolorspace a space named again
            // would be built again: nothing is remembered until a build ends, and each level is CLR
            // frames no budget or PostScript error can stop. No decode procedure has a colour space to set.
            if (_cieBuildRunning)
                return Uncarried(family, n, "a CIE-based space was set by a CIE-based space's own decode procedure");
            _cieBuildRunning = true;
            try
            {
                PsCieSpace cie = PsCieSpace.Build(i, family, dict, out string why);
                if (cie == null) return Uncarried(family, n, why);
                return new PsImageColorSpace(family, cie.Components, cie.LabSpace, null) { Cie = cie };
            }
            finally
            {
                _cieBuildRunning = false;
            }
        }

        /// <summary>Whether this thread is inside a CIE-based space's build; restored as the build ends, on every path.</summary>
        [System.ThreadStatic] private static bool _cieBuildRunning;

        /// <summary>
        /// <c>[/ICCBased dict]</c> (PostScript 3, as PDF's §8.6.5.5): <c>/N</c> components, the profile
        /// from <c>/DataSource</c> — a string, or the inline block a file <c>currentfile</c> made — and
        /// <c>/Range</c> and a device <c>/Alternate</c> when the dictionary has them, written as PDF's
        /// <c>[/ICCBased stream]</c>, so images and colours in it are carried exactly. Refused, the count
        /// kept, where the profile is no profile or describes another number of components than
        /// <c>/N</c> — a reader would refuse that space, or paint it wrongly.
        /// </summary>
        private static PsImageColorSpace ResolveIcc(PsDict dict, PsInterpreter i, int depth)
        {
            if (!dict.TryGetName("N", out PsObject count) || !count.IsNumber || count.AsInt() is not (1 or 3 or 4))
                return new PsImageColorSpace(PdfColorSpaceNames.ICCBased, 3, null,
                    "its /N is not 1, 3 or 4", setColorArity: -1);
            int n = count.AsInt();
            if (!dict.TryGetName("DataSource", out PsObject source))
                return Uncarried(PdfColorSpaceNames.ICCBased, n, "its dictionary has no /DataSource");

            byte[] profile = null;
            if (source.Type == PsType.String) profile = source.AsString().ToArray();
            else if (source.Type == PsType.File && i != null)
            {
                profile = source.AsFile().ReadInlineBlock(i, out bool jpeg);
                if (jpeg) return Uncarried(PdfColorSpaceNames.ICCBased, n, "its /DataSource file is a JPEG, not a profile");
                if (profile == null) return Uncarried(PdfColorSpaceNames.ICCBased, n, "its /DataSource file could not be read");
            }
            if (profile == null) return Uncarried(PdfColorSpaceNames.ICCBased, n, "its /DataSource is not a string or a file");
            if (source.Type == PsType.String) i?.ChargeAllocation(profile.Length, "setcolorspace");

            int described = IccDataColourSpaces.ComponentCountOf(profile);
            if (described == 0) return Uncarried(PdfColorSpaceNames.ICCBased, n, "its /DataSource is not an ICC profile");
            if (described != n)
                return Uncarried(PdfColorSpaceNames.ICCBased, n, $"its /N is {n} but its profile describes {described} components");

            double[] range = NumbersOf(dict, "Range", 2 * n);
            var stream = new PdfStream();
            stream[PdfName.N] = new PdfNumber(n);
            if (range != null) stream[PdfName.Range] = NumberArray(range);
            if (dict.TryGetName("Alternate", out PsObject alternateValue)
                && Resolve(alternateValue, i, depth + 1) is { Base: null, PdfColorSpace: PdfName alternate } device && device.Components == n)
                stream[new PdfName("Alternate")] = alternate;
            stream.RawData = profile;

            var pdf = new PdfArray();
            pdf.Add(new PdfName(PdfColorSpaceNames.ICCBased));
            pdf.Add(stream);
            return new PsImageColorSpace(PdfColorSpaceNames.ICCBased, n, pdf, null) { NamedRange = range ?? UnitRanges(n) };
        }

        /// <summary>
        /// <c>[/CalGray dict]</c>, <c>[/CalRGB dict]</c> and <c>[/Lab dict]</c> carried as PDF writes them
        /// (§8.6.5.2–4) — their dictionaries hold the same keys, copied where each has its shape: a
        /// <c>/WhitePoint</c> (required, Y 1), a <c>/BlackPoint</c>, <c>/Gamma</c>, <c>/Matrix</c> and
        /// <c>/Range</c>. Without a usable white point the space travels as its device analogue, as a bare
        /// name does, or for <c>/Lab</c> is refused.
        /// </summary>
        private static PsImageColorSpace ResolveCie(string family, PsDict dict)
        {
            int n = family == PdfColorSpaceNames.CalGray ? 1 : 3;
            double[] white = NumbersOf(dict, "WhitePoint", 3);
            if (white == null || !(white[0] > 0) || white[1] != 1 || !(white[2] > 0))
                // The fallback resolves the CIE family to its device analogue, which re-enters nothing —
                // depth 0 is correct, there is no nested space below here.
                return FromFamilyName(family, null, null, 0);

            var pdfDict = new PdfDictionary();
            pdfDict[PdfName.WhitePoint] = NumberArray(white);
            if (NumbersOf(dict, "BlackPoint", 3) is double[] black) pdfDict[new PdfName("BlackPoint")] = NumberArray(black);
            double[] range = null;
            switch (family)
            {
                case PdfColorSpaceNames.CalGray:
                    if (NumbersOf(dict, "Gamma", 1) is double[] gamma) pdfDict[PdfName.Gamma] = new PdfNumber(gamma[0]);
                    break;
                case PdfColorSpaceNames.CalRGB:
                    if (NumbersOf(dict, "Gamma", 3) is double[] gammas) pdfDict[PdfName.Gamma] = NumberArray(gammas);
                    if (NumbersOf(dict, "Matrix", 9) is double[] matrix) pdfDict[PdfName.Matrix] = NumberArray(matrix);
                    break;
                default:
                    double[] ab = NumbersOf(dict, "Range", 4) ?? new double[] { -100, 100, -100, 100 };
                    pdfDict[PdfName.Range] = NumberArray(ab);
                    range = new[] { 0, 100, ab[0], ab[1], ab[2], ab[3] };
                    break;
            }

            var pdf = new PdfArray();
            pdf.Add(new PdfName(family));
            pdf.Add(pdfDict);
            return new PsImageColorSpace(family, n, pdf, null) { NamedRange = range ?? UnitRanges(n), WhitePoint = white };
        }

        /// <summary><paramref name="count"/> numbers under <paramref name="key"/>: one alone when the count is 1, else an array of them; null otherwise.</summary>
        private static double[] NumbersOf(PsDict dict, string key, int count)
        {
            if (!dict.TryGetName(key, out PsObject value)) return null;
            if (count == 1 && value.IsNumber) return new[] { value.Num };
            if (value.Type != PsType.Array || value.AsArray().Length != count) return null;
            var numbers = new double[count];
            for (int k = 0; k < count; k++)
            {
                PsObject item = value.AsArray()[k];
                if (!item.IsNumber || double.IsNaN(item.Num) || double.IsInfinity(item.Num)) return null;
                numbers[k] = item.Num;
            }
            return numbers;
        }

        private static PdfArray NumberArray(double[] values) => Emit.PdfValues.Numbers(values);

        private static double[] UnitRanges(int n) => Emit.PdfValues.UnitRanges(n);

        /// <summary>A one-ink separation image carried as an <c>/Indexed</c> one over <paramref name="alternate"/> (<see cref="PsSpaceColor.SeparationImageSpace"/>).</summary>
        internal static PsImageColorSpace IndexedOver(PsImageColorSpace alternate, int hival, byte[] lookup, string approximation)
        {
            var pdf = new PdfArray();
            pdf.Add(PdfName.Indexed);
            pdf.Add(alternate.PdfColorSpace);
            pdf.Add(new PdfNumber(hival));
            pdf.Add(new PdfString(lookup, isHex: true));
            return new PsImageColorSpace("Separation", 1, pdf, null, approximation: approximation)
            {
                Base = alternate, Hival = hival, Lookup = lookup,
            };
        }

        /// <summary>
        /// <c>/Pattern</c>, or <c>[/Pattern base]</c> — the space an uncoloured pattern's components
        /// are in (PLRM §4.9); <c>setcolor</c> takes a pattern, so its arity is not a count.
        /// </summary>
        internal static PsImageColorSpace PatternOver(PsImageColorSpace baseSpace) =>
            new("Pattern", 1, null, "a Pattern space has no samples for an image to carry", setColorArity: -1)
            {
                PatternBase = baseSpace,
            };

        /// <summary>A <c>[/Pattern base]</c> space's base; null for <c>/Pattern</c> alone and for any other space.</summary>
        public PsImageColorSpace PatternBase { get; private init; }

        /// <summary>
        /// <c>[/Indexed base hival lookup]</c> — one sample per pixel, indexing a palette.
        ///
        /// <para>The palette is copied out here rather than referenced, because a <c>PsString</c>
        /// aliases the program's own storage and the program may write to it afterwards.</para>
        /// </summary>
        private static PsImageColorSpace ResolveIndexed(PsArray array, PsInterpreter i, int depth)
        {
            if (array == null || array.Length < 4)
                return Uncarried("Indexed", 1, "the /Indexed space is malformed");

            var baseSpace = Resolve(array[1], i, depth + 1);
            if (baseSpace == null || baseSpace.PdfColorSpace == null)
                return Uncarried("Indexed", 1,
                    "the /Indexed base space is one this converter cannot carry" +
                    (baseSpace?.CarryFailure == null ? "" : " — " + baseSpace.CarryFailure));

            if (!array[2].IsNumber)
                return Uncarried("Indexed", 1, "the /Indexed hival is not a number");

            int hival = array[2].AsInt();
            if (hival < 0 || hival > 255)
                return Uncarried("Indexed", 1, $"the /Indexed hival {hival} is outside 0..255");

            if (array[3].Type != PsType.String)
                return Uncarried("Indexed", 1,
                    "the /Indexed lookup is a procedure rather than a string, which this converter " +
                    "cannot evaluate");

            // A palette holds hival + 1 entries, whatever the string's length: only those are copied
            // (a space is resolved at every setcolorspace, and a graphics state keeps it), and charged.
            PsString table = array[3].AsString();
            long entries = (hival + 1L) * baseSpace.Components;
            byte[] lookup = table.Length > entries ? table.Interval(0, (int)entries).ToArray() : table.ToArray();
            i?.ChargeAllocation(lookup.Length, "setcolorspace");

            // Over a CIE-based space the palette is converted entry by entry to the Lab the space is
            // carried in, each byte spanning its component's range (§8.6.6.3 as PLRM §4.8.5 has it).
            if (baseSpace.Cie is PsCieSpace cie)
            {
                int n = cie.Components;
                var lab = new byte[(hival + 1) * 3];
                var components = new double[n];
                for (int entry = 0; entry <= hival; entry++)
                {
                    for (int k = 0; k < n; k++)
                    {
                        int at = entry * n + k;
                        double unit = at < lookup.Length ? lookup[at] / 255.0 : 0;
                        components[k] = cie.Range[2 * k] + unit * (cie.Range[2 * k + 1] - cie.Range[2 * k]);
                    }
                    cie.ToLabBytes(components, lab, entry * 3);
                }
                lookup = lab;
                baseSpace = LabOf(cie);
            }

            var pdf = new PdfArray();
            pdf.Add(PdfName.Indexed);
            pdf.Add(baseSpace.PdfColorSpace);
            pdf.Add(new PdfNumber(hival));
            pdf.Add(new PdfString(lookup, isHex: true));

            return new PsImageColorSpace("Indexed", 1, pdf, null) { Base = baseSpace, Hival = hival, Lookup = lookup };
        }
    }
}
