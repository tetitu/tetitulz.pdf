// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Accessibility.Models;
using Tetitulz.Pdf.Accessibility.Tagging;
using Tetitulz.Pdf.Base.Encryption;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Writing;
using Tetitulz.Pdf.Encryption;
using Xunit;

namespace Tetitulz.Pdf.Accessibility.Tests;

/// <summary>
/// <see cref="PdfAutoTagger.Plan"/> — tagging an <b>encrypted</b> document <i>in place</i>.
///
/// <para>
/// <see cref="EncryptedRemediationRoundTripTests"/> covers the other route,
/// <c>PdfEncryptedRoundTrip</c>, which decrypts the whole file and re-encrypts it. That works,
/// but it rewrites every byte, so it can preserve neither an existing signature nor the owner
/// password — it sets user = owner = whichever password it was handed. Tagging only adds and
/// replaces objects, so it is exactly the case an appended revision exists for, and these tests
/// pin the difference: the original file survives byte-for-byte as a prefix of the output, and
/// <b>both</b> of the document's original passwords still open the result.
/// </para>
///
/// <para>
/// <b>No new project reference.</b> The Accessibility assembly still cannot decrypt and still
/// contains no password. The operation travels to the decryptor instead — <c>Plan</c> describes
/// the revision and <c>Encryption.PdfEncryptedIncrementalUpdate.Run</c> encrypts and appends it.
/// The <c>→ Encryption</c> edge in this csproj is test-only and documented there.
/// </para>
/// </summary>
public sealed class EncryptedIncrementalTaggingTests
{
    private const string UserPassword  = "reader-pw";
    private const string OwnerPassword = "author-pw";

    // ------------------------------------------------------------------
    //  The headline: an encrypted document is tagged without being rewritten
    // ------------------------------------------------------------------

    [Fact]
    public void Plan_AppendedThroughTheEncryptedWriter_TagsInPlaceAndKeepsBothPasswords()
    {
        byte[] plain = ContentBearingPdfTests.ContentPdf();
        byte[] encrypted = Encrypt(plain);

        // Sanity-pin the fixture: it starts untagged, so anything found below came from the plan.
        using (var check = new MemoryStream(Decrypt(encrypted, UserPassword)))
        using (var doc = PdfDocument.Open(check))
            Assert.False(doc.Catalog.Dictionary.ContainsKey(PdfName.StructTreeRoot));

        byte[] produced;
        using (var input  = new MemoryStream(encrypted))
        using (var output = new MemoryStream())
        {
            // Default options on purpose: CompressRewrittenStreams is true, so the rewritten
            // content is Flate-encoded and *then* encrypted. That is the production combination,
            // and the order of the two is the part that would silently produce an unreadable page.
            PdfEncryptedIncrementalUpdate.Run(input, output, OwnerPassword,
                (decrypted, firstFree) => PdfAutoTagger.Plan(decrypted, firstFree).Objects);
            produced = output.ToArray();
        }

        // In place: the encrypted original is a byte-for-byte prefix of the result. A round trip
        // cannot satisfy this — it rewrites every byte — and this is what leaves an existing
        // signature's /ByteRange covering the bytes it signed.
        Assert.True(produced.Length > encrypted.Length,
            "a revision must have been appended, not merely a copy of the input");
        Assert.Equal(encrypted, produced.Take(encrypted.Length).ToArray());

        using (var doc = PdfDocument.Open(new MemoryStream(produced)))
            Assert.True(doc.IsEncrypted, "the appended revision must not leave the document in clear text");

        // Both original passwords still open it, and both see the structure tree. The round trip
        // loses this: it can only reproduce /O and /U from the one password it was given.
        foreach (string password in new[] { UserPassword, OwnerPassword })
        {
            using var tagged = PdfDocument.Open(new MemoryStream(Decrypt(produced, password)));
            Assert.True(tagged.Catalog.Dictionary.ContainsKey(PdfName.StructTreeRoot),
                "opening with " + password + " must reach the appended /StructTreeRoot");
        }

        // And the tagging is real rather than a hollow tree: the page's own content stream, read
        // back through the decryptor, now carries marked content it did not have before.
        using var final = PdfDocument.Open(new MemoryStream(Decrypt(produced, UserPassword)));
        string content = DecodePageContent(final, 0);
        Assert.Contains("/MCID", content, StringComparison.Ordinal);
        Assert.Contains("BDC", content, StringComparison.Ordinal);
        Assert.Contains("EMC", content, StringComparison.Ordinal);
        Assert.Contains(ContentBearingPdfTests.HeadingText, content, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------
    //  Object numbering: allocate above the TARGET file's ceiling
    // ------------------------------------------------------------------

    /// <summary>
    /// The document a plan is built against is a decrypted <i>copy</i>: decryption unpacks object
    /// streams and drops the cross-reference stream, so the copy's highest object number can be
    /// lower than that of the file the revision is appended to. Allocating from the copy's own
    /// ceiling would mint a number that is live in the target and silently supersede it.
    /// </summary>
    [Fact]
    public void Plan_AllocatesAboveTheTargetFilesCeiling_NotTheDocumentItPlannedAgainst()
    {
        byte[] plain = ContentBearingPdfTests.ContentPdf();

        using var ms  = new MemoryStream(plain);
        using var doc = PdfDocument.Open(ms);

        int planningCeiling = doc.CrossReferenceTable.GetMaxObjectNumber();
        Assert.True(planningCeiling > 0, "the fixture must have objects to plan against");

        // Stands in for an encrypted original whose ceiling is 25 higher than the decrypted copy's.
        int firstFree = planningCeiling + 25;

        AutoTagPlan plan = PdfAutoTagger.Plan(doc, firstFree);

        List<int> minted = plan.Objects
            .Select(o => o.ObjectNumber)
            .Where(n => n > planningCeiling)
            .ToList();

        Assert.NotEmpty(minted);   // the plan really does allocate — otherwise this proves nothing
        foreach (int number in minted)
        {
            Assert.True(number >= firstFree,
                "object " + number + " was minted below the target file's first free number (" +
                firstFree + "), so appending it would overwrite a live object");
        }

        // The revised objects keep the numbers they already have — the gap is only closed upward.
        foreach (IndirectObject obj in plan.Objects)
            Assert.True(obj.ObjectNumber >= firstFree || obj.ObjectNumber <= planningCeiling,
                "object " + obj.ObjectNumber + " falls in the reserved gap");
    }

    // ------------------------------------------------------------------
    //  Honest reporting when the caller's write does not land
    // ------------------------------------------------------------------

    /// <summary>
    /// Handing a plan out moves the "did the revision reach the output?" check outside this
    /// assembly, where <c>VerifiedWrite</c> normally performs it. <c>Describe</c> keeps the
    /// invariant by making the caller state the answer, so a lost revision can never be reported
    /// as a tagged document.
    /// </summary>
    [Fact]
    public void Describe_ReportsNothingTagged_WhenTheRevisionWasNotWritten()
    {
        byte[] plain = ContentBearingPdfTests.ContentPdf();

        using var ms  = new MemoryStream(plain);
        using var doc = PdfDocument.Open(ms);

        AutoTagPlan plan = PdfAutoTagger.Plan(doc, doc.CrossReferenceTable.GetMaxObjectNumber() + 1);

        AutoTagResult applied = plan.Describe(revisionWritten: true);
        Assert.True(applied.ContentMarked);
        Assert.True(applied.ElementsAdded > 0);
        Assert.True(applied.McidCount > 0);

        AutoTagResult lost = plan.Describe(revisionWritten: false);
        Assert.False(lost.ContentMarked);
        Assert.Equal(0, lost.ElementsAdded);
        Assert.Equal(0, lost.McidCount);
        Assert.Contains(lost.Warnings, w => w.Contains("NOT present", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------
    //  Helpers
    // ------------------------------------------------------------------

    private static byte[] Encrypt(byte[] plain)
    {
        using var input  = new MemoryStream(plain);
        using var output = new MemoryStream();
        PdfEncryptor.Encrypt(input, output, new EncryptionParameters
        {
            UserPassword  = UserPassword,
            OwnerPassword = OwnerPassword,
            Algorithm     = EncryptionAlgorithm.AES128,
            Permissions   = PdfPermissions.All
        });
        return output.ToArray();
    }

    private static byte[] Decrypt(byte[] encrypted, string password)
    {
        using var input  = new MemoryStream(encrypted);
        using var output = new MemoryStream();
        PdfDecryptor.Decrypt(input, output, password);
        return output.ToArray();
    }

    private static string DecodePageContent(PdfDocument doc, int pageIndex)
    {
        PdfDictionary page = doc.Pages[pageIndex].Dictionary;
        PdfObject contents = doc.ResolveObject(page.Get(PdfName.Contents));

        var buffer = new MemoryStream();
        if (contents is PdfArray arr)
        {
            foreach (PdfObject part in arr)
            {
                if (doc.ResolveObject(part) is PdfStream ps)
                {
                    byte[] d = ps.GetDecodedData();
                    buffer.Write(d, 0, d.Length);
                    buffer.WriteByte((byte)'\n');
                }
            }
        }
        else if (contents is PdfStream s)
        {
            byte[] d = s.GetDecodedData();
            buffer.Write(d, 0, d.Length);
        }

        return Encoding.ASCII.GetString(buffer.ToArray());
    }
}
