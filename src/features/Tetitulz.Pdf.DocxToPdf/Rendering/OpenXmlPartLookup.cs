// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;

namespace Tetitulz.Pdf.DocxToPdf.Rendering
{
    /// <summary>
    /// Resolves an <c>r:id</c> carried by a drawing against the part that actually owns it.
    ///
    /// <para>
    /// <b>Relationship ids are numbered per part, not per document.</b> A picture, chart or diagram
    /// in a header, a footer, a footnote or a nested text box hangs off <i>that</i> part's
    /// relationships, so the same <c>rId9</c> routinely names one thing there and something else
    /// entirely in the main document. Resolving against <c>MainDocumentPart</c> alone therefore has
    /// three failure modes, all of them silent-to-wrong: <c>GetPartById</c> throws for an id the
    /// main document does not own, the id names a part of the wrong type and the graphic is
    /// dropped, or — worst — it names a <i>different</i> valid image and that one gets painted.
    /// </para>
    ///
    /// <para>
    /// This is the single implementation for that decision. It existed three times over (the
    /// picture, chart and SmartArt paths) before being promoted here; a fourth copy is how the
    /// three drifted in the first place.
    /// </para>
    /// </summary>
    internal static class OpenXmlPartLookup
    {
        /// <summary>
        /// The part whose relationships an element's <c>r:id</c> references — found by walking up
        /// to the element's part root. Returns <c>null</c> for an element that was constructed
        /// detached rather than loaded from a part, which is the caller's cue to fall back to
        /// <c>MainDocumentPart</c>.
        /// </summary>
        internal static OpenXmlPart OwningPart(OpenXmlElement element)
        {
            var node = element;
            while (node != null)
            {
                if (node is OpenXmlPartRootElement root) return root.OpenXmlPart;
                node = node.Parent;
            }
            return null;
        }

        /// <summary>
        /// <c>GetPartById</c> without the throw. An id the part does not own is an ordinary
        /// outcome here — callers legitimately try more than one owner, and a producer may write
        /// a stale id — so it is reported as <c>null</c> rather than as an exception that would
        /// abort the whole conversion.
        /// </summary>
        internal static OpenXmlPart TryGetPart(OpenXmlPart owner, string relationshipId)
        {
            if (owner == null || string.IsNullOrEmpty(relationshipId)) return null;
            try
            {
                return owner.GetPartById(relationshipId);
            }
            catch (ArgumentOutOfRangeException)
            {
                return null;
            }
        }

        /// <summary>
        /// Resolves <paramref name="relationshipId"/> against the part owning
        /// <paramref name="element"/>, falling back to <paramref name="fallbackOwner"/> (normally
        /// the main document) when the element is detached from any part.
        /// </summary>
        internal static OpenXmlPart Resolve(OpenXmlElement element, string relationshipId,
            OpenXmlPart fallbackOwner)
            => TryGetPart(OwningPart(element) ?? fallbackOwner, relationshipId);
    }
}
