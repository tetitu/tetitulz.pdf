// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Core.Document
{
    /// <summary>
    /// Viewer-preference flags from the document catalog's /ViewerPreferences dict
    /// (ISO 32000-1 §12.2).  These are hints the PDF viewer may honour — things
    /// like "hide toolbars on open", "centre window", or "print scaling".  All
    /// properties are nullable so callers can distinguish "absent" from "false".
    /// </summary>
    public sealed class PdfViewerPreferences
    {
        private readonly PdfDictionary _dict;
        private readonly Func<PdfObject, PdfObject> _resolve;

        /// <remarks>
        /// <para><b>The document is carried so the entries can be resolved.</b> This type held only
        /// the dictionary, and <c>PdfDictionary</c>'s indexer does not follow an indirect
        /// reference — so <c>/ViewerPreferences</c> was resolved by <c>PdfCatalog</c> and then
        /// every value inside it read as absent. §7.3.10 permits any of them to be indirect.</para>
        ///
        /// <para>It is not a cosmetic loss: <c>/PrintArea</c> and <c>/ViewArea</c> feed
        /// <c>PdfPageRenderer.ResolveRenderBox</c>, the single page-box decision <b>both</b>
        /// renderers go through — so an indirect one silently fell back to the CropBox default in
        /// the raster path and the SVG export alike. The two agree, which is exactly why the parity
        /// suite cannot see it.</para>
        /// </remarks>
        internal PdfViewerPreferences(PdfDictionary dict, PdfDocument document = null)
            : this(dict, document != null ? document.ResolveObject : (Func<PdfObject, PdfObject>)null)
        {
        }

        private PdfViewerPreferences(PdfDictionary dict, Func<PdfObject, PdfObject> resolve)
        {
            _dict = dict;
            _resolve = resolve;
        }

        /// <summary>
        /// The <c>/ViewerPreferences</c> of <paramref name="catalog"/>, read through
        /// <paramref name="resolve"/>.
        /// </summary>
        /// <param name="catalog">The document catalog dictionary. May be null.</param>
        /// <param name="resolve">
        /// Follows an indirect reference. Null means "values are direct".
        /// </param>
        /// <returns>
        /// A wrapper — never null. Its <see cref="Dictionary"/> is null when the catalog carries no
        /// <c>/ViewerPreferences</c>, which callers use to tell "no preferences dictionary at all"
        /// from "no such preference", two different findings for a conformance checker.
        /// </returns>
        /// <remarks>
        /// <para><b>It takes a resolve delegate rather than a <see cref="PdfDocument"/>, for the
        /// reason <see cref="PdfRoleMap"/> states</b>: an encrypted document loads no catalog, so
        /// <c>doc.Catalog</c> is null and a checker that has reached the catalog dictionary by hand
        /// must be able to supply its own resolver. That is exactly why three of them —
        /// <c>Plugins.Conformance.PdfUaChecker</c> (UA04), <c>Accessibility</c>'s deep checker and
        /// its PDF/UA-2 structure checker — each read <c>/ViewerPreferences /DisplayDocTitle</c> by
        /// hand: the only door was <see cref="PdfCatalog.GetViewerPreferences"/>, which needs a
        /// catalog object none of them has.</para>
        ///
        /// <para>Sharing the read follows the precedent CLAUDE.md records for <c>/P</c>: where the
        /// two accessibility halves compute a check independently and merge the results, the single
        /// step that could make them disagree is shared rather than mirrored.</para>
        /// </remarks>
        public static PdfViewerPreferences Read(PdfDictionary catalog, Func<PdfObject, PdfObject> resolve)
        {
            PdfObject raw = catalog?[PdfName.ViewerPreferences];
            PdfObject prefs = raw != null && resolve != null ? resolve(raw) ?? raw : raw;
            return new PdfViewerPreferences(prefs as PdfDictionary, resolve);
        }

        /// <summary>Raw backing dictionary; null when the catalog has no /ViewerPreferences entry.</summary>
        public PdfDictionary Dictionary => _dict;

        public bool?   HideToolbar          => ReadBool("HideToolbar");
        public bool?   HideMenubar          => ReadBool("HideMenubar");
        public bool?   HideWindowUI         => ReadBool("HideWindowUI");
        public bool?   FitWindow            => ReadBool("FitWindow");
        public bool?   CenterWindow         => ReadBool("CenterWindow");
        public bool?   DisplayDocTitle      => ReadBool("DisplayDocTitle");
        public bool?   PickTrayByPDFSize    => ReadBool("PickTrayByPDFSize");

        /// <summary>/NonFullScreenPageMode — UseNone, UseOutlines, UseThumbs, UseOC.</summary>
        public string  NonFullScreenPageMode => ReadName("NonFullScreenPageMode");

        /// <summary>/Direction — L2R (default) or R2L.</summary>
        public string  Direction             => ReadName("Direction");

        /// <summary>/ViewArea, /ViewClip, /PrintArea, /PrintClip — "MediaBox", "CropBox", etc.</summary>
        public string  ViewArea              => ReadName("ViewArea");
        public string  ViewClip              => ReadName("ViewClip");
        public string  PrintArea             => ReadName("PrintArea");
        public string  PrintClip             => ReadName("PrintClip");

        /// <summary>/PrintScaling — None or AppDefault.</summary>
        public string  PrintScaling          => ReadName("PrintScaling");

        /// <summary>/Duplex — Simplex, DuplexFlipShortEdge, DuplexFlipLongEdge.</summary>
        public string  Duplex                => ReadName("Duplex");

        /// <summary>/NumCopies — default 1. An integer (Table 150), saturated to <see cref="int"/>; anything else reads as absent.</summary>
        /// <remarks>
        /// It was <c>(int)LongValue</c> whatever the number: a real such as 2.5 is not a
        /// <see cref="long"/>, and a value past <see cref="int.MaxValue"/> kept its low 32 bits — a
        /// file asking for 4 294 967 297 copies read as 1.
        /// </remarks>
        public int?    NumCopies
            => _dict != null && Resolve(_dict[PdfName.NumCopies]) is PdfNumber n && n.IsInteger
               ? (int?)n.IntValue : null;

        /// <summary>/PrintPageRange — array of integers.</summary>
        public PdfArray PrintPageRange
            => Resolve(_dict?[PdfName.PrintPageRange]) as PdfArray;

        // PdfDictionary's string-keyed indexer: the backing store is keyed by the raw name
        // string, so a constant key needs no PdfName wrapper allocated around it.
        private bool? ReadBool(string key)
        {
            if (_dict == null) return null;
            return Resolve(_dict[key]) is PdfBoolean b ? (bool?)b.Value : null;
        }

        private string ReadName(string key)
        {
            if (_dict == null) return null;
            return (Resolve(_dict[key]) as PdfName)?.Value;
        }

        /// <summary>
        /// Follows an indirect reference when a document is available. Null-tolerant on both
        /// counts: the wrapper is constructed with a null dictionary for a catalog that carries no
        /// preferences, and the document-less overload stays valid for a caller holding only a
        /// dictionary.
        /// </summary>
        private PdfObject Resolve(PdfObject value)
            => value is PdfReference && _resolve != null ? _resolve(value) : value;
    }
}
