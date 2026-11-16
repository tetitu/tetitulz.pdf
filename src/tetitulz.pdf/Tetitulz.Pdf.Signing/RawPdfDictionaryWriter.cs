// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using Tetitulz.Pdf.Core;
using Tetitulz.Pdf.Core.Writing;

namespace Tetitulz.Pdf.Signing
{
    /// <summary>
    /// Writes the entries of a PDF dictionary straight to a <see cref="PdfWriter"/>, refusing to
    /// emit the same key twice.
    /// </summary>
    /// <remarks>
    /// <para><b>Why the signing writers emit raw bytes at all, rather than building a
    /// <c>PdfDictionary</c> and serialising it.</b> The signature dictionary is not a value that can
    /// be written and forgotten: <c>/ByteRange</c> and <c>/Contents</c> are placeholders whose
    /// <i>byte offsets in the output stream</i> are captured mid-write and overwritten afterwards,
    /// once the digest over the surrounding bytes is known. A serialiser would have to report those
    /// offsets back, and the byte-exactness of that patch is what the whole scheme rests on. So the
    /// raw writing stays; what it lacked was any structure to violate.</para>
    ///
    /// <para><b>What it lacked, concretely.</b> A bad edit emitted <c>/Location</c> twice, and
    /// <b>all 197 tests in the signing suite passed</b>: every one of them either parses the file —
    /// where the parser silently keeps one of the two — or verifies the signature, which is computed
    /// over a byte range and is entirely indifferent to what the dictionary says. ISO 32000-1
    /// §7.3.7 leaves a dictionary with a repeated key <b>undefined</b>, so the result is a file two
    /// conforming readers can legitimately disagree about while it looks valid to both.</para>
    ///
    /// <para><b>The hazard was already known and handled by hand</b>, which is the argument for
    /// making it structural: <c>PdfSigner</c>'s adopted-field copy skips <c>/V</c> while copying an
    /// existing field's entries, precisely so the <c>/V</c> it adds afterwards is not a duplicate.
    /// That is the correct behaviour arrived at by remembering. This type is the same rule arrived
    /// at by construction — and <c>SignatureDictionaryKeyUniquenessTests</c> is the check that the
    /// bytes really do come out unique, because a guard and its evidence are different things.</para>
    ///
    /// <para>Scope is deliberately one dictionary's <b>own</b> level. A nested dictionary
    /// (<c>/Reference</c>, <c>/TransformParams</c>) is a different entry set and is written through
    /// its own instance or as an opaque value; conflating the two would make a legitimately repeated
    /// <c>/Type</c> look like a collision.</para>
    /// </remarks>
    internal sealed class RawPdfDictionaryWriter
    {
        private readonly PdfWriter _writer;
        private readonly string _what;
        private readonly HashSet<string> _claimed = new HashSet<string>(StringComparer.Ordinal);

        /// <param name="writer">The writer to emit to. Nothing is buffered; entries go straight out.</param>
        /// <param name="what">
        /// How this dictionary is named in the exception, e.g. "the signature dictionary". A
        /// duplicate-key failure is a programming error in this assembly, so the message names the
        /// dictionary rather than the document.
        /// </param>
        internal RawPdfDictionaryWriter(PdfWriter writer, string what)
        {
            _writer = writer ?? throw new ArgumentNullException(nameof(writer));
            _what = what ?? "a PDF dictionary";
        }

        /// <summary>
        /// Writes <c> /Name value</c> and claims <paramref name="name"/>.
        /// </summary>
        /// <param name="name">The key WITHOUT its leading solidus — <c>"Type"</c>, not <c>"/Type"</c>.</param>
        /// <param name="value">
        /// The value exactly as it should appear: <c>"/Sig"</c>, <c>"(a reason)"</c>,
        /// <c>"12 0 R"</c>. Written verbatim, so the caller keeps responsibility for escaping —
        /// which is why a literal string must already have been through
        /// <c>SigningHelpers.EscapePdfString</c>.
        /// </param>
        internal void Entry(string name, string value)
        {
            Claim(name);
            _writer.WriteRaw(" /" + name + " " + value);
        }

        /// <summary>
        /// Writes <c> /Name </c> — key and trailing space only — and claims the name, leaving the
        /// caller to write the value. This is the placeholder case: <c>/ByteRange</c> and
        /// <c>/Contents</c> need the stream position captured between the key and the value.
        /// </summary>
        /// <param name="name">The key without its leading solidus.</param>
        internal void BeginEntry(string name)
        {
            Claim(name);
            _writer.WriteRaw(" /" + name + " ");
        }

        /// <summary>
        /// Claims <paramref name="name"/> without writing anything — for an entry emitted through
        /// another path, such as the adopted field's existing keys, which are copied with
        /// <c>PdfWriter.WriteObject</c> rather than as text.
        /// </summary>
        /// <param name="name">The key without its leading solidus.</param>
        internal void ClaimOnly(string name) => Claim(name);

        private void Claim(string name)
        {
            if (!_claimed.Add(name))
            {
                throw new PdfSigningException(
                    $"Internal error: {_what} would contain /{name} twice. ISO 32000-1 §7.3.7 " +
                    "leaves a repeated key undefined — one reader takes the first occurrence and " +
                    "another the last — so the entry is refused rather than written.");
            }
        }
    }
}
