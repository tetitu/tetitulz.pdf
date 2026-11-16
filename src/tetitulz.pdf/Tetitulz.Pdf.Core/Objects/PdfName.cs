// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;

namespace Tetitulz.Pdf.Core.Objects
{
    public sealed class PdfName : PdfObject, IEquatable<PdfName>
    {
        public string Value { get; }

        public PdfName(string value)
        {
            Value = value ?? throw new ArgumentNullException(nameof(value));
        }

        /// <summary>A name read from a file, its bytes not UTF-8 when <paramref name="bytesNotUtf8"/>.</summary>
        public PdfName(string value, bool bytesNotUtf8) : this(value)
        {
            BytesNotUtf8 = bytesNotUtf8;
        }

        /// <summary>
        /// True when this name was read from bytes that are not valid UTF-8, so <see cref="Value"/> is
        /// their Latin-1 reading — what ISO 32000-2 §7.3.5 does not allow and PDF/A-4 checks (ISO
        /// 19005-4 6.1.7). The value cannot say it (<c>é</c> reads from <c>C3 A9</c> and from <c>E9</c>
        /// alike), so the parser carries it here. Equality and hashing ignore it: two names of one value
        /// are the same key however their bytes were spelled.
        /// </summary>
        public bool BytesNotUtf8 { get; }

        // Well-known names
        public static readonly PdfName Type = new PdfName("Type");
        public static readonly PdfName Subtype = new PdfName("Subtype");
        public static readonly PdfName Page = new PdfName("Page");
        public static readonly PdfName Pages = new PdfName("Pages");
        public static readonly PdfName Catalog = new PdfName("Catalog");
        public static readonly PdfName Count = new PdfName("Count");
        public static readonly PdfName Kids = new PdfName("Kids");
        public static readonly PdfName Parent = new PdfName("Parent");
        public static readonly PdfName MediaBox = new PdfName("MediaBox");
        public static readonly PdfName CropBox = new PdfName("CropBox");
        public static readonly PdfName Resources = new PdfName("Resources");
        public static readonly PdfName Contents = new PdfName("Contents");
        public static readonly PdfName Rotate = new PdfName("Rotate");
        public static readonly PdfName Annots = new PdfName("Annots");
        public static readonly PdfName Root = new PdfName("Root");
        public static readonly PdfName Size = new PdfName("Size");
        public static readonly PdfName Prev = new PdfName("Prev");
        public static readonly PdfName Info = new PdfName("Info");
        public static readonly PdfName ID = new PdfName("ID");
        public static readonly PdfName Encrypt = new PdfName("Encrypt");
        public static readonly PdfName Length = new PdfName("Length");
        public static readonly PdfName Filter = new PdfName("Filter");
        public static readonly PdfName DecodeParms = new PdfName("DecodeParms");
        /// <summary><c>/DL</c> — a stream's decoded length as its producer declared it (ISO 32000-2
        /// Table 5): read before a bounded decode, never trusted in place of one.</summary>
        public static readonly PdfName DL = new PdfName("DL");
        public static readonly PdfName FlateDecode = new PdfName("FlateDecode");
        public static readonly PdfName LZWDecode = new PdfName("LZWDecode");
        public static readonly PdfName BrotliDecode = new PdfName("BrotliDecode");
        public static readonly PdfName ASCII85Decode = new PdfName("ASCII85Decode");
        public static readonly PdfName ASCIIHexDecode = new PdfName("ASCIIHexDecode");
        public static readonly PdfName Columns = new PdfName("Columns");
        public static readonly PdfName Predictor = new PdfName("Predictor");
        public static readonly PdfName Colors = new PdfName("Colors");
        public static readonly PdfName BitsPerComponent = new PdfName("BitsPerComponent");
        public static readonly PdfName W = new PdfName("W");
        public static readonly PdfName Index = new PdfName("Index");
        public static readonly PdfName XRef = new PdfName("XRef");
        public static readonly PdfName ObjStm = new PdfName("ObjStm");
        public static readonly PdfName N = new PdfName("N");
        public static readonly PdfName First = new PdfName("First");
        public static readonly PdfName Font = new PdfName("Font");
        public static readonly PdfName XObject = new PdfName("XObject");
        public static readonly PdfName ExtGState = new PdfName("ExtGState");
        public static readonly PdfName ColorSpace = new PdfName("ColorSpace");
        /// <summary><c>/Colorants</c> — a DeviceN attributes dictionary's spot colorants, each a
        /// Separation colour space (ISO 32000-1 Table 71).</summary>
        public static readonly PdfName Colorants = new PdfName("Colorants");
        public static readonly PdfName Pattern = new PdfName("Pattern");
        /// <summary><c>/ProcSet</c> — a resource dictionary's legacy procedure-set array;
        /// deprecated in PDF 2.0 (ISO 32000-2:2020 §0.3), safe to ignore on write and read.</summary>
        public static readonly PdfName ProcSet = new PdfName("ProcSet");
        /// <summary><c>/CharProcs</c> — a Type 3 font's glyph content streams (§9.6.5).</summary>
        public static readonly PdfName CharProcs = new PdfName("CharProcs");
        /// <summary><c>/Renditions</c> — the <c>/Names</c> dictionary's renditions name tree (§13.2.3).</summary>
        public static readonly PdfName Renditions = new PdfName("Renditions");
        /// <summary><c>/Trapped</c> — Info-dictionary trapping state; ISO 15930 requires it.</summary>
        public static readonly PdfName Trapped = new PdfName("Trapped");
        /// <summary><c>/OPI</c> — Open Prepress Interface dictionary; PDF/X forbids it.</summary>
        public static readonly PdfName OPI = new PdfName("OPI");
        /// <summary><c>/OutputConditionIdentifier</c> — required entry of an output intent.</summary>
        public static readonly PdfName OutputConditionIdentifier = new PdfName("OutputConditionIdentifier");
        /// <summary><c>/DestOutputProfile</c> — the output intent's embedded ICC profile.</summary>
        public static readonly PdfName DestOutputProfile = new PdfName("DestOutputProfile");
        /// <summary><c>/OutputIntent</c> — the <c>/Type</c> of an output intent dictionary (§14.11.5, Table 401).</summary>
        public static readonly PdfName OutputIntent = new PdfName("OutputIntent");
        /// <summary><c>/OutputCondition</c> — an output intent's human-readable condition (Table 401).</summary>
        public static readonly PdfName OutputCondition = new PdfName("OutputCondition");
        /// <summary><c>/RegistryName</c> — the registry an output intent's identifier is defined in (Table 401).</summary>
        public static readonly PdfName RegistryName = new PdfName("RegistryName");
        public static readonly PdfName BaseFont = new PdfName("BaseFont");
        public static readonly PdfName Encoding = new PdfName("Encoding");
        public static readonly PdfName ToUnicode = new PdfName("ToUnicode");
        public static readonly PdfName Differences = new PdfName("Differences");
        public static readonly PdfName DescendantFonts = new PdfName("DescendantFonts");
        public static readonly PdfName CIDSystemInfo = new PdfName("CIDSystemInfo");
        public static readonly PdfName Type0 = new PdfName("Type0");
        public static readonly PdfName Type1 = new PdfName("Type1");
        public static readonly PdfName TrueType = new PdfName("TrueType");
        public static readonly PdfName CIDFontType0 = new PdfName("CIDFontType0");
        public static readonly PdfName CIDFontType2 = new PdfName("CIDFontType2");
        public static readonly PdfName WinAnsiEncoding = new PdfName("WinAnsiEncoding");
        public static readonly PdfName MacRomanEncoding = new PdfName("MacRomanEncoding");
        public static readonly PdfName StandardEncoding = new PdfName("StandardEncoding");
        public static readonly PdfName BaseEncoding = new PdfName("BaseEncoding");
        public static readonly PdfName Type3 = new PdfName("Type3");
        public static readonly PdfName IdentityH = new PdfName("Identity-H");  // the Identity-H CMap, not the /Identity crypt filter
        public static readonly PdfName Properties = new PdfName("Properties");  // resource category naming BDC/BMC property dicts
        public static readonly PdfName Tabs = new PdfName("Tabs");

        public static readonly PdfName Nums = new PdfName("Nums");

        // Signature-related names
        public static readonly PdfName Sig = new PdfName("Sig");
        public static readonly PdfName ByteRange = new PdfName("ByteRange");
        public static readonly PdfName AcroForm = new PdfName("AcroForm");
        public static readonly PdfName Fields = new PdfName("Fields");
        /// <summary><c>/CO</c> — the interactive form's calculation order (§12.7.3, Table 224).</summary>
        public static readonly PdfName CO = new PdfName("CO");
        /// <summary>The catalog key of an FDF form-data file (§12.7.7.2).</summary>
        public static readonly PdfName FDF = new PdfName("FDF");
        public static readonly PdfName SigFlags = new PdfName("SigFlags");
        public static readonly PdfName FT = new PdfName("FT");
        public static readonly PdfName T = new PdfName("T");
        public static readonly PdfName V = new PdfName("V");
        public static readonly PdfName DV = new PdfName("DV");
        /// <summary>§12.7.4.3 Table 228 — the rich-text value of a variable text field.</summary>
        public static readonly PdfName RV = new PdfName("RV");
        public static readonly PdfName Ff = new PdfName("Ff");
        public static readonly PdfName SubFilter = new PdfName("SubFilter");
        public static readonly PdfName Reason = new PdfName("Reason");
        public static readonly PdfName Location = new PdfName("Location");
        public static readonly PdfName ContactInfo = new PdfName("ContactInfo");
        public static readonly PdfName M = new PdfName("M");
        public static readonly PdfName Cert = new PdfName("Cert");
        public static readonly PdfName Rect = new PdfName("Rect");
        public static readonly PdfName F = new PdfName("F");
        public static readonly PdfName P = new PdfName("P");
        public static readonly PdfName Annot = new PdfName("Annot");
        public static readonly PdfName Widget = new PdfName("Widget");
        public static readonly PdfName AP = new PdfName("AP");
        public static readonly PdfName Reference = new PdfName("Reference");
        public static readonly PdfName Ref = new PdfName("Ref");
        public static readonly PdfName TransformMethod = new PdfName("TransformMethod");
        public static readonly PdfName TransformParams = new PdfName("TransformParams");
        public static readonly PdfName SV = new PdfName("SV");
        public static readonly PdfName DigestMethod = new PdfName("DigestMethod");
        public static readonly PdfName DocMDP = new PdfName("DocMDP");
        public static readonly PdfName DSS = new PdfName("DSS");
        // §12.8.2.4 Table 259 — the FieldMDP transform and its parameters; Table 258 — the signature
        // reference dictionary's type and the object the analysis is performed on.
        public static readonly PdfName FieldMDP = new PdfName("FieldMDP");
        public static readonly PdfName SigRef = new PdfName("SigRef");
        public static readonly PdfName Data = new PdfName("Data");
        public static readonly PdfName Include = new PdfName("Include");
        public static readonly PdfName Exclude = new PdfName("Exclude");
        public static readonly PdfName All = new PdfName("All");
        // §12.7.5.5 Table 235 — a signature field's lock and seed value dictionaries (Tables 236–238).
        public static readonly PdfName Lock = new PdfName("Lock");
        public static readonly PdfName SigFieldLock = new PdfName("SigFieldLock");
        public static readonly PdfName Reasons = new PdfName("Reasons");
        public static readonly PdfName MDP = new PdfName("MDP");
        public static readonly PdfName TimeStamp = new PdfName("TimeStamp");
        public static readonly PdfName LegalAttestation = new PdfName("LegalAttestation");
        public static readonly PdfName AddRevInfo = new PdfName("AddRevInfo");
        public static readonly PdfName LockDocument = new PdfName("LockDocument");
        public static readonly PdfName AppearanceFilter = new PdfName("AppearanceFilter");
        public static readonly PdfName SVCert = new PdfName("SVCert");
        public static readonly PdfName Issuer = new PdfName("Issuer");
        public static readonly PdfName OID = new PdfName("OID");
        public static readonly PdfName SubjectDN = new PdfName("SubjectDN");
        public static readonly PdfName KeyUsage = new PdfName("KeyUsage");
        public static readonly PdfName URL = new PdfName("URL");
        public static readonly PdfName URLType = new PdfName("URLType");

        // Metadata names
        public static readonly PdfName Title = new PdfName("Title");
        public static readonly PdfName Author = new PdfName("Author");
        public static readonly PdfName Subject = new PdfName("Subject");
        public static readonly PdfName Keywords = new PdfName("Keywords");
        public static readonly PdfName Creator = new PdfName("Creator");
        public static readonly PdfName Producer = new PdfName("Producer");
        public static readonly PdfName CreationDate = new PdfName("CreationDate");
        public static readonly PdfName ModDate = new PdfName("ModDate");
        public static readonly PdfName Metadata = new PdfName("Metadata");
        /// <summary><c>/PieceInfo</c> — a page-piece dictionary (§14.5); PDF/A-4 admits a trailer <c>/Info</c> only beside one.</summary>
        public static readonly PdfName PieceInfo = new PdfName("PieceInfo");
        /// <summary><c>/DestOutputProfileRef</c> — an output intent's reference to an external profile (ISO 32000-2 Table 401).</summary>
        public static readonly PdfName DestOutputProfileRef = new PdfName("DestOutputProfileRef");
        /// <summary><c>/Subtype2</c> — a form XObject's PostScript-passthrough marker (PDF 1.3, §8.10 note), which PDF/A forbids as <c>/PS</c>.</summary>
        public static readonly PdfName Subtype2 = new PdfName("Subtype2");
        /// <summary><c>/PresSteps</c> — a page's sub-page navigation node (§12.4.4.2), which PDF/A-2 onwards forbids.</summary>
        public static readonly PdfName PresSteps = new PdfName("PresSteps");
        /// <summary><c>/FFilter</c> — the filters of a stream's external file (Table 5), which PDF/A forbids.</summary>
        public static readonly PdfName FFilter = new PdfName("FFilter");
        /// <summary><c>/FDecodeParms</c> — the parameters of <c>/FFilter</c> (Table 5), which PDF/A forbids.</summary>
        public static readonly PdfName FDecodeParms = new PdfName("FDecodeParms");
        /// <summary><c>/XML</c> — the <c>/Subtype</c> of a metadata stream (§14.3.2, Table 318).</summary>
        public static readonly PdfName XML = new PdfName("XML");

        // ISO 32000-2 §14.12, Tables 408 and 409 — document parts.
        public static readonly PdfName DPartRoot = new PdfName("DPartRoot");
        public static readonly PdfName DPartRootNode = new PdfName("DPartRootNode");
        public static readonly PdfName DParts = new PdfName("DParts");
        /// <summary><c>/DPM</c> — a DPart's metadata dictionary.</summary>
        public static readonly PdfName DPM = new PdfName("DPM");
        /// <summary><c>/Start</c> — a DPart's first page, an indirect reference to a page object.</summary>
        public static readonly PdfName Start = new PdfName("Start");
        /// <summary><c>/End</c> — a DPart's last page, an indirect reference to a page object.</summary>
        public static readonly PdfName End = new PdfName("End");

        // ISO 32000-2 §12.9.1 Table 265 (viewports, the page's /VP) and §12.9 (measure dictionaries).
        public static readonly PdfName VP = new PdfName("VP");
        public static readonly PdfName Measure = new PdfName("Measure");

        // ISO 32000-2 §7.6.7 Table 28 — an unencrypted wrapper's encrypted payload dictionary, the
        // file specification's /EP, and the /AFRelationship value that marks the payload.
        public static readonly PdfName EP = new PdfName("EP");
        public static readonly PdfName EncryptedPayload = new PdfName("EncryptedPayload");

        // ISO 32000-1 Annex F, Table F.1 — the linearization parameter dictionary's /E. Its /L,
        // /H, /O, /N and /T are all already in this cache under other dictionaries' entries, which
        // is the point of a cache keyed by the name rather than by what uses it.
        public static readonly PdfName E = new PdfName("E");

        // Encryption-related names
        public static readonly PdfName U = new PdfName("U");
        public static readonly PdfName O = new PdfName("O");
        public static readonly PdfName R = new PdfName("R");
        public static readonly PdfName CF = new PdfName("CF");
        public static readonly PdfName StmF = new PdfName("StmF");
        public static readonly PdfName StrF = new PdfName("StrF");
        /// <summary>The crypt filter for embedded file streams (§7.6.5); <c>/StmF</c>'s when absent.</summary>
        public static readonly PdfName EFF = new PdfName("EFF");
        public static readonly PdfName UE = new PdfName("UE");
        public static readonly PdfName OE = new PdfName("OE");
        public static readonly PdfName Perms = new PdfName("Perms");
        // §12.8.4, Table 258 — the Perms dictionary's usage-rights signature. Deprecated in
        // PDF 2.0 (ISO 32000-2:2020 §0.3, "UR signatures").
        public static readonly PdfName UR3 = new PdfName("UR3");
        // ISO 32000-2 Table 256 — the usage-rights transform method as the standard names it;
        // Viewers, and this library's signer, write /UR3 there instead.
        public static readonly PdfName UR = new PdfName("UR");
        // Table 258 — the UR transform parameters' rights names.
        public static readonly PdfName FillIn = new PdfName("FillIn");
        public static readonly PdfName Import = new PdfName("Import");
        public static readonly PdfName Export = new PdfName("Export");
        public static readonly PdfName SubmitStandalone = new PdfName("SubmitStandalone");
        public static readonly PdfName FullSave = new PdfName("FullSave");
        public static readonly PdfName Create = new PdfName("Create");
        public static readonly PdfName Modify = new PdfName("Modify");
        public static readonly PdfName Copy = new PdfName("Copy");
        public static readonly PdfName AESV2 = new PdfName("AESV2");
        public static readonly PdfName AESV3 = new PdfName("AESV3");
        // ISO/TS 32003 (PDF 2.0): AES-GCM crypt filter method, declared by V=6/R=7.
        public static readonly PdfName AESV4 = new PdfName("AESV4");
        public static readonly PdfName Identity = new PdfName("Identity");
        public static readonly PdfName StdCF = new PdfName("StdCF");
        // ISO 32000-2 §7.6.4.1: the crypt filter a public-key handler names in place of StdCF.
        public static readonly PdfName DefaultCryptFilter = new PdfName("DefaultCryptFilter");
        public static readonly PdfName AuthEvent = new PdfName("AuthEvent");
        public static readonly PdfName CFM = new PdfName("CFM");
        public static readonly PdfName CryptFilterV2 = new PdfName("V2");
        public static readonly PdfName CryptFilterNone = new PdfName("None");
        public static readonly PdfName EncryptMetadata = new PdfName("EncryptMetadata");

        // ISO/TS 32004 (PDF 2.0): PDF MAC integrity protection.
        /// <summary>Encrypt dictionary — 32-byte HKDF salt (§6.4).</summary>
        public static readonly PdfName KDFSalt = new PdfName("KDFSalt");
        /// <summary>Trailer dictionary — the PDF MAC token's location and, for a standalone
        /// token, its own bytes (§5.2.3).</summary>
        public static readonly PdfName AuthCode = new PdfName("AuthCode");
        /// <summary>AuthCode dictionary — <c>/Standalone</c> or <c>/AttachedToSig</c>.</summary>
        public static readonly PdfName MACLocation = new PdfName("MACLocation");
        public static readonly PdfName MAC = new PdfName("MAC");
        public static readonly PdfName Standalone = new PdfName("Standalone");
        public static readonly PdfName AttachedToSig = new PdfName("AttachedToSig");
        /// <summary>AuthCode dictionary — indirect reference to the signature dictionary a
        /// <c>MACLocation /AttachedToSig</c> token is affixed to.</summary>
        public static readonly PdfName SigObjRef = new PdfName("SigObjRef");

        // Blockchain proof names
        public static readonly PdfName BlockchainProof = new PdfName("BlockchainProof");
        public static readonly PdfName TxHash = new PdfName("TxHash");
        public static readonly PdfName DocumentHash = new PdfName("DocumentHash");

        // Device color space names
        public static readonly PdfName DeviceGray  = new PdfName("DeviceGray");
        public static readonly PdfName DeviceRGB   = new PdfName("DeviceRGB");
        public static readonly PdfName DeviceCMYK  = new PdfName("DeviceCMYK");

        // Default colour spaces (ISO 32000-2 §8.6.5.6): a resource dictionary's /ColorSpace entries
        // that remap the matching device space for what is painted with it.
        public static readonly PdfName DefaultGray = new PdfName("DefaultGray");
        public static readonly PdfName DefaultRGB  = new PdfName("DefaultRGB");
        public static readonly PdfName DefaultCMYK = new PdfName("DefaultCMYK");

        // Image-related names
        public static readonly PdfName Image = new PdfName("Image");
        public static readonly PdfName Width = new PdfName("Width");
        public static readonly PdfName Height = new PdfName("Height");
        public static readonly PdfName Interpolate = new PdfName("Interpolate");
        /// <summary>An image XObject's alternate images (ISO 32000-1 §8.9.5.4, Table 89).</summary>
        public static readonly PdfName Alternates = new PdfName("Alternates");
        /// <summary><c>/Intent</c> — an image XObject's rendering intent, §8.9.5.2 Table 89. It
        /// overrides the graphics state's <c>/RI</c> for that image alone.</summary>
        public static readonly PdfName Intent = new PdfName("Intent");
        public static readonly PdfName H   = new PdfName("H");    // abbreviated Height in inline images
        public static readonly PdfName CS  = new PdfName("CS");   // abbreviated ColorSpace in inline images
        public static readonly PdfName BPC = new PdfName("BPC");  // abbreviated BitsPerComponent in inline images
        public static readonly PdfName I   = new PdfName("I");    // abbreviated Interpolate in inline images
        public static readonly PdfName DP  = new PdfName("DP");   // abbreviated DecodeParms in inline images
        public static readonly PdfName SMask = new PdfName("SMask");
        // JPXDecode only: 0/absent = ignore any opacity channel in the codestream,
        // 1 = straight alpha, 2 = premultiplied alpha (ISO 32000-1 Table 89).
        public static readonly PdfName SMaskInData = new PdfName("SMaskInData");
        public static readonly PdfName ImageMask = new PdfName("ImageMask");
        public static readonly PdfName Mask      = new PdfName("Mask");   // stencil or colour-key mask
        public static readonly PdfName DCTDecode = new PdfName("DCTDecode");
        public static readonly PdfName JPXDecode = new PdfName("JPXDecode");
        public static readonly PdfName CCITTFaxDecode = new PdfName("CCITTFaxDecode");
        public static readonly PdfName RunLengthDecode = new PdfName("RunLengthDecode");
        public static readonly PdfName JBIG2Decode = new PdfName("JBIG2Decode");
        public static readonly PdfName JBIG2Globals = new PdfName("JBIG2Globals");
        public static readonly PdfName Crypt = new PdfName("Crypt");
        public static readonly PdfName IM  = new PdfName("IM");  // abbreviated ImageMask in inline images
        public static readonly PdfName RGB = new PdfName("RGB");  // abbreviated DeviceRGB in inline images
        public static readonly PdfName CMYK = new PdfName("CMYK");  // abbreviated DeviceCMYK in inline images
        public static readonly PdfName Fl  = new PdfName("Fl");  // abbreviated FlateDecode in inline images
        public static readonly PdfName LZW = new PdfName("LZW");  // abbreviated LZWDecode in inline images
        public static readonly PdfName A85 = new PdfName("A85");  // abbreviated ASCII85Decode in inline images
        public static readonly PdfName AHx = new PdfName("AHx");  // abbreviated ASCIIHexDecode in inline images
        public static readonly PdfName DCT = new PdfName("DCT");  // abbreviated DCTDecode in inline images
        public static readonly PdfName RL  = new PdfName("RL");  // abbreviated RunLengthDecode in inline images
        public static readonly PdfName CCF = new PdfName("CCF");  // abbreviated CCITTFaxDecode in inline images

        // Action-related names
        public static readonly PdfName S = new PdfName("S");
        public static readonly PdfName JS = new PdfName("JS");
        public static readonly PdfName JavaScript = new PdfName("JavaScript");
        public static readonly PdfName Launch = new PdfName("Launch");
        public static readonly PdfName SubmitForm = new PdfName("SubmitForm");
        public static readonly PdfName URI = new PdfName("URI");
        public static readonly PdfName OpenAction = new PdfName("OpenAction");
        public static readonly PdfName AA = new PdfName("AA");
        public static readonly PdfName A = new PdfName("A");
        public static readonly PdfName EmbeddedFiles = new PdfName("EmbeddedFiles");
        /// <summary>§7.11.4 Table 44 — the <c>/Type</c> of an embedded file stream.</summary>
        public static readonly PdfName EmbeddedFile = new PdfName("EmbeddedFile");
        /// <summary>§7.11.3 Table 43 — the <c>/Type</c> of a file specification dictionary.</summary>
        public static readonly PdfName Filespec = new PdfName("Filespec");
        public static readonly PdfName Names = new PdfName("Names");
        /// <summary>§7.11.3 Table 43 — a file specification's human-readable description.</summary>
        public static readonly PdfName Desc = new PdfName("Desc");
        // Table 31 (catalog name dictionary), §13.5 — deprecated in PDF 2.0 (ISO 32000-2:2020 §0.3).
        public static readonly PdfName AlternatePresentations = new PdfName("AlternatePresentations");
        public static readonly PdfName Next = new PdfName("Next");
        public static readonly PdfName XFA = new PdfName("XFA");
        public static readonly PdfName RichMedia = new PdfName("RichMedia");
        /// <summary>A 3D annotation's stream or 3D reference dictionary (§13.6.2, <c>/3DD</c>).</summary>
        public static readonly PdfName ThreeDD = new PdfName("3DD");
        /// <summary>The <c>/Type</c> of a 3D reference dictionary (§13.6.2, <c>/3DRef</c>).</summary>
        public static readonly PdfName ThreeDRef = new PdfName("3DRef");
        public static readonly PdfName Action     = new PdfName("Action");
        public static readonly PdfName Link       = new PdfName("Link");

        // Additional structural names
        public static readonly PdfName Dests = new PdfName("Dests");

        /// <summary>The <c>/GoTo</c> action subtype (§12.6.4.2) — a jump within this document.</summary>
        public static readonly PdfName GoTo = new PdfName("GoTo");

        /// <summary>
        /// The <c>/XYZ</c> destination form (§12.3.2.2, Table 151): <c>[page /XYZ left top zoom]</c>,
        /// where a null <c>left</c> or <c>zoom</c> means "leave that unchanged".
        /// </summary>
        public static readonly PdfName XYZ = new PdfName("XYZ");

        /// <summary>The <c>/FitH</c> destination form (§12.3.2.2): <c>[page /FitH top]</c>.</summary>
        public static readonly PdfName FitH = new PdfName("FitH");

        /// <summary>The <c>/FitBH</c> destination form (§12.3.2.2): <c>[page /FitBH top]</c>.</summary>
        public static readonly PdfName FitBH = new PdfName("FitBH");

        /// <summary>The <c>/FitR</c> destination form (§12.3.2.2): <c>[page /FitR left bottom right top]</c>.</summary>
        public static readonly PdfName FitR = new PdfName("FitR");
        public static readonly PdfName Outlines = new PdfName("Outlines");
        public static readonly PdfName PageMode = new PdfName("PageMode");
        public static readonly PdfName PageLayout = new PdfName("PageLayout");
        // §7.5.2: the catalog's optional version override, e.g. /2.0 — wins over the file
        // header's %PDF-x.y when present and numerically later.
        public static readonly PdfName Version = new PdfName("Version");

        // Tagged-PDF / accessibility
        public static readonly PdfName MarkInfo = new PdfName("MarkInfo");
        public static readonly PdfName StructTreeRoot = new PdfName("StructTreeRoot");
        public static readonly PdfName Lang = new PdfName("Lang");
        // §14.8.2.3.1 Table 321 — deprecated in PDF 2.0 (ISO 32000-2:2020 §0.3).
        public static readonly PdfName Suspects = new PdfName("Suspects");

        // PDF 2.0 namespaces (§14.7.4 "Namespaces", §14.8.6 "Standard structure namespaces").
        // /NS on a structure element or namespace dictionary is the namespace name/reference
        // (Table 355, Table 356); /Namespaces on the structure tree root is the array of every
        // namespace dictionary the tree uses (Table 354).
        public static readonly PdfName NS = new PdfName("NS");
        public static readonly PdfName Namespaces = new PdfName("Namespaces");
        // §14.7.2 Table 322: the name tree from element identifiers (/ID) to the elements that carry them.
        public static readonly PdfName IDTree = new PdfName("IDTree");

        // The /Type value of a namespace dictionary itself (Table 355). PDF/UA-2 requires the
        // standard structure namespace to be declared explicitly, because §14.8.6 defaults an
        // absent /NS to the PDF 1.7 namespace rather than leaving it unspecified.
        public static readonly PdfName Namespace = new PdfName("Namespace");
        // Table 356's own namespace-scoped role map — distinct from the classic, single-namespace
        // /RoleMap (Table 29) already cached above.
        public static readonly PdfName RoleMapNS = new PdfName("RoleMapNS");
        // §14.8.5.2 Table 377's ARIA-1.1 attribute owner key, on an attribute object whose /O is
        // /ARIA-1.1 (already cached as PdfName.O, shared with the encryption owner-password key
        // of the same one-letter name). Lowercase "role", per the spec's own worked example in
        // ISO 14289-2:2024 §8.2.5.31 (`/role /doc-bibliography`) — PDF names are case-sensitive.
        public static readonly PdfName Role = new PdfName("role");
        // ISO 32000-2:2020 §14.8.5.5 Table 382 — the standard List attributes, on an L element.
        // ListNumbering is inheritable; ContinuedList and ContinuedFrom (PDF 2.0) are not.
        public static readonly PdfName ListNumbering = new PdfName("ListNumbering");
        public static readonly PdfName ContinuedList = new PdfName("ContinuedList");
        public static readonly PdfName ContinuedFrom = new PdfName("ContinuedFrom");
        // ISO 14289-2:2024 §8.2.5.14.2 Table 3 — the FENote attribute owner's one standard key.
        public static readonly PdfName NoteType = new PdfName("NoteType");
        // ISO 32000-2:2020 Table 202 — a GoTo action's structure destination (PDF 2.0), which takes
        // precedence over its required /D.
        public static readonly PdfName SD = new PdfName("SD");
        // ISO 32000-2:2020 Table 357 — a marked-content reference's content stream, when the marked
        // content is not in the page's own content (a form XObject).
        public static readonly PdfName Stm = new PdfName("Stm");

        // No /XResolution or /YResolution here on purpose: neither is an ISO 32000-1 image-dictionary
        // key. An image XObject has no resolution of its own — §8.9.5.2 maps its samples onto the
        // unit square of user space, so its effective DPI is a property of the CTM at the `Do` that
        // paints it (see Plugins.Maintenance.ImagePlacementScanner). Caching the names made the
        // non-existent keys look real, and the optimizer read /XResolution for years: it always
        // returned null, the fallback was 72, and DPI downsampling therefore never ran.

        // LZW / filter parameters
        public static readonly PdfName EarlyChange = new PdfName("EarlyChange");

        // CID font width tables
        public static readonly PdfName WMode = new PdfName("WMode");
        public static readonly PdfName DW2   = new PdfName("DW2");
        public static readonly PdfName W2    = new PdfName("W2");
        public static readonly PdfName CIDToGIDMap = new PdfName("CIDToGIDMap");
        public static readonly PdfName Registry = new PdfName("Registry");
        public static readonly PdfName Ordering = new PdfName("Ordering");
        public static readonly PdfName Supplement = new PdfName("Supplement");

        // ExtGState keys
        public static readonly PdfName LW  = new PdfName("LW");   // line width
        public static readonly PdfName LC  = new PdfName("LC");   // line cap
        public static readonly PdfName LJ  = new PdfName("LJ");   // line join
        public static readonly PdfName ML  = new PdfName("ML");   // miter limit
        public static readonly PdfName ca  = new PdfName("ca");   // fill alpha (non-stroking)
        public static readonly PdfName BM  = new PdfName("BM");   // blend mode
        public static readonly PdfName OP  = new PdfName("OP");   // overprint (stroking)
        public static readonly PdfName op  = new PdfName("op");   // overprint (non-stroking)
        public static readonly PdfName OPM = new PdfName("OPM");  // overprint mode
        public static readonly PdfName G   = new PdfName("G");    // soft-mask group XObject
        public static readonly PdfName BC  = new PdfName("BC");   // soft-mask backdrop colour

        // Shading / function keys
        public static readonly PdfName Shading            = new PdfName("Shading");
        public static readonly PdfName ShadingType        = new PdfName("ShadingType");
        public static readonly PdfName VerticesPerRow     = new PdfName("VerticesPerRow"); // type 5 lattice mesh
        public static readonly PdfName Coords             = new PdfName("Coords");
        public static readonly PdfName Function           = new PdfName("Function");
        public static readonly PdfName Domain             = new PdfName("Domain");
        public static readonly PdfName BitsPerCoordinate  = new PdfName("BitsPerCoordinate");
        public static readonly PdfName BitsPerFlag        = new PdfName("BitsPerFlag");
        public static readonly PdfName Decode             = new PdfName("Decode");
        /// <summary><c>/Indexed</c> — the colour-space family, §8.6.6.3. A <b>value</b> rather than
        /// a key: it is the first element of an <c>[/Indexed base hival lookup]</c> array.</summary>
        public static readonly PdfName Indexed            = new PdfName("Indexed");
        /// <summary><c>/Separation</c> — the colour-space family, §8.6.6.4; a value, like <see cref="Indexed"/>.</summary>
        public static readonly PdfName Separation         = new PdfName("Separation");
        public static readonly PdfName AntiAlias          = new PdfName("AntiAlias");
        public static readonly PdfName TilingType         = new PdfName("TilingType");
        public static readonly PdfName FunctionType       = new PdfName("FunctionType");
        public static readonly PdfName C0                 = new PdfName("C0");
        public static readonly PdfName C1                 = new PdfName("C1");
        public static readonly PdfName BitsPerSample      = new PdfName("BitsPerSample");
        public static readonly PdfName Encode             = new PdfName("Encode");
        public static readonly PdfName Order              = new PdfName("Order");
        public static readonly PdfName Bounds             = new PdfName("Bounds");
        public static readonly PdfName Functions          = new PdfName("Functions");
        public static readonly PdfName Extend             = new PdfName("Extend");

        // Form / content stream keys
        public static readonly PdfName Matrix      = new PdfName("Matrix");
        public static readonly PdfName BBox        = new PdfName("BBox");
        public static readonly PdfName MCID        = new PdfName("MCID");
        /// <summary>A marked-content reference — how a structure element names content on a page other than its own <c>/Pg</c>.</summary>
        public static readonly PdfName MCR         = new PdfName("MCR");
        /// <summary>
        /// An object reference's target (Table 325) — the annotation or XObject a structure element
        /// names instead of marked content. Four assemblies allocated this by hand; adding it here
        /// is what <c>NoRedundantPdfNameAllocationTests</c> then requires them all to use.
        /// </summary>
        public static readonly PdfName Obj         = new PdfName("Obj");
        /// <summary>The page's key into the structure tree's <c>/ParentTree</c>.</summary>
        public static readonly PdfName StructParents = new PdfName("StructParents");
        // Singular. Table 168: the key an ANNOTATION or a marked-content reference carries,
        // where /StructParents (plural) is the key a PAGE or form XObject carries. They are
        // two different keys, and a merge that shifts one and not the other leaves half the
        // structure pointing at the other document's entries.
        public static readonly PdfName StructParent = new PdfName("StructParent");
        /// <summary>Table-cell attribute: how many columns the cell spans.</summary>
        public static readonly PdfName ColSpan     = new PdfName("ColSpan");
        /// <summary>Table-cell attribute: how many rows the cell spans.</summary>
        public static readonly PdfName RowSpan     = new PdfName("RowSpan");
        public static readonly PdfName PatternType = new PdfName("PatternType");

        // Encryption handler
        public static readonly PdfName Standard = new PdfName("Standard");

        // Font embedding (PDF/A validation)
        public static readonly PdfName FontDescriptor = new PdfName("FontDescriptor");
        public static readonly PdfName FontFile = new PdfName("FontFile");
        public static readonly PdfName FontFile2 = new PdfName("FontFile2");
        public static readonly PdfName FontFile3 = new PdfName("FontFile3");
        // Table 122 (Type1 FontDescriptor) / Table 124 (CIDFont FontDescriptor) — both deprecated
        // in PDF 2.0 (ISO 32000-2:2020 §0.3).
        public static readonly PdfName CharSet = new PdfName("CharSet");
        public static readonly PdfName CIDSet = new PdfName("CIDSet");
        public static readonly PdfName OutputIntents = new PdfName("OutputIntents");
        public static readonly PdfName OpenType  = new PdfName("OpenType");  // /FontFile3 subtype
        public static readonly PdfName Type1C    = new PdfName("Type1C");    // /FontFile3 subtype: CFF (Table 126)
        public static readonly PdfName CIDFontType0C = new PdfName("CIDFontType0C");   // /FontFile3 subtype: CID-keyed CFF
        public static readonly PdfName CMap      = new PdfName("CMap");      // an embedded CMap stream's /Type (Table 118)
        public static readonly PdfName CMapName  = new PdfName("CMapName");
        public static readonly PdfName UseCMap   = new PdfName("UseCMap");

        // Bookmark / outline names
        public static readonly PdfName Last = new PdfName("Last");
        public static readonly PdfName Dest = new PdfName("Dest");

        // Annotation names (additional)
        public static readonly PdfName C = new PdfName("C");
        public static readonly PdfName QuadPoints = new PdfName("QuadPoints");
        public static readonly PdfName IC = new PdfName("IC");
        public static readonly PdfName CA = new PdfName("CA");
        public static readonly PdfName BS = new PdfName("BS");
        public static readonly PdfName Subj = new PdfName("Subj");

        // OCG / Named destinations / form-field names
        public static readonly PdfName Name = new PdfName("Name");

        // Tagged-PDF / structure tree
        public static readonly PdfName K              = new PdfName("K");
        public static readonly PdfName Pg             = new PdfName("Pg");
        public static readonly PdfName StructElem     = new PdfName("StructElem");
        public static readonly PdfName ActualText     = new PdfName("ActualText");
        /// <summary>§14.8.2.3.3: a marked-content tag whose show strings hold their characters in reverse order.</summary>
        public static readonly PdfName ReversedChars  = new PdfName("ReversedChars");
        public static readonly PdfName Alt            = new PdfName("Alt");
        public static readonly PdfName RoleMap        = new PdfName("RoleMap");
        public static readonly PdfName ParentTree     = new PdfName("ParentTree");
        public static readonly PdfName ParentTreeNextKey = new PdfName("ParentTreeNextKey");
        public static readonly PdfName D              = new PdfName("D");
        public static readonly PdfName Scope          = new PdfName("Scope");  // table-cell scope attribute
        public static readonly PdfName ClassMap       = new PdfName("ClassMap");  // §14.7.5.2
        public static readonly PdfName Headers        = new PdfName("Headers");   // §14.8.5.4 cell → header ids

        // Article threads (§12.4.3); the catalogue entry Annex F F.3.5 puts in part 4.
        public static readonly PdfName Threads           = new PdfName("Threads");

        // Viewer preferences
        public static readonly PdfName ViewerPreferences = new PdfName("ViewerPreferences");
        public static readonly PdfName DisplayDocTitle   = new PdfName("DisplayDocTitle");
        public static readonly PdfName TabOrder          = new PdfName("TabOrder");
        public static readonly PdfName DocOpen           = new PdfName("DocOpen");
        public static readonly PdfName TR               = new PdfName("TR");
        public static readonly PdfName NumCopies         = new PdfName("NumCopies");
        public static readonly PdfName PrintPageRange    = new PdfName("PrintPageRange");
        // Prepress viewer preferences — deprecated in PDF 2.0 (ISO 32000-2:2020 §0.3).
        public static readonly PdfName ViewArea          = new PdfName("ViewArea");
        public static readonly PdfName ViewClip          = new PdfName("ViewClip");
        public static readonly PdfName PrintArea         = new PdfName("PrintArea");
        public static readonly PdfName PrintClip         = new PdfName("PrintClip");

        // OCG
        public static readonly PdfName OCG          = new PdfName("OCG");  // an optional-content GROUP; /OCGs is the catalog's inventory of them
        public static readonly PdfName OCGs         = new PdfName("OCGs");
        public static readonly PdfName OCProperties  = new PdfName("OCProperties");
        public static readonly PdfName ON            = new PdfName("ON");
        public static readonly PdfName OFF           = new PdfName("OFF");
        public static readonly PdfName Marked        = new PdfName("Marked");
        public static readonly PdfName NeedAppearances = new PdfName("NeedAppearances");
        // ISO 32000-1 Table 28: the catalog's XFA rendering flag and its requirements array — keys PDF/A-2 onwards forbids.
        public static readonly PdfName NeedsRendering = new PdfName("NeedsRendering");
        public static readonly PdfName Requirements = new PdfName("Requirements");
        // §8.11.4.3 Table 100/101: the alternate optional-content configurations, and the
        // radio-button groups within one configuration. Both are read by the merge, which must
        // decide what a second document's layers do to them.
        // §12.4.2: the catalog's page-label number tree, and a range's starting number. Both
        // were allocated locally by PdfPageLabels, which is the only place that had needed them
        // until the merge had to shift a label range by the pages ahead of it.
        public static readonly PdfName PageLabels = new PdfName("PageLabels");
        public static readonly PdfName St = new PdfName("St");
        public static readonly PdfName Configs = new PdfName("Configs");
        public static readonly PdfName RBGroups = new PdfName("RBGroups");
        public static readonly PdfName BaseState     = new PdfName("BaseState");
        public static readonly PdfName OC            = new PdfName("OC");  // optional content on an XObject, annotation or BDC block
        public static readonly PdfName VE            = new PdfName("VE");  // OCMD visibility expression

        // XRef
        public static readonly PdfName XRefStm       = new PdfName("XRefStm");

        // Form fields
        public static readonly PdfName DA            = new PdfName("DA");
        public static readonly PdfName TU            = new PdfName("TU");
        public static readonly PdfName DR            = new PdfName("DR");
        public static readonly PdfName Q             = new PdfName("Q");    // variable-text quadding
        public static readonly PdfName Opt           = new PdfName("Opt");  // choice-field options
        public static readonly PdfName ChoiceSelectedIndices = I;           // a choice field's /I (Table 230): the selected options' indices; the same name as an inline image's /Interpolate abbreviation
        public static readonly PdfName MaxLen        = new PdfName("MaxLen");
        public static readonly PdfName Off           = new PdfName("Off");  // the always-present "unset" appearance state
        public static readonly PdfName Yes           = new PdfName("Yes");  // conventional check-box on-state
        public static readonly PdfName Document      = new PdfName("Document");
        public static readonly PdfName Collection    = new PdfName("Collection");

        // XObject form subtype
        public static readonly PdfName Form          = new PdfName("Form");
        public static readonly PdfName FormType      = new PdfName("FormType");
        public static readonly PdfName PS            = new PdfName("PS");  // deprecated PostScript XObject subtype
        public static readonly PdfName Level1        = new PdfName("Level1");  // PS XObject Level-1 alternate (§8.10.7)

        // Font metrics
        public static readonly PdfName FirstChar     = new PdfName("FirstChar");
        public static readonly PdfName LastChar      = new PdfName("LastChar");
        public static readonly PdfName Widths        = new PdfName("Widths");
        public static readonly PdfName MissingWidth  = new PdfName("MissingWidth");
        public static readonly PdfName DW            = new PdfName("DW");
        public static readonly PdfName FontName      = new PdfName("FontName");
        public static readonly PdfName FontBBox      = new PdfName("FontBBox");
        public static readonly PdfName Flags         = new PdfName("Flags");
        public static readonly PdfName ItalicAngle   = new PdfName("ItalicAngle");
        public static readonly PdfName FontWeight   = new PdfName("FontWeight");  // §9.8.2 Table 122
        public static readonly PdfName Ascent        = new PdfName("Ascent");
        public static readonly PdfName Descent       = new PdfName("Descent");
        public static readonly PdfName CapHeight     = new PdfName("CapHeight");
        public static readonly PdfName StemV         = new PdfName("StemV");
        public static readonly PdfName Length1       = new PdfName("Length1");  // clear-text length of an embedded Type 1 font program
        public static readonly PdfName Length2       = new PdfName("Length2");  // eexec-encrypted length
        public static readonly PdfName Length3       = new PdfName("Length3");  // trailing-zeros length (0 when the program is already binary)

        // File attachments / associated files
        public static readonly PdfName EF            = new PdfName("EF");
        public static readonly PdfName UF             = new PdfName("UF");
        public static readonly PdfName AFRelationship = new PdfName("AFRelationship");
        public static readonly PdfName AF             = new PdfName("AF");
        // §12.5.6.15 Table 179 — the file specification a file attachment annotation references.
        public static readonly PdfName FS             = new PdfName("FS");
        public static readonly PdfName Params        = new PdfName("Params");
        // §7.11.3 Table 44 — platform-specific file-spec strings, obsolescent since ISO 32000-1:2008
        // and deprecated outright in PDF 2.0 (ISO 32000-2:2020 §0.3).
        public static readonly PdfName DOS  = new PdfName("DOS");
        public static readonly PdfName Mac  = new PdfName("Mac");
        public static readonly PdfName Unix = new PdfName("Unix");
        // Launch action, Table 207 — same three OS-specific entries, this time directly on the
        // action dictionary rather than a file specification; deprecated in PDF 2.0. Win is
        // distinct from the file-spec keys above (which have no Windows entry at all).
        public static readonly PdfName Win  = new PdfName("Win");

        // Annotations
        public static readonly PdfName Vertices      = new PdfName("Vertices");
        public static readonly PdfName InkList       = new PdfName("InkList");
        public static readonly PdfName LE            = new PdfName("LE");   // line endings (Line, PolyLine)
        public static readonly PdfName BE            = new PdfName("BE");   // border effect
        public static readonly PdfName IT            = new PdfName("IT");   // annotation intent
        public static readonly PdfName LL            = new PdfName("LL");   // Line: leader line length
        public static readonly PdfName LLE           = new PdfName("LLE");  // Line: leader line extension
        public static readonly PdfName LLO           = new PdfName("LLO");  // Line: leader line offset
        public static readonly PdfName Cap           = new PdfName("Cap");  // Line: caption shown
        public static readonly PdfName Border        = new PdfName("Border");
        public static readonly PdfName NM            = new PdfName("NM");
        public static readonly PdfName IRT           = new PdfName("IRT"); // the annotation this one replies to (Table 170)
        public static readonly PdfName RT            = new PdfName("RT");  // the reply's relationship (Table 170): /R a reply, /Group a group member
        public static readonly PdfName AS            = new PdfName("AS");  // selected appearance state
        public static readonly PdfName MK            = new PdfName("MK");  // appearance characteristics dictionary
        public static readonly PdfName MKRotation    = new PdfName("R");  // widget rotation inside /MK; same string as the encryption /R
        public static readonly PdfName MKCaption     = CA;                // a button's normal caption inside /MK (Table 192); the same name as the stroking-alpha /CA
        public static readonly PdfName RC            = new PdfName("RC"); // a button's rollover caption inside /MK (Table 192); a markup annotation's rich text (Table 170)
        public static readonly PdfName AC            = new PdfName("AC"); // a button's down caption inside /MK (Table 192)

        // Page box types
        public static readonly PdfName TrimBox       = new PdfName("TrimBox");
        public static readonly PdfName ArtBox        = new PdfName("ArtBox");
        public static readonly PdfName BleedBox      = new PdfName("BleedBox");
        public static readonly PdfName UserUnit      = new PdfName("UserUnit");

        // Page transitions (/Trans)
        public static readonly PdfName Trans         = new PdfName("Trans");
        public static readonly PdfName Dur           = new PdfName("Dur");    // §12.4.4 advance timing
        public static readonly PdfName None          = new PdfName("None");   // e.g. /Di /None (§12.4.4)
        public static readonly PdfName Di            = new PdfName("Di");   // direction, in degrees
        public static readonly PdfName Dm            = new PdfName("Dm");   // dimension: /H or /V
        public static readonly PdfName SS            = new PdfName("SS");   // scale for the Fly transition
        public static readonly PdfName B             = new PdfName("B");    // opaque background for Fly

        // Thumbnails
        public static readonly PdfName Thumb         = new PdfName("Thumb");

        // Transparency / shading
        public static readonly PdfName Group         = new PdfName("Group");
        public static readonly PdfName Transparency  = new PdfName("Transparency");
        public static readonly PdfName Range         = new PdfName("Range");
        public static readonly PdfName L             = new PdfName("L");

        // CIE-based colour-space parameters (§8.6.5 Tables 62-65)
        public static readonly PdfName Gamma         = new PdfName("Gamma");
        public static readonly PdfName WhitePoint    = new PdfName("WhitePoint");

        // Halftone (§10.5): the graphics-state halftone and its origin
        public static readonly PdfName HT            = new PdfName("HT");
        public static readonly PdfName HTO           = new PdfName("HTO");
        /// <summary><c>/HTP</c> — PDF 1.x's halftone phase (§10.5), which PDF/A-2 forbids.</summary>
        public static readonly PdfName HTP           = new PdfName("HTP");
        public static readonly PdfName HalftoneType  = new PdfName("HalftoneType");
        public static readonly PdfName HalftoneName  = new PdfName("HalftoneName");
        /// <summary>The halftone dictionaries' keys (ISO 32000-2 §10.6.5): a Type 1's screen, a Type 10's two squares, a Type 16's second rectangle, a Type 5's default.</summary>
        public static readonly PdfName Halftone      = new PdfName("Halftone");
        public static readonly PdfName Frequency     = new PdfName("Frequency");
        public static readonly PdfName Angle         = new PdfName("Angle");
        public static readonly PdfName SpotFunction  = new PdfName("SpotFunction");
        public static readonly PdfName AccurateScreens = new PdfName("AccurateScreens");
        public static readonly PdfName Xsquare       = new PdfName("Xsquare");
        public static readonly PdfName Ysquare       = new PdfName("Ysquare");
        public static readonly PdfName Width2        = new PdfName("Width2");
        public static readonly PdfName Height2       = new PdfName("Height2");
        public static readonly PdfName Default       = new PdfName("Default");
        public static readonly PdfName TransferFunction = new PdfName("TransferFunction");

        // The rest of the ExtGState parameter set (§8.4.5 Table 58). Both renderers read these,
        // and before they were cached each kept its own answer: the raster path allocated a
        // PdfName per `gs` operator while the SVG writer held private `static readonly` fields
        // for the same nine strings. Caching them here is what lets
        // NoRedundantPdfNameAllocationTests hold the two sides on one set.
        public static readonly PdfName RI            = new PdfName("RI");
        public static readonly PdfName SA            = new PdfName("SA");
        public static readonly PdfName AIS           = new PdfName("AIS");
        public static readonly PdfName TK            = new PdfName("TK");
        public static readonly PdfName TR2           = new PdfName("TR2");
        public static readonly PdfName FL            = new PdfName("FL");
        public static readonly PdfName SM            = new PdfName("SM");   // smoothness tolerance
        public static readonly PdfName BG            = new PdfName("BG");
        public static readonly PdfName BG2           = new PdfName("BG2");
        public static readonly PdfName UCR           = new PdfName("UCR");
        public static readonly PdfName UCR2          = new PdfName("UCR2");
        // PDF 2.0 (ISO 32000-2 §8.6.5.9 / PDF Association AN001): black point compensation,
        // one of the names ON/OFF/Default.
        public static readonly PdfName UseBlackPtComp = new PdfName("UseBlackPtComp");

        // Also read by both renderers, and duplicated the same way: a soft mask's /Matte
        // (§11.6.5.3), a shading's /Background (§8.7.4.3 Table 78), the tiling-pattern step and
        // paint type (§8.7.3.2 Table 75), a Type 3 font's /FontMatrix, and the FontDescriptor
        // /XHeight whose call site carried the comment "no PdfName.XHeight yet".
        public static readonly PdfName Matte         = new PdfName("Matte");
        public static readonly PdfName Background    = new PdfName("Background");
        public static readonly PdfName XStep         = new PdfName("XStep");
        public static readonly PdfName YStep         = new PdfName("YStep");
        public static readonly PdfName PaintType     = new PdfName("PaintType");
        public static readonly PdfName FontMatrix    = new PdfName("FontMatrix");
        public static readonly PdfName XHeight       = new PdfName("XHeight");

        // CCITT fax decode parameters
        public static readonly PdfName Rows          = new PdfName("Rows");
        public static readonly PdfName BlackIs1      = new PdfName("BlackIs1");
        public static readonly PdfName EncodedByteAlign = new PdfName("EncodedByteAlign");
        public static readonly PdfName EndOfBlock    = new PdfName("EndOfBlock");
        public static readonly PdfName EndOfLine     = new PdfName("EndOfLine");

        public bool Equals(PdfName other)
        {
            if (other is null) return false;
            return Value == other.Value;
        }

        public override bool Equals(object obj) => Equals(obj as PdfName);
        public override int GetHashCode() => Value.GetHashCode();
        public override string ToString() => "/" + Value;

        public static bool operator ==(PdfName left, PdfName right)
        {
            if (left is null) return right is null;
            return left.Equals(right);
        }

        public static bool operator !=(PdfName left, PdfName right) => !(left == right);
    }
}
