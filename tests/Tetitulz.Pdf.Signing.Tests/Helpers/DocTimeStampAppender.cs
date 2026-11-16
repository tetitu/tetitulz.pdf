// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Org.BouncyCastle.X509;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Writing;

using Tetitulz.Pdf.Signing.Tests.TestInfrastructure;

namespace Tetitulz.Pdf.Signing.Tests.Helpers;

/// <summary>
/// Appends a PAdES <c>/DocTimeStamp</c> (<c>/SubFilter /ETSI.RFC3161</c>) revision to a PDF, with
/// the RFC 3161 token minted locally.
/// <para>
/// The shipped <see cref="Timestamping.PdfDocumentTimestamper"/> can only obtain its token from a
/// live TSA over HTTP, so it cannot be used in a self-contained test. This mirrors the revision
/// shape it writes — sig dictionary, widget, AcroForm/catalog update, classic xref, /Prev — and
/// then patches the token into the reserved /Contents hole exactly as the real one does.
/// </para>
/// </summary>
internal static class DocTimeStampAppender
{
    // Same shape as the library's placeholder: four fixed-width slots, overwritten in place with
    // the real values and right-padded with spaces (which sit AFTER the closing ']').
    private const string ByteRangePlaceholder = "[0000000000 0000000000 0000000000 0000000000]";
    private const int TokenReserveBytes = 8192;

    /// <param name="indirectSubFilter">
    /// Writes <c>/SubFilter</c> as an indirect reference to an object holding
    /// <c>/ETSI.RFC3161</c>, which §7.3.10 permits. The revision stays internally consistent and
    /// the token is minted over the final bytes either way, so the timestamp is genuinely valid in
    /// both spellings — the ONLY difference is how the name is written.
    /// </param>
    public static byte[] Append(
        byte[] pdf,
        SigningTestCrypto.Issued tsa,
        DateTime genTime,
        string fieldName = "DocTimeStamp1",
        bool indirectSubFilter = false,
        params X509Certificate[] tsaChain)
        => AppendCore(pdf, tsa, genTime, fieldName, indirectSubFilter, "SHA-256", tsaChain);

    /// <summary>
    /// The same revision with the RFC 3161 token minted under <paramref name="tokenDigestName"/> —
    /// "SHA-1" for the legacy-TSA case the weak-algorithm policy has to refuse.
    /// </summary>
    /// <remarks>
    /// A separate name rather than an optional parameter: <see cref="Append"/> ends in a
    /// <c>params</c> array, so an optional argument inserted before it would silently rebind every
    /// existing call site's chain certificate.
    /// </remarks>
    public static byte[] AppendWithTokenDigest(
        byte[] pdf,
        SigningTestCrypto.Issued tsa,
        DateTime genTime,
        string tokenDigestName,
        string fieldName = "DocTimeStamp1",
        params X509Certificate[] tsaChain)
        => AppendCore(pdf, tsa, genTime, fieldName, false, tokenDigestName, tsaChain);

    private static byte[] AppendCore(
        byte[] pdf,
        SigningTestCrypto.Issued tsa,
        DateTime genTime,
        string fieldName,
        bool indirectSubFilter,
        string tokenDigestName,
        X509Certificate[] tsaChain)
    {
        int maxObj;
        long prevXref;
        int catalogObjNum;
        int acroFormObjNum = 0;
        PdfDictionary catalogClone;
        PdfDictionary acroFormClone;
        PdfArray fieldsArr = new();
        PdfObject? idObj;

        using (var read = new MemoryStream(pdf, writable: false))
        using (var doc = PdfDocument.Open(read))
        {
            maxObj = doc.CrossReferenceTable.GetMaxObjectNumber();
            prevXref = doc.Parser.StartXrefOffset;
            catalogObjNum = ((PdfReference)doc.Trailer[PdfName.Root]).ReferenceObjectNumber;
            idObj = doc.Trailer[PdfName.ID];

            catalogClone = PdfDictionary.CloneShallow(doc.Catalog.Dictionary);

            var acroFormObj = catalogClone[PdfName.AcroForm];
            PdfDictionary? existingAcroForm = null;
            if (acroFormObj is PdfReference afRef)
            {
                acroFormObjNum = afRef.ReferenceObjectNumber;
                existingAcroForm = doc.ResolveObject(acroFormObj) as PdfDictionary;
            }
            else if (acroFormObj is PdfDictionary afDict)
            {
                existingAcroForm = afDict;
            }

            acroFormClone = existingAcroForm != null
                ? PdfDictionary.CloneShallow(existingAcroForm)
                : new PdfDictionary();

            if (existingAcroForm?[PdfName.Fields] is PdfArray existingFields)
                for (int i = 0; i < existingFields.Count; i++)
                    fieldsArr.Add(existingFields[i]);
        }

        int sigDictObjNum = maxObj + 1;
        int fieldObjNum = maxObj + 2;
        int subFilterObjNum = maxObj + 3;
        int newSize = indirectSubFilter ? maxObj + 4 : maxObj + 3;

        fieldsArr.Add(new PdfReference(fieldObjNum, 0));
        acroFormClone.Set(PdfName.Fields, fieldsArr);
        acroFormClone.Set(PdfName.SigFlags, new PdfNumber(3));

        var output = new MemoryStream();
        output.Write(pdf, 0, pdf.Length);
        var writer = new PdfWriter(output);
        var xref = new List<(int objNum, long offset)>();

        // ---- 1. Signature dictionary with the ByteRange / Contents placeholders. ----
        long sigDictOffset = output.Position;
        writer.WriteRaw($"{sigDictObjNum} 0 obj\n");
        writer.WriteRaw("<< /Type /Sig /Filter /Adobe.PPKLite /SubFilter ");
        writer.WriteRaw(indirectSubFilter ? $"{subFilterObjNum} 0 R" : "/ETSI.RFC3161");
        writer.WriteRaw(" /ByteRange ");
        long byteRangePos = output.Position;
        writer.WriteRaw(ByteRangePlaceholder);
        writer.WriteRaw(" /Contents <");
        long contentsStart = output.Position;
        writer.WriteRaw(new string('0', TokenReserveBytes * 2));
        long contentsEnd = output.Position;
        writer.WriteRaw(">");
        writer.WriteRaw(" >>\nendobj\n");
        xref.Add((sigDictObjNum, sigDictOffset));

        // ---- 2. The widget carrying the timestamp field. ----
        long fieldOffset = output.Position;
        writer.WriteRaw($"{fieldObjNum} 0 obj\n");
        writer.WriteRaw("<< /Type /Annot /Subtype /Widget /FT /Sig");
        writer.WriteRaw($" /T ({fieldName}) /V {sigDictObjNum} 0 R /F 4 /Rect [0 0 0 0] >>\n");
        writer.WriteRaw("endobj\n");
        xref.Add((fieldObjNum, fieldOffset));

        // ---- 2b. The /SubFilter name as its own object, when asked for. ----
        if (indirectSubFilter)
        {
            long subFilterOffset = output.Position;
            writer.WriteRaw($"{subFilterObjNum} 0 obj\n/ETSI.RFC3161\nendobj\n");
            xref.Add((subFilterObjNum, subFilterOffset));
        }

        // ---- 3. AcroForm + catalog. ----
        if (acroFormObjNum > 0)
        {
            xref.Add((acroFormObjNum, writer.WriteIndirectObject(acroFormObjNum, 0, acroFormClone)));
            catalogClone.Set(PdfName.AcroForm, new PdfReference(acroFormObjNum, 0));
        }
        else
        {
            catalogClone.Set(PdfName.AcroForm, acroFormClone);
        }
        xref.Add((catalogObjNum, writer.WriteIndirectObject(catalogObjNum, 0, catalogClone)));

        // ---- 4. xref + trailer. ----
        long xrefOffset = writer.WriteXrefTable(xref);
        var trailer = new PdfDictionary();
        trailer.Set(PdfName.Size, new PdfNumber(newSize));
        trailer.Set(PdfName.Root, new PdfReference(catalogObjNum, 0));
        trailer.Set(PdfName.Prev, new PdfNumber(prevXref));
        if (idObj != null) trailer.Set(PdfName.ID, idObj);
        writer.WriteTrailer(trailer, xrefOffset);

        // ---- 5. Patch the real ByteRange over the placeholder. ----
        // Through the library's own SignatureByteRange, so this stand-in cannot drift from the
        // method it stands in for. It had drifted: PdfDocumentTimestamper left the '<' inside the
        // signed range and this fixture did not, which is how the shipped arithmetic came to be
        // covered by nothing at all.
        var range = SignatureByteRange.Compute(contentsStart, contentsEnd, output.Length);
        long br1 = range.BeforeLength;
        long br2 = range.AfterStart;
        long br3 = range.AfterLength;

        byte[] brBytes = Encoding.ASCII.GetBytes(range.Format(ByteRangePlaceholder.Length));
        output.Position = byteRangePos;
        output.Write(brBytes, 0, brBytes.Length);

        // ---- 6. Mint a token over exactly those two ranges and patch it in. ----
        byte[] all = output.ToArray();
        byte[] signed = new byte[br1 + br3];
        Array.Copy(all, 0, signed, 0, (int)br1);
        Array.Copy(all, (int)br2, signed, (int)br1, (int)br3);

        byte[] token = SigningTestCrypto.TimestampToken(signed, tsa, genTime, tokenDigestName, tsaChain);
        if (token.Length > TokenReserveBytes)
            throw new InvalidOperationException(
                $"Token of {token.Length} bytes exceeds the {TokenReserveBytes}-byte reserve.");

        string hex = BitConverter.ToString(token).Replace("-", "").ToLowerInvariant()
                                 .PadRight(TokenReserveBytes * 2, '0');
        output.Position = contentsStart;
        output.Write(Encoding.ASCII.GetBytes(hex), 0, hex.Length);
        output.Flush();

        return output.ToArray();
    }
}
