// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using Tetitulz.Pdf.Core.Plugins;

namespace Tetitulz.Pdf.Base
{
    public static partial class PdfOperations
    {
        /// <summary>
        /// Converts <paramref name="input"/> towards a registered conformance level — "PDF/A-2b",
        /// "PDF/X-4" and so on — by dispatching to the matching
        /// <see cref="IConformanceConversionPlugin"/>.
        /// </summary>
        /// <param name="input">The PDF to convert. Not disposed.</param>
        /// <param name="output">Where the converted document is written. Not disposed.</param>
        /// <param name="targetConformanceLevel">
        /// The level to convert to, matched case-insensitively against each plugin's
        /// <see cref="IConformanceConversionPlugin.TargetConformanceLevel"/>. Use
        /// <see cref="GetSupportedConversionTargets"/> to discover what is registered.
        /// </param>
        /// <returns>
        /// The plugin's result, or <c>null</c> when no plugin claims that level — the ordinary case,
        /// because the converters ship as a separate assembly and are opt-in.
        /// <see cref="LastConversionError"/> then says which targets <i>were</i> available, so "no
        /// plugin registered" is never mistaken for "the document was converted".
        /// </returns>
        /// <remarks>
        /// <para><b>Read both answers.</b> <see cref="PluginConversionResult.OutputWritten"/> says a
        /// file came out; <see cref="PluginConversionResult.IsConformant"/> says it is believed to
        /// meet the level. They differ whenever a converter improved a document it could not fully
        /// repair — a font that cannot legally be embedded, a colour space with no conversion — and
        /// treating the first as the second is how a file ships claiming a level it does not meet.
        /// <see cref="PluginConversionResult.Blockers"/> names what stopped it.</para>
        ///
        /// <para><b>Why this dispatches instead of calling a converter directly.</b> The same reason
        /// <see cref="Validate(Stream, string)"/> does: <c>Tetitulz.Pdf.Plugins.Conformance</c>
        /// references <c>Base</c>, so Base referencing it back would close a cycle. Register with
        /// <c>ConformanceConversionPlugin.RegisterAll(PdfPluginHost.Instance)</c>.</para>
        ///
        /// <para><b>Converting does not validate.</b> The two vocabularies are deliberately the
        /// same, so the honest sequence is to convert to a level and then pass the same string to
        /// <see cref="Validate(Stream, string)"/> over the output — a converter's own verdict is its
        /// opinion of its own work, which CLAUDE.md's third testing blind spot is written about.</para>
        /// </remarks>
        public static PluginConversionResult ConvertToConformance(
            Stream input, Stream output, string targetConformanceLevel)
        {
            _lastConversionError = null;
            if (input == null) throw new ArgumentNullException(nameof(input));
            if (output == null) throw new ArgumentNullException(nameof(output));
            if (string.IsNullOrWhiteSpace(targetConformanceLevel))
                throw new ArgumentException("A target conformance level is required.",
                    nameof(targetConformanceLevel));

            var available = new List<string>();
            foreach (var converter in PdfPluginHost.Instance.GetConformanceConversionPlugins())
            {
                available.Add(converter.TargetConformanceLevel);

                if (!string.Equals(converter.TargetConformanceLevel, targetConformanceLevel,
                        StringComparison.OrdinalIgnoreCase))
                    continue;

                // Seekable, and disposed here when we made the copy: passing EnsureSeekable's result
                // inline would abandon a full-document MemoryStream per call on a non-seekable input.
                Stream seekable = Tetitulz.Pdf.Core.IO.SeekableStream.Ensure(
                    input, rewind: true, out bool ownsSeekable);
                try { return converter.Convert(seekable, output); }
                finally { if (ownsSeekable) seekable.Dispose(); }
            }

            _lastConversionError = available.Count == 0
                ? $"No conformance-conversion plugin is registered, so '{targetConformanceLevel}' " +
                  "could not be produced. Register one (for example " +
                  "ConformanceConversionPlugin.RegisterAll(PdfPluginHost.Instance))."
                : $"No conformance-conversion plugin claims '{targetConformanceLevel}'. " +
                  "Registered targets: " + string.Join(", ", available) + ".";
            return null;
        }

        /// <summary>
        /// As <see cref="ConvertToConformance(Stream, Stream, string)"/>, for an encrypted document
        /// opened with <paramref name="password"/>.
        /// </summary>
        public static PluginConversionResult ConvertToConformance(
            Stream input, Stream output, string targetConformanceLevel, string password)
            => WithDecrypted(input, password,
                   s => ConvertToConformance(s, output, targetConformanceLevel));

        /// <summary>
        /// Conformance levels a registered <see cref="IConformanceConversionPlugin"/> can produce.
        /// </summary>
        public static IReadOnlyList<string> GetSupportedConversionTargets()
        {
            var targets = new List<string>();
            foreach (var converter in PdfPluginHost.Instance.GetConformanceConversionPlugins())
                targets.Add(converter.TargetConformanceLevel);
            return targets;
        }

        /// <summary>
        /// Why the most recent <see cref="ConvertToConformance(Stream, Stream, string)"/> returned
        /// <c>null</c>, or <c>null</c> when it returned a result. Read it on the calling thread — a
        /// <c>null</c> result with no reason would be indistinguishable from a document that needed
        /// no conversion.
        /// </summary>
        public static string LastConversionError => _lastConversionError;

        [ThreadStatic] private static string _lastConversionError;
    }
}
