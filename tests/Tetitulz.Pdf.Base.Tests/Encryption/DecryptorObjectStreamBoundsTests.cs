// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Text;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Encryption;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Encryption;

/// <summary>
/// Bounds on the object-stream index reader inside <see cref="PdfDecryptor"/>.
///
/// <para><b>There are two ObjStm index readers in this tree.</b> The one in
/// <c>PdfParser.GetObjectStreamReader</c> has carried a <c>/N</c> cap for a long time; this one —
/// which runs on <i>every encrypted document</i> — had none at all. <c>/N</c> was accepted up to
/// <c>int.MaxValue</c> and an entry list was grown to match, one <c>ReadInteger</c> pair per
/// declared entry, from a stream that need not contain any of them.</para>
///
/// <para>Both now apply the same two rules from the same constants in <c>PdfLimits</c>: a hard cap
/// on the declared count, and a cross-check that the decoded body is large enough for the count to
/// be true. The second is the one that matters — a cap on the count alone bounds the total but not
/// the amplification, because the file is never charged for what it declares.</para>
///
/// <para>The refusal is reported rather than silent: an object stream that is skipped and one that
/// contained nothing are otherwise indistinguishable to a caller, and the rest of the document
/// still decrypts either way.</para>
/// </summary>
public sealed class DecryptorObjectStreamBoundsTests
{
    [Fact]
    public void ObjStmDeclaringAbsurdEntryCount_IsRefusedAndReported()
    {
        var decrypted = new Dictionary<int, PdfObject>();
        PdfDecryptor.ExtractObjectsFromStream(ObjStm(declaredN: 40_000_000), decrypted);

        Assert.Empty(decrypted);
        Assert.NotNull(PdfDecryptor.LastObjectStreamRejection);
        Assert.Contains("40000000", PdfDecryptor.LastObjectStreamRejection);
    }

    /// <summary>
    /// A count inside the constant cap but still far larger than the body can hold. This is the
    /// case the count cap by itself lets through, and the one that buys ~7 800x amplification.
    /// </summary>
    [Fact]
    public void ObjStmDeclaringMoreEntriesThanItsBodyCanHold_IsRefusedAndReported()
    {
        var decrypted = new Dictionary<int, PdfObject>();
        PdfDecryptor.ExtractObjectsFromStream(ObjStm(declaredN: 65536), decrypted);

        Assert.Empty(decrypted);
        Assert.NotNull(PdfDecryptor.LastObjectStreamRejection);
        Assert.Contains("262144", PdfDecryptor.LastObjectStreamRejection);   // the index bytes needed
    }

    /// <summary>
    /// The control, and the reason the two assertions above are about the bound rather than about
    /// the reader being broken: an honest object stream still yields its objects, and leaves the
    /// diagnostic slot clear.
    /// </summary>
    [Fact]
    public void HonestObjStm_StillExtractsItsObjects_AndReportsNothing()
    {
        PdfDecryptor.ExtractObjectsFromStream(ObjStm(declaredN: 40_000_000), new Dictionary<int, PdfObject>());
        Assert.NotNull(PdfDecryptor.LastObjectStreamRejection);   // the slot is dirty going in…

        var decrypted = new Dictionary<int, PdfObject>();
        PdfDecryptor.ExtractObjectsFromStream(ObjStm(declaredN: null), decrypted);

        Assert.Equal(2, decrypted.Count);
        Assert.Equal(11, ((PdfDictionary)decrypted[11]).GetInteger(new PdfName("Marker")));
        Assert.Equal(12, ((PdfDictionary)decrypted[12]).GetInteger(new PdfName("Marker")));

        // …and clean coming out. This assertion is the point of dirtying it above, and it was
        // missing: the method's name promised "AndReportsNothing" and its body never checked, so
        // the slot went un-reset for as long as the test looked like it covered this. A
        // [ThreadStatic] slot gives per-THREAD isolation, not per-DOCUMENT scope — without the
        // reset, a caller asking "was anything dropped from this document?" gets the PREVIOUS
        // document's answer.
        Assert.Null(PdfDecryptor.LastObjectStreamRejection);
    }

    /// <summary>
    /// An uncompressed object stream holding two small dictionaries.
    /// </summary>
    /// <param name="declaredN">
    /// The <c>/N</c> to write. <c>null</c> writes the truthful count, which is what makes the
    /// control a control.
    /// </param>
    private static PdfStream ObjStm(int? declaredN)
    {
        const string index  = "11 0 12 38 ";
        const string bodies = "<< /Type /TestObj /Marker 11 >>\n       "
                            + "<< /Type /TestObj /Marker 12 >>\n";

        var stream = new PdfStream { RawData = Encoding.ASCII.GetBytes(index + bodies) };
        stream.Set(PdfName.Type, PdfName.ObjStm);
        stream.Set(PdfName.N, new PdfNumber(declaredN ?? 2));
        stream.Set(PdfName.First, new PdfNumber(index.Length));
        return stream;
    }
}
