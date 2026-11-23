// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Signing;

/// <summary>
/// The shape Acrobat Reader validates a visible signature's appearance in: every entry of the
/// <c>/AP /N</c> form's <c>/Resources /XObject</c> resolves to a <c>/Subtype /Form</c> stream that
/// carries a <c>/Resources</c> dictionary of its own. An image named there directly makes Acrobat
/// report "Expected a dict object" when the signature is clicked, and the signature is never
/// validated.
/// </summary>
internal static class SignatureAppearanceXObjectRule
{
    /// <summary>
    /// Asserts the rule over <paramref name="appearance"/>, resolving references through
    /// <paramref name="resolve"/>, and returns how many image XObjects the inner forms' own
    /// <c>/Resources</c> reach — the image the appearance draws, which a caller asserts is there, so
    /// an appearance that names nothing at all cannot pass for one that follows the rule.
    /// </summary>
    public static int AssertAndCountImages(PdfStream appearance, Func<PdfObject, PdfObject> resolve)
    {
        var resources = Assert.IsType<PdfDictionary>(resolve(appearance[PdfName.Resources]));
        var xobjects = resolve(resources[PdfName.XObject]) as PdfDictionary;
        if (xobjects == null) return 0;

        int images = 0;
        foreach (var entry in xobjects)
        {
            var form = resolve(entry.Value) as PdfStream;
            Assert.True(form != null,
                $"the appearance names /{entry.Key.Value} under /XObject, and it resolves to no stream");
            Assert.True((resolve(form![PdfName.Subtype]) as PdfName)?.Value == "Form",
                $"the appearance names /{entry.Key.Value} under /XObject, and it is a " +
                $"/{(resolve(form[PdfName.Subtype]) as PdfName)?.Value} — Acrobat validates only a form there");

            var inner = resolve(form[PdfName.Resources]) as PdfDictionary;
            Assert.True(inner != null,
                $"the form the appearance names as /{entry.Key.Value} carries no /Resources dictionary");

            if (resolve(inner![PdfName.XObject]) is PdfDictionary innerXObjects)
                foreach (var drawn in innerXObjects)
                    if (resolve(drawn.Value) is PdfStream s && (resolve(s[PdfName.Subtype]) as PdfName)?.Value == "Image")
                        images++;
        }
        return images;
    }
}
