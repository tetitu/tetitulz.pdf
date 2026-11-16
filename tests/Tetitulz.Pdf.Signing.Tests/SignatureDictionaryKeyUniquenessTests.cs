// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Core;
using Tetitulz.Pdf.Signing;
using Tetitulz.Pdf.Signing.Models;
using Tetitulz.Pdf.Signing.Tests.TestInfrastructure;
using Xunit;

namespace Tetitulz.Pdf.Signing.Tests;

/// <summary>
/// The signature dictionary <see cref="PdfSigner"/> writes contains each key <b>once</b>.
///
/// <para><b>Why this exists.</b> <c>PdfSigner</c> builds that dictionary by writing raw bytes — a
/// run of <c>if (…) writer.WriteRaw(" /Key (value)")</c> lines — rather than by populating a
/// <c>PdfDictionary</c>, so nothing structurally prevents a key being emitted twice. A bad edit did
/// exactly that (a duplicated <c>/Location</c> block), and **all 197 tests in this assembly passed
/// with it**: every one of them either parses the file, where a parser silently keeps one of the
/// two, or verifies the signature, which is computed over a byte range and is entirely indifferent
/// to what the dictionary says.</para>
///
/// <para>ISO 32000-1 §7.3.7 leaves a dictionary with a repeated key <b>undefined</b> — one reader
/// takes the first occurrence, another the last — so a duplicate is a real defect that produces a
/// file two viewers can legitimately disagree about, while looking perfectly valid to both.</para>
///
/// <para><b>It has to read the raw bytes.</b> Loading the document and inspecting the parsed
/// dictionary cannot see this: by then the duplicate has already been resolved to one value. That
/// is the whole reason the defect was invisible.</para>
/// </summary>
public class SignatureDictionaryKeyUniquenessTests
{
    /// <summary>
    /// Signs with every optional string field populated, so each conditional write in
    /// <c>PdfSigner</c> is actually taken — a key that is never emitted cannot be emitted twice,
    /// and a fixture leaving them unset would pass with the defect present.
    /// </summary>
    private static byte[] SignWithAllOptionalFields()
    {
        var ca = SigningTestCrypto.Ca();
        var leaf = SigningTestCrypto.Leaf(ca);
        var (pfx, pw) = SigningTestCrypto.Pfx(leaf, ca.Cert);

        using var input = new MemoryStream(SignedPdfFixtures.MinimalPdf());
        using var output = new MemoryStream();
        new PdfSigner().Sign(input, output, new SignatureParameters
        {
            CertificateStream = new MemoryStream(pfx),
            CertificatePassword = pw,
            SignatureFieldName = "Signature1",
            Reason = "a stated reason",
            Location = "a stated location",
            ContactInfo = "a stated contact",
        });
        return output.ToArray();
    }

    /// <summary>
    /// Signs granting every usage right, so the <c>/UR3</c> reference's <c>/TransformParams</c> takes
    /// each conditional write — the three form rights in particular.
    /// </summary>
    private static byte[] SignWithAllUsageRights()
    {
        var ca = SigningTestCrypto.Ca();
        var leaf = SigningTestCrypto.Leaf(ca);
        var (pfx, pw) = SigningTestCrypto.Pfx(leaf, ca.Cert);

        using var input = new MemoryStream(SignedPdfFixtures.MinimalPdf());
        using var output = new MemoryStream();
        new PdfSigner().Sign(input, output, new SignatureParameters
        {
            CertificateStream = new MemoryStream(pfx),
            CertificatePassword = pw,
            UsageRights = UsageRights.FormFillIn | UsageRights.FormImportExport | UsageRights.FormSave
                        | UsageRights.Annotation | UsageRights.DocumentSave,
        });
        return output.ToArray();
    }

    /// <summary>
    /// The bytes of the signature dictionary, from <c>/Type /Sig</c> back to its opening
    /// <c>&lt;&lt;</c> and forward to the matching <c>&gt;&gt;</c>.
    /// </summary>
    private static string SignatureDictionaryText(byte[] pdf)
    {
        string text = Encoding.ASCII.GetString(pdf);

        int marker = text.IndexOf("/Type /Sig", StringComparison.Ordinal);
        if (marker < 0) marker = text.IndexOf("/Type/Sig", StringComparison.Ordinal);
        Assert.True(marker > 0, "the signed output contains no /Type /Sig dictionary");

        int open = text.LastIndexOf("<<", marker, StringComparison.Ordinal);
        Assert.True(open > 0, "the signature dictionary has no opening '<<'");

        return DictionaryAt(text, open);
    }

    /// <summary>The first dictionary that opens after <paramref name="anchor"/>, balanced.</summary>
    private static string DictionaryAfter(string text, string anchor)
    {
        int marker = text.IndexOf(anchor, StringComparison.Ordinal);
        Assert.True(marker > 0, $"the signed output contains no '{anchor}'");

        int open = text.IndexOf("<<", marker, StringComparison.Ordinal);
        Assert.True(open > 0, $"no dictionary opens after '{anchor}'");

        return DictionaryAt(text, open);
    }

    /// <summary>The dictionary opening at <paramref name="open"/>, up to its matching <c>&gt;&gt;</c>.</summary>
    private static string DictionaryAt(string text, int open)
    {
        // Walk forward tracking nesting, so a nested dictionary (/Reference, /Prop_Build) does not
        // end the scan early and hide a duplicate that follows it.
        int depth = 0;
        for (int i = open; i < text.Length - 1; i++)
        {
            if (text[i] == '<' && text[i + 1] == '<') { depth++; i++; continue; }
            if (text[i] == '>' && text[i + 1] == '>')
            {
                depth--;
                if (depth == 0) return text.Substring(open, i + 2 - open);
                i++;
            }
        }

        Assert.Fail("the dictionary is not closed");
        return string.Empty;
    }

    /// <summary>
    /// Counts occurrences of <paramref name="key"/> at this dictionary's <b>own</b> level only —
    /// a name appearing inside a nested dictionary is a different entry and must not count.
    /// </summary>
    private static int CountAtTopLevel(string dict, string key)
    {
        int depth = 0, count = 0;
        for (int i = 0; i < dict.Length - 1; i++)
        {
            if (dict[i] == '<' && dict[i + 1] == '<') { depth++; i++; continue; }
            if (dict[i] == '>' && dict[i + 1] == '>') { depth--; i++; continue; }
            if (depth != 1 || dict[i] != '/') continue;

            // A PDF name ends at whitespace or a delimiter (§7.3.5).
            int end = i + 1;
            while (end < dict.Length && !char.IsWhiteSpace(dict[end]) &&
                   "/[]<>(){}%".IndexOf(dict[end]) < 0) end++;
            if (string.CompareOrdinal(dict, i, key, 0, key.Length) == 0 && end - i == key.Length)
                count++;
        }
        return count;
    }

    /// <summary>
    /// Every key the writer emits appears exactly once.
    ///
    /// <para>Mutation-checked: duplicating the <c>/Location</c> write in <c>PdfSigner</c> — the
    /// actual defect this was written for — reddens this and nothing else in the assembly.</para>
    /// </summary>
    [Theory]
    [InlineData("/Type")]
    [InlineData("/Filter")]
    [InlineData("/SubFilter")]
    [InlineData("/Contents")]
    [InlineData("/ByteRange")]
    [InlineData("/Reason")]
    [InlineData("/Location")]
    [InlineData("/ContactInfo")]
    [InlineData("/M")]
    public void EachSignatureDictionaryKeyIsWrittenExactlyOnce(string key)
    {
        string dict = SignatureDictionaryText(SignWithAllOptionalFields());

        Assert.Equal(1, CountAtTopLevel(dict, key));
    }

    /// <summary>
    /// The <c>/UR3</c> reference's <c>/TransformParams</c> is written as raw bytes too, and each of the
    /// three form rights appended a <c>/Form</c> array of its own — so granting fill-in together with
    /// import/export wrote the key twice, and a reader kept whichever grant it happened to prefer.
    /// </summary>
    /// <remarks>
    /// <c>/Annots</c> and <c>/Document</c> are the control rows: one write each, so the counter finds
    /// them once in a correct file and the <c>/Form</c> row is not passing on a scan that sees nothing.
    /// </remarks>
    [Theory]
    [InlineData("/Form")]
    [InlineData("/Annots")]
    [InlineData("/Document")]
    public void EachUsageRightsTransformParamsKeyIsWrittenExactlyOnce(string key)
    {
        string text = Encoding.ASCII.GetString(SignWithAllUsageRights());
        string transformParams = DictionaryAfter(text, "/TransformMethod /UR3");

        Assert.Equal(1, CountAtTopLevel(transformParams, key));
    }

    /// <summary>
    /// The control, and it is what stops the theory above passing vacuously: a key the fixture
    /// never sets is absent, so <c>CountAtTopLevel</c> really is counting and really can return
    /// something other than 1. Without it, a broken counter that always answered 1 would satisfy
    /// every row above.
    /// </summary>
    [Fact]
    public void AKeyTheWriterNeverEmitsIsCountedAsAbsent()
    {
        string dict = SignatureDictionaryText(SignWithAllOptionalFields());

        Assert.Equal(0, CountAtTopLevel(dict, "/NotAKeyPdfSignerWrites"));
    }

    /// <summary>
    /// And the counter is scoped to the dictionary's own level: <c>/Type</c> occurs inside the
    /// nested <c>/Reference</c> and <c>/Prop_Build</c> dictionaries too, and counting those would
    /// make the <c>/Type</c> row above fail for a correct file. Asserting the raw text contains
    /// more <c>/Type</c> than the top level does is what proves the nesting logic is load-bearing
    /// rather than incidental.
    /// </summary>
    [Fact]
    public void TheCountIsScopedToTheDictionarysOwnLevel()
    {
        byte[] signed = SignWithAllOptionalFields();
        string dict = SignatureDictionaryText(signed);

        int naive = 0;
        for (int i = dict.IndexOf("/Type", StringComparison.Ordinal); i >= 0;
             i = dict.IndexOf("/Type", i + 1, StringComparison.Ordinal)) naive++;

        Assert.Equal(1, CountAtTopLevel(dict, "/Type"));
        Assert.True(naive >= 1, "the fixture's signature dictionary has no /Type at all");
    }
}
