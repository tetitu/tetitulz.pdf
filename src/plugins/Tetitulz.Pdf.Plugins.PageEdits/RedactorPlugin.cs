// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System;
using System.Collections.Generic;
using System.IO;
using Tetitulz.Pdf.Core.Plugins;

namespace Tetitulz.Pdf.Plugins.PageEdits
{
    /// <summary>
    /// <see cref="IRedactPlugin"/> that performs **true, data-removing redaction** through
    /// <see cref="PdfRedactor.RedactRegions"/>: the glyphs and image samples inside each region
    /// are physically removed, intersecting annotations are dropped, and the result is written as
    /// a full single-revision PDF so the removed data survives in no earlier revision.
    ///
    /// Registration:
    /// <code>
    ///   PdfPluginHost.Instance.Register(new RedactorPlugin());
    /// </code>
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Removal, not a cover.</b> Every name on the path — the contract
    /// <see cref="IRedactPlugin"/>, this method, <see cref="RedactionRegion"/>, and the
    /// plugin id <c>Tetitulz.Plugins.Redactor</c> — says redaction, and a caller acts on the name;
    /// <see cref="PdfRedactor.CoverRegions"/>, which only draws an opaque box and leaves the text
    /// underneath extractable, would be a confident-looking leak behind it.
    /// </para>
    /// <para>
    /// <b>It fails closed where removal cannot be proved.</b> <see cref="Redact"/> throws
    /// <c>PdfRedactionException</c> for an encrypted document, past the engine's depth and work
    /// ceilings, and for a region meeting a tiling pattern or soft mask that draws text, or a paint
    /// through one that draws an image. <b>A Form XObject the named page shares with a page not
    /// named is not refused:</b> it is cloned at the named page and redacted there, and its original —
    /// what the region held included — stays in the output for the other page;
    /// <c>PdfRedactionEngine.LastSharedFormWarning</c> (Base) names it, and a caller that must not
    /// keep it names every page that draws the form. A caller that genuinely wants a drawn box calls
    /// <see cref="PdfRedactor.CoverRegions"/> directly, which is named for what it does.
    /// </para>
    /// </remarks>
    public sealed class RedactorPlugin : IRedactPlugin
    {
        public string Name        => "Tetitulz.Plugins.Redactor";
        public string Description => "Redacts PDF page regions, physically removing the text and image data inside them.";
        public Version Version    => new Version(1, 1, 0);

        public void Initialize() { }
        public void Shutdown()   { }

        /// <summary>
        /// Removes the content inside each region. See the type remarks: where removal cannot be proved
        /// it throws, and a form shared with a page not named keeps its original for that page.
        /// </summary>
        /// <exception cref="Tetitulz.Pdf.Base.Operations.Documents.PdfRedactionException">
        /// The content could not be removed in a way that is provably leak-free.
        /// </exception>
        public void Redact(Stream input, Stream output, IReadOnlyList<RedactionRegion> regions)
        {
            if (input == null)   throw new ArgumentNullException(nameof(input));
            if (output == null)  throw new ArgumentNullException(nameof(output));
            if (regions == null) throw new ArgumentNullException(nameof(regions));

            PdfRedactor.RedactRegions(input, output, regions);
        }
    }
}
