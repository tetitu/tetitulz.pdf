// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Encryption;
using Xunit;

namespace Tetitulz.Pdf.Encryption.Tests;

/// <summary>
/// The external vector <c>HashR6TerminationReadingTests</c> was waiting for. It settles the
/// Algorithm 2.B termination predicate, which was the single OPEN/HIGH row in <c>GAPS.md</c>.
/// </summary>
/// <remarks>
/// <para><b>Where these came from, and why there are two producers.</b> AES-256 (R6) files were
/// generated with <b>An independent checker</b> and, independently, with <b>An independent implementation</b> —
/// two implementations that share no code. Agreement between them is the evidence; one alone could
/// be idiosyncratic. Every vector below is that producer's own <c>/U</c>: its 8-byte validation
/// salt and the 32-byte hash it stored beside it.</para>
///
/// <para><b>Six of them discriminate, and they are the whole point.</b> For roughly thirty-seven
/// passwords in thirty-eight the two readings of the predicate produce the same key, so an
/// arbitrary vector proves nothing about which is right — it would pass under either. These six
/// were found by generating 240 files (120 per producer) and keeping the ones where the readings
/// part company: <b>2 of 120 from an independent checker, 4 of 120 from an independent implementation</b>, a rate consistent with the
/// one-in-37.7 this suite already measured. In all six the answer is the same, and it is not the
/// reading this library used to have.</para>
///
/// <para><b>The two agreeing rows are the acceptance control.</b> Without them, "the hash matches"
/// would be satisfied by a change that broke the common case in some compensating way; with them,
/// the 234 non-divergent files these are drawn from are asserted to be unaffected.</para>
///
/// <para><b>The end-to-end pair is a separate claim.</b> A hash vector settles the predicate; it
/// says nothing about the key salt, the intermediate key, the AES-256 unwrap of <c>/UE</c>, or
/// object decryption. Those two files are the <i>first externally produced encrypted PDFs</i> this
/// suite has ever opened — §20a records that every R6 test before them round-tripped this library
/// against itself, which is why a change applied to both halves was invisible.</para>
///
/// <para><b>Read this file as characterisation of somebody else's output</b>, not of ours. If a
/// row here ever disagrees with the implementation, the implementation moved.</para>
/// </remarks>
public class HashR6ExternalVectorTests
{
    /// <summary>
    /// <b>Every external vector's stored hash is what this library computes.</b>
    /// </summary>
    /// <param name="password">The password the producer was given.</param>
    /// <param name="validationSaltHex">The 8-byte salt at <c>/U[32..40]</c>.</param>
    /// <param name="expectedHashHex">The 32 bytes at <c>/U[0..32]</c>.</param>
    /// <param name="producer">Which implementation wrote the file.</param>
    /// <param name="discriminates">
    /// Whether the two readings of the predicate differ for this input — that is, whether this row
    /// can tell them apart at all.
    /// </param>
    [Theory]
    [InlineData("pw34", "3f8e12d4f5237c85", "e45482fcf0a26190062e054ceb267d73ce815593e3910f3ff296cc0ace87003d", "producer A", true)]
    [InlineData("pw61", "4ab5b2b96b1444b6", "1c56beba392fa699b337325542efe2ef617b69748c0891456e88aaeaced7317f", "producer A", true)]
    [InlineData("pw2", "fc8ae9ad2ae3a9da", "4c936c40885bbeee7aa814b3b97f3cda9dfd4c06b357aab0981cf8c640a5c8dd", "producer B", true)]
    [InlineData("pw71", "437e15dcadc48b7e", "f39a1474ed92b0ca4126d38e8f3965e806daf9391eaea481d9c8ca41f05dce58", "producer B", true)]
    [InlineData("pw74", "20139f71ffa1005f", "a8a15e4e9d161de07cfaa395045b8dbc4651fd0278a9665bf7038fdf98c2702b", "producer B", true)]
    [InlineData("pw83", "345ec49fb3fb358c", "24abdb9befe9792707ea5af4765ebfa0a33b2bea8c6e67b09db55cc2bd9fdb65", "producer B", true)]
    [InlineData("pw1", "a091ad80adf69525", "10ccb0d7ad4897f5c28939b970bbf6460afa0b41577fd6d10bb5c4619a538a6a", "producer A", false)]
    [InlineData("pw1", "561968f7d70270ef", "6ffbc1793f18942a7ff8cf83eaa85baa79cd35a580df9da884a98afe95a6ba76", "producer B", false)]
    public void TheStoredValidationHashOfAnExternallyProducedFileIsReproduced(
        string password, string validationSaltHex, string expectedHashHex,
        string producer, bool discriminates)
    {
        byte[] hash = PdfEncryptionPrimitives.ComputeHashR6(
            Encoding.UTF8.GetBytes(password), Hex(validationSaltHex));

        Assert.Equal(expectedHashHex, Hex(hash));

        // The row's own premise, asserted rather than asserted-about-in-a-comment: a
        // discriminating row must actually discriminate, or it is a row that would pass under
        // either reading and is silently not doing its job.
        byte[] superseded = PdfEncryptionPrimitives.ComputeHashR6(
            Encoding.UTF8.GetBytes(password), Hex(validationSaltHex), null,
            PdfEncryptionPrimitives.HashR6SupersededTerminationOffset);

        if (discriminates)
            Assert.NotEqual(expectedHashHex, Hex(superseded));
        else
            Assert.Equal(expectedHashHex, Hex(superseded));

        Assert.False(string.IsNullOrEmpty(producer));
    }

    /// <summary>
    /// <b>A an independent checker-produced AES-256 file opens with its password, end to end.</b>
    /// </summary>
    /// <remarks>
    /// <para>The password is a discriminating one, deliberately: under the superseded reading this
    /// file's correct password is rejected, which is the user-visible half of the defect — "an independent checker
    /// and a common viewer open it, this library says the password is wrong".</para>
    /// <para><b>Its owner password is deliberately different</b>, and that detail is load-bearing.
    /// The first version of this fixture gave the file the same string for both, and it opened
    /// under the superseded reading anyway: <c>TryPassword</c> falls through to the owner route,
    /// whose hash is taken over a different salt and so diverges on a different set of inputs. A
    /// vector that both readings accept is a vector that proves nothing, and this one only has the
    /// user route to succeed by.</para>
    /// </remarks>
    [Fact]
    public void AnAes256FileFromProducerAOpensWithItsPassword()
    {
        AssertOpensAndDecrypts(Convert.FromBase64String(ProducerADivergentPdfBase64), "u21");
    }

    /// <summary>
    /// <b>A an independent implementation-produced AES-256 file opens with its password, end to end.</b>
    /// </summary>
    /// <remarks>
    /// The second producer, and the second discriminating password — again with a different owner
    /// password, for the reason above. Two files from two implementations rules out the possibility
    /// that one of them shares a mistake with us.
    /// </remarks>
    [Fact]
    public void AnAes256FileFromProducerBOpensWithItsPassword()
    {
        AssertOpensAndDecrypts(Convert.FromBase64String(ProducerBDivergentPdfBase64), "u15");
    }

    /// <summary>
    /// The refusal control: a wrong password on the same file is rejected.
    /// </summary>
    /// <remarks>
    /// Pairs with the two acceptance tests above, because "the file opened" would also be true of a
    /// decryptor that had stopped checking. The wrong password differs from the right one by one
    /// character, so nothing but the validation hash separates them. It is also not the owner
    /// password, which is a different string on this file.
    /// </remarks>
    [Fact]
    public void TheWrongPasswordIsRejectedOnTheSameExternalFile()
    {
        byte[] pdf = Convert.FromBase64String(ProducerADivergentPdfBase64);

        using var input = new MemoryStream(pdf);
        EncryptionInfo info = PdfDecryptor.GetEncryptionInfo(input);

        Assert.True(PdfDecryptor.TryPassword(info, "u21"));
        Assert.False(PdfDecryptor.TryPassword(info, "u22"));
    }

    /// <summary>
    /// The external files really are revision 6, so these vectors exercise Algorithm 2.B at all.
    /// </summary>
    /// <remarks>
    /// A vector file that turned out to be R4 would satisfy every assertion above by never reaching
    /// the code under test — the "fixture never reached the feature" explanation for a passing test,
    /// checked rather than assumed.
    /// </remarks>
    [Fact]
    public void BothExternalFilesAreRevisionSixAes256()
    {
        foreach (string b64 in new[] { ProducerADivergentPdfBase64, ProducerBDivergentPdfBase64 })
        {
            using var input = new MemoryStream(Convert.FromBase64String(b64));
            EncryptionInfo info = PdfDecryptor.GetEncryptionInfo(input);

            Assert.Equal(6, info.R);
            Assert.Equal(5, info.V);
        }
    }

    // ------------------------------------------------------------------

    private static void AssertOpensAndDecrypts(byte[] pdf, string password)
    {
        using var input = new MemoryStream(pdf);
        EncryptionInfo info = PdfDecryptor.GetEncryptionInfo(input);
        Assert.True(PdfDecryptor.TryPassword(info, password),
            "the correct password was rejected for an externally produced R6 file");

        input.Position = 0;
        using var output = new MemoryStream();
        PdfDecryptor.Decrypt(input, output, password);

        // The page's content stream, decoded. Asserting on the CONTENT rather than on "it did not
        // throw": a decryptor handed the wrong file key produces bytes too, and they are also
        // non-empty. Decoded rather than searched raw, because an independent checker recompresses the stream while
        // an independent implementation leaves it alone — and a Flate stream that inflates at all is itself evidence the
        // key was right, which a substring search over ciphertext-shaped bytes is not.
        output.Position = 0;
        using var decrypted = PdfDocument.Open(output);
        var page = decrypted.Pages[0].Dictionary;
        var contents = decrypted.ResolveObject(page[PdfName.Contents]) as PdfStream;

        Assert.NotNull(contents);
        string text = Encoding.GetEncoding("iso-8859-1").GetString(contents!.GetDecodedData());
        Assert.Contains("(R6) Tj", text, StringComparison.Ordinal);
    }

    private static byte[] Hex(string hex)
    {
        var bytes = new byte[hex.Length / 2];
        for (int i = 0; i < bytes.Length; i++)
            bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
        return bytes;
    }

    private static string Hex(byte[] bytes) =>
        BitConverter.ToString(bytes).Replace("-", string.Empty).ToLowerInvariant();

    // ------------------------------------------------------------------
    //  The fixtures: two 1.2 KB AES-256 files, one per producer, each encrypting the same
    //  one-page source whose content stream draws the text "R6".
    // ------------------------------------------------------------------

    private const string ProducerADivergentPdfBase64 =
        "JVBERi0xLjcKJb/3ov4KMSAwIG9iago8PCAvRXh0ZW5zaW9ucyA8PCAvQURCRSA8PCAvQmFzZVZlcnNpb24gLzEuNyAvRXh0"
        + "ZW5zaW9uTGV2ZWwgOCA+PiA+PiAvUGFnZXMgMiAwIFIgL1R5cGUgL0NhdGFsb2cgPj4KZW5kb2JqCjIgMCBvYmoKPDwgL0Nv"
        + "dW50IDEgL0tpZHMgWyAzIDAgUiBdIC9UeXBlIC9QYWdlcyA+PgplbmRvYmoKMyAwIG9iago8PCAvQ29udGVudHMgNCAwIFIg"
        + "L01lZGlhQm94IFsgMCAwIDIwMCAyMDAgXSAvUGFyZW50IDIgMCBSIC9SZXNvdXJjZXMgPDwgPj4gL1R5cGUgL1BhZ2UgPj4K"
        + "ZW5kb2JqCjQgMCBvYmoKPDwgL0xlbmd0aCA2NCAvRmlsdGVyIC9GbGF0ZURlY29kZSA+PgpzdHJlYW0K8wBuZB8OlK0DHit+"
        + "JtcC8uP498t/S50Pq9LNrZ82fHVCHClyIzbWunyleW+Vr2WwpslmvR3weGqR/RWJAPnRlWVuZHN0cmVhbQplbmRvYmoKNSAw"
        + "IG9iago8PCAvQ0YgPDwgL1N0ZENGIDw8IC9BdXRoRXZlbnQgL0RvY09wZW4gL0NGTSAvQUVTVjMgL0xlbmd0aCAzMiA+PiA+"
        + "PiAvRmlsdGVyIC9TdGFuZGFyZCAvTGVuZ3RoIDI1NiAvTyA8YTExMGNhZDM1NzY2NTJjYjg3N2JiZDQ3OTFiMTZhZWM5NGIy"
        + "ZThlZTkwNTA0NzRlMmM1NTYzYzc1Mjg3OTkyMjg3NjI5YzhkNDY5MDMwY2M0NDhmOTFhMWJlMDlkZTQ1PiAvT0UgPGEwYTk2"
        + "Yzk2MTU1MTFjMmZhZDQ0NTQ2ZGZhNjk5OWEyNTc1ZTU3YWJmOGVlYzdmMTNmMjJmZWIzZDI2YWJkNWM+IC9QIC00IC9QZXJt"
        + "cyA8ZWUxN2RhNjA3YTBhZTQ0MmNjODdkMTNiYzE2ZjcxYzA+IC9SIDYgL1N0bUYgL1N0ZENGIC9TdHJGIC9TdGRDRiAvVSA8"
        + "NjFjZmFkYzRmN2U5YjZlNTEyYzI2Y2U3NmJjZmE0YjZlYzA4Y2Y4Y2RjYzUxNDIxNmMzYTA1MjhlMzFlMGFjZGJlNDczZWQ1"
        + "YWMxZDM1N2QzZjYxMWNkMjY1MmEyODNmPiAvVUUgPDJhMzc4OWE0ZmI3ZGU0ZTQzMWI0ZjMyZGNmNjgzNDIyODg4M2IzZDhi"
        + "MTY1YmYyMDA5NzMzMWM2YjI3MjRmNDM+IC9WIDUgPj4KZW5kb2JqCnhyZWYKMCA2CjAwMDAwMDAwMDAgNjU1MzUgZiAKMDAw"
        + "MDAwMDAxNSAwMDAwMCBuIAowMDAwMDAwMTMwIDAwMDAwIG4gCjAwMDAwMDAxODkgMDAwMDAgbiAKMDAwMDAwMDI5NSAwMDAw"
        + "MCBuIAowMDAwMDAwNDI5IDAwMDAwIG4gCnRyYWlsZXIgPDwgL1Jvb3QgMSAwIFIgL1NpemUgNiAvSUQgWzxhNWQzNWM4NDFh"
        + "OTYwNjcyNDI0YzgxNzU2MzY2MjY4Zj48YTVkMzVjODQxYTk2MDY3MjQyNGM4MTc1NjM2NjI2OGY+XSAvRW5jcnlwdCA1IDAg"
        + "UiA+PgpzdGFydHhyZWYKOTc2CiUlRU9GCg==";

    private const string ProducerBDivergentPdfBase64 =
        "JVBERi0xLjcKJfbk/N8KMSAwIG9iago8PAovVHlwZSAvQ2F0YWxvZwovUGFnZXMgMiAwIFIKPj4KZW5kb2JqCjQgMCBvYmoK"
        + "PDwKL0xlbmd0aCA2NAo+PgpzdHJlYW0NCvotqgw9iyAIcDnzjng5Iw+h2eyAjY+q6zCBeC8OEb+YIYu5e82Rc8tBUnIHEffQ"
        + "Fiu8PfOHoN+M3o5Tcnz9x6INCmVuZHN0cmVhbQplbmRvYmoKNSAwIG9iago8PAovTGVuZ3RoIDEyOAovVHlwZSAvT2JqU3Rt"
        + "Ci9OIDIKL0ZpbHRlciAvRmxhdGVEZWNvZGUKL0ZpcnN0IDkKPj4Kc3RyZWFtDQoVtJevF8q0d6k88heur5YzIsaO/IniwTjz"
        + "F5pVaYS7Bv8UBCYVeUaaFs6mOA4cKCpQOnBoZEul3VhW7zYITdXn267hSRgfWzqjAfF/YE5KWa2XUNKa+SkVK+JmirgMSHkf"
        + "hlHc9XFNxUvno7E8yRgAD0t++UYR18HS7W2szizrTg0KZW5kc3RyZWFtCmVuZG9iago2IDAgb2JqCjw8Ci9GaWx0ZXIgL1N0"
        + "YW5kYXJkCi9WIDUKL1IgNgovTGVuZ3RoIDI1NgovUCAtNAovVSA8QkYyNUI2RTJEMEJFNjc4NUIxNDJFRjI1RDUzNkVENjQw"
        + "MkRFRDI2RTdFQ0M2REM3MTZFMkQ2MTBBMjY4QjM3RTczNDM4Rjg1M0RBNTk3RkE1NjRCNTI4RjVDNTEzOTI2PgovVUUgPDdB"
        + "NjI2OEMyMTNGMjk3QThEOTExQTA4MkUwQzgyMEY3NzNGQ0QxOTlBOTA2OUUyRkZGNzM1RkY2RDAxNDZGRDA+Ci9PIDxEMTFE"
        + "REUxNDEwQTlGRjhBRjY0OEQ4MkEwQ0EzRTBFOTlGRTQ2NjdBNTJEMEQ2MjRCQkZDREU0MTM1RDZGMTdDNTg5RjE4NUE0MTc4"
        + "ODE2NTAwNjQ3MjE0RjgyNDlEMzM+Ci9PRSA8QzI2QTcxMTM5QTUxREQyNjc1NEVBMzY4ODBEOEFGMzIyQUEzQTIzNTI3RDgw"
        + "OUUyRDAwQkYyQTQ2REQxQTE5Qz4KL0NGIDw8Ci9TdGRDRiA8PAovQ0ZNIC9BRVNWMwovTGVuZ3RoIDI1Ngo+Pgo+PgovU3Rt"
        + "RiAvU3RkQ0YKL1N0ckYgL1N0ZENGCi9QZXJtcyA8RjQyMzk5MjU2OUI5MDJGRkZENjRCNUVDQjhERUQ5MTU+Cj4+CmVuZG9i"
        + "ago3IDAgb2JqCjw8Ci9MZW5ndGggMzQKL1Jvb3QgMSAwIFIKL0VuY3J5cHQgNiAwIFIKL0lEIFs8MTEzRjk2RUY0MEQ5OUM0"
        + "OTcxMjUxMTA1OTJDMUUzODQ+IDwxMTNGOTZFRjQwRDk5QzQ5NzEyNTExMDU5MkMxRTM4ND5dCi9UeXBlIC9YUmVmCi9TaXpl"
        + "IDgKL0luZGV4IFswIDddCi9XIFsxIDIgMV0KL0ZpbHRlciAvRmxhdGVEZWNvZGUKPj4Kc3RyZWFtDQp4nGNgYPjPyMDPwMTA"
        + "CsKMjAwODIwMWxgYGWcxAAAjYQKxDQplbmRzdHJlYW0KZW5kb2JqCnN0YXJ0eHJlZgo5MzgKJSVFT0YK";
}
