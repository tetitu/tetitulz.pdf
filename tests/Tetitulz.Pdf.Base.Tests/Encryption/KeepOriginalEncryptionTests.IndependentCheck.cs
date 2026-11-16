// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base.Encryption;
using Tetitulz.Pdf.Base.Operations.Documents;
using Tetitulz.Pdf.Base.Tests.TestInfrastructure;
using Tetitulz.Pdf.Core;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Encryption;
using Tetitulz.Pdf.Plugins.Maintenance;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Encryption;

public sealed partial class KeepOriginalEncryptionTests
{
    /// <summary>an independent reader (<see cref="IndependentPdfCheck"/>), which did not write the file, opens the output with either password and refuses a wrong one.</summary>
    [Theory]
    [InlineData(EncryptionAlgorithm.RC4_128)]
    [InlineData(EncryptionAlgorithm.AES128)]
    [InlineData(EncryptionAlgorithm.AES256)]
    public void KeepOriginal_Output_OpensInAnIndependentReader_WithEitherPassword(EncryptionAlgorithm algorithm)
    {
        byte[] encrypted = Encrypt(MinimalPdfBuilder.CreateMinimalPdf("check"), algorithm);
        var (result, produced) = Run(encrypted, ReEncryptionPolicy.KeepOriginal, (p, o) => p.CopyTo(o));
        Assert.True(result.Succeeded, result.FailureReason);

        Assert.True(IndependentPdfCheck.Run(produced, User).Clean, IndependentPdfCheck.Run(produced, User).ToString());
        Assert.True(IndependentPdfCheck.Run(produced, Owner).Clean, IndependentPdfCheck.Run(produced, Owner).ToString());
        Assert.False(IndependentPdfCheck.Run(produced, "neither").Opened);
    }
}
