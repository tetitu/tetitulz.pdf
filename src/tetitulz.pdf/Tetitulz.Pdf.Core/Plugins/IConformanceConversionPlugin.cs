// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.IO;

namespace Tetitulz.Pdf.Core.Plugins
{
    /// <summary>
    /// Converts a document towards a conformance level — PDF/A-2b, PDF/X-4 and so on — for a caller
    /// holding only <c>PdfOperations</c>.
    /// </summary>
    /// <remarks>
    /// <para><b>The mirror of <see cref="IValidationPlugin"/>, and it exists for the same reason.</b>
    /// The converters live in a plugin that references <c>Base</c>, so <c>Base</c> cannot reference
    /// them back; dispatch through <c>PdfPluginHost</c> is the only direction that does not close a
    /// cycle. Until this contract existed the converters were reachable only by taking a direct
    /// dependency on the Conformance package and naming <c>PdfAConverter</c> by hand — which works,
    /// and which nothing in the facade could tell you about.</para>
    ///
    /// <para><b>A conversion is not a promise of conformance.</b> Some defects cannot be repaired
    /// without deciding what the document means — a missing output intent can be supplied, a
    /// non-embeddable font cannot — so the result reports what it could not resolve rather than
    /// failing or, worse, writing a file that claims a level it does not meet. That is why
    /// <see cref="PluginConversionResult.IsConformant"/> and
    /// <see cref="PluginConversionResult.OutputWritten"/> are separate answers.</para>
    /// </remarks>
    public interface IConformanceConversionPlugin : IPdfPlugin
    {
        /// <summary>
        /// The level this plugin converts <i>to</i>, spelled as the standard names it: "PDF/A-1b",
        /// "PDF/A-2b", "PDF/A-3b", "PDF/X-1a", "PDF/X-3", "PDF/X-4". Matched case-insensitively.
        /// </summary>
        /// <remarks>
        /// Deliberately the same vocabulary as <see cref="IValidationPlugin.ConformanceLevel"/>, so
        /// a caller can convert to a level and then validate the result by passing the same string
        /// to both.
        /// </remarks>
        string TargetConformanceLevel { get; }

        /// <summary>
        /// Converts <paramref name="input"/> and writes the result to <paramref name="output"/>.
        /// Neither stream is disposed.
        /// </summary>
        PluginConversionResult Convert(Stream input, Stream output);
    }

    /// <summary>What a conversion did, and what it could not do.</summary>
    public sealed class PluginConversionResult
    {
        /// <summary>The level that was aimed at, as the plugin spells it.</summary>
        public string TargetConformanceLevel { get; set; } = string.Empty;

        /// <summary>
        /// Whether anything was written to the output stream. <c>false</c> means the input was
        /// refused outright — the output is untouched and <see cref="Blockers"/> says why.
        /// </summary>
        public bool OutputWritten { get; set; }

        /// <summary>
        /// Whether the written output is believed to meet the level. <b>Never infer this from
        /// <see cref="OutputWritten"/>:</b> a converter may write a usable, improved file that still
        /// falls short, and reporting that as success is the failure this pair exists to prevent.
        /// </summary>
        public bool IsConformant { get; set; }

        /// <summary>
        /// What stopped the document reaching the level — a font that cannot be embedded, an
        /// unconvertible colour space. Empty when <see cref="IsConformant"/>.
        /// </summary>
        public IList<string> Blockers { get; } = new List<string>();

        /// <summary>What the conversion changed or approximated, for a caller who needs to know.</summary>
        public IList<string> Warnings { get; } = new List<string>();
    }
}
