// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base.Encryption;
using Tetitulz.Pdf.Base.Operations.Documents;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Base.Operations.Forms;
using Tetitulz.Pdf.Base.Operations.Metadata;
using Tetitulz.Pdf.Base.Operations.Structure;
using Tetitulz.Pdf.Base.Tests.Helpers;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Plugins.Maintenance;
using Tetitulz.Pdf.Plugins.PageEdits;
using Tetitulz.Pdf.Signing;
using Tetitulz.Pdf.Signing.Models;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.TestInfrastructure;

/// <summary>
/// Every writer, run over one document, and its output handed to <see cref="IndependentPdfCheck"/> — an implementation that
/// shares nothing with this library's parser.
/// </summary>
/// <remarks>
/// <para><b>Why a second opinion and not more self-checks.</b> A writer and the reader it is tested
/// with can share a misunderstanding — a <c>/Size</c> one short, a <c>/Prev</c> to the wrong
/// section, a stream <c>/Length</c> the reader never trusts — and every round-trip test passes.
/// The check re-derives the structure from the bytes and says what it had to forgive.</para>
/// <para><b>Two layouts of the same input.</b> The fixture is written with a classic cross-reference
/// table, and a second time by an independent producer with object streams and a cross-reference stream — the layout
/// an incremental writer must continue in kind, and the one a hand-built fixture never exercises.
/// </para>
/// </remarks>
public class WriterOutputIndependentCheckTests
{
    private const string Password = "writer-check";

    public static IEnumerable<object[]> Cases()
    {
        foreach (string writer in Writers.Keys)
            foreach (string layout in new[] { "xref-table", "object-streams" })
                yield return new object[] { writer, layout };
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void WriterOutput_PassesAnIndependentStructureCheck(string writer, string layout)
    {
        byte[] input = layout == "object-streams" ? WriterCheckFixture.ObjectStreamsLayout() : WriterCheckFixture.Fixture();
        Assert.True(IndependentPdfCheck.Run(input).Clean, "fixture: " + IndependentPdfCheck.Run(input));

        (byte[] output, string? password) = Writers[writer](input);

        IndependentPdfCheck.Outcome outcome = IndependentPdfCheck.Run(output, password);
        Assert.True(outcome.Clean, writer + " on " + layout + ": " + outcome);
    }

    // ------------------------------------------------------------------ the writers

    private delegate (byte[] Output, string? Password) Writer(byte[] input);

    private static Writer Plain(Action<Stream, Stream> write) => input =>
    {
        using var i = new MemoryStream(input, writable: false);
        using var o = new MemoryStream();
        write(i, o);
        return (o.ToArray(), null);
    };

    private static Writer Encrypted(EncryptionAlgorithm algorithm) => input =>
    {
        using var i = new MemoryStream(input, writable: false);
        using var o = new MemoryStream();
        Tetitulz.Pdf.Encryption.PdfEncryptor.Encrypt(i, o, new EncryptionParameters
        {
            UserPassword = Password,
            OwnerPassword = Password + "-owner",
            Algorithm = algorithm,
        });
        return (o.ToArray(), Password);
    };

    /// <summary>
    /// The same writer, the check then opening the file with the <b>owner</b> password alone — the one
    /// independent read of Algorithm 3's <c>/O</c>: a value computed over all sixteen digest bytes
    /// instead of the first <i>n</i> opens for the user and fails here, and only at 40 bits.
    /// </summary>
    private static Writer EncryptedOwnerOpens(EncryptionAlgorithm algorithm) => input =>
    {
        (byte[] output, _) = Encrypted(algorithm)(input);
        return (output, Password + "-owner");
    };

    private static readonly Dictionary<string, Writer> Writers = new(StringComparer.Ordinal)
    {
        // Forms
        ["AddTextField"] = Plain((i, o) => PdfOperations.AddTextField(i, o,
            new FormTextField { FieldName = "added", PageIndex = 0, X = 300, Y = 600 })),
        ["AddCheckbox"] = Plain((i, o) => PdfOperations.AddCheckbox(i, o,
            new FormCheckboxField { FieldName = "agree", PageIndex = 0, X = 300, Y = 560, IsChecked = true })),
        ["FillForm"] = Plain((i, o) => PdfOperations.FillForm(i, o,
            new Dictionary<string, string> { ["name"] = "Filled value" })),
        ["FlattenForm"] = Plain((i, o) => PdfOperations.FlattenForm(i, o)),

        // Annotations
        ["AddLink"] = Plain((i, o) => PdfOperations.AddLink(i, o, 0, 72, 400, 200, 420, "https://example.com/")),
        ["AddTextNote"] = Plain((i, o) => PdfOperations.AddTextNote(i, o, 0, 100, 300, "A note")),
        ["AddHighlight"] = Plain((i, o) => PdfOperations.AddHighlight(i, o, 0, 72, 700, 200, 714)),

        // Structure
        ["WriteBookmarks"] = Plain((i, o) => PdfOperations.WriteBookmarks(i, o, new[]
        {
            new PdfBookmark("First", 1, 0), new PdfBookmark("Second", 2, 0),
        })),
        ["SetPageLabels"] = Plain((i, o) => PdfOperations.SetPageLabels(i, o, new[]
        {
            new PageLabelRange { StartPageIndex = 0, Style = "r" },
            new PageLabelRange { StartPageIndex = 1, Style = "D" },
        })),
        ["SetDestination"] = Plain((i, o) => PdfOperations.SetDestination(i, o, "chapter-2", 1)),
        ["CreateLayer"] = Plain((i, o) => PdfOperations.CreateLayer(i, o, "Notes")),

        // Pages
        ["InsertBlankPage"] = Plain((i, o) => PdfOperations.InsertBlankPage(i, o, 1)),
        ["DeletePage"] = Plain((i, o) => PdfOperations.DeletePage(i, o, 2)),
        ["RotatePage"] = Plain((i, o) => PdfOperations.RotatePage(i, o, 0, 90)),
        ["RotateAllPages"] = Plain((i, o) => PdfOperations.RotateAllPages(i, o, 180)),
        ["SetPageBox"] = Plain((i, o) => PdfOperations.SetPageBox(i, o, 0, PdfPageBoxKind.CropBox, 10, 10, 500, 700)),

        // Metadata and attachments
        ["WriteMetadata"] = Plain((i, o) => PdfOperations.WriteMetadata(i, o,
            new PdfMetadataUpdate { Title = "Checked", Author = "Writer check" })),
        ["AddAttachment"] = Plain((i, o) => PdfOperations.AddAttachment(i, o, "notes.txt",
            Encoding.ASCII.GetBytes("attached"), "text/plain")),
        // The fixture is PDF 1.7, so 2.0 appends a revision.
        ["SetVersion"] = Plain((i, o) => Assert.True(PdfOperations.SetVersion(i, o,
            Tetitulz.Pdf.Core.Document.PdfVersion.Pdf20).RevisionAppended)),

        // Content
        ["AddHeaderFooter"] = Plain((i, o) => PdfOperations.AddHeaderFooter(i, o,
            new HeaderFooterOptions { HeaderText = "Header", FooterText = "Footer" })),
        ["AppendContent"] = Plain((i, o) => PdfOperations.AppendContent(i, o, 0,
            b => b.SetFillColorRgb(0.5f, 0.5f, 0.5f).Rectangle(50, 50, 20, 20).Fill())),
        ["AddPageText"] = Plain((i, o) => PdfOperations.AddPageText(i, o, 0, new Tetitulz.Pdf.Base.Operations.Documents.PageTextOptions
        {
            Text = "Added", X = 72, Y = 400, FontProvider = _ => Tetitulz.Pdf.TestHelpers.MinimalTrueType.Build("Added"),
        })),
        ["ConvertRgbToGrayscale"] = Plain((i, o) => PdfOperations.ConvertRgbToGrayscale(i, o)),
        ["RemoveContent"] = Plain((i, o) => PdfOperations.RemoveContent(i, o, PdfContentClass.Text | PdfContentClass.Vector)),
        ["ConvertRgbToCmyk"] = Plain((i, o) => PdfOperations.ConvertRgbToCmyk(i, o)),
        ["ConvertToCmyk"] = Plain((i, o) => PdfOperations.ConvertToCmyk(i, o, new Tetitulz.Pdf.Base.Operations.Metadata.CmykConversionOptions
        {
            OutputIccProfile = Tetitulz.Pdf.TestHelpers.SyntheticCmykProfile.Build(Tetitulz.Pdf.TestHelpers.SyntheticCmykProfile.KFromLightness()),
        })),
        ["Create2Up"] = Plain((i, o) => PdfOperations.Create2Up(i, o)),
        ["Create4Up"] = Plain((i, o) => PdfOperations.Create4Up(i, o)),
        ["FlattenTransparency"] = Plain((i, o) => PdfOperations.FlattenTransparency(i, o)),
        ["Bates"] = Plain((i, o) => PdfBatesNumbering.Apply(i, o)),
        ["TranscodeHtj2kImages"] = Plain((i, o) =>
        {
            // The fixture has no image: an HTJ2K one is appended first, then transcoded — the check reads both revisions.
            Assert.True(Tetitulz.Pdf.Core.Imaging.Jpx.JpxTranscoding.TryToHighThroughput(
                Tetitulz.Pdf.TestHelpers.JpxLosslessFixtures.Rgb40x24_Mct_TwoLevels.Codestream, out byte[] ht, out string reason), reason);
            using var withImage = new MemoryStream();
            PdfOperations.AppendContent(i, withImage, 0, b => b.DrawImage(b.AddImage(ht, 40, 24, "JPXDecode"), 72, 72, 40, 24));
            withImage.Position = 0;
            Assert.Single(PdfOperations.TranscodeHtj2kImagesToPart1(withImage, o).Transcoded);
        }),
        ["Redact"] = Plain((i, o) => PdfRedactionEngine.Redact(i, o, new[]
        {
            new PdfRedactionArea { PageIndex = 0, X = 60, Y = 690, Width = 200, Height = 30 },
        })),

        // Whole-file rewrites
        ["Sanitize"] = Plain((i, o) => PdfSanitizer.Sanitize(i, o)),
        ["Repair"] = Plain((i, o) => PdfRepair.Repair(i, o)),
        ["Optimize"] = Plain((i, o) => PdfOptimizer.Optimize(i, o)),
        ["Linearize"] = Plain((i, o) => PdfLinearizer.Linearize(i, o)),
        ["Merge"] = Plain((i, o) =>
        {
            byte[] bytes = ((MemoryStream)i).ToArray();
            PdfMerger.Merge(o, new MemoryStream(bytes, writable: false), new MemoryStream(bytes, writable: false));
        }),
        ["ExtractPages"] = Plain((i, o) => PdfSplitter.ExtractPages(i, o, new[] { 1, 3 })),
        ["Watermark"] = Plain((i, o) => PdfWatermarker.AddWatermark(i, o, new WatermarkOptions())),

        // Encryption and signing
        ["Encrypt-RC4-40"] = Encrypted(EncryptionAlgorithm.RC4_40),
        ["Encrypt-RC4-40-R3"] = Encrypted(EncryptionAlgorithm.RC4_40R3),
        ["Encrypt-RC4-40-R3-owner"] = EncryptedOwnerOpens(EncryptionAlgorithm.RC4_40R3),
        ["Encrypt-RC4-128-owner"] = EncryptedOwnerOpens(EncryptionAlgorithm.RC4_128),
        ["Encrypt-RC4-128"] = Encrypted(EncryptionAlgorithm.RC4_128),
        ["Encrypt-AES-128"] = Encrypted(EncryptionAlgorithm.AES128),
        ["Encrypt-AES-256"] = Encrypted(EncryptionAlgorithm.AES256),
        ["Sign"] = Plain((i, o) =>
        {
            var (pfx, pfxPassword) = TestCertificateGenerator.GenerateTestCertificate();
            PdfSigningOperations.Sign(i, o, new SignatureParameters
            {
                CertificateStream = new MemoryStream(pfx),
                CertificatePassword = pfxPassword,
                SignatureFieldName = "WriterCheckSignature",
            });
        }),
    };
}
