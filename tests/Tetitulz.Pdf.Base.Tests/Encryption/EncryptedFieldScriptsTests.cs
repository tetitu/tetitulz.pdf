// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.IO;
using System.Linq;
using Tetitulz.Pdf.Base.Encryption;
using Tetitulz.Pdf.Base.Operations.Forms;
using Tetitulz.Pdf.Base.Tests.Operations;
using Tetitulz.Pdf.Core;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Encryption;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Encryption;

/// <summary>
/// <see cref="PdfFieldScripts"/> over an encrypted form: the password door reads the field's
/// scripts, and a door without one refuses rather than accept a value no script was read for.
/// </summary>
/// <remarks>
/// No engine is registered here, and a class in <c>PluginHostCollection</c> may register a stub
/// meanwhile, so the evidence that the scripts were read is "the field's Keystroke script either
/// ran or was reported unrun" — under its decrypted name, which ciphertext cannot give.
/// </remarks>
[Collection(Tetitulz.Pdf.Base.Tests.TestInfrastructure.DecryptorHolderCollection.Name)]
public sealed class EncryptedFieldScriptsTests
{
    private const string UserPassword = "s3cret";

    private static byte[] EncryptedForm()
    {
        PdfEncryptionOperations.Register();

        using var input = new MemoryStream(PdfFieldScriptsTests.FormBytes(new[]
        {
            ("A", (string?)"upper", (string?)null, (string?)null),
        }));
        using var output = new MemoryStream();
        PdfEncryptor.Encrypt(input, output, new EncryptionParameters
        {
            UserPassword = UserPassword,
            OwnerPassword = "owner-only",
            Algorithm = EncryptionAlgorithm.AES128,
            Permissions = PdfPermissions.All,
        });
        return output.ToArray();
    }

    [Fact]
    public void ThePasswordDoor_ReadsTheFieldsScripts()
    {
        FieldCommitResult r = PdfOperations.CommitFieldValue(new MemoryStream(EncryptedForm()), UserPassword,
            "A", "x", new Dictionary<string, string>());

        Assert.True(r.ScriptsRan || r.Warnings.Any(w => w.StartsWith("A (Keystroke)")));
    }

    [Fact]
    public void ADoorWithoutAPassword_RefusesAnEncryptedForm_AndNamesTheOneThatTakesIt()
    {
        byte[] pdf = EncryptedForm();

        var refused = Assert.Throws<PdfEncryptedDocumentException>(() =>
            PdfOperations.CommitFieldValue(new MemoryStream(pdf), "A", "x", new Dictionary<string, string>()));
        Assert.Contains("CommitFieldValue(Stream, string,", refused.Message);

        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        Assert.Throws<PdfEncryptedDocumentException>(() =>
            PdfFieldScripts.Commit(doc, "A", "x", new Dictionary<string, string>()));

        // The control: the same form unencrypted is read through the same door.
        FieldCommitResult plain = PdfOperations.CommitFieldValue(
            new MemoryStream(PdfFieldScriptsTests.FormBytes(new[] { ("A", (string?)"upper", (string?)null, (string?)null) })),
            "A", "x", new Dictionary<string, string>());
        Assert.True(plain.ScriptsRan || plain.Warnings.Any(w => w.StartsWith("A (Keystroke)")));
    }
}
