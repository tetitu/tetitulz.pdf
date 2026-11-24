// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Linq;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Document;

/// <summary>
/// <c>DocMdpAnalyzer.SignatureReferences</c> — the one reader of a signature's <c>/Reference</c>,
/// for the DocMDP analysis and the FieldMDP one alike.
/// </summary>
/// <remarks>
/// <para><b>Why this exists, and why it is one method.</b> ISO 32000-2 Table 252 gives
/// <c>/Reference</c> as an <i>array</i> of signature reference dictionaries, and producers also
/// write a lone dictionary. <c>DocMdpAnalyzer</c> accepted both shapes; <c>FieldMdpAnalyzer</c>
/// required an array. A file whose <c>/Reference</c> was a single dictionary therefore had its
/// DocMDP certification <b>honoured</b> and its FieldMDP lock <b>silently ignored</b>.</para>
///
/// <para><b>That asymmetry failed in the unsafe direction.</b> The lock is the entry that says a
/// signed field may not change, so the strict reading dropped the half that <i>enforces</i> while
/// the lenient reading governed the half that <i>grants</i>. A document could be certified at a
/// permission level the reader honoured, with a field lock the reader never checked, and nothing
/// reported either fact.</para>
///
/// <para>Consolidating the two readings makes the shapes a single question — and, per CLAUDE.md's
/// rule 5, the survivor of that merge is what has to be covered, which is what this file is.</para>
/// </remarks>
public sealed class SignatureReferencesTests
{
    /// <summary>A resolver over a small object map, as every caller of the reader supplies.</summary>
    private static PdfObject Resolve(Dictionary<int, PdfObject> objects, PdfObject o)
        => o is PdfReference r && objects.TryGetValue(r.ReferenceObjectNumber, out PdfObject? target)
            ? target : o;

    private static PdfDictionary Reference(string transformMethod)
    {
        var d = new PdfDictionary();
        d.Set(PdfName.TransformMethod, new PdfName(transformMethod));
        return d;
    }

    private static PdfDictionary SignatureWith(PdfObject referenceValue)
    {
        var sig = new PdfDictionary();
        sig.Set(PdfName.Type, PdfName.Sig);
        sig.Set(PdfName.Reference, referenceValue);
        return sig;
    }

    private static List<PdfDictionary> Read(
        PdfDictionary sig, Dictionary<int, PdfObject>? objects = null)
    {
        var map = objects ?? new Dictionary<int, PdfObject>();
        return DocMdpAnalyzer.SignatureReferences(sig, o => Resolve(map, o)).ToList();
    }

    // ── the two shapes ────────────────────────────────────────────────────────────────

    /// <summary>Table 252's array form, in order.</summary>
    [Fact]
    public void AnArrayOfReferences_IsReadInOrder()
    {
        var array = new PdfArray();
        array.Add(Reference("DocMDP"));
        array.Add(Reference("FieldMDP"));

        var read = Read(SignatureWith(array));

        Assert.Equal(2, read.Count);
        Assert.Equal("DocMDP", ((PdfName)read[0][PdfName.TransformMethod]).Value);
        Assert.Equal("FieldMDP", ((PdfName)read[1][PdfName.TransformMethod]).Value);
    }

    /// <summary>
    /// <b>The lone dictionary.</b> Table 252 says array, and a
    /// reader that refuses this silently discards the transform the entry exists to declare.
    /// </summary>
    [Fact]
    public void ALoneDictionary_IsReadAsASingleReference()
    {
        var read = Read(SignatureWith(Reference("FieldMDP")));

        PdfDictionary only = Assert.Single(read);
        Assert.Equal("FieldMDP", ((PdfName)only[PdfName.TransformMethod]).Value);
    }

    // ── §7.3.10: any of these may be indirect ────────────────────────────────────────

    [Fact]
    public void AnIndirectArray_IsFollowed()
    {
        var array = new PdfArray();
        array.Add(Reference("FieldMDP"));
        var objects = new Dictionary<int, PdfObject> { [4] = array };

        var read = Read(SignatureWith(new PdfReference(4, 0)), objects);

        Assert.Single(read);
    }

    [Fact]
    public void AnIndirectLoneDictionary_IsFollowed()
    {
        var objects = new Dictionary<int, PdfObject> { [7] = Reference("DocMDP") };

        var read = Read(SignatureWith(new PdfReference(7, 0)), objects);

        Assert.Single(read);
    }

    [Fact]
    public void AnIndirectElementInsideTheArray_IsFollowed()
    {
        var array = new PdfArray();
        array.Add(new PdfReference(9, 0));
        var objects = new Dictionary<int, PdfObject> { [9] = Reference("FieldMDP") };

        var read = Read(SignatureWith(array), objects);

        PdfDictionary only = Assert.Single(read);
        Assert.Equal("FieldMDP", ((PdfName)only[PdfName.TransformMethod]).Value);
    }

    // ── what is skipped rather than guessed at ───────────────────────────────────────

    /// <summary>
    /// A non-dictionary element contributes nothing, and the elements around it still do — a
    /// damaged array must not cost the references it does carry.
    /// </summary>
    [Fact]
    public void ANonDictionaryElement_IsSkippedWithoutLosingTheOthers()
    {
        var array = new PdfArray();
        array.Add(new PdfNumber(42));
        array.Add(Reference("FieldMDP"));
        array.Add(PdfNull.Instance);

        var read = Read(SignatureWith(array));

        Assert.Single(read);
    }

    [Fact]
    public void AnUnresolvableReference_YieldsNothing()
    {
        // Object 3 is not in the map: the reference resolves to itself, which is not a dictionary.
        var read = Read(SignatureWith(new PdfReference(3, 0)));

        Assert.Empty(read);
    }

    [Fact]
    public void AReferenceValueThatIsNeitherArrayNorDictionary_YieldsNothing()
        => Assert.Empty(Read(SignatureWith(new PdfNumber(1))));

    [Fact]
    public void ASignatureWithNoReferenceEntry_YieldsNothing()
    {
        var sig = new PdfDictionary();
        sig.Set(PdfName.Type, PdfName.Sig);

        Assert.Empty(Read(sig));
    }

    [Fact]
    public void ANullSignature_YieldsNothing()
        => Assert.Empty(DocMdpAnalyzer.SignatureReferences(null, o => o).ToList());
}
