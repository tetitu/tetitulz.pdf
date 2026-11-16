// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.CompilerServices;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Base.Rendering
{
    /// <summary>
    /// Default colour spaces (ISO 32000-2 §8.6.5.6) for both renderers: "When a device colour space is
    /// selected, the ColorSpace subdictionary of the current resource dictionary is checked for the
    /// presence of an entry designating a corresponding default colour space (DefaultGray, DefaultRGB,
    /// or DefaultCMYK …). If such an entry is present, its value shall be used as the colour space for
    /// the operation currently being performed" — whichever way the device space was selected: an
    /// operator (<c>g</c>, <c>rg</c>, <c>k</c> and their stroking twins), <c>cs</c>/<c>CS</c>, an image,
    /// an inline image or a shading; and "the underlying colour space of a Pattern colour space, the
    /// base colour space of an Indexed colour space, the alternate colour space of a Separation or
    /// DeviceN colour space" too.
    /// </summary>
    /// <remarks>
    /// <para><b>One leaf, and both renderers paint the remapped colour exactly as an explicit
    /// <c>cs</c> naming the default and an <c>sc</c> with the same operands would</b> — the clause's
    /// NOTE: "the current colour space is defined by the default colour space rather than DeviceGray,
    /// DeviceRGB or DeviceCMYK".</para>
    /// <para><b>What is refused, and stays the device space.</b> "Any colour space other than a Lab,
    /// Indexed, or Pattern colour space may be used as a default colour space", with "the same number of
    /// components as the original space": a default of another family or arity is not one, and the
    /// device colour is painted as written — reported once per resource dictionary
    /// (<see cref="RenderDiagnosticKind.RenderParameterIgnored"/>), never silently.</para>
    /// </remarks>
    internal static class DefaultColorSpaces
    {
        private static readonly ConditionalWeakTable<PdfDictionary, object> Reported =
            new ConditionalWeakTable<PdfDictionary, object>();

        private static readonly object ReportedMarker = new object();

        /// <summary>
        /// The default space <paramref name="resources"/> substitutes for <paramref name="device"/> (a
        /// device family name, or an inline image's abbreviation), resolved; <see langword="null"/> when
        /// none is declared, or when the one declared cannot be a default (reported).
        /// </summary>
        internal static PdfObject For(string device, PdfDictionary resources, PdfDocument doc)
        {
            if (resources == null || doc == null || device == null) return null;
            string family = PdfColorSpaceNames.Expand(device);
            PdfName key = family == PdfColorSpaceNames.DeviceRGB ? PdfName.DefaultRGB
                : family == PdfColorSpaceNames.DeviceCMYK ? PdfName.DefaultCMYK
                : family == PdfColorSpaceNames.DeviceGray ? PdfName.DefaultGray
                : null;
            if (key == null) return null;

            var spaces = doc.ResolveObject(resources[PdfName.ColorSpace]) as PdfDictionary;
            var declared = spaces?[key];
            if (declared == null) return null;
            var substitute = doc.ResolveObject(declared) ?? declared;

            var space = PdfColor.ResolveSpace(substitute, doc);
            int arity = PdfColorSpaceNames.ComponentCount(family, PdfColorSpaceNames.RgbComponents);
            string refusal =
                space.Family == null ? "is not a colour space"
                : space.Family == PdfColorSpaceNames.Lab || space.Family == PdfColorSpaceNames.Indexed
                  || space.Family == PdfColorSpaceNames.Pattern ? $"is a {space.Family} space, which a default may not be"
                : space.Components != arity ? $"has {space.Components} components where {family} has {arity}"
                : null;
            if (refusal == null) return substitute;

            // GetValue's factory runs once per resource dictionary, so the refusal is reported once.
            Reported.GetValue(spaces, _ =>
            {
                RenderDiagnostics.Report(RenderDiagnosticKind.RenderParameterIgnored,
                    $"/{key.Value} {refusal} (ISO 32000-2 §8.6.5.6); {family} is painted as written.");
                return ReportedMarker;
            });
            return null;
        }

        /// <summary>
        /// <paramref name="space"/> with every device space it selects remapped through
        /// <paramref name="resources"/>' defaults: a device name itself, or an Indexed or Pattern
        /// space's base (slot 1), or a Separation or DeviceN alternate (slot 2), as a copy; the space
        /// itself when nothing applies.
        /// </summary>
        internal static PdfObject Remap(PdfObject space, PdfDictionary resources, PdfDocument doc)
        {
            if (space == null || resources == null || doc == null) return space;
            var resolved = doc.ResolveObject(space) ?? space;
            if (resolved is PdfName name)
                return For(name.Value, resources, doc) ?? space;

            if (!(resolved is PdfArray array) || array.Count < 2 || !(array[0] is PdfName head)) return space;
            string family = PdfColorSpaceNames.Expand(head.Value);
            int slot = family == PdfColorSpaceNames.Indexed || family == PdfColorSpaceNames.Pattern ? 1
                : family == PdfColorSpaceNames.Separation || family == PdfColorSpaceNames.DeviceN ? 2
                : -1;
            if (slot < 0 || slot >= array.Count) return space;
            if (!((doc.ResolveObject(array[slot]) ?? array[slot]) is PdfName baseName)) return space;
            var substitute = For(baseName.Value, resources, doc);
            if (substitute == null) return space;

            var copy = new PdfArray();
            for (int i = 0; i < array.Count; i++) copy.Add(i == slot ? substitute : array[i]);
            return copy;
        }

        /// <summary>
        /// <paramref name="dict"/> — an image's or a shading's — with its <c>/ColorSpace</c> remapped
        /// (<see cref="Remap"/>), as a shallow copy (an image stream with its data); the dictionary
        /// itself when nothing applies, so a caller can tell by identity.
        /// </summary>
        internal static T WithDefaults<T>(T dict, PdfDictionary resources, PdfDocument doc) where T : PdfDictionary
        {
            if (dict == null || resources == null || doc == null) return dict;
            var space = dict[PdfName.ColorSpace];
            var remapped = Remap(space, resources, doc);
            if (ReferenceEquals(remapped, space)) return dict;
            var copy = (T)PdfDictionary.CloneShallow(dict);
            copy.Set(PdfName.ColorSpace, remapped);
            return copy;
        }
    }
}
