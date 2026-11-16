// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;

namespace Tetitulz.Pdf.Core
{
    public class PdfException : Exception
    {
        public PdfException() { }
        public PdfException(string message) : base(message) { }
        public PdfException(string message, Exception innerException) : base(message, innerException) { }
    }

    public class PdfParseException : PdfException
    {
        public long Position { get; }

        public PdfParseException(string message) : base(message) { }
        public PdfParseException(string message, long position) : base($"{message} at position {position}")
        {
            Position = position;
        }
        public PdfParseException(string message, Exception innerException) : base(message, innerException) { }
    }

    public class PdfSigningException : PdfException
    {
        public PdfSigningException(string message) : base(message) { }
        public PdfSigningException(string message, Exception innerException) : base(message, innerException) { }
    }

    public class PdfVerificationException : PdfException
    {
        public PdfVerificationException(string message) : base(message) { }
        public PdfVerificationException(string message, Exception innerException) : base(message, innerException) { }
    }

    /// <summary>
    /// Thrown when registering a plugin whose <c>Name</c> is already taken by another
    /// registered plugin.
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="InvalidOperationException"/> on purpose:
    /// <c>PdfPluginHost.RegisterFromAssembly</c> skips duplicates silently while scanning, so with
    /// <c>InvalidOperationException</c> as the test, a plugin whose <c>Initialize()</c> threw
    /// <c>InvalidOperationException</c> (the natural choice for "configuration is invalid") would
    /// be misdiagnosed as a duplicate and dropped with no diagnostic at all.
    /// </remarks>
    public class DuplicatePluginException : PdfException
    {
        /// <summary>The plugin name that was already registered.</summary>
        public string PluginName { get; }

        public DuplicatePluginException(string pluginName)
            : base($"A plugin named \"{pluginName}\" is already registered.")
        {
            PluginName = pluginName;
        }
    }

    /// <summary>
    /// Thrown by whole-file rewriting operations (sanitize / optimize / linearize) when the
    /// input document is encrypted.
    /// </summary>
    /// <remarks>
    /// These operations renumber objects. Per-object encryption keys are derived from the
    /// object number (PDF 32000-1 §7.6.2), so renumbering while copying the trailer's
    /// <c>/Encrypt</c> dictionary verbatim silently produces a file whose streams and strings
    /// can no longer be decrypted. Failing closed is the only safe behaviour: decrypt the
    /// document first, run the operation, then re-encrypt if required.
    /// </remarks>
    public class PdfEncryptedDocumentException : PdfException
    {
        public PdfEncryptedDocumentException(string message) : base(message) { }
        public PdfEncryptedDocumentException(string message, Exception innerException) : base(message, innerException) { }
    }
}
