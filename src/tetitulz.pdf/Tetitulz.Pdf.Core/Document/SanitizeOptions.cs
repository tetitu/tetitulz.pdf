// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

namespace Tetitulz.Pdf.Core.Document
{
    /// <summary>
    /// Options for PDF sanitization - removing potentially dangerous content.
    /// </summary>
    public sealed class SanitizeOptions
    {
        /// <summary>Remove all JavaScript actions (/JS, /JavaScript).</summary>
        public bool RemoveJavaScript { get; set; } = true;

        /// <summary>
        /// Remove embedded files: the catalog's <c>/EmbeddedFiles</c> and <c>/AF</c>, and every file
        /// specification's <c>/EF</c> and <c>/RF</c> payload wherever it stands, an annotation, a page,
        /// a form or a structure element.
        /// </summary>
        public bool RemoveEmbeddedFiles { get; set; } = true;

        /// <summary>Remove form submit actions (/SubmitForm).</summary>
        public bool RemoveFormActions { get; set; } = true;

        /// <summary>Remove launch actions (/Launch) that could execute programs.</summary>
        public bool RemoveLaunchActions { get; set; } = true;

        /// <summary>Remove URI actions.</summary>
        public bool RemoveURIActions { get; set; } = false;

        /// <summary>Remove the document's open action (/OpenAction).</summary>
        public bool RemoveOpenAction { get; set; } = true;

        /// <summary>
        /// Remove XFA (XML Forms Architecture) dynamic form data from /AcroForm.
        /// XFA forms can execute arbitrary script and are forbidden in PDF/A.
        /// </summary>
        public bool RemoveXfa { get; set; } = true;

        /// <summary>
        /// Remove the annotations that play media from pages — <c>/RichMedia</c> (embedded
        /// Flash/multimedia, which can execute arbitrary script), <c>/Screen</c>, <c>/Movie</c> and
        /// <c>/Sound</c>, each of which names a clip, embedded or at a remote address — and the actions
        /// that play or drive one (<c>/Rendition</c>, <c>/Movie</c>, <c>/Sound</c>,
        /// <c>/RichMediaExecute</c>, <c>/GoTo3DView</c>).
        /// </summary>
        public bool RemoveRichMediaAnnotations { get; set; } = true;

        /// <summary>
        /// Remove <c>/3D</c> annotations (embedded U3D/PRC artwork). Their <c>/3DD</c> activation
        /// dictionary carries an <c>/OnInstantiate</c> script stream (ISO 32000-2 §13.6.3), so a 3D
        /// annotation is a script carrier in the same way a <c>/RichMedia</c> one is, and is dropped
        /// by the same sweep. On by default.
        /// </summary>
        public bool Remove3DAnnotations { get; set; } = true;

        /// <summary>
        /// Remove actions that fetch or launch a resource outside this document — <c>/GoToR</c>,
        /// <c>/GoToE</c>, <c>/ImportData</c> and <c>/Thread</c> whose target is a file. A remote
        /// target is a server-side-request and credential-leak route (a UNC path makes the viewer
        /// authenticate to the attacker's host), which a sanitizer is expected to close. A stream whose
        /// data is elsewhere, a reference XObject and a URL file specification go with them. On by
        /// default; it does not touch in-document navigation (<c>/GoTo</c>).
        /// </summary>
        public bool RemoveRemoteGoToActions { get; set; } = true;

        /// <summary>
        /// Even when <see cref="RemoveURIActions"/> is off, drop a <c>/URI</c> action whose scheme is
        /// not <c>http</c>, <c>https</c> or <c>mailto</c>. An ordinary web link survives; a
        /// <c>file:</c> URL or a UNC path (the same leak route as <see cref="RemoveRemoteGoToActions"/>)
        /// does not, written with or without a scheme, nor does a catalog <c>/URI /Base</c> of that kind,
        /// against which a relative link resolves. On by default; ignored when
        /// <see cref="RemoveURIActions"/> removes every URI action anyway.
        /// </summary>
        public bool RemoveNonWebUriActions { get; set; } = true;

        /// <summary>
        /// Remove every <c>/AA</c> (additional-actions) entry wholesale, not only those holding a
        /// dangerous action type. Off by default, because a benign <c>/AA</c> trigger (a page's
        /// <c>/O</c> running an in-document <c>/GoTo</c>) is legitimate content a general sanitize
        /// keeps. The PDF/A and PDF/X converters turn it on: ISO 19005 and ISO 15930 forbid the
        /// <c>/AA</c> key itself on a catalog, a page and most annotations whatever it contains.
        /// </summary>
        public bool RemoveAllAdditionalActions { get; set; }

        /// <summary>
        /// Sanitize a signed document anyway. Off by default: sanitizing rewrites every object, so a
        /// signature's <c>/ByteRange</c> would name offsets that no longer exist, and the output
        /// would still look signed while verifying as corrupt.
        /// </summary>
        public bool AllowSignatureInvalidation { get; set; }

        /// <summary>Default sanitization options (removes all dangerous content).</summary>
        public static SanitizeOptions Default => new SanitizeOptions();
    }
}
