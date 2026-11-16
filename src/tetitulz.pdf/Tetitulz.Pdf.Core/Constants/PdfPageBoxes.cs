// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Core.Constants
{
    /// <summary>The five page boundary boxes (ISO 32000-1 §14.11.2, Table 30).</summary>
    public enum PdfPageBoxKind
    {
        /// <summary>The sheet the page is imposed on — the only one that is required.</summary>
        MediaBox,
        /// <summary>The visible region. Defaults to the <c>/MediaBox</c>.</summary>
        CropBox,
        /// <summary>Where the page is clipped when produced with a bleed.</summary>
        BleedBox,
        /// <summary>The intended finished size after trimming.</summary>
        TrimBox,
        /// <summary>The extent of the page's meaningful content.</summary>
        ArtBox,
    }

    /// <summary>
    /// The <see cref="PdfPageBoxKind"/> ↔ <see cref="PdfName"/> mapping, in one place.
    /// </summary>
    /// <remarks>
    /// <para>A leaf by construction — no static fields, so no type initializer (the hazard CLAUDE.md
    /// records, where a sideways static reference crashed the Base test host mid-run). The
    /// <c>PdfName</c>s it returns are the cached ones on <see cref="PdfName"/>, not new
    /// allocations, so <c>NoRedundantPdfNameAllocationTests</c> stays satisfied.</para>
    ///
    /// <para>Here rather than in Base because the render path needs the same string→box decision
    /// (<c>PdfPageRenderer</c> resolves a <c>/ViewerPreferences</c> box name) and Base's writers
    /// need the enum→name one. Two tables of five names is exactly the kind of duplication this
    /// repository keeps re-finding.</para>
    /// </remarks>
    public static class PdfPageBoxes
    {
        /// <summary>The dictionary key a box kind is stored under.</summary>
        /// <param name="kind">The box.</param>
        /// <returns>The cached <see cref="PdfName"/>; <see cref="PdfName.MediaBox"/> for an
        /// out-of-range value, which is the one box §14.11.2 makes required.</returns>
        public static PdfName ToName(PdfPageBoxKind kind)
        {
            switch (kind)
            {
                case PdfPageBoxKind.CropBox:  return PdfName.CropBox;
                case PdfPageBoxKind.BleedBox: return PdfName.BleedBox;
                case PdfPageBoxKind.TrimBox:  return PdfName.TrimBox;
                case PdfPageBoxKind.ArtBox:   return PdfName.ArtBox;
                default:                      return PdfName.MediaBox;
            }
        }

        /// <summary>
        /// Parses a box name as written in a PDF — with or without the leading solidus, and
        /// case-sensitively, because PDF names are case-sensitive (§7.3.5).
        /// </summary>
        /// <param name="name">e.g. <c>"TrimBox"</c> or <c>"/TrimBox"</c>.</param>
        /// <param name="kind">The parsed box.</param>
        /// <returns>False when <paramref name="name"/> names no page box.</returns>
        public static bool TryParse(string name, out PdfPageBoxKind kind)
        {
            kind = PdfPageBoxKind.MediaBox;
            if (string.IsNullOrEmpty(name)) return false;
            string bare = name[0] == '/' ? name.Substring(1) : name;

            switch (bare)
            {
                case "MediaBox": kind = PdfPageBoxKind.MediaBox; return true;
                case "CropBox":  kind = PdfPageBoxKind.CropBox;  return true;
                case "BleedBox": kind = PdfPageBoxKind.BleedBox; return true;
                case "TrimBox":  kind = PdfPageBoxKind.TrimBox;  return true;
                case "ArtBox":   kind = PdfPageBoxKind.ArtBox;   return true;
                default: return false;
            }
        }

        /// <summary>
        /// The four boxes §14.11.2 requires to lie within the <c>/MediaBox</c> — i.e. every kind
        /// except <see cref="PdfPageBoxKind.MediaBox"/> itself.
        /// </summary>
        public static bool IsContainedInMediaBox(PdfPageBoxKind kind)
            => kind != PdfPageBoxKind.MediaBox;
    }
}
