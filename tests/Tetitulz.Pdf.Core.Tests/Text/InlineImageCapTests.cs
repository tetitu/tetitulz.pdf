// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Text;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Text;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Text;

/// <summary>
/// An inline image whose data runs past <see cref="ContentStreamParser.MaxInlineImageBytes"/> without an
/// <c>EI</c> is a refusal of the file's own content, so it is a <see cref="PdfParseException"/> — never an
/// exception outside <see cref="PdfException"/>, which most callers do not catch. The ceiling is lowered
/// here as a parameter; production reaches it at 256 MiB.
/// </summary>
[Collection(Constants.PdfLimitsCollection.Name)]
public sealed class InlineImageCapTests
{
    private const string Head = "q BI /W 1 /H 1 /CS /G /BPC 8 ID ";

    [Fact]
    public void Adversarial_inline_image_with_no_EI_past_the_cap_is_a_PdfParseException()
    {
        byte[] content = Encoding.ASCII.GetBytes(Head + new string('x', 2_000));
        try
        {
            ContentStreamParser.MaxInlineImageBytes = 1_000;

            var ex = Assert.Throws<PdfParseException>(() => ContentStreamParser.Parse(content));
            Assert.Contains("without EI terminator", ex.Message, StringComparison.Ordinal);
        }
        finally { ContentStreamParser.MaxInlineImageBytes = ContentStreamParser.DefaultMaxInlineImageBytes; }
    }

    /// <summary>The control: the same image closed by its <c>EI</c> within the cap parses, the operations after it too.</summary>
    [Fact]
    public void Inline_image_closed_within_the_cap_parses()
    {
        byte[] content = Encoding.ASCII.GetBytes(Head + "x EI Q");
        try
        {
            ContentStreamParser.MaxInlineImageBytes = 1_000;

            var ops = ContentStreamParser.Parse(content);

            Assert.Equal(new[] { "q", "BI", "Q" }, ops.ConvertAll(o => o.Operator).ToArray());
        }
        finally { ContentStreamParser.MaxInlineImageBytes = ContentStreamParser.DefaultMaxInlineImageBytes; }
    }
}
