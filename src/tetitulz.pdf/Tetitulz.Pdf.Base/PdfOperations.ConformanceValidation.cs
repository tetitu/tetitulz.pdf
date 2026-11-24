// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Tetitulz.Pdf.Base.Images;
using Tetitulz.Pdf.Base.Layout;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.Base.Scripting;
using Tetitulz.Pdf.Base.Signing;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Forms;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Plugins;
using Tetitulz.Pdf.Core.Text;
using Tetitulz.Pdf.Core.Writing;

namespace Tetitulz.Pdf.Base
{
    public static partial class PdfOperations
    {
        /// <summary>
        /// Validates an <b>encrypted</b> document against a conformance level.
        /// </summary>
        /// <param name="password">User or owner password.</param>
        /// <remarks>
        /// <para>A separate overload with a <b>required</b> password: appending
        /// <c>password = null</c> to the signature above would be a binary break, and the
        /// password-free overload keeps its own call path.</para>
        /// <para><b>The password does not cross the plugin boundary — the decrypted stream does.</b>
        /// The dispatch passes a <c>Stream</c>, so <c>WithDecrypted</c> hands the plugin plaintext
        /// and the plugin contract carries no password. It is the
        /// same inversion used everywhere else here — the <i>operation</i> or the <i>data</i>
        /// travels, never the key.</para>
        /// </remarks>
        /// <param name="input">The PDF stream. Not disposed.</param>
        /// <param name="conformanceLevel">The level to validate against, e.g. <c>"PDF/X-1a"</c>.</param>
        public static PluginValidationResult Validate(
            Stream input, string conformanceLevel, string password)
            => WithDecrypted(input, password, s => Validate(s, conformanceLevel));

        /// <summary>
        /// Validates <paramref name="input"/> against a registered conformance level — "PDF/X-1a",
        /// "PDF/UA-1" and so on — by dispatching to the matching <see cref="IValidationPlugin"/>.
        /// </summary>
        /// <param name="input">The PDF stream. Not disposed.</param>
        /// <param name="conformanceLevel">
        /// The level to validate, matched case-insensitively against each plugin's
        /// <see cref="IValidationPlugin.ConformanceLevel"/>. Use
        /// <see cref="GetSupportedConformanceLevels"/> to discover what is registered.
        /// </param>
        /// <returns>
        /// The plugin's result, or <c>null</c> when no plugin claims that level — which is the
        /// ordinary case, because the validators ship as separate assemblies and are opt-in.
        /// <see cref="LastValidationError"/> then says which levels *were* available, so "no
        /// plugin registered" is never mistaken for "the document is fine".
        /// <para><b>A conformant result is not automatically a full pass.</b> Content-level checks
        /// are bounded, and a stream may fail to decode; a check that could not run reports no
        /// violation, so <c>IsConformant == true</c> with an empty
        /// <see cref="PluginValidationResult.Violations"/> list can also mean "nothing was
        /// examined". Test <see cref="PluginValidationResult.IsPartial"/> and read
        /// <see cref="PluginValidationResult.Informational"/> for which checks were skipped and
        /// why.</para>
        /// </returns>
        /// <remarks>
        /// <para><b>Why this dispatches instead of calling a validator directly.</b> The PDF/X and
        /// PDF/UA engines live in <c>Tetitulz.Pdf.Plugins.Conformance</c>, which references
        /// <c>Base</c>. Base referencing it back would be a cycle, so the facade reaches them the
        /// only way the architecture permits: through the plugin host. Register with
        /// <c>PdfXValidationPlugin.RegisterAll(PdfPluginHost.Instance)</c>.</para>
        /// </remarks>
        public static PluginValidationResult Validate(Stream input, string conformanceLevel)
        {
            _lastValidationError = null;
            if (input == null) throw new ArgumentNullException(nameof(input));
            if (string.IsNullOrWhiteSpace(conformanceLevel))
                throw new ArgumentException("A conformance level is required.", nameof(conformanceLevel));

            var available = new List<string>();
            foreach (var validator in PdfPluginHost.Instance.GetValidationPlugins())
            {
                available.Add(validator.ConformanceLevel);

                if (string.Equals(validator.ConformanceLevel, conformanceLevel,
                        StringComparison.OrdinalIgnoreCase))
                {
                    // EnsureSeekable copies a non-seekable input into a fresh MemoryStream, and
                    // passing that inline abandoned it: a service validating request bodies leaked
                    // one full-document copy per call to the large-object heap. Same shape as the
                    // batch-render leak PdfPageRenderer.BufferPdf was extracted to end.
                    Stream seekable = Tetitulz.Pdf.Core.IO.SeekableStream.Ensure(input, rewind: true, out bool ownsSeekable);
                    try { return validator.Validate(seekable); }
                    finally { if (ownsSeekable) seekable.Dispose(); }
                }
            }

            _lastValidationError = available.Count == 0
                ? $"No validation plugin is registered, so '{conformanceLevel}' could not be checked. " +
                  "Register one (for example PdfXValidationPlugin.RegisterAll(PdfPluginHost.Instance))."
                : $"No validation plugin claims '{conformanceLevel}'. Registered levels: " +
                  string.Join(", ", available) + ".";
            return null;
        }

        /// <summary>Conformance levels that a registered <see cref="IValidationPlugin"/> can check.</summary>
        public static IReadOnlyList<string> GetSupportedConformanceLevels()
        {
            var levels = new List<string>();
            foreach (var validator in PdfPluginHost.Instance.GetValidationPlugins())
                levels.Add(validator.ConformanceLevel);
            return levels;
        }

        /// <summary>
        /// Why the most recent <see cref="Validate(Stream, string)"/> returned <c>null</c>, or
        /// <c>null</c> when it returned a result. Read it on the calling thread — a <c>null</c>
        /// result with no reason would be indistinguishable from a clean document.
        /// </summary>
        [ThreadStatic]
        private static string _lastValidationError;
        public static string LastValidationError => _lastValidationError;
    }
}
