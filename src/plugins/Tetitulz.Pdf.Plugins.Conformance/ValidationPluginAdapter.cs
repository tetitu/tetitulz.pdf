// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using Tetitulz.Pdf.Core.Plugins;

namespace Tetitulz.Pdf.Plugins.Conformance
{
    /// <summary>
    /// What the PDF/A, PDF/X and PDF/UA-1 validation plugins share: running their validator over
    /// the caller's stream and putting the stream back where it was, and carrying one finding into
    /// the host's model. Each plugin's routing (UA99 and A99/X99 to <c>Informational</c>, never
    /// touching <c>IsConformant</c>) stays its own.
    /// </summary>
    internal static class ValidationPluginAdapter
    {
        /// <summary>
        /// <paramref name="validate"/> over <paramref name="pdfStream"/>, the stream's position kept
        /// when it can seek; a null result is refused, naming <paramref name="validatorName"/>.
        /// </summary>
        internal static T RunAt<T>(Stream pdfStream, Func<Stream, T> validate, string validatorName) where T : class
        {
            if (pdfStream == null) throw new ArgumentNullException(nameof(pdfStream));

            long pos = pdfStream.CanSeek ? pdfStream.Position : -1;
            T inner = validate(pdfStream)
                ?? throw new InvalidOperationException(validatorName + " returned null.");
            if (pdfStream.CanSeek && pos >= 0) pdfStream.Position = pos;
            return inner;
        }

        /// <summary>One finding in the host's model, a null code or message written as empty.</summary>
        internal static PluginValidationViolation Entry(string code, string message)
            => new PluginValidationViolation { Code = code ?? string.Empty, Message = message ?? string.Empty };
    }
}
