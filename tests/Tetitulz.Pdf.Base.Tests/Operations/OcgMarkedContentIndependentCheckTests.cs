// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Base.Tests.TestInfrastructure;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// A page wrapped by <c>PdfOcgManager.AssignPageContentToLayer</c> is judged by an independent
/// reader, not only by this library's own.
/// </summary>
/// <remarks>
/// <para><b>Content surgery is where a self-check is worth least.</b> Everything the wrap writes —
/// two content streams, a <c>/Contents</c> array, a <c>/Properties</c> entry, an appended revision
/// — is read back by the same parser that produced it, so a shared misunderstanding is invisible.
/// <see cref="IndependentPdfCheck"/> re-derives the structure from the bytes and says whether they are a PDF.</para>
/// </remarks>
public sealed class OcgMarkedContentIndependentCheckTests
{
    [Fact]
    public void AWrappedPageIsAValidDocumentToAnIndependentReader()
    {
        var outcome = IndependentPdfCheck.Run(OcgMarkedContentFixture.LayeredPageBytes());
        Assert.True(outcome.Clean, "the wrapped document: " + outcome);
    }
}
