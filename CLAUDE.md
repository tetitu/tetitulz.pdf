# Tetitulz.Pdf — Contributor & Agent Guide

A pure-managed .NET PDF library: parse, render, extract, edit, sign, convert. No native PDF engine.

This is the **rulebook digest**: invariants and conventions, one line each where possible.
**[`CONTRIBUTING.md`](CONTRIBUTING.md) is the long form** — the same headings, each rule with its
reasoning, plus the key-file map. **Read its section before changing code a rule here
names, or before "fixing" something a rule protects.** A rule changes in both files in one commit;
this file wins on the rule, that one on the reasoning. Open gaps: `GAPS.md`, developer-local and
not published (closed work leaves it); history: git.

---

## Layout

| Path | Contents |
|---|---|
| `src/tetitulz.pdf/` | `Core`, `Base`, `Encryption`, `Signing` |
| `src/features/` | `HtmlToPdf`, `DocxToPdf`, `Builder`, `Accessibility` |
| `src/plugins/` | the plugin ring |
| `tests/` | One test project per production project; no cross-project test dependencies |
| — | The WinForms viewer control, `Printing`, the apps and the demos are a separate repository, `tetitulz.pdf-apps`, that takes this one as a submodule |
| `benchmarks/`, `tools/` | Developer-local, not in the published repository; never packed |
| `docs/` | Index only — [`docs/README.md`](docs/README.md) |

There are **27** library `.nupkg`s — one per project under `src/`.
They include the 19 plugins under `src/plugins/` (CertStoreSign, CMap, Conformance, Docx,
EthereumSign, Fonts, Html, HtmlBrowser, HtmlScript, JavaScript, Maintenance, Ocr, PageEdits,
PostScript, Renderers, SignatureVerify, Svg, Templating, Text). **Both claims are guarded** by
`ProjectRegisterFreshnessTests`, which reads *this* file — keep them here only.

### Target frameworks

- Every `src/` project: `net10.0;netstandard2.0`. Tests: `net10.0`.
- **A .NET Framework host can reach every package.** On netstandard2.0 use `Core.Text.Latin1Text`
  (`Encode`/`Decode`) — not `Encoding.Latin1` (which is .NET 5+ and does not compile) and not
  `Encoding.GetEncoding("iso-8859-1")` (which compiles but throws on a .NET Core netstandard2.0 host
  with no CodePages provider); `!double.IsNaN(x) && !double.IsInfinity(x)` (not `double.IsFinite`), and
  `Core.Objects.PdfReferenceEquality` (not `ReferenceEqualityComparer`) — the **only** reference-identity
  comparer in the tree; do not add another (guarded by `SingleReferenceIdentityComparerTests`).
  **A `float` or `double` becomes an integer only through `Core.Util.SaturatingCast`**, never a cast:
  out of range a cast is the type's minimum on .NET Framework, and a `byte`/`ushort` cast wraps on
  .NET 10 too (guarded by `SaturatingCastGuardTests`, by the compiler's types).
  **A key a file chooses hashes through `Core.Util.KeyedHash`** (SipHash-2-4, keyed once a process):
  `double.GetHashCode` XORs a value's halves on every runtime and .NET Framework's string hash is fixed,
  so either lets a file put every key in one bucket. `Dictionary`/`HashSet` of `string` guard themselves
  there; a string tuple and LINQ's `Distinct`/`GroupBy`/`ToLookup` only under the host's
  `UseRandomizedStringHashAlgorithm`.
- Legacy-leg differences: `Plugins.Maintenance` Lossless uses deflate Optimal; **`HtmlToPdf` needs a
  `System.Memory` binding redirect on .NET Framework**, which an SDK-style exe generates itself; only a
  host that generates none (a `web.config`, `AutoGenerateBindingRedirects` off) writes it.
- `DocxToPdf`'s `ProcessArgumentEscaper` is legacy-leg only; **net10.0 keeps `ArgumentList`**. It is
  security-sensitive: if you touch it, re-run the three escaping-rule mutations.
- `Plugins.CertStoreSign`'s `KeyPin` (a token's PIN handed to the key's provider) is net10.0-leg and
  Windows only. **Its tests never use the protected test key without its PIN unless `Silent` is set.**
- **Word and LibreOffice are handed a copy of the package with what leads out of it pointing nowhere**
  (`Engines.ExternalEngineInput`): Word fetches an attached template, an external image and a subdocument
  with macros off and links not updated. A file that is no package (a ZIP with no OPC frame around a main
  document of its own, an OpenDocument file, among them), and an `aFChunk`, are refused.
- **`Accessibility` and `Conformance` must stay in step** on TFMs (`NU1201`, not a compile error).

### Platforms

- Windows and Linux supported; macOS unverified. `Core` has no native dependency; `Base` pulls SkiaSharp
  native assets for all three.
- **Zero P/Invokes in library code** — guarded by `NoLibraryPInvokeTests`. A native codec goes in a
  plugin that isolates it, never a P/Invoke in `Base` (the libopenjp2 interop was removed).
  **Do not "port away from Win32".**
- Use `SkiaSharp.NativeAssets.Linux`, **not** `.Linux.NoDependencies` (no fontconfig).
- SkiaSharp is on the stable line; text state lives on `SKFont`. **Five deliberate `CS0618` sites**
  (`PdfRenderContext._currentPath` mutators, GAPS.md §20c) stay: no `NoWarn`, no "finishing" them
  without a measurement.
- **`SKSamplingOptions.Default` is byte-identical to the obsolete overloads; `Linear` is not.** A draw
  that scales takes sampling from `/Interpolate` (Mitchell when true, nearest when false).
- **`SkiaSharp.HarfBuzz` / `HarfBuzzSharp.NativeAssets.*` must not come back.**
- Fonts: `Core.Fonts.FontFileLocator` owns the OS font-directory list and maps the standard 14 to
  Liberation first. **No fonts ship in the core packages**; `Plugins.Fonts` carries Liberation and
  Noto Sans Hebrew and Arabic (OFL, unmodified, the licence texts packed and written beside them)
  and registers the directory it extracts them to through the one public door,
  `Core.Fonts.FontSearchPath.Add` — searched **first**, ahead of the OS directories and of
  `TETITULZ_PDF_FONTDIR`, which still *replaces* the OS search path. A family asked for by its own
  name comes before its substitutes; a registered directory's file wins a same-name, same-style tie.
- **`SKTypeface.FromFamilyName` lies on both platforms** — use `Core.Fonts.FontFileLocator.IsFamilyMatch`
  (the one implementation; `FontCache`, `SystemFontResolver` and the font remediator all call it), never `!= null`.
- **A `FontCache` typeface is invalid outside the scope that disposes the cache** — use `WithResolvedTypeface`.
- **A font program's own counts are hostile input.** An INDEX's offsets ascend or the INDEX is refused
  (`CffParser.TryLocateIndex`); a directory whose records declare more than twice the font is refused
  (`SfntTableDirectory`) — each record can fit and still overlap; a `/Subrs` block's closing `def` is
  sought once (`Type1Parser.LastScanBytesExamined` is the evidence); an FDSelect's ranges ascend, and a
  Private DICT or Subrs INDEX that several Font DICTs name is read once, distinct ones within the
  program's length (`CffParser.ParseFdSelect`, `ParseCidKeyedCff`). A reader that bounds each item by
  the buffer has bounded the largest item, not the sum.

### Shaping — a reader must not shape

- **Neither renderer shapes text**; shaping again double-shapes stored presentation forms.
- **Producers do shape**: Builder, DocxToPdf, the AcroForm and signature appearance generators all go
  through `Core.Text.TextRunComposer` → `OpenTypeShaper`, and place marks by the font's `GPOS`
  anchors. **A composed run is written only through `GlyphRunShowText`** — a hand-built
  `<…> Tj` drops its placements and draws every niqqud point at its letter's left edge.
- Extraction cannot see a shaping defect; assert *which presentation form was chosen*.

### XML and regular expressions over a file's text

- **Every XML reader sets `DtdProcessing.Prohibit` (or `Ignore`) and `XmlResolver = null`**: XMP,
  XFDF, the XFA packets, SVG, embedded e-invoices, the conformance checkers. No new reader without them.
- **A regular expression over a field the file controls is unambiguous (no two adjacent quantified
  runs that can split one string several ways: `\d+(?:\.\d*)?`, never `\d*\.?\d+` or `\s*[,\s]\s*`
  on untrimmed text), is run only under a length cap on the field, and carries a match timeout of a
  second, never `RegexOptions.Compiled`** (a compiled pattern's first match pays its JIT inside the
  timeout window, and a loaded host then answers "not found"; `NoCompiledRegexTests`) —
  `XfaMeasure`, `Core.Text.CssColor`, the HTML mapper's `LengthPattern`, the auto-linker's bounded
  URL pattern. The cap is what a test asserts; a pattern's linearity has no answer a test can see.
  **Or it is a scan, held by a test to the pattern's answers** (`HandScannedPatternTests`, the SVG
  export's id references, a remote stylesheet's `url()` values — `RebaseRemoteCssUrls`, where a
  `var()`, `env()` or `attr()` names nothing) — and a search for patterns greps target-typed `new(` too.
- **A file's grammar comes from a specification or from two independent implementations, never from
  recollection** — the XFA readers' came from an open-source reader's element tables and the form designer's own output, and `GAPS.md` §19d
  names the download that settles a disagreement.

### Encryption

- **A `ProjectReference` to Encryption decrypts nothing** — `PdfEncryptionOperations.Register()` must run
  once at startup, and the failure is silent (`GetPageCount` answers 0). Only hosts using the ambient
  holder need it; ask which mechanism a surface uses.
- Read-only Base operations take a password directly (`PdfFormReader.GetFields(Stream, string)`,
  `PdfImageReplacer.FindImages(Stream, string)`) over `Base.Encryption.PdfDecryptedInput`.
- **A reader that resolves the trailer's `/Root` itself sees ciphertext on encrypted input: refuse,
  don't answer.** Attachments listed ciphertext names and extracted encrypted bytes as the file. **A
  door that takes no password refuses too** — `Base.Operations.EncryptedRead.Refuse`, naming the door
  that takes one; "none" from a document it could not read is a false answer. A converter refuses
  through `EncryptedRead.RefuseConversion` (text, SVG, HTML, Office) rather than write an empty output.
- **Whole-file writers fail closed on encrypted input** (sanitize, optimize, linearize, redact,
  template-fill). Routes: `PdfEncryptedIncrementalUpdate` via each writer's `Plan` (keeps signature and
  owner password) or `PdfEncryptedRoundTrip.Run` (keeps neither). **A clear-text revision writer
  refuses through `Base.Operations.EncryptedRevision.Refuse`** right after it opens; `AppendRevision` is the backstop.
- **The encryptors keep the input's layout** (`EncryptionParameters.UseObjectStreams` overrides): the
  compressed layout goes through `PdfCompressedWriteOptions.ObjectEncryptor` — an object stream
  encrypted whole under its own number, members clear inside; `/Encrypt` and the cross-reference stream
  unencrypted; `/AuthCode` carried into the latter. **`PdfWriter` tells an embedded file by its `/Type`**,
  so an encryptor declares an `/EF`-named stream `/EmbeddedFile` before the writer sees it.
  **A direct dictionary, array or string in the trailer (but `/ID`) is made an object and encrypted** —
  the writer encrypts nothing outside an indirect object. **`/EncryptMetadata false` exempts the
  document's own metadata stream alone** (Table 20; `IPdfDocumentMetadataEncryptor`): a page's, an
  image's or a font's XMP is encrypted, and the decryptor leaves only the catalog's `/Metadata` plain;
  both encryptors refuse it below AES-128 (Table 21: the entry means nothing there, and readers decrypt anyway).
- Both open with a `PdfDecryptionCredential`; **neither infers the output handler**. `ReuseOpenPassword`
  writes `/Standard` and **is refused for a document with no password**; `Recipients` writes
  `/Adobe.PubSec` to caller-supplied certificates; `KeepOriginal` (opt-in) writes under the original
  file key, `/Encrypt` and first `/ID`. A re-protect policy must never change *which key opens the file*.
- Signing an encrypted document: `PdfEncryptionOperations.Sign` — graph from a decrypted copy, revision
  appended encrypted; `PdfSigner.Plan`/`Complete` cross between. `PdfSigner.Sign` refusing encrypted
  input is correct. `Encryption → Signing` is the one allowed edge.
- **Verification takes the graph from one document and every byte, offset and length from the
  original file.** Never pass the decrypted copy as both.
- §7.6.2: a signature's `/Contents` alone is unencrypted — `Core.Document.SignatureDictionaryShape`.
- **SASLprep is a V≥5 rule, applied on both sides at one site each and nowhere else**:
  `PdfEncryptor.ComputeAes256Keys` and `PdfDecryptor.ValidateAndDeriveAes256Key`. Never at a public
  door (it then reaches revision 2–4, whose password is raw Latin-1 bytes), and never on one side
  only (a no-break space then writes a file its own password cannot open). ASCII passwords hide both.
- **`/Adobe.PubSec`: the envelope is the seed then the permissions, and a plaintext metadata stream
  is in the file key** (Table 27; §7.6.5.3 step c's `FF FF FF FF`) — one layout and one digest,
  `PdfPublicKeyDecryptor.SeedOffset` / `DeriveEncryptionKey`, read by the encryptor. **A round trip
  through our own pair proves nothing about either**; the test opens the envelope and derives the
  key itself. `/EncryptMetadata` is read from the crypt filter first, then `/Encrypt`.
- **A PDF MAC token that is absent is "unprotected" only when none was required**
  (`PdfMacVerifier.WhyATokenIsRequired`): `/AuthCode` and the encryption dictionary are plain text,
  so a token is removed by deleting its entry. Under V≥5 the requirement is permission bit 13, read
  only where something authenticates it: `/Perms` confirming `/P` under the standard handler, the
  opened recipient envelope's own permissions (`ResolvedEncryption.EnvelopePermissions`) under the
  public-key one. Below V5 the only witness is the bit with a `/KDFSalt`, which the same edit
  removes — **so no token is written below V5** (ISO/TS 32004 Table 5; `PdfEncryptor.CanCarryPdfMac`,
  asked by both encryptors and the round trip, which neither rewrites such a token nor drops it).
  An `/AuthCode` that is not a direct dictionary is invalid, not absent. **What a token's gap holds is
  read, not sized**: a standalone token's range starts at byte 0 and leaves out only the token as hex
  (`StandalonePdfMacSeal.GapHoldsOnlyToken`, ISO/TS 32004 Table 6), a signature's only its `/Contents`.
- **Do not "fix" layering with a `ProjectReference`** (BouncyCastle into more graphs; GAPS.md §5a).
- **SSRF: a `bool` about a hostname does not protect the connection.** Use
  `PrivateAddressGuard.TryResolveSafeAddresses` and connect to what it returns —
  `GuardedHttpConnect.ConnectAsync` as the `ConnectCallback` on net10.0, installed by reflection from
  netstandard2.0 on a .NET 5–9 host. .NET Framework has no hook, so there the handler is the library's
  own transport, `GuardedHttpTransport` (the same connection, TLS by the host's name, a bounded
  HTTP/1.1 exchange); a request with credentials or a proxy, or a URL the system proxy applies to,
  keeps the pre-check alone (GAPS.md §17a); a comment must not promise more.
- **A browser is guarded at its proxy, not at its request route** (`Plugins.HtmlBrowser`): the
  route does not see a beacon or a worker's WebSocket and answers before the browser connects, so
  the browser is launched behind `GuardedSocksProxy` — loopback, SOCKS5, handed the host name, which
  connects only to what `TryResolveSafeAddresses` returned — with WebRTC's UDP kept inside it. The
  route keeps `file:` containment and schemes. **A new channel is probed against a live browser
  and a loopback listener, guard off then on**; a rule asserted without one missed two. **Chromium
  runs in its sandbox** (`HtmlToPdfOptions.ChromiumSandbox`, off only where it cannot start): the proxy
  and the route bound an intact renderer, not one a page compromised.
- **Credentials for a fetch to an attacker-chosen URL ride it only when host-scoped.** A revocation
  URL (AIA/CRL/OCSP) comes from an untrusted certificate, so `DssBuilder.RevocationCredentials` drops
  any `ICredentials` that is not a `CredentialCache` (a bare `NetworkCredential` or
  `DefaultNetworkCredentials` answers any host); the TSA request keeps the caller's, its URL being the caller's own.
  **A seed value's `/TimeStamp /URL` is the document's**: asked only under `UseDocumentTimestampAuthority`, then
  as an OCSP request is (`TsaClient.GetTimestampTokenFromDocumentUrl`); a seed's weak `/DigestMethod` is passed over.
- `IExternalDecryptor`: the seam is the CMS envelope, not the RSA primitive; **the certificate match
  happens before the device is touched** (`GetRecipientCertificate` stays).
- Accessibility writers are not fail-closed: they `Plan`, and a caller-supplied
  `RemediationOptions.EncryptedRevisionWriter` writes. Accessibility references no crypto.

### Editing under a signature

- `PdfImageReplacer` supersedes in place (same object number) through `IncrementalUpdateWriter`;
  `PdfRedactor` is the opposite trade. **Preserve the signature *or* remove the data, never both.**
- `PdfRevisions` finds boundaries **physically** (`%%EOF` with a `startxref` within 64 bytes pointing
  strictly backwards); a linearized first-page terminator is recognised **by structure**, not its integer.
- Redacting a **shared** Form XObject leaves the original reachable from other pages;
  `PdfRedactionEngine.LastSharedFormWarning` names them.
- **LTV data is only ever added.** `DocMdpAnalyzer` admits an object under `/DSS` by its position's
  shape (Table 261/263 keys, a stream's keys alone, a digest-keyed `/VRI`), never by reachability, and
  a changed object only when its old version had that shape — a rewritten content stream listed under
  `/Certs` is a violation.
- **A CRL vouches only within its scope** (RFC 5280 §6.3.3). "Signed by the issuer and current" is
  not the scope: a delta, a CA-only or user-only partition, a per-reason or per-distribution-point
  list, an indirect CRL are each genuine and silent about a certificate outside them, and absence
  from one is `Unknown`, never `Good` — `Signing.Cms.CrlScope`, asked by the verifier and the LTV
  embedder alike. A listing revokes whatever the partition. A self-signed top of a chain is outside
  every scope and is not checked against the leaf's CRL.
- **An OCSP response is evidence for the certificate its `SingleResponse` names** — serial and issuer
  hashes, `Signing.Cms.OcspSingleResponse`, asked by the embedder before it writes and the verifier
  before it reads; a delegated responder's certificate must be within its validity period at the time
  each relies on the response. **The embedder must never write what the verifier will not read.**
- **Freshness, the OCSP responder and a timestamp's algorithms are one rule on both sides**
  (`Cms.RevocationFreshness`, `OcspSingleResponse.WhyTheResponderCannotVouch`,
  `WeakAlgorithmPolicy.DetectWeakTimestamp`): the embedder may be stricter, never looser; a weak
  token is embedded and reported (`TsaClient.LastTokenWeakAlgorithm`), a broken one refused.
- **The signer is told what default verification refuses, in the verifier's words**
  (`PdfSigner.LastSignerWarning`): the leaf checks are one function for the chain walk and the signer
  (`CertificateChainValidator.WhyTheLeafIsRefused`), the weak digest one for a SignerInfo and a
  digest to sign with (`WeakAlgorithmPolicy.DetectWeakDigest`). A warning, never a refusal; a new
  leaf check goes into that function, or signing stops warning of it.
- **A chain is held to more than signatures and dates** (`Cms.CertificateChainValidator`): an issuer
  is sought in the bag, then among the anchors; a signer's extended key usage must name a
  document-signing purpose when it names any (`RequireSignerKeyPurpose`); a critical extension the
  walk does not process refuses the certificate, an anchor excepted; a CA's name constraints hold
  every certificate below it.
- **Revocation is checked along the path the walk anchored** (`CertificateChainValidator.Resolve(…, out
  trustedPath)`, continued above a non-root anchor by `RevocationPath`), for signers and timestamp
  authorities alike: a chain built apart follows whichever same-named certificate's key verifies first,
  and a bag can put a decoy there. `BuildChain` follows no issuer whose key did not verify.
- **A signature's `/VRI` entry is read beside the document-wide `/OCSPs` and `/CRLs`, never instead**
  (`RevocationChecker.Sources`): evidence is matched to its certificate already, and an empty entry a
  later revision adds must not hide a listing.
- **A timestamp's authority is judged now as well as at its own time**: not revoked, and its chain
  valid at the current time or the token covered by a later document timestamp that may itself
  vouch (`AuthorityMayVouch`, `KeepRenewed`; `RequireCurrentTimestampAuthority`). A chain judged only
  at GenTime is judged where the token says to look.
- **Online revocation at verification is opt-in and borrows both sides**: the fetch is the embedder's
  (`DssBuilder.FetchOcspResponse` / `FetchCrl` — the guard, the budget, the host-scoped credentials),
  the judgement the verifier's (`RevocationChecker.CheckChainWithOnlineFallback`, the same functions
  as embedded data), **judged now**, since online data is current; nothing fetched is written back,
  a document that settles the status is not asked, and a failed fetch is a warning, never a verdict.
- **A hash of a prefix says nothing of what follows it, in the revision carrying the proof too**
  (`DocMdpAnalyzer.ChangesBesidesCatalogEntry`, the blockchain verifier). **A permitted change is no
  error and is said** (`DocMdpAnalysis.PermittedChanges`, a verifier warning): an annotation or a new
  signature's appearance can stand over the signed text.
- **A proof that travels with the document names its own signer** (`BlockchainSignatureProvider.Judge`):
  the sender is held to `BlockchainParameters.ExpectedSignerAddress` when the caller sets one and the
  result warns when not; a transaction in no block is not found; an empty hash matches nothing.
- **What a `/ByteRange` gap holds is read, not sized** (`SignedByteRanges.GapHoldsOnlyContents`): a
  padded `/Contents` written as a literal string takes half the room its hex form is allowed, and the
  rest, which no digest covers, holds a forger's objects and the table a moved block's `startxref` lands on.
  **A signature's dictionary is among the bytes it signs** (`SignedByteRanges.GapIsInObjectAt`, against the
  cross-reference entry in force): a digest says nothing of where its `/ByteRange` was read, and a copy in a
  field added later carries a `/Reason`, `/Name` and field name of the forger's over a signer's good digest.
- **A redaction removes what a page says about its text as well as the glyphs**
  (`PdfRedactionEngine`): marked-content properties, the structure tree's alternatives for the page,
  an operand or an operator no reader reads, the page's thumbnail and private data, an image's masks
  and alternates, a structure element's associated files, a dropped annotation's words, file, popup and
  replies, a field whose every widget went (its value scrubbed, and out of `/CO`). A tiling pattern or soft mask drawing
  text is refused, and so is a region meeting a paint through one that draws an image
  (`RefuseCoveredImageSource`: its pixels would be covered and kept); a form's XFA packets go whole. Its limits are in the class summary; **a new kind of
  leak probed is a row in `RedactionLeftBehindTests`**, which reads the whole output for a marker. **A
  region takes a glyph where the page draws it** (`RedactionPlacementTests`, each position worked out
  from the specification): `Q` leaves the text matrices, a form starts from the state that draws it, a
  graphics state sets a font, a vertical run moves down from its position vector, stroked text reaches
  to its miter, and a Type 3 glyph is read for where it paints (`RedactionType3Font`; what cannot be
  placed goes with any region). **The cover boxes are drawn in the page's initial state**, what the page
  left open closed first. **A search-and-redact reads
  its result back** (`PdfTextRedactor`): the layout cannot place rotated, mirrored or overlapping text,
  so it matches nothing, and the plain extractor refuses the call over what is still there. **It cleans
  the document's own text of its matches too** — the information dictionary, the outline's titles, the XMP
  packet, one it cannot read or bound removed whole (`IncludeDocumentText`, `LastDocumentTextRemoved`).
  **The incremental removers keep the bytes** (attachments, pages, content classes, annotations): their
  docs say so; destroying is `Redact`'s and a rewrite's. **A rewrite that removes annotations removes what
  they said** (the optimizer's `RemoveAnnotations`): every annotation a page listed and every node of the
  field tree scrubbed where it stands, and `/AcroForm` gone — a structure element still reaches one.
- **A later revision is judged by what the signed revision held, never by what it now says it is**
  (`DocMdpAnalyzer`): a stream that existed changes at no level, whatever keys its rewrite carries; an
  object it named without holding is not supplied later; a signature's keys make no annotation or field a
  signature, and no field takes a signature it held as its value; `/DSS` loses no entry; an
  annotation is what a page's `/Annots` listed; a new signature field carries no action; below P=3
  `/Annots` keeps every entry and adds only new objects; `/Fields` only grows; an added annotation only
  links (no `/AA`, no action but a lone `/URI` or `/GoTo`); `/DR` only gains names, and `/DA` and a
  choice field's `/Opt` do not change, since a redraw reads them; a signed field and its widget do not
  change at all, its appearance being the name and date a reader takes for the signature's. **A change need write
  no object**: a cross-reference entry naming other signed bytes, a trailer naming another catalog
  (`ClassifyTableChanges`, against the signed revision's own tables). The certification is the
  signature that covers least. **A field lock covers what a reader shows** (`FieldMdpAnalyzer`): value
  and appearance, every field of the name, a listed parent's children.
- **A writer that supersedes the catalog asks `PdfSigner.CatalogObjectNumberOrRefuse`** (Base's
  writers, `Core.Writing.CatalogObject.NumberOrRefuse`) — a trailer
  whose `/Root` is inline has no number to supersede, and a fix to one writer is a finding about every
  writer of the same shape. **A rewrite cleans the catalog a reader takes** (`PdfSanitizer`): an inline
  `/Root` or `/Info` is made an object of its own first, and a `/Root` naming no dictionary (a reference
  to a reference) is pointed at the catalog the parser settled on. **Every object it writes has been cleaned**: what the
  write walk keeps past the end of the cleaning walk's reference chain (`MaxIndirectReferenceChain`) is
  cleaned before it is written.

---

## Architecture

### The reference invariant is acyclicity, not direction

The project graph is a **DAG with `Core` at the bottom**. Core: object model, parsing, filters,
writing, signing crypto — no SkiaSharp, no UI. Base: operations and rendering, may use SkiaSharp.
Features depend on Core/Base only, with two sanctioned exceptions.

1. **`Builder` references no other feature**; `DocxToPdf` and `HtmlToPdf` may reference it.
2. **`Conformance` references no feature**; `Accessibility → Plugins.Conformance` is sanctioned.
3. **`PostScript` references no feature**: `Printing` (in `tetitulz.pdf-apps`) references it, a print job being PostScript.
4. **`Core` / `Base` / `Signing` / `Encryption` reference no plugin.**
5. **Plugin → plugin is banned** — share downward (`Core.Fonts.PredefinedCMapRegistry` is the model).

Plugin → feature is allowed (`Plugins.Docx → DocxToPdf`, `Plugins.Html → HtmlToPdf`) — but never from
`Conformance` or `PostScript`, the targets of rules 2 and 3, where it would close a cycle. Enforced by
`AssemblyLayeringTests` (cycles, banned edges, banned packages, undeclared third-party namespaces).

### What makes something a plugin rather than part of Base

- **A plugin earns its place by keeping a dependency out of the core graph** (Tesseract,
  Nethereum, Jint). Best shape: `Plugins.CMap` — mechanism in Core, payload in the plugin.
- An adapter plugin isolating nothing is fine but is no evidence a capability "belongs" there.
  **Size is not the criterion** (`Jbig2Decoder` stays in Core). **`Base` must not reference `Jint`.**
- A plugin contract lives where the types it mentions allow (`Core/Plugins/`, else
  `Signing.Plugins`, `Base.Scripting`). Base reaches plugin features through `PdfPluginHost`.
- **A document's own JavaScript runs only when the host registers the engine by name** —
  `JintJavaScriptPlugin` carries `PdfPluginExplicitRegistrationAttribute`, which every discovery
  door passes over, and the failure is silent (no script runs). **A web page's own JavaScript likewise**:
  `JintHtmlScriptPlugin` carries the same attribute. **The object model is held to
  Acrobat's own answers**, never to recollection (`Fixtures/Acrobat`, `AcrobatParityTests`; GAPS.md
  §25a): a round's answers file is the saved probe document's `results` field byte for byte.

### Which side owns which PDF/UA check

- `Plugins.Conformance`'s `PdfUaChecker`: UA00–UA09, UA25–UA29, UA50+, its own UA99.
  `Accessibility`'s `PdfDeepAccessibilityChecker`: UA10–UA24, UA30–UA32, plus its own UA00, UA06, UA99.
  PDF/UA-2 codes are `UA2-<clause>` and never reuse the UA00–UA99 range.
- **New deep codes from UA30 up (UA30–UA49); new plugin codes from UA50 up.**
- The font pair is numbered oppositely (deep UA20 Unicode / UA21 embedding; plugin UA25 embedding /
  UA26 Unicode) and is not de-duplicated. **UA16 (leaf) and UA30 (subtree) stay two checks.**
- `PdfAccessibility.Audit` drops exactly UA28, UA29, UA50; the plugin's UA99 maps to `Info`.
- `/P` is read through `PdfPermissionValue.FromNumber`; `/DisplayDocTitle` through
  `PdfViewerPreferences.Read` (resolve delegate, because an encrypted document has no catalog).
- **`PdfXValidator.ScanPage` is shared by four standards; PDF/X print-scoping is a parameter**
  (`printElementsOnly`, only X04/X06/X08/X09), never a filter inside the walk.
- **Conformance holds two content walks** (`ScanPage`, `DefinedMcids`) **and must not grow a third.**
  What a page *draws* is one reading of `ScanPage`'s streams, `PdfAValidator.WalkDrawnOperations`
  (the device colours and the content stream rules alike) — not a second propagation. **One
  validation scans and parses a page once**: a check reads its operations through `ScanReuse.Parse`
  (a page's own content read outside the scans: `ParseAndKeep`).
- **Which annotations are reader content** — not Popup, not PrinterMark, not hidden, not outside the CropBox — is
  `PdfUaAnnotationScope.IsPresentedToReader`, for UA27, the deep checker and the auto-tagger. A check
  narrows it only at its own site (UA27 also leaves `/Widget` to UA08).
- **Which fonts are used for rendering** (ISO 14289-1 7.21.4.1: painted, not merely named; mode 3
  exempt) is `PdfUaFontUse.PaintedFonts`, for UA25 and UA21 alike. `null` means "treat all as used".
  The codes each font paints (PDF/A-1's CharSet, A84/UA57's glyphs) are `PaintedCodes`, the same loop.
- **One incremental writer, in Core**: `Core.Writing.IncrementalUpdateWriter`. **It keeps a
  superseded object's generation** (only a 0 is corrected); **a reference to an existing object is
  the caller's** — `ExistingObjectGeneration.ReferenceTo`, never `new PdfReference(n, 0)`.
- **One structure-tree walk, in Base**: `Operations.Structure.StructureTreeWalk` — the accessibility
  walker, the tagged-text extractor, `PdfStructureTreeReader` and the PDF/UA checker all read
  `/StructTreeRoot` through it (`Enter`, a `Kid` per `/K` slot in order, `Leave`; visited by object
  number or identity; the depth, node and edge ceilings reported, never silent). **A new reader of the
  tree does not walk it itself** — four walks each lacked, at some point, a bound another carried.
- **A text object's first byte does not say whose it is.** Completing a page, a sequence the producer
  opened inside `BT…ET` forbids wrapping the object and leaves every line sharing its bytes to the
  producer — §14.7.5.1: no structure content item inside another; **a balance check cannot see it.**
- **A colour-space classifier descends every base**: `/Indexed`'s base, a `/Separation` or `/DeviceN`
  alternate, **and `[/Pattern base]`** (§8.7.3.3 — an uncoloured pattern paints in it). X08, X18 and
  A16–A18 all three; a new classifier starts from one of them.
- **A PostScript pen moves through the font matrix** — PLRM §5.3's width vector, one
  `PsTextOps.AdvancePen`; a site reading the matrix's A term piles a rotated line's strings up along x.
  **A show that continues the line joins the text object before it** as a `TJ` string
  (`ContentStreamEmitter.ContinueText`: a driver's one-glyph shows are a line, not letters) — only
  given the advance a reader moves by, so only where the font's `/W` is the pen's advance.
  **A DSC data section is skipped only when binary** (DSC 3.0 §5.7: an ASCII `%%BeginData` holds the
  `image` that reads it; a `Lines` count is lines). **An approximation the converter makes is
  reported** (CalRGB carried as DeviceRGB) — a comment promising the report is not the report.
  **A PostScript path is in device space** (PLRM §4.4): every CTM change re-expresses the open path,
  charged by its segment count. **Every text operator dispatches on the font's kind**; a font
  dictionary names its own embedded program, which `findfont` reaches by the program's header.
  **A driver's font is read when it is used**: a Type 42 font's glyphs arrive after `definefont`,
  often inside a `save` its `restore` rolls back, so each is copied out at its first use.
  **A domain error is a `rangecheck`, never a clamp.** A binary inline-image block ends where its
  data says (RunLength's EOD, a JPEG's EOI, LZW's EOD code, a zlib stream's final block through
  `FlateDecodeFilter.DecodeEmbedded`); CCITT stays refused by name because its data does not say.
  **A program's work and memory are charged where they happen, by size**: every block a file hands
  over through `PsFile.Charge`, each decode's input and output to the work budget and a failed one at
  the most its input could have made, each key and name by its length (`TickKey`), each filter kept.
  **What a program can nest is walked iteratively or under a depth** (`DiscardFileSources`,
  `MaxFilterChainDepth`), and a procedure run while a space is built may reach the builder again.

### Folder taxonomy — the namespace follows the folder

`Base.Operations.` → `Forms` (AcroForm, FDF/XFDF) · `Documents` (whole-document: redaction,
templating, pages, comparison, repair, revisions) · `Extraction` (text, tables, links,
graphics, layout, 3-D, rich media) · `Structure` (bookmarks, destinations, OC, labels, tag tree, OCR
layer) · `Metadata` (info, XMP, ICC, usage rights, DocMDP) · `Images` (the **edit** side; `Base/Images/`
is decode) · `Barcodes`, `Fonts`. The cross-cutting types that stay at `Operations` root are listed
in the long form. **The sanitizer is the one whole-document operation in Core**
(`Core.Document.PdfSanitizer`): a signing host sanitizes first without Base's SkiaSharp. Not a
precedent — an operation goes down only when a host without Base must reach it and it needs nothing of Base's.

### KISS / SOLID

- Implement only what the gap needs; a working 80% beats an untestable 100%. No one-implementation
  abstraction layers.
- Parsers parse, renderers render, writers write. Depend on `PdfDictionary`/`PdfObject`/`Stream`.

---

## DRY — the shared decision points

**A comment asserting two implementations agree is a duplication report — extract the helper.**
**Consolidating part of a cluster is worse than none**: the unit of work is every site answering the
same question. Before writing any of these, use the one implementation:

| Concern | The one implementation |
|---|---|
| Colour conversion + quantisation (`Channel`) | `Base/Rendering/PdfColor` |
| BT.601 weights | `Core.Constants.PdfColorSpaceNames` |
| Literal-string / name escaping (§7.3.4.2, §7.3.5) — a caller's or a file's name written into content or a `/DA` too, or it closes its operand and adds operators | `PdfStringEscape`, `PdfNameEscape` (two readers, must agree) |
| Dictionary cloning / operand coercion | `CloneShallow`/`DeepClone`; `Core.Text.ContentOperand` |
| Stream filters / seek-normalisation | `Core.IO.Filters`; `Base.PdfHelpers.EnsureSeekable` (Signing has its own) |
| Object map → `IndirectObject` list; appended-revision tail | `RevisionObjects`; `PdfPageContentAppender.AppendRevision` |
| Content added to an existing page: names never replace the page's; drawn in the page's initial state | `PdfContentStreamBuilder.ReserveResourceNames` + `PdfPageContentAppender.ResourceNames`; `PdfPageContentAppender.StateIsolation` (shared `q`/`Q` pair) |
| A resource name a page does not use — for the font or image a writer adds to an existing page's resources: the template engine's replacement font (one an edit), the OCR layer's, the page text writer's; a fixed name replaced an earlier edit's | `PdfPageContentAppender.FreeResourceName` |
| Content added to many pages in one revision — the stamp's objects written once, the pages that start from one `/Resources` naming one merged copy, never a copy a page; chosen pages as all | `PdfPageContentAppender.AppendContentToAllPages` / `AppendContentToPages` over `ResourceSharing` (`Merged`) |
| A page's stamped forms (a flattened field or annotation) — registered in the `/XObject` dictionary the page already reads, superseded once for every page sharing it, names taken against what the revision added | `Base.Operations.StampedXObjects` |
| A list a file fills kept distinct (a set beside the list, never a search of it); a message joining it names twenty and counts the rest | `Core.Util.DistinctStrings` (`Add`, `Joined`) |
| Which pages share a content stream — counted once an operation, a page's old streams taken out when it gets a private clone; a document written by what an explicit root reaches, with an `/ID` | `PdfFullRewriter.ContentUsage`; `PdfFullRewriter.WriteReachable` |
| The decoded length of each stream a listing measures — once a stream, under the listing's ceiling, a refusal's reason kept | `Operations.Extraction.DecodedLengths` |
| An element's attribute objects (`/A` and its `/C` classes, each once, first writer wins) — one reading for every element naming the same ones | `Core.Document.PdfStructureAttributes` (`SourcesOf`, `ReadAll`) |
| A code point of decoded text — a surrogate pair's scalar, else the unit, so a lone surrogate is one slot no font maps — both raster glyph lookups | `Rendering.GlyphIdResolver.CodePointAt` |
| What every extraction of a split reads of the whole document — the flattened `/Names` (`/Dests` by page), the outline's shape, the field tree — once a split; a page's field filter narrowed to the fields that reach it, only where the whole walk could neither stop short nor report | `Plugins.PageEdits.PdfSplitter.SplitWalks`; `AcroFormFieldIndex` |
| What a split's object walk refuses to enter — a page the extraction does not keep (Core's page list, not `/Type` alone) and a field the form plan dropped, unless a kept page lists it; **an entry of a shared resource dictionary no kept page draws** (content, appearances, and the forms, Type 3 glyphs, tiling patterns and soft-mask groups they draw — whole, and said, where that cannot be read or something unread names it); a direct value several merged or split pages inherit, written once; a copied object numbered only when `DeepClone` made it | `PdfSplitter.CollectionGuard`, `PdfObjectHelper.IsPageObject`; `SplitResourceUse` (`LastResourceWarning`); `PdfObjectHelper.InheritedCopies`, `Stamp` |
| What one built-in call of an untrusted script may build — Jint asks its limits only when a statement starts, so `repeat`, `pad*`, `concat`, `replace*`, `join`, `toLocaleString` and `JSON.stringify` are counted first, to the memory limit as characters — the form engine and the page engine alike; a script's regex timeout is its own, for a cached prepared script too | `Core.Scripting.ScriptBuiltinBounds` (`Prelude`, `CharactersFor`); HtmlScript's `JsScriptingOptions.PrepareEngine` / `RegexTimeout` |
| Standard-14 family; simple-font `/BaseFont`+`/Widths`; §9.6.6 encoding ladder | `Standard14Names`; `PdfSimpleFont` (`ReadEncoding`) |
| A font's own streams decoded — a CMap (`/ToUnicode`, an embedded `/Encoding`) under `MaxCMapStreamBytes`, a program under `MaxFontProgramBytes`, never the general ceiling — every reader of them; a `/ToUnicode` that will not decode leaves the font loaded without it | `Core.Fonts.FontStreamBytes` (`CMap`, `Program`; `FontStreamDecodeGuardTests`) |
| A CID of an Adobe collection as text (the collection's registered Unicode maps, an encoding CMap read backwards keeping the canonical character over its compatibility form) — the font model's extraction and the PostScript converter's `charpath` alike | `Core.Fonts.CidCollectionText` (`TextOf`, `Decode`); `CMapParser` (`AddReversed`) |
| A TrueType program's glyphs as text when it carries glyphs alone (a driver's download: no `cmap`, no names, a made-up collection) — the installed face it was taken from, found by its `head` (all but the fields a re-assembly recomputes) and confirmed glyph by glyph by its bytes, its `cmap` read backwards; never by name | `Core.Fonts.InstalledFontGlyphs` (`TryIdentify`, `TextOf`, `GlyphOf`) |
| A CFF Type 2 charstring's path, width and stems — drawn into an `IGlyphPathSink`, the raster renderer's SkiaSharp path and the PostScript converter's `charpath` outline alike; the five path operators Type 1 and Type 2 share | `Core.Fonts.Cff.Type2Charstrings` (`CffFontSet.Outline`, `CidOutline` for a reader outside Core); `Core.Fonts.CharstringPath` |
| A Type 3 font's `/FontMatrix`, a corrupt one the default and said — both renderers and the redaction engine | `Rendering.Type3Encoding.FontMatrixOf` |
| Which Standard-14 face draws through its built-in encoding whatever `/Encoding` says (Symbol, ZapfDingbats, `ZaDb` — §9.6.6.4), and its AFM glyph by code or by name — the reader and the PostScript plugin alike | `Standard14Metrics.HasBuiltInEncoding`, `TryGetBuiltInGlyph`, `TryGetWidthByGlyphName` |
| Annotation walk by subtype | `AnnotationSubtypeWalk` |
| SSRF: address class; connect to the checked address; CRL URLs | `PrivateAddressGuard`; `GuardedHttpConnect`; `DssBuilder.SafeCrlDistributionUrls` |
| Fetching an untrusted URL — guarded handler, redirects re-checked per hop, **headers-only sends**, body capped under the budget token | `Core.Net.GuardedHttpFetch` (revocation fetches, HtmlToPdf's `GuardedRemoteResourceLoader`) |
| Is a path strictly inside a directory — both made absolute, the trailing separator, case as the file system's — a local file an HTML page names (both engines) and an extracted attachment's target | `Core.IO.DirectoryContainment.IsInside` |
| Name/number-tree walk (limits the caller's, stop reason reported) | `Core/Document/PdfTreeWalk` (`PdfNameTree`, `PdfNumberTree`) |
| Name-tree key order (bytes, §7.9.6); rewriting a catalog name tree — other categories kept, keys kept as stored, a partial read refuses | `PdfNameTree.CompareKeys`; `Operations.Structure.CatalogNameTree` |
| sfnt assembly; composite-font object graph | `SfntBuilder`; `Core.Writing.Type0FontBuilder` |
| A composite font's Type0, descendant CIDFont and `/CIDSystemInfo` dictionaries (Tables 114, 117, 116) — every composite-font writer, each adding its own `/DW`, `/W`, `/CIDToGIDMap`, `/ToUnicode` after them | `Core.Writing.CompositeFontDictionaries` (`Type0FontBuilder`, the OCR text layer, the PostScript converter's fonts and CMap streams) |
| A TrueType composite glyph's component records — read, renumber, bounded by the glyph's end | `Core.Fonts.CompositeGlyph` |
| Straight-colour pixel access | `Base/Images/StraightPixelLayout` + `BitmapPixels` |
| Form-wide `/DA`, `/Q` default after the climb | `AnnotationFallbackModel.DefaultAppearanceOrFormDefault` / `QuaddingOrFormDefault` |
| Reading a `/DA` string (font, size, `g`/`rg`/`k` fill — numbers, never pasted tokens); its default size | `Base.Operations.Forms.DefaultAppearanceString` (`Parse`, `DefaultFontSize`) |
| An annotation's `/C`/`/IC` as a colour; the appearance builder's free-text placement and stamp colour/width/caption | `AnnotationFallbackModel.AnnotationColor`; the fallback's `FreeTextBaseline`, `StampColor`, `StampMinLineWidth`, `StampCaption` |
| Can a one-byte simple font carry this text | `Core.Text.TextRunComposer.NeedsCompositeFont` |
| Where a paragraph may break between its lines — its orphans before a break, its widows after one, both for a fragment between two (CSS 2.1 §13.3.3), a forced break exempt — the page breaker, `SplitAtHeight` and a column's balance | `PdfExporter.LinesBeforeBreak` / `MinFragmentLines` |
| Where an English word may break at a line's end — Liang over hyph-utf8's American English patterns (embedded unmodified, with their notice), the file's exceptions, 2 letters before and 3 after, a token's punctuation set aside; the last point whose head and hyphen fit — the Builder's wrap and reflow alike | `Core.Text.Hyphenation.EnglishHyphenation` (`BreakPoints`, `BreakPointsWithin`) |
| A font's mark positioning (`GPOS` `curs`/`mark`/`mkmk`: cursive attachment (the left glyph's advance from the join, the hanging glyph's height chained, the RightToLeft flag choosing which hangs), mark-to-base, mark-to-ligature (the last component), mark-to-mark, single adjustment, and context and chained context in all three formats (glyph, class, coverage — one rule matcher) with nested lookups, through extension lookups, bounded; `GDEF` classes, the ignore flags, the mark-attachment type and the mark filtering set, the set deciding) — applied in logical order, placed in visual order, the mark's advance removed; **kerning** when the producer asks (`TextRunOptions.Kerning`: the `kern` feature's pair adjustments, formats 1 and 2, else the legacy `kern` table) — an advance change widens the gap after its glyph **in reading order**, so right to left it goes to the letter drawn on the left | `Core.Fonts.OpenTypeMarkPositioning`; `TextRunComposer` (`ShapedGlyphRun.Placements`) |
| A font's `GSUB` `rclt` substitutions — one for one (single, directly or through context, chained context and extension lookups), applied before positioning, the code point kept; the same reader and rule matcher as `GPOS`, in substitution mode — and its `calt` ones when the producer asks (`TextRunOptions.ContextualAlternates`, switched per producer as kerning is) | `OpenTypeMarkPositioning.TryLoadSubstitution` / `Substitute`; `TextRunComposer` |
| A font's own ligatures (`GSUB` type 4 — `rlig` always, `liga` when the producer asks: `TextRunOptions.Ligatures`, the Builder's switches, DOCX `w14:ligatures`, CSS `font-variant-ligatures`; `dlig` and `hlig` each under its own switch, `DiscretionaryLigatures` / `HistoricalLigatures`, from the same two sources): several glyphs become one, the components removed and skipped by every later lookup, a mark between them skipped only under the lookup's flags; the ligature placed where its first component was, its `GlyphMapping.Text` the letters it joined — **recorded by every producer through `RecordUsedGlyphs` (or `EmbeddedFont.Note`'s text) and written by the one `/ToUnicode` writer**; a new producer that registers glyph ids alone writes a ligature back as its first letter | `OpenTypeMarkPositioning.Substitute` (`Substituted`); `TextRunComposer`; `ShapedGlyphRun.RecordUsedGlyphs`; `ToUnicodeCMapWriter.Build(…, codeToText)` |
| A run's OpenType script tag (Unicode's Script property, generated from the Character Database by `tools/unicode-scripts` — never a hand range; the Indic second-generation tags, `kana` for both kana, the space-padded `lao `/`yi  `/`nko `/`vai `): **per segment** — a run is cut into maximal runs of one script in logical order, a glyph of no script (space, digit, point, punctuation) taking its neighbour's, a run with none `latn` — each segment's lookups taken under its own tag, so a Latin word beside Hebrew kerns under `latn` while the Hebrew is pointed under `hebr`; looked up with a shaper's fallbacks — the first-generation tag, `DFLT`, `dflt`, `latn` — at **every** by-script site of the OpenType reader | `Core.Text.UnicodeScripts` (`TagOf(int)`, `Fallbacks`); `TextRunComposer.ScriptSegments`; `OpenTypeMarkPositioning.LookupsFor` / `JoiningLookups` |
| An Arabic-block character's joining type (Unicode's, generated — never a hand range): which characters a join passes through, and how a letter the presentation-form table lacks joins; such a letter's position read from the forms its neighbours took, drawn through the font's `init`/`medi`/`fina` | `Core.Text.ArabicJoiningTypes`; `OpenTypeShaper.TryGetUnformedJoiningFeature` |
| A Hebrew letter and its point drawn as the presentation form where the font has its glyph (the pairs from the platform's canonical decompositions, either mark order) — and written back in `/ToUnicode` as the letter and point | `Core.Text.HebrewPresentationForms` (`Compose`, `Decomposition`); `ToUnicodeCMapWriter` |
| A lam joined to a following alef drawn as the lam-alef ligature (after joining; isolated or final from the lam's form; madda and hamza kept) where the font has it — **unless the font's own required rules (`rlig`, `rclt`) redraw the two forms**, as one ligature glyph or as two pieces (Segoe UI), which then win, since the font's mark anchors are on the glyphs its rules produce, not necessarily on the glyph its `cmap` maps U+FEFB to — and written back as lam and alef | `Core.Text.ArabicLigatures` (`Compose`, `Decomposition`); `TextRunComposer.FontRedrawsLamAlef`; `ToUnicodeCMapWriter` |
| A glyph's several characters in a visual line an extractor reorders (reversed when right-to-left, so the line's reversal restores them) | `Core.Text.UnicodeBidi.InVisualOrder` |
| A drawn line's direction when nothing says its paragraph's — its ends' strong characters when they agree, else the more of its strong characters, else its left end — both extractors under bidi | `Core.Text.UnicodeBidi.VisualLineDirection` |
| The embeddings, overrides and isolates open at a point of a text (UAX #9 X1–X8: an isolate shields its content, a PDF never closes one) and the override's direction — the Builder's tokens and the legacy DOCX lines alike | `Core.Text.BidiControlStack` |
| A legacy DOCX line in visual order — UAX #9 over its paragraph once, before wrapping, each line located in it and given its part (L1 per line), each segment cut where its level changes, L2 over the pieces — a body line and a cell line alike; a run's spaces through every wrap; where a tab stop aligns the text after it (mirrored right to left, as Word 16 places it) | DocxToPdf `Rendering.LineBidi` (`Paragraph`); `UnicodeText.SpacedWords`; `ParagraphRenderer.AlignedOffset` |
| A glyph's advance when its font may not say — an explicit 0 is a real width, the default only for no data — the raster renderer, both extractors | `Core.Fonts.GlyphWidth.Units` |
| One character code's text, from the font's per-code cache (a simple font's 256, a composite font's up to `MaxCachedCodes`) — never a copy of the code's bytes decoded afresh; both extractors | `PdfFont.Decode(CMapCodec.Code)`; `FontCache.DecodeCode` |
| A composed run into a content stream — `<…> Tj`, or the `TJ`/`Ts` its placements need, with a producer's own per-glyph adjustments (word spacing) — every producer | `Core.Text.GlyphRunShowText.Operators`; `PdfContentStreamBuilder.ShowGlyphs(…, placements, …)` |
| What a widget shows (password masking) | `AnnotationFallbackModel.DisplayedWidgetValue` |
| §12.7.4.3 field layout (multiline, comb) | `FieldLayout.For` |
| Writing AcroForm fields (with appearances) — **one author**; a producer outside Base reports placements instead | `PdfFormFieldBuilder.AddFields` (one revision); Builder's `PdfExporter.PlacedFormFields` |
| File-spec name (`/UF` before `/F`) | `PdfAttachmentManager.FileSpecName` |
| CMap sections as tokens | `CMapParser.SectionTokens` |
| Fill a buffer from a stream | `Core.IO.StreamBytes.Fill` |
| Per-page text walk (callback must not change the answer) | `PdfTextExtractor.ExtractText(PdfDocument, Action<int,string>)` |
| Signed byte ranges: read, digest, reaches-EOF | `Core/Document/SignedByteRanges` |
| CMS into `/Contents`; **DER** encoding | `SigningHelpers.WriteContents`; `CmsSignatureGenerator.EncodeForContents` |
| A signature dictionary's text strings (`/Reason`, `/Location`, `/ContactInfo`, a new field's `/T`) — a literal when ASCII, else UTF-16BE hex, never the raw writer's ASCII | `SigningHelpers.TextStringToken` |
| Add a signature field; list signatures once each | `SignatureFieldRegistration`; `SignatureFieldWalk.DistinctSignatures` |
| A visible signature's image — drawn through an inner form whose own `/Resources` names it, the appearance's `/Resources /XObject` naming that form alone (Acrobat does not validate a signature whose appearance names an image there: "Expected a dict object") — the image stamp and the text block's logo alike | `Core.Writing.SignatureAppearanceImage` (`InnerForm`, `AppearanceXObjects`, `Invocation`) |
| `/DSS` revision; superseded object number | `PdfLtvRefresher.DssRevision`; `SigningHelpers.ObjectNumberOf` |
| Signature ordering / certification rules | `SignatureOrdering` over `DocMdpAnalyzer`; `DocMdpAnalyzer.FindCertification` |
| Signature timestamp imprint | `Signing/Timestamping/SignatureTimestamp` |
| Revocation path | `DssBuilder.RevocationPath` |
| Signature-field name lookup; reported name | `PdfAcroForm.FindSignatureField`; `PdfFormField.ReportedName` |
| Whether a CRL may vouch for a certificate (RFC 5280 §6.3.3: not a delta, not a partition the certificate is outside — CA/user, reasons, distribution point — not indirect) — the verifier reading absence as Good and the LTV embedder alike | `Signing.Cms.CrlScope.WhyItCannotVouchFor` |
| Which `SingleResponse` of an OCSP response is about a certificate (its serial, and the issuer's name and key hashes under the `CertID`'s own algorithm — serials are unique only within an issuer); whether a delegated responder's certificate is within its validity period when the response is relied on — the verifier before it reads a status and the LTV embedder before it writes a response; whether the response's signer may speak for the issuer (the issuer's key first, then each certificate carried: issued by the issuer, id-kp-OCSPSigning, within its period) | `Signing.Cms.OcspSingleResponse` (`Find`, `WhyTheResponderCannotVouch`, `WhyADelegatedResponderCannotSign`) |
| Whether revocation data is current at a time — `thisUpdate` not in the future, `nextUpdate` not elapsed, the age cap without one (zero is off), an OCSP `producedAt` not in the future, under a skew; differences, never a moved bound — the verifier under its options and the LTV embedder and `/DSS` inventory under the verifier's defaults (an OCSP response's seven days the embedder's stricter own) | `Signing.Cms.RevocationFreshness.WhyNotCurrent` |
| Whether a timestamp token is weak (its SignerInfo, its message imprint, the TSA key) — the verifier refusing it under its policy and the TSA client reporting it at signing | `Signing.Cms.WeakAlgorithmPolicy.DetectWeakTimestamp` |
| Which extended key usages a document is signed under (any, e-mail protection, code signing, RFC 9336's document signing, Adobe's and Microsoft's) — the verifier refusing a signer and a picker offering certificates to sign with | `Core.Constants.PdfEkuOids.PermitsDocumentSigning`; `CertStoreLookup.PermitsDocumentSigning` |
| Whether a public key a file or a server supplied is affordable to verify with — an RSA exponent of at most 256 bits (64 above a 3072-bit modulus), DSA's q 256 and p 10 000 bits, an EC field of 661 — asked before every verification; a chain walk's issuer verifications under one allowance | `Signing.Cms.PublicKeyCost` (`WhyTooCostly`, `Affordable`); `CertificateChainValidator.MaxIssuerVerifications` |
| A CMS's signing-time attribute — none under PAdES (EN 319 142-1 §6.3 Table 1), the instant `/M` claims otherwise, never BouncyCastle's "now" — `PdfSigner.Sign`, `Plan`/`Complete`, the Ed448 CMS and the deferred builder over a `PreparedSignature` | `Signing.Cms.CmsSignatureGenerator.SigningTimeAttributeFor`, `SignedAttributeGenerator` |
| Joining partial field names (an empty one adds no segment; a name past `MaxQualifiedFieldNameLength` is its stand-in, the head and the whole name's SHA-256) | `Core.Forms.FieldNames.Qualify` |
| Is a `/Kids` entry a field or a widget (a name is a non-empty `/T`); the `/FT` a bare widget kid declares — the field model and the signer alike | `Core.Forms.AcroFieldClassification` (`IsFieldNode`, `WidgetFieldType`) |
| What an action says (its target, a `/JS` text stream per §7.9.3) and which `/AA` key is which trigger — **dispatcher and runner alike** | `Operations.PdfActionTypes` (`DescribeTarget`, `Triggers`, `AdditionalActionKey`) |
| Every action a document holds (scripts tree, catalog, pages, fields, annotations, outline; chains; one document ceiling) — PDF/A's and PDF/X's action types too | `PdfActionDispatcher.ExtractActions` |
| Which dictionaries hold `/A` or `/AA` (catalog, pages, annotations with the widget told apart, every field node); one entry per violation message, counted | `Plugins.Conformance.ActionOwnerWalk`; `ActionFindings` |
| A writer that rebuilds an entry from what it read (`/Names`, `/AcroForm`, `/Fields`) telling "absent" from "unreadable", and refusing the latter — the name-tree rewriter, the field builder, every signature writer | `Core.Document.EntryRead`; Signing's `SignatureFieldRegistration.FormOf` / `FieldsOf` |
| The PDF/UA checkers' and the accessibility fixer's dereference, and what it could not read (counted per run, one "incomplete" notice — UA99, UA2-99, a fixer warning) | `Plugins.Conformance.UnreadableObjects` |
| `/Lock` / FieldMDP; `/Reference` reading | `SignatureFieldLock`, `FieldMdpAnalyzer`; `DocMdpAnalyzer.SignatureReferences` |
| `/SV` seed values | `SeedValueConstraints` (`PdfSigner` reads `Choices` only) |
| Raw object scan without xref | `RawObjectScanner` over `ObjectHeaderScanner` |
| Duplicate-overlap text rule (per glyph) | `Core.Text.OverlappingTextIndex` |
| Would a rewrite break a signature? | `Core.Document.PdfSignatureDetection` |
| A sanitized document's payloads and routes out: every file specification's `/EF` and `/RF`, wherever it stands; an external stream, a reference XObject, a URL file specification | `PdfSanitizer.RemoveEveryFileSpecificationsPayload`, `RemoveRoutesOut` |
| Which streams are embedded files — the ones §7.6.5's `/EFF` crypt filter applies to (a stream naming itself `/EmbeddedFile`, or one a file specification's `/EF` names, directly or through a reference, or its `/RF` related files list) — the encryptor, the decryptor and the linearizer's embedded file stream hint table alike; `/EFF` reaches the writer through `IPdfEmbeddedFileEncryptor` | `Core.Document.EmbeddedFileStreams`; `PdfDecryptor.ResolvedEncryption.EmbeddedFileAlgorithm` |
| A stream's own crypt filter (§7.4.10: `/Crypt` first in its `/Filter`, `/Identity` by default) — decides over `/StmF` and `/EFF` for the writer, the encryptor and the decryptor alike, and leaves a decrypted stream | `Core.IO.Filters.StreamCryptFilter` (`TryGetName`, `Remove`) |
| Network timeout meaning; revocation budget | `SigningNetworkOptions.EffectiveTimeout`; `DssBuilder.RevocationBudget` |
| `/MK /R` widget rotation | `Base/Operations/Forms/WidgetRotation` |
| `%PDF-` header origin | `Core/Parsing/PdfHeaderLocator` |
| ICC header gate | `IccProfile.TryReadHeader` |
| xref sections per revision | `PdfParser` (`RevisionSectionPositions`) |
| PDF/UA XMP identifier | `Plugins.Conformance/PdfUaXmpIdentifier` |
| A simple TrueType font against the TrueType clauses — declared encoding, `/Differences` names in the Adobe Glyph List, stated flags, the embedded program, the `cmap` rule's PDF/A-1, ISO 32000-1 and ISO 32000-2 forms — PDF/UA-1's UA51, PDF/A's A80, PDF/UA-2's §8.4.5.7 | `Plugins.Conformance.TrueTypeFontRules` (`TrueTypeCmapRule`) |
| A font subset's `/CharSet` and `/CIDSet` against its program (the six-capital tag; `.notdef` and CID 0 not counted; a TrueType CID by its glyph's outline data through `/CIDToGIDMap`) — PDF/A-1's presence rule and glyphs-used reading (a painted code named through `EmbeddedGlyphSets.GlyphNamesByCode`), PDF/A-2/3's and PDF/UA-1's exact reading, A83 and UA56; what a program holds, for any caller outside Core | `Plugins.Conformance.FontSubsetSets` (`SubsetSetRule`) over `Core.Fonts.EmbeddedGlyphSets` |
| Whether an embedded program defines the glyph a painted code selects (Type 1 names, Type 3 procedures, §9.6.6.4 for TrueType with an empty glyph defined only for whitespace, CIDs through the charset or `/CIDToGIDMap`, a composite font's strings decoded by `EmbeddedGlyphSets.CidReader` over `CMapCodec`) — A84 and UA57; a font this library cannot read is not judged and is said in `LastValidationError`, never an A99 | `Plugins.Conformance.FontGlyphPresence` over `Core.Fonts.EmbeddedGlyphSets.GlyphPresence` |
| Which shown codes reference the `.notdef` glyph (ISO 32000-2 §9.7.6.3: CID 0, a code no codespace admits, a valid code no `cidchar`/`cidrange` maps — `CMapCodec.Code.CidMapped`, the CID kept for the renderer; §9.6.5.2/§9.6.6.4: a `/Differences` name that is `.notdef`, resolves to glyph 0 or the program lacks, a code a fully known encoding names nothing for — never a symbolic TrueType font's `cmap`) — PDF/UA-2's §8.4.5.9 and PDF/A's A86 alike, every string in any render mode | `Core.Fonts.NotdefReferences` (`For`, `In`); `PdfUaFontUse.ShownStrings` |
| The glyph name a simple font's code selects through its `/Encoding` — the four named tables (Annex D), the one name-to-table switch, a font's own code-to-name map with `/Differences` applied (codes left to the program's built-in encoding absent) — the renderer, `Type3Encoding`, the font remediator's `/CharSet` | `Core.Fonts.SimpleFontEncodingNames` (`ByCode`; `FontCache.WinAnsiNames` and the rest are its aliases) |
| Does a font map its codes to Unicode (ISO 14289-1 7.21.7; ISO 19005 level A and U — a Type 3 font's `/Differences` names in the Adobe Glyph List) — PDF/UA's UA26 and PDF/A's A77 | `Plugins.Conformance.FontUnicodeMapping.Maps` |
| Is a structure type standard — ISO 32000-1 Table 333 (PDF/A-2 and -3's level A; Accessibility's checker and tagger, which add PDF 2.0's), the PDF Reference 1.4's (PDF/A-1's) | `Core.Constants.PdfStructureTypes.IsStandardInIso32000Part1` / `IsStandardInPdf14` |
| Merging into an XMP packet (hardened load, bounds, depth-capped search, serialise); a packet's bytes for any reader, decoded no further than 2 MB; the `/Metadata` revision, and the pass-through write a declaration writer makes of it (open, refuse, plan, append) | `Core.Metadata.XmpPacketDom` (`TryReadPacketBytes`; the conformance plugin, outside Core's internals, `XmpStreamBytes`); `Base.Operations.Metadata.XmpMetadataRevision` (`Write`: the declarations writer and the PDF/UA identifier) |
| An existing object's generation — superseding it, referring to it (the table, not a reference; never a clone) | `Core.Writing.ExistingObjectGeneration` (`Of`, `ReferenceTo`) |
| An output intent (profile gate, `/N` from the header, same-`/S` replaced, `/Trapped`) | `Base.Operations.Metadata.PdfIccEmbedder.WriteOutputIntent` |
| The size an **authored** image may have (per side, area) — refused before it is written or decoded; **the writer asks it on every image** (`PdfContentStreamBuilder.AddImage`/`AddJpxImage` throw), a producer that can skip and report asks first | `Core.Writing.AuthoredImageSize` |
| How much of a DOCX part the converter will inflate (every part, before the package is opened; the two image readers stream under the same figure) | `PdfLimits.MaxDocxPartBytes` |
| Raw image samples into a content stream — deflated there, never by the caller, when the page is built; an image several pages register (the same arrays, the same dictionary — never a content hash) written once a document, the producers decoding each image once so they hand over the same arrays | `PdfContentStreamBuilder.AddImage` (`filter: null`); `Core.Writing.ImageObjectShare` |
| A barcode digit (ASCII only — never `char.IsDigit`); a QR version's byte capacity (derived, not tabled) | `Operations.Barcodes.AsciiDigits`; `QrLayout.ByteCapacity` |
| An ECI's character set — Data Matrix's and QR's alike (the AIM assignments an independent reader and an independent reader agree on: 0–13, 15–18, 20–35, the code pages through the provider asked directly and ISO-8859-10, -11, -14 and -16, which no code page carries, through tables generated from Unicode's mapping files (`Iso8859Tables`), decoded strictly; any other ECI refused by number); QR kanji and Hanzi segments (`QrGridReader.ReadDoubleByte`); a QR version and level's blocks, alignment centres and format positions; a Reed–Solomon field's generator base (QR α⁰, Data Matrix α¹ — roots, syndromes and Forney follow it) — each the standard's, from two implementations: **a writer and its reader sharing a mistake pass every round trip**, so each symbology is read from an independent encoder and its output decoded by an independent reader | `Operations.Barcodes.EciCharacterSets`; `QrLayout` (`BlockSizes`, `AlignmentPositions`, `FormatPositionA`/`B`); `GaloisField256` (`GeneratorBase`) |
| Signature-dictionary shape | `Core/Document/SignatureDictionaryShape` |
| OOXML page-number formats (exact, case-sensitive) | `OrdinalNumbering.FormatOoxmlNumber` |
| CLUT interpolation | `IccLut.TryInterpolateClut` |
| One period of a tiling pattern (§8.7.3.1): the step-sized tile anchored at the cell's corner, holding every cell copy that reaches into it — the overlap of a `/BBox` larger than its step — in lattice order, bounded — both renderers | `Rendering.TilingLattice.For` |
| A combined fill and stroke (`B`/`b`, text modes 2 and 6) — §11.7.4.4: direct, a knockout group (the fill kept out from under the stroke), or the overprint case's group at alpha 1 — both renderers | `Rendering.FillStrokeCompositing.Of` (`FillStrokeComposite`) |
| Overprint simulation, raster — pages and tiles: whether a page overprints (every route an ExtGState is reached by, bounded), its plates recombined (process inks through `CmykToRgb`, or a CMYK proof profile's `A2B` when the preview is the press's, each spot multiplied in through its tint transform); the plate passes shared with the separation render; a spot ink's sRGB by tint, alone (a DeviceN ink through the Separation its `/Colorants` define, §8.6.6.5) — the preview and a PSD's spot channel alike | `Rendering.OverprintPreview` (`PageOverprints`, `Preview`, `Recombine`, `Appearance`, `Ink`); `PressProof`; `PdfSeparationRenderer.RenderPlates` |
| A halftone screen in device space — every halftone type (threshold arrays, Types 6, 10 and 16 tiled under `(W, −H₂)` and `(W₂, H)`; spot functions, Type 1, Table 126 run as its PostScript code; Type 5 per colorant), the one inking rule `(255 − tint) × M < T × 255` | `Rendering.Halftones.HalftoneScreen` (`FromThresholdArray`, `FromSpotFunction`), `HalftoneSet`, `SpotFunctions` |
| What every finished surface goes through, pages, tiles and overprint previews alike, never a plate — the proof, then the destination profile | `PdfPageRenderer.FinishSurface` over `SoftProof` and `RenderDestination` |
| An ICC profile embedded in an encoded render — PNG `iCCP`, JPEG `APP2` segments, WebP `ICCP` in the extended form | `Images.ImageProvenance` (`EmbedIccPng`, `EmbedIccJpeg`, `EmbedIccWebp`) |
| A `/Separation` drawn through a named-colour profile — the Lab line from white — at every site that reads a Separation's alternate and tint transform, both renderers | `Rendering.NamedSpotColors.Substitute` |
| The document's colour into one device family — CMYK through a profile, RGB through an intent — content, images, shadings, palettes, spot alternates, patterns, groups; the PDF/X and PDF/A converters alike; an image's CMYK | `Operations.Metadata.ColourConverter` (`PdfColorSpaceConverter.ConvertToCmyk`, `ConvertToRgb`); `PdfColorSpaceConverter.RgbToCmyk` |
| OCR sample buffer | `Base/Images/OcrSampleBuffer` |
| Non-finite glyph advance | `MatrixGuard.SanitizeAdvance` |
| A real past `float`'s range — an operator with such an operand skipped, a graphics-state entry read as absent, a form, pattern, annotation or page box holding one not drawn and reported — both renderers | `MatrixGuard` (`HasNonFiniteOperand`, `TryFinite`, `HasNonFiniteNumber`, `PatternGeometryNotFinite`) |
| A file-supplied number read as an `int` — saturated outside the range (never the low 32 bits of `LongValue`), the fallback for NaN, the same on .NET Framework | `Rendering.PdfResolve.Int` / `Saturate` |
| Any `float` or `double` as an integer type — truncated toward zero, the nearer bound outside the range, 0 for NaN — on every runtime (.NET Framework's cast is the minimum) and for `byte`/`ushort` (a cast wraps even on .NET 10); every conversion in `src/` | `Core.Util.SaturatingCast` (`PdfNumber.IntValue`, `PdfResolve.Saturate` over it) |
| A PDF **flag word** — `/P` and annotation `/F`: the low 32 bits of an integer, `null` for anything else (never `IntValue`, which saturates) | `PdfNumber.FlagWord`; faces `PdfPermissionValue`, `PdfAnnotationFlags.FromNumber` |
| The hash of a key a file chose — a number, a name, a bitmap — under a key drawn once a process, never `double.GetHashCode` or an unkeyed content hash (keys in one bucket make every insert walk them all); a string's on .NET Framework | `Core.Util.KeyedHash` (`PdfName`, PostScript's `PsObject`, `Jbig2SymbolEncoder.SymbolKey`) |
| Maintenance page-tree walk; loose-map deref | `PageTreeWalk`; `ContentScanGraph.Deref` |
| A document outline's items, depth first in reading order, one visited set, the depth cap reported — the action dispatcher, PDF/UA-1's language rule and PDF/UA-2 alike | `Operations.Structure.OutlineItems.Walk` |
| Which device colour families a page **paints** for PDF/A, every part (drawn streams only — `Do`, `scn`, `sh`, `Tf`, `gs` from the page and its appearances — each colour remapped through the painting stream's `Default*` space, bases included; **the initial DeviceGray** until a stream that begins in the initial state sets a space; from PDF/A-2 a drawn group's blending space) | `PdfAValidator.CollectPaintedDeviceFamilies` over `ScanPage`'s `ContentStreamSources` |
| A conformance check over each page's resource dictionaries reading a dictionary the pages share **once a validation** — the walk below a page's own content shared, each check's loop skipping what it has read (it dedupes its findings, so no answer changes; the skips are counted) | `Plugins.Conformance.ScanReuse` (`SharedWalk`, `FirstVisit`) |
| Code points a font must cover (the **shaped** run), and whether a font covers one — an Arabic presentation form it reaches from the letter through its own `GSUB` joining feature counts; the cmap-only probe defers such a form, never rejects it | `TextRunComposer.CodePointsToCover`; `TrueTypeFontProgram.CoversShaped` (never `Covers`, at a producer); `FontCoverageProbe.Covers` |
| Font file for family+style (with tie-break) | `FontFileLocator.MatchStems` |
| Is the family a platform resolved the one asked for (separators and case folded) | `FontFileLocator.IsFamilyMatch` |
| What a font's name says about weight and slant (`Bold`/`Heavy`/`Black`; `Italic`/`Oblique`) — the standard-14 face (so its widths), the raster face, the SVG face and the remediator's embedded face | `Core.Fonts.FontNameStyle` (`IsBold`, `IsItalic`); `FontCache.StyleNamedBy` |
| xref-stream field layout | `Core.Writing.XrefStreamFields` |
| The PDF version a feature needs (ISO 32000-2's "(PDF 1.x)" marks — object streams, each encryption algorithm, a filter, an entry, a content-stream feature) and the floor read off the objects a writer is about to write; a header is the version asked for raised to it, never lower, and a raise the caller asked below is reported in one wording — `PdfDocumentBuilder` (so the Builder, HTML and DOCX `Version` options), the legacy DOCX assembler, the one-page generators' skeleton, the PostScript converter, the compressed writer, the encryptor and the optimizer alike | `Core.Writing.PdfFeatureVersions` (`Scan`, `Floor.HeaderFor`, `RaiseReport`); `PdfContentStreamBuilder.RequireContentFeatures` |
| Type 0 function `/Decode` | `PdfFunctionEvaluator.ApplyDecode` |
| SVG writer ambient context | `SvgContentWriter.SetAmbient` / `InheritFrom` |
| QuadPoints / normalised `/Rect` | `PdfAnnotationWriter.QuadPointsFor` / `NormalisedRect` |
| Box intersection + non-negative extent | `PdfSpatialQuery.RectIntersects` / `RequireNonNegativeExtent` |
| A caller's number before it reaches a file or a render — finite, positive, non-negative — naming the parameter and the options member | `Core.Util.NumericArguments` |
| A CSS colour value (hex, `rgb()`/`hsl()` with `%`, the 148 names, `transparent`; ASCII digits; refused past `MaxLength` before the pattern runs) — HTML and SVG alike; a fully transparent one paints nothing | `Core.Text.CssColor.TryParse`, `IsFullyTransparent` |
| A CSS absolute length (`pt px pc in cm mm q`) in points or pixels — each form bit-identical to the arithmetic it replaced | `Core.Text.CssLength` (`TryToPoints`, `TryToPixels`) |
| An XFA measurement (`in pt mm cm pc mp px`, a bare number in points; refused past `MaxTextLength`) and an `r,g,b` colour | `Builder.Import.XfaMeasure` |
| An XFA packet's bytes, stopped at the one ceiling; whether a packet may be built into a tree — its bytes, depth, nodes and attributes, by a streaming pass that builds nothing — the datasets reader, the filler, the layout and the template reader alike | `Core.Forms.XfaPacket.BoundedBytes`; `XfaDatasets.IsWithinBounds` |
| Text made legal in XML — each code point XML 1.0 forbids (U+FFFE and U+FFFF too) replaced with U+FFFD in text a caller authored (the Builder's document writers, the PDF/UA XMP packet), removed from text read out of a PDF (the SVG export, the PDF to Office converters), where it has no glyph | `Core.Text.XmlLegalText` (`Replace`, `Remove`) |
| A PDF affine matrix — composed (row vectors: the left factor first), applied to a point, taken about a point (`transform-origin`), a rectangle's turned bounds — the HTML mapper composing a transform and the exporter applying it to paint and to annotation rectangles alike; in the renderers' ring, at their `float`, the same arithmetic once more (the SVG converter's `Transform` goes through it) — two by ring and precision, never a third | `Core.Writing.TransformMatrix` (producers, `double`); `Base.Rendering.PdfMatrix` (renderers, `float`) |
| A bounded walk of the structure tree — elements in document order, each `/K` slot in array order before the element it names is entered, a parent left after its children, a shared subtree once; the depth, node and edge ceilings (`PdfLimits.MaxStructTree*`) and every unresolvable reference reported, the liveness on the walk — every reader of `/StructTreeRoot` | `Operations.Structure.StructureTreeWalk` (`Enter`, `Kid`, `Leave`, `Report`; `RawKidsOf`, `RoleOf`) |
| `/XYZ` destination; how a destination names a page | `Core.Writing.XyzDestination` (`Build`, `PageOf`) |
| `/ToUnicode` CMap for two-byte codes | `Core.Writing.ToUnicodeCMapWriter` |
| Destination array → page index; a page reference or number → index (DPart `/Start`, `/End`); a page object's index, through the document's one index built at the first question — never a scan of the pages per destination | `Base.Operations.PdfDestinationPage` (`IndexFromDestArray`, `IndexOfPage`) over `Core.Document.PdfDocument.PageIndexOfObject` |
| Every image XObject the pages reach, through nested forms, tiling patterns, Type 3 fonts and annotation appearances, once per object — and the walk itself, for any caller reading another category over the same resource dictionaries and containers | `Operations.Images.DocumentImageIndex` (`onResources`, `onContainer`) |
| Does a page need flattening, or does it contain transparency — every transparency construct it reaches (groups, soft masks, alpha, blend modes, image opacity, a shading pattern's graphics state, an annotation's opacity and blend mode), the broader of PDF/A-1's and PDF/X's readings — the flattener and PDF/A-2 to -4's blending-space rule (A41) alike | `Operations.Documents.TransparencyScan` (behind `PdfTransparencyFlattener.FindTransparency`, a stream or an open document) |
| Which blend modes are standard (ISO 32000's two tables and `/Compatible`) — what a renderer recognises in a `/BM` array and what PDF/A-2 to -4 permit (A40) | `Core.Constants.PdfBlendModes.IsStandard` |
| Where each top-level operation of a page paints — every paint call it makes, a nested form's included — in user space; which of them paint transparently, and the page's content with them taken out (a path ends unpainted, text shown invisibly, an image or group form dropped) | `PdfPageRenderer.MeasureOperations` (over the display list); `Operations.Documents.TransparencyRegions` |
| A parsed operation written back as content — its numbers as the parser read them (a float at four decimals moves every glyph under a PostScript driver's `cm`), an inline image as `BI`, its keys as written, `ID`, its bytes, `EI` — the template engine's untouched operations too | `Operations.Documents.ContentOperationWriter` |
| A painting operation taken **out** of a content stream with every other operation's meaning kept — a path's paint becomes `n` (a clip set with it still clips), clipping text stays in mode 7 and the mode is put back, invisible text in mode 3, an image / inline image / shading dropped — the transparency flattener's region pass and the content-class filter and the page-element removal alike | `Operations.Documents.ContentOperationRemoval` |
| One painting operation of a page as an element — what it is, the box it paints in, a run's text as its font maps it — and the page written without some of them: **a text run taken out leaves the pen where it left it** (a one-number `TJ` of the run's own advance — **the font's own widths (§9.4.4), never the measure of the face a host drew a font that is not embedded in**), so text after it on the line stays, in any reader; what the document hides by default is no element; a stroke's box is its points and half its line width; written as one new revision by reachability. **The text of runs a reader chose is replaced in their slot by the template engine's one locator and emitter** (`ReplaceRun`: the runs named by their operations — on one line of the page, whatever text objects they are in — their own text the key, the same words elsewhere untouched and not re-read as a miss), never by a second erase-and-draw | `Operations.Documents.PdfPageElements` over `PdfPageRenderer.MeasureOperations`, `Rendering.ShownTextAdvance` and `PdfTemplateEngine.RunTexts`; `PdfOperations.ListPageElements` / `RemovePageElements` / `ReplacePageElementText` over `PdfTemplateEngine.ReplaceRun` |
| Text drawn on an existing page as its own content — any script the font covers, each line shaped once and laid out in its own direction **from the point the pen goes down** (a right-to-left line runs left from it), upright as the page is shown, in a font embedded under a resource name the page does not use, one appended revision; refused whole, nothing written, for a character the font cannot draw | `Operations.Documents.PdfPageTextWriter` (`PdfOperations.AddPageText`) |
| A gap between words in plain extraction — a `TJ` kern below −0.2 em, or a same-line `Tm` jump wider than 0.2 em of the text as drawn, measured from where the last appended glyph ended; **a `Td` moves from the line's start**, so one that lands where the last glyph ended (a browser's kerning pair) is no gap whatever its operand | `PdfTextExtractor.WordGapEmFraction`, `ContinuesWhereTheLastGlyphEnded`, `TextState.LastShownEnd` |
| An image XObject's pixels at its own size (every depth, `/Decode`, colour space, ICC, masks) — the raster draw, the exporter and the SVG writer alike (the writer hands over the samples it has read and its intent) — and an inline image's: the raster `BI` decodes and draws through the same two (`DrawDecodedImage`, plates included) | `ContentStreamInterpreter.DecodeImageXObject` (over `DecodeImageWithMasks` / `DecodeSamplesWithMasks`); `DrawDecodedImage`; outside Base, as straight RGB and alpha, `Base.Images.PdfImagePixels.Read` |
| An inline image's `/ColorSpace` named from the resources (§8.9.7), or an `/Indexed` base so named, as a colour space — a copy, never the cached operand; reserved names never looked up | `Rendering.InlineImageColorSpace.Resolve` |
| A colour space named from the resources wherever a name may stand for one (inline images, a Pattern space's underlying space); an uncoloured pattern's components through its underlying space (tint transform, ICC, CIE) — both renderers | `Rendering.ColorSpaceResources.Lookup`; `PatternUnderlyingColor.ToColor` |
| Image sample arity; colour-key `/Mask` ranges and mask (§8.9.6.4) | `Rendering.ImageSamples` (a leaf — not `ContentStreamInterpreter`) |
| Zlib (Flate) encoding — no hand-rolled framing or Adler-32 | `Core.IO.Filters.FlateEncodeFilter.Encode` |
| Big-endian 16/32-bit integers into or out of a byte array, a stream or a list — sfnt, CFF, WOFF, ICC, JPEG 2000, JBIG2 (a site keeps its local name as a forwarder; TIFF and GIF are other questions) | `Core.IO.BigEndian` |
| One PNG-filtered row undone — PNG images and `/Predictor` 10–15 streams alike (PNG alone refuses a type above 4) | `Core.IO.Filters.PredictorHelper.UndoPngRow` |
| An array of numbers as a `PdfArray` | `PdfArray.OfNumbers` |
| CSS text cut at top-level separators (outside parentheses and quotes; a stray `)` passed over) — declarations, tokens and every comma list; the `)` closing a `(` | `HtmlToBuilderMapper.SplitTopLevel`, `ClosingParen` |
| An OOXML element's attribute by local name | DocxToPdf `Rendering.OoxmlAttribute.Value` |
| A `/DSS` or `/VRI` revocation list read — each stream once, under the embedder's 20 MB response ceiling, at most 1000, what is skipped said; each CRL and OCSP stream parsed once a document, a CRL's signature checked once an issuer — the verifier and the inventory alike | `Signing.Cms.RevocationChecker` (`ReadStreams`, `CachedCrl`, `CachedOcsp`); `DssBuilder.MaxResponseBytes` |
| The LTV revocation walk and its network steps, sync and async — one implementation run two ways (`sync` blocks in place, so the thread-static diagnostics stay on the caller's thread) | `DssBuilder.WalkRevocationPath` (`FetchOcspCore`, `FetchCrlCore`, `PostOcspRequestCore`) |
| Brotli decoding (RFC 7932) — WOFF 2.0 and `/BrotliDecode` alike, both legs, the output bounded by the caller; its tables generated from the RFC's text | `Core.IO.Brotli.BrotliDecoder` |
| UR transform parameters ↔ `UsageRights` (Table 258); a usage-rights signature is `/Perms /UR3` | `Core.Document.UsageRightsTransform` |
| Which text-showing operators paint (`Tf`/`Tr`, restored at `Q`) | `Core.Text.TextPaintTracker` |
| Where a document uses LZW: every stream the cross-reference table lists (unreadable objects counted), a `/Filter` name or chain, an inline image's `/F` or `/Filter` — PDF/X's X15 and PDF/A's A30 alike | `Plugins.Conformance.LzwUse` |
| Which transfer-function keys an `/ExtGState` sets (`/TR`, `/TR2`; anything but `/Default`, `/Identity` counted) — PDF 2.0's deprecation report and PDF/X's X14 alike | `Plugins.Conformance.TransferFunctionEntries.Declared` |
| Which XMP properties a PDF/A-1 to -3 packet may hold — the XMP Specification's predefined schemas (January 2004 for PDF/A-1, June 2005 for -2 and -3, generated by `tools/xmp-schemas`, never typed), ISO 19005's own, and those the packet's `pdfaExtension:schemas` describes; a value's RDF shape against its declared type; the description's own rules. **A writer that adds a property to a PDF/A packet describes its schema** (`PdfUaXmpWriter` for `pdfuaid`) | `Plugins.Conformance.XmpPropertyRules`, `XmpPredefinedSchemas.ForPart` |
| A `JPXDecode` image's own header against the conformance rules — the images the pages reach, once each; its colour channels, its colour specifications (`APPROX`, `METH`, CIEJab — ignored under a `/ColorSpace`), its depths, HT code-blocks — PDF/A's A24–A29 and PDF/X-4's X20–X23 alike | `Plugins.Conformance.JpxImageRules` (`EachImage`, `TryRead`) |
| The halftones a graphics state's `/HT` names (a type 5's components under their colorants), and whether ISO 32000-1 Table 130 requires one a `/TransferFunction` — PDF/A's A57 and PDF/X's X26 alike | `Plugins.Conformance.Halftones` (`Visit`, `TransferFunctionRequired`) |
| An optional content configuration (`/D`, each of `/Configs`, each dictionary once): its `/Name`, the names two share, the groups its `/Order` leaves out under one document budget — PDF/A's A70–A72 and PDF/X's X27 alike | `Plugins.Conformance.OptionalContentConfigurations` |
| JBIG2's Annex A integer coding (the six value ranges, the PREV rule) and its symbol-ID contexts — the decoder and the encoder alike; a symbol's or a region's generic coding — the page's region and every dictionary symbol | `Jbig2Decoder.Jbig2IntegerDecoder.Ranges` / `Advance` (read by `Jbig2IntegerEncoder`), `DecodeSymbolId` / `Jbig2SymbolEncoder.EncodeSymbolId`; `Jbig2EncodeFilter.CodeGenericBitmap` |
| MQ arithmetic coding — JBIG2 and JPEG 2000: the decoder (segment end, restart, initial states) and the encoder (T.88 FLUSH, T.800 C.2.9 termination); **the prefix of a codeword segment that decodes its first passes exactly** (D.4.1 padding — the padded value inside the interval at **both** ends), computed against the finished segment, never estimated | `Core.IO.Filters.MqDecoder`; `MqEncoder` (`Mark`, `PrefixLengths`) |
| JPEG 2000 rate allocation: a code-block's per-pass distortion (E-6 reconstruction) and prefix lengths; the convex hull of its (bytes, distortion) points; one slope threshold per layer over the tile, judged by writing the layer's packets; what a coefficient's error is worth in the samples (synthesis gains **measured through the decoder's inverse wavelet and component transforms**, never tabled) | `Core.Imaging.Jpx.JpxBlockEncoder` (`JpxBlockCode`); `JpxRateControl` (`HullOf`, `Allocate`); `JpxSynthesisGain` |
| The dictionary a JPEG 2000 file embedded as it is wants (ISO 32000-2 §7.4.9): size from the SIZ; `/BitsPerComponent` only when the components share a depth; `/ColorSpace` only when the file names a device space (sRGB, greyscale, CMYK — or by count for a raw codestream), **never for sYCC or an ICC profile**, which the file describes; `/SMaskInData 1` for a declared opacity channel — `PdfImageToPdf`, `ReplacementImage.Jpeg2000` and the Builder alike | `Core.Imaging.Jpx.JpxImageDictionary`; `PdfContentStreamBuilder.AddJpxImage` |
| A JPEG's frame (the one marker walk: dimensions, components, an Adobe APP14 before or after the frame header) and the dictionary it is embedded under — the device space its component count names, a four-component frame CMYK only under Adobe's marker and with the inverting `/Decode`, any other refused, never guessed — Base's image writers, the SVG converter and the Builder alike | `Core.Imaging.RasterImageInspector.TryInspectJpeg`; `Core.Imaging.JpegColorSpace` (`Of`, `DecodeFor`) |
| Raw samples → JPEG 2000 for any consumer (8- or 16-bit big-endian, grey/RGB/CMYK; a quality's step — 1 at 100, doubling every 16⅔ points; the JP2 container's bytes counted against a size target) | `Core.Imaging.Jpx.JpxImageEncoding` (`Encode`, `StepForQuality`); `JpxEncoder.WrapAsJp2` |
| JPEG 2000 framing; JP2 box walk; SIZ grid and precision; JP2 colour-channel count | `Core.Imaging.Jpx.JpxHeader` |
| JPEG 2000 SIZ reader and grid check; Annex B geometry (B-1 to B-18); header precedence (tile COC > tile COD > main COC > main COD) | `Core.Imaging.Jpx.JpxImageSize`; `JpxGeometry`; `JpxCodestream.*For` |
| JPEG 2000 packet order (five progressions, POC volumes, B-20); packet-header syntax (bit stuffing, tag trees, Table B.4, B-19); where a codeword segment ends per code-block style (Tables D.8/D.9) — tier 1 reads the chunks, never re-derives the boundaries | `Core.Imaging.Jpx.JpxPacketSequence`; `JpxBitReader`, `JpxTagTree`, `JpxPacketDecoder` (`IsTerminated`) |
| JPEG 2000 coefficient bit modelling (Annex D contexts, passes, style flags, §H.1 ROI); a decoded value is **twice the magnitude with E-6's half at the last decoded plane** — `Truncate` for the integer; **an `int` band holds 30 planes, a `long` band the rest (M_b + ROI shift decides)**; ε_b/μ_b/M_b per band (E-2, E-5). Tables D.1–D.3 are stated once, as rule functions the decoder's lookup tables and the encoder's state are both built from | `Core.Imaging.Jpx.JpxBlockDecoder` (`ZeroCodingContext`, `SignContext`); `JpxQuantizationSteps`; the encoder's `JpxBlockContexts` asks the same functions |
| HT code-blocks (ITU-T T.814): the cleanup pass's MEL/VLC/MagSgn streams over the quad scan — **MagSgn and VLC a word at a time, each problem raised at the bit where §7.1's procedure raises it** — SigProp and MagRef; **one HT set decoded — the last with cleanup bytes, S_blk = P + P0 + S_skip** — into the Part 1 decoder's convention, so nothing after tier 1 knows; Annex C's tables extracted, never typed (each context a complete prefix code); tier 2's set T is the one place an HT segment ends, **and a block's contributions before its first cleanup are read once, by Annex B's trial — placeholder passes alone (§B.3 NOTE 4), or under MIXED a Part 1 block (§A.4's length rules), which then decodes as Part 1**; **written as one HT set per block** — without a size target one cleanup pass down to plane 0 (zero bit-planes M_b − 1), the decoder's mirror, its segments the fixture model's byte for byte; under one, the set the target chooses, SigProp and MagRef packed by **Annex F's own procedures**, the allocation spending the target's leftover on each block's best upgrade | `Core.Imaging.Jpx.JpxHtBlockDecoder`; `JpxHtBlockEncoder`, `JpxHtRefinementEncoder`; `JpxHtRateControl`; `JpxHtCodeTables`; `JpxPacketDecoder.IsTerminated`, `TryReadUndecided`; `JpxCodeBlock.Coder` |
| Lossless transcoding between the Part 1 and HT block coders (T.803 §C.1.10–C.1.12): each block decoded by its own coder and re-coded by the other from the values' known bits; **a block keeps its passes per layer** — Part 1's passes above the last cleanup become placeholder passes, **folded into the zero bit-planes where the first layer brings the last cleanup** (the reference decoder reads no more than three HT passes); a selective transcode is MIXED; the headers rewritten (Rsiz and CPF, CAP's Ccap15, COD/COC style, one tile-part per tile, POCs merged, lengths and packed headers dropped); a document's HTJ2K images made Part 1 in place | `Core.Imaging.Jpx.JpxTranscoder`, `JpxTranscoding` (JP2 ↔ JPH); `Base.Operations.Images.PdfJpxTranscoder` |
| JPEG 2000 inverse wavelet (Annex F: 2D_SR, symmetric extension — **read from the mirror, never built; the columns lifted a row at a time** — 5-3 and 9-7 lifting **with K on even and 1/K on odd samples as F-7 prints it**); Δ_b (E-3, Table E.1 gains paired with that convention) and E-6/E-7 reconstruction; RCT/ICT, level shift, clipping — **on the wavelet's `double` planes, stored once into an `int` plane to 31 bits and a `long` plane above**; Part 2 MCT and NLT; the grid image and 8-bit narrowing | `Core.Imaging.Jpx.JpxWavelet`; `JpxDequantizer`; `JpxComponentTransforms` (`RoundInPlace`, `Store`, `IsWide`), `JpxMultipleComponentTransform`, `JpxNonLinearity`; `JpxDecodedImage` (`Samples`, `WideSamples`) |
| The JPEG 2000 decode, whole (parse → tier 2 → tier 1 → dequantise → wavelet → transforms → place), under one `WorkBudget` — **shared** (atomic ticks) once `JpxParallel` splits it across workers, never a second budget or a lock; per-tile samples in component coordinates. The encoder shares the same loop (tiles or components through the wavelet, then every code-block of every tile), **each item writing its own slot, so the codestream is the same at any degree** | `Core.Imaging.Jpx.JpxDecoder` (`TryDecode`), `JpxTier1Decoder`; `JpxParallel` (the only worker loop) |
| JP2 colour and channel boxes (colr/pclr/cmap/cdef/opct) and ISO 32000-2 §7.4.9's plan for a PDF consumer (/ColorSpace override, precedence, palette, /SMaskInData, /Decode); ICC signature → arity | `Core.Imaging.Jpx.Jp2ColourDescription` (`TryRead`, `TryPlanForPdf`); `Core.Imaging.IccDataColourSpaces` |
| A `JPXDecode` image's pixels for any consumer — the plan applied (stencil, palette, `/Decode` gate, opacity split, sYCC → RGB, ICC and Lab handed on) and **every sample narrowed by one rule**, the plugin's included; the decoder-before-plugin order | `Core.Imaging.Jpx.JpxImageDecoding` (`TryDecode`, `NarrowTo8Bit`) over `JpxPdfImage`; `Base.Images.JpxImageDecoder` (the order) |
| How far below its native size an image is decoded for a device target (the headroom, the floor, the halvings a resolution-level codec may stop short by) — Skia's codec decode, the raw decimation and the JPEG 2000 reduction alike | `Base.Images.ImageDownsampling` (`Wanted`, `IsReducible`, `HalvingsFor`) |
| A `DCTDecode` image's samples (one and three components through the codec with no colour conversion; four through it with a synthetic profile spliced into the stream, so Skia copies the inks out — two decodes) and whether the codec's colours stand for the image's — a non-device space with the frame's arity is coloured from its samples; the raster decode, the SVG pass-through and `PdfImageReplacer`'s fitting ask alike | `Base.Images.JpegSamples` (`CodecColoursAreTheImages`, `TryDecode`, `Describing`) |
| A paint's ink on a separation plate: `/All`, `/None`, the named colorants, DeviceCMYK and OPM 1, a solid DeviceRGB paint through `/BG` and `/UCR` when the state sets them (§10.3.4, `BlackGeneration`), other spaces by the inverse of `CmykToRgb` — and an image's route, where §8.6.7 keeps the overprint mode off, as for a shading's samples; a colorant space's names and tints (a solid paint's snapshot and a shading's samples alike); which overprinting images leave colorants alone on a composite | `Rendering.SeparationPlate` (`TryTint`, `RouteImage`, `ColorantTints`); `ShadingSpace.OnPlate`; `OverprintCompositor.ImageLeavesColorantsAlone` |
| Absolute colorimetric: the relative table's PCS colour scaled by the media white (`wtpt`) over D50 per channel — no scaling for a version 2 display profile, a missing `wtpt`, or a white at D50 to 1e-3 (the reference colour engine's `_cmsReadMediaWhitePoint`); the output side divides by the destination's | `IccProfile.MediaWhiteScale`; `IccOutputTransform` |
| Black-point compensation, both sides: a profile's black point (the reference colour engine's source and destination searches; the round trip comes back through the relative table); sRGB into an output profile — the `B2A` table for the intent, compensated, white kept white, or a device link from RGB applied as it is (the plates and `PdfColorSpaceConverter` alike); an ICC colour into the render, compensated; PDF 2.0's `/UseBlackPtComp` — carried as the intent's `WithoutBlackPointCompensation` modifier, read only through `Table`/`Compensates` (an image's `/Intent` keeps the state's switch) | `Rendering.IccBlackPoint` (`Source`, `TryDetectDestination`); `IccOutputTransform`; `IccProfile.TryEvaluate`; `IccProfile.ParseB2A(…, intent)`; `IccRenderingIntents` (`Of`, `Table`, `Compensates`, `UseBlackPtComp`) |
| A label a caller typed against a page's text: the quote, apostrophe and hyphen look-alikes folded (gershayim, geresh, curly quotes, two apostrophes, maqaf, the minus sign), NFKC (refused characters kept), Hebrew points removed, lower case, every whitespace and colon dropped — the field extractor keys each line through it a text element at a time, in logical order from the bidi map (**a right-to-left line under its own direction**, any other by most of its strong characters, a tie by its first — the direction that also decides the value's side, `KeyedLine.Rtl`), so a key position names its word and its glyph. Text search and redaction fold apart, opt-in (`Core.Text.HebrewSearchNormalizer`); which characters are Hebrew marks is that class's test alone | `Forms.InvoiceValueParser.NormalizeForMatching` (`LabelKey`, `AppendLabelKey`); `PdfFieldExtractor.Prepare`; `HebrewSearchNormalizer.IsHebrewMark` |
| The invisible layout marks a number laid out in RTL text carries (LRM, RLM, ALM, the BOM, the embeddings, overrides and isolates) — skipped by every reader of a number | `Core.Text.UnicodeBidi.IsInvisibleLayoutMark` (`InvoiceValueParser`, `IsraeliIdentifiers`) |
| Where text may hold an Israeli identifier: 8–9 ASCII digits, groups joined by one hyphen (and one space where the caller allows), no adjoining digit, no part of a decimal — the redactor and the invoice extractor alike | `Core.Text.IsraeliIdentifiers.Candidates` |
| An amount drawn with its currency sign inside it — Word's `3 ₪,539.43` for `₪ 3,539.43`: digits, a word space, the sign, a separator and digits; the glyphs read in visual order, the sign put before the amount where nothing else is, the rest closed up on the integer part — taken apart once, at the invoice extractor's entry, for the fields and the item table alike | `Forms.CurrencySignsInsideAmounts.MovedOut` |
| What a word contributes to a visual line — its glyphs left to right, or its text at its box when it has none — reading order and label keys alike | `TextDirection.VisualGlyphs` |
| A character's bidi class and a bracket's pair — generated from the Character Database by `tools/unicode-bidi`, never a hand range (`₪` is ET, a Hebrew point NSM), read by code point; **the resolution is held to Unicode's `BidiCharacterTest.txt`, every case, levels and order** — a bidi change that passes its own tests and not that file is not done | `Core.Text.UnicodeBidi` (`UnicodeBidi.Classes.cs`, `UnicodeBidi.Brackets.cs`; `BidiCharacterConformanceTests`) |
| A glyph of a `/ReversedChars` run (§14.8.2.3.3) as drawn, its brackets not mirrored a second time — the line text, the label keys and the plain extractor alike | `TextDirection.VisualText` over `UnicodeBidi.AsDrawnInReversedRun` |
| Which glyphs share a line — a baseline's row, joined side by side within the smaller glyph's band, never stacked — and which drawn spaces are its (that band, among a row's glyphs); `OcrTextLayout` and `PdfTableExtractor.ClusterByY` form rows of their own | `PdfStructuredTextExtractor` (`GroupByBaseline`, `BlankCentresOnLine`) |
| A column gap — the text strategy's separator candidates, a prose row's crossing, a phrase's end | `PdfTableExtractor.SeparatesColumns` |
| A line's segments, parted at the column-wide gap that ends a value; whether a title phrase stands as one — the document type, the receipt word and the number after a title alike | `PdfFieldExtractor.Segments` (`TooFar`); `IsraeliInvoiceExtractor.StandsAsTitle` |
| An amount that is a field's value — no letter touching it, none after it, but a currency's — and which of a string's numbers is the amount; the one currency vocabulary | `Forms.InvoiceValueParser.TryParseAmountValue` over `TryParseAmount`'s `TryChooseAmount` |
| Which lines and words a caller-built layout holds — null lines, a line's null `Words` and null words passed over — for every walk of one: the spatial queries and the table extractor at each walk, the field and invoice extractors once, through a cleaned copy at their entry | `Extraction.TextLayouts` (`LinesOf`, `WordsOf`; `WithoutNulls`) |
| Israel's dated tax figures — the VAT rate from 2005, the pre-VAT amount (or, mixing exempt items, the VAT) above which a tax invoice needs an allocation number, which document types the rule covers — judged on the invoice's own date; the rates the taxed-at-rate subtotal labels are built from | `Forms.IsraeliTaxRules` (`VatRateOn`, `AllocationThresholdOn`, `AllocationVatThresholdOn`, `NeedsAllocationNumber`, `StandardVatRates`) |
| Invoices as tables for analysis — one typed row per invoice, line item or finding; CSV with a byte-order mark and formula-defused text; the workbook | `Forms.InvoiceDataTable`, `IsraeliInvoiceTables`; `Plugins.Docx.InvoiceWorkbook` |
| Text an invoice carries and a reader does not see — invisible beside visible text, off the page, under a point high — read with the rest and said | `IsraeliInvoiceExtractor.ReportTextNotShown` (`InvoiceFindingCodes.TextNotShown`) |
| A spreadsheet cell's text: XML-legal, and a `_xHHHH_` sequence escaped so it shows as written — the invoice workbook and the PDF-to-XLSX converter alike | `Plugins.Docx.XmlTextSanitizer.ForSpreadsheet` |

Two documented exceptions: `Plugins.PostScript`'s own subtractive colour model, and `DocxToPdf`'s
`EncodeWinAnsiLiteral` (transcodes to CP1252).

### Constants — no bare literals for named PDF concepts

`Tetitulz.Pdf.Core.Constants` is the single home for spec-named values — **check it before writing a
literal; add to it rather than copy.** `PdfName` (keys), `PdfOperators`/`PdfColorSpaceNames`/
`PdfAnnotationSubtypes`/`PdfBlendModes`/`StandardFonts` (case labels), `PdfUnits`, `PdfPageSizes`,
`PdfLimits`, `PdfAnnotationFlags`, `TextRenderMode`/`PdfLineCap`/`PdfLineJoin`, `PdfDigestOids`/`PdfEkuOids`,
`PdfPageLabelStyles`.

1. `PdfName` cannot be a `case` label; `new PdfName("X")` where `PdfName.X` exists fails the build.
   Never cite an example of an uncached name.
2. Where two values differ on purpose, give each its own name.
3. A cap whose exponent the file chooses needs a constant.
4. `/All` does not go through the tint transform.

**A decode sized by what a budget has left is asked before it runs, and refused at nothing**
(`PdfStream.TryGetDecodedData`: the pipeline reads a ceiling below one as its own default, the largest
there is); **a refused decode spends the budget too**, half of what is left, since only the decoder
knows how far it got (`FormXObjectInventory.Measure`, `ScanFormXObject`, `ScanNestedStream`).

`PdfLimits` supplies values; `Core.Util.WorkBudget` bounds aggregate work. **Counts a file chooses
multiply: bound the product** (`MaxOpenTypeScriptSteps`, `MaxStructTreeEdges`,
`MaxLinearizationSharedReferences`, `MaxAnnotationsPerDocument` — pages × a shared `/Annots`,
`MaxSignatureCoverageMultiple` — signatures × the revision each digests, `OpenTypeMarkPositioning.MaxExaminationsPerGlyph`
— a font's lookups × a run's glyphs, a format-12 `cmap`'s code-point visits across overlapping groups,
a chart's series × their caches, a list marker's length × the numbered paragraphs, N-up's columns × rows
in `long`) — a cap on one factor bounds nothing. **Every recursion over
untrusted nesting carries a depth** against `MaxObjectGraphDepth` (`PdfParser` and
`ContentStreamParser` alike): a stack overflow is uncatchable. **A per-stream budget sized by the
stream's parameters rather than its bytes spends the page's budget too** (`WorkBudget.Parent`,
carried to a filter as `StreamFilter.Work` through `FilterPipeline.Decode`; the JBIG2 composition
budget under `RenderBudget.ImageWork`, `MaxImageDecodeWorkPerPage`; the parser's own object and
cross-reference streams under `PdfParser.SyntaxStreamWork`, `MaxSyntaxStreamDecodeWork`): a stream
of a few dozen bytes can spend its whole budget, and only the page's (the document's) bounds how many
streams do. **A syntax stream whose `/Filter` names an image codec is refused by name before any
decode** (`Parsing.SyntaxStreamFilters`): samples are never syntax, and the codec spends the
document's budget once for every open. **A function a page evaluates once a pixel spends the page's budget**
(`PdfFunctionEvaluator.SpendFrom`, `RenderBudget.FunctionWork`, `MaxFunctionWorkPerPage`) — both renderers'
operator loops, a halftone screen and a colour conversion open it; a new bulk evaluator opens one. **A resource that can
name its own kind** (a form, a pattern cell, a soft-mask group, a Type 3 glyph procedure) **is
charged to the page's budget in both renderers** — one invocation, its decoded length, the
operations it runs, and in the raster renderer every offscreen bitmap its area
(`MaxFormXObjectInvocationsPerPage`, `MaxFormContentBytesPerPage`, `MaxFormOperationsPerPage`,
`MaxOffscreenPixelsPerPage`). **A ceiling's unit has a price, and the price is measured.** **A paint's
price is its area**: the raster renderer charges every fill, stroke, glyph, image and shading — the
page's own stream's too — the device area it covers inside the clip at its kind's measured weight
(`MaxPaintWorkPerPage`, `RenderBudget`'s weights), because the page box chooses the bitmap up to the
area ceiling at any dpi; a stroke pays its length × width where that is less. **A report a branch can
reach is made once a page** — the depth cap, a tiling overlap past the lattice
(`RenderBudget.FirstDepthRefusal`, `FirstTilingApproximation`): every branch the ceilings admit, and
every one they refuse, reaches it. **Behind every site, `RenderDiagnostics.Report` delivers each kind at
most `MaxRenderReportsPerKindPerPage` times a page** (the scope both operator loops open,
`RenderDiagnostics.ForPage`): a static site reached once a colour sample cannot gate itself.

> **Every `PdfLimits` / `PsLimits` limit is settable.** `X` is the property, `DefaultX` the `const`.
> **Read `X` at runtime; use `DefaultX` only where a compile-time constant is required** (a default
> parameter takes a `-1` sentinel instead). Limits are process-global — set once at startup; a
> per-call ceiling is a parameter, and **its forwarding must be mutated too**. A test writing a limit
> joins `PdfLimitsCollection` and restores with `ResetToDefaults()` in a `finally`. Values are not
> validated on assignment.

---

## Coding conventions

- C# `latest`; namespaces match folders; `internal` by default; no `using static`; no extra global usings.
- **Comments and docs describe the current state**: what the code does and why, in the present tense.
  No history — not what a line used to do, which defect or finding produced it, nor a date; a reason
  that needs a contrast states the alternative ("a cast would…"), not the past ("the cast did…").
  History lives in git.
- `float` for user-space coordinates, `double` only where the spec mandates. Pattern matching freely.
- **Every public exception derives from `Core.PdfException`** — guarded by `ExceptionHierarchyTests`,
  whose `OutsideTheHierarchy` list is empty. **A refusal of the file's own content throws one**
  (a domain subclass where it exists); `InvalidOperationException` is a caller's misuse. A filter's
  `InvalidDataException` leaves `FilterPipeline` as a `PdfFilterException`.
- **Read the doc comments first** — they are usually right and the declarations wrong. **A comment
  holds one `<summary>`**: a member written in under another's comment carries both and leaves that one
  none. **A partial type is documented on one part**: the compiler publishes every part's comment, so a
  part's note about its own file is an ordinary `//` comment (both guarded by `OneSummaryPerDocCommentTests`).
- **A file-supplied number is not a `long` until `PdfNumber.IsInteger` says so** — a `(long)` cast of
  an out-of-range double yields `long.MinValue`. Only a literal with a period reaches that path in a test.
- **An unreadable object is not an absent one.** `GetObject` and `ResolveObject` answer both with the
  null object; a reader handed one reads `PdfParser.LastObjectParseError` **immediately** (the slot
  belongs to the most recent call) and refuses or reports — a tree walk stops with
  `PdfTreeTruncation.UnreadableNode`, the DocMDP analyzer fails closed, a writer that leaves the object
  out says so (`DocumentEditDiagnostics`, `PdfSplitter.LastObjectWarning`). A test's unreadable object
  is a stray `]`: the tolerant parser reads an unterminated dictionary on into the next object.
- **A public enum member is appended, never inserted** — its value is compiled into every caller
  (`EncryptionAlgorithm_HasExpectedValues` pins `EncryptionAlgorithm`'s).
- **A public `float`/`double` argument is refused for NaN and ±∞, not by `<= 0` alone** — NaN passes
  every comparison, `Math.Max(NaN, 1)` is NaN, and `PdfRealFormat` writes a non-finite value as `0`,
  so it comes out the other end as a successful write of a blank or degenerate result. **One check,
  `Core.Util.NumericArguments`** (`RequireFinite` / `RequirePositive` / `RequireNonNegative`, naming
  the parameter and an options member), at every entry taking a number or numeric options — the page
  every producer makes (`PdfDocumentBuilder.AddPage`), the tile clip, the ink dpi, Bates,
  header/footer, N-up, image-to-PDF, barcodes, the visual diff, URI links, redaction areas, GCR; a
  private copy of the check is a copy nothing tests. **A model every exporter reads (the Builder's
  `Document`) is validated once, before any of them reads it, by one walk that names the element's path
  and the property** (`Builder.Model.ModelMeasurements`) — not in a setter, not in one exporter; a
  size is refused below 0, a margin, indent, spacing or offset only for NaN and ±∞. **A public constructor
  that sizes a buffer from two `int`s multiplies in `long` and refuses what does not fit**
  (`InkAccumulator`); **a pad or repeat width a caller sets has a cap** (`PdfBatesNumbering.MaxDigits`).
- **A file-supplied field is judged before any arithmetic uses it** — the BMP decoder refuses a depth
  outside the format's seven before the row stride is computed from it; a wrapped stride passed the
  truncation check and allocated the whole image before the per-pixel switch refused. A TIFF field of
  a type the format does not define is dropped, its count sizing nothing.

### Nullable reference types — per-project, and enforced

- **`<Nullable>` is not set repo-wide — do not turn it on globally.** Opted in at zero: `Builder`,
  `HtmlToPdf`, `Signing`, `Plugins.{Text,Svg,Docx,Html,Ocr}`
  (`EthereumSign`: `annotations`). `Core`, `Base`, `Encryption` stay oblivious.
- `src/Directory.Build.targets` / `.props` make nullable and doc-tag codes errors. A new project starts
  with `<NullableWarningsAreErrors>false</NullableWarningsAreErrors>` and a tracking comment — **never
  a shared `NoWarn`**.
- netstandard2.0 analyses differently (no `[NotNullWhen]` on `IsNullOrEmpty`; no `required`).
  `CS8618`: `= ""` and `?` are opposite statements — read the consumers. Widening a shared helper
  moves the obligation to its other callers.

---

## Diagnostics Pattern

Failure-tolerant code must not swallow exceptions silently:

1. Render-time → `RenderDiagnostics.Report(kind, detail, ex)`.
2. Parsing / extraction / signing → a `static string Last*Error` slot.
3. A feature tolerating many failures and continuing → its own sink (model:
   `Accessibility.Internal.AccessibilityDiagnostics`). **A slot per concern** is often better when the
   concerns are few and known (`PdfMerger`). Count what one call can write, not what the file can;
   **a slot a file or a script can write repeatedly has a cap, and the cap has a test**
   (`PdfJsEngine.LastUnsupportedRequest`).

- **No clear-only slots**: a slot nothing writes makes every `Assert.Null` vacuous. Revive it on the
  real give-up paths or delete it (`DiagnosticSlotResetTests` checks both directions).
- **Adding a `RenderDiagnosticKind` is often the whole fix.** Keep `PatternFillUnresolved` (nothing
  painted) apart from `PatternApproximated` (painted, not as described); `RenderParameterIgnored` is
  once per content stream, and **only when `IgnoredParameterReporting.Deviates`** — presence is not deviation.
- **A verification that cannot distinguish its two outcomes says nothing.**
- **A security comment states what the guard is, never what is hoped** — a comment denying a known
  limit is read as a promise by the next reader.

### Concurrency — `Last*` slots are `[ThreadStatic]`

**Every new `Last*` slot is `[ThreadStatic]`** (guarded by `BaseDiagnosticSurfaceTests`). Read it on
the calling thread; it does not flow across `await` (report on a result object instead); and **reset
it at the start of the operation on every path** (`DiagnosticSlotResetTests`). A thread-static
hand-off between two operators is a claim that nothing runs between them, which a language does not
make (the PostScript `findfont`/`setfont` capture was one).

---

## Rendering

### There are two renderers, and a rule about that

`ContentStreamInterpreter` (raster) and `SvgContentWriter` (vector, entry `PdfSvgRenderer`) **must
agree**; every silently-wrong-output defect so far was one lagging the other.

- Their partials share suffixes (`.Path`, `.Text`, `.Color`, `.Shading`, `.Images`, `.XObject`,
  `.InlineImage`, `.GraphicsState`, `.MarkedContent`). **Adding a file to one? Ask whether the other needs it.**
- **A spec rule that answers a value gets one implementation reached from both**: OC visibility
  (`IsOcEntryHidden`/`IsAnnotationOcHidden`), colour-key mask (`ImageSamples.TryComputeColorKeyMask`), image decode
  (`DecodeImageStream`), mesh vertex colour (`MeshVertexColor`), page box (`ResolveRenderBox` +
  `PageBoxMetrics`), Type 3 encoding (`Type3Encoding`), stale widgets (`AnnotationFallbackModel`), all
  colour (`PdfColor`), nested ICC (`IccColorTransform.ResolveAlternateColor`), ICC arity
  (`PdfColor.IccComponentCount`), `/BM` (`PdfBlendModeEntry.FirstRecognised`), annotation visibility
  (`AnnotationVisibility.IsHidden`), annotation composite (`AnnotationAppearance`; an `/AS` naming a state `/N` lacks draws
  nothing — `TryNamedState`, the flattener too), no-`/AP` widget
  border (`AnnotationFallbackModel.WidgetBorderColor`), a group's blending space (`GroupBlendSpace`),
  a device colour under a `Default*` colour space — operators, `cs`, images, inline images, shadings and
  special spaces' device bases (`DefaultColorSpaces`).
- **`PdfResolve` for every dictionary read** — the indexer does not follow references. After fixing
  one read, grep the whole family — **the family includes a metrics array's elements**: `/W`, `/W2`
  and `/DW2` are read through one resolver (`PdfType0Font.ParseWArray`, `CidVerticalMetrics.TryFind`
  take it), by the renderers and the PDF/UA-2 check alike — and so is every element of an array a
  dictionary holds: a `/Rect`, `/BBox`, `/Matrix`, `/QuadPoints`, a border, a colour, a CIE space's
  parameters, a `/BC`, a `/Decode` (`PdfResolve.ArrayDouble`, `ArrayFloat`; a page box arrives from
  `PdfPage` with its numbers in place; `ContentOperand`'s element readers are for operands, which hold no references).
- **A `Q` leaves the text matrices** (§9.3.1): both renderers, both extractors, the redaction and template
  engines. A walker of a page's text that restores them looks for what follows in the wrong place.
- **One quantisation rule: `PdfColor.Channel`.** Never `(byte)(v * 255)`.
- **Never touch pixels through `SKBitmap.Pixels`** — use `BitmapPixels`. Straight is not raw on a
  `Premul` surface. Both renderers decode images through one path, to 8888 (Premul where the image
  carries transparency); **a stencil decoded for an SVG `<mask>` is the one `Gray8`/`Rgb888x` left —
  check `CanStore` before writing such a bitmap.**
- **Share downward into a leaf, never sideways** (`ContentStreamInterpreter` has a static initializer).
- **A comment asserting a safety property is not the property** — a depth cap bounds one branch, not
  the number of branches.
- Assert the two against each other (`SvgRasterParityTests`, `RenderGeometryParityTests`); **SVG
  assertions parse an `XDocument`**; an advance defect needs **a later glyph's position** asserted.
- SVG-owned: `Shared.IdCounter` for every minted id, and **the page's one font cache and font-program
  reads** (`Shared.Fonts`, `FontPrograms`) — a child writer is a form invocation; `SvgSyntax` is a leaf. **The SVG writer decodes
  no image itself** — the JPEG pass-through and the stencil `<mask>` are its; every other image is
  `DecodeImageXObject`'s, then the transfer function, then PNG.
- **A font program the SVG embedder synthesises is glyph-indexed** (`SvgFontEmbedder.Program.GlyphIndexed`:
  a bare CFF wrapped, a Type 1 converted and wrapped, its `cmap` U+F0000 + glyph through
  `CffOtfWrapper.TryBuild(…, glyphIndexedCmap: true)`): the visible run is written in those code points
  and the document's text in the zero-opacity overlay; **a payload is checked by SkiaSharp's reader**,
  never by a round trip through our own. An sfnt is emitted as it is; the stub `cmap` stays for Skia.
- **A tiling cell is drawn at the tile's scale, never `DpiScale`** — the tile is one step in whole
  pixels, so the two differ by the rounding, and a cell drawn at the page's scale falls short of its tile.

### ContentStreamInterpreter

Operator handlers live in `ContentStreamInterpreter`: register in `Dispatch`, implement as private
`Op<Name>`. **Never put interpreter concerns in `PdfRenderContext`** (sole exception: the
parsed-operation cache).

### Performance — measure before you rank

- **PNG encoding dominates a render; opening a document is negligible; tokenizing is 25% of a vector
  page's allocation** (61% before §13b's struct lexer; 7% of a text page's). Rank by allocation, not a
  noisy clock. Say which regime a ratio is from, and re-measure a ratio before ranking by it.
- `PngCompression` defaults to `Fast` (cannot change a pixel). `RenderPageToPixels` returns no
  SkiaSharp type on purpose. **`PdfRenderSession` is a tile optimisation and a page loop's**, not
  thread-safe — not a win for repainting one page, but **a one-shot render opens the whole document**,
  so a loop of them over a document's pages is quadratic (800 pages: 35 s against 0.8 s). Batch rendering: `RenderAllPagesCore`, `MaxRenderThreads = 1` for
  calling-thread diagnostics.
- **One encoder, `PdfImageEncoder`**, for PNG/JPEG/WebP and the separated TIFF of plates — no other format elsewhere; raw samples
  from outside Base reach it through the public `PdfRawImageEncoder.EncodePng`. **JPEG 2000 is not
  its fourth format**: that is the in-house codec's own door, `Core.Imaging.Jpx.JpxImageEncoding`.
- Benchmarks: run `-- verify` first; read the allocation column; a per-call cost is not a ranking
  until multiplied by a call count; stub-and-measure for an upper bound. **A new entry point must be
  checked against the existing path on encryption, colour and clipping.**
- **A fit measured by a whole-chunk trial is found by doubling and bisection**, never one item at a
  time (`PdfSplitPlanner.PlanUnderByteCeiling`: the size only grows with the pages, so the ceiling is
  crossed once). The trial count is the evidence — the result cannot show which search ran.
- **A per-item search that may run to the end of the buffer is quadratic in the item count** — stop
  at the next item's boundary and remember a search that found nothing
  (`ObjectHeaderScanner.ResumeCursor`).

---

## Testing

- Every new method gets a test; tests build minimal in-memory PDFs; xUnit, no mocks unless unavoidable;
  `tests/Tetitulz.Pdf.<Xxx>.Tests/<Feature>Tests.cs`. `dotnet test` after every change.
- **DoS / recursion guards get an adversarial-input test** that would have hung pre-fix.
- **A class of defect found by reading is then probed by running** — a test that feeds the class to
  a whole producer (every measurement NaN; a 65 535² frame header; a 257 MB part) reaches the layers
  a read of one component cannot: Core's `AddPage`, the writer's image size and the DOCX part cap
  are each such a layer, below reads that come back clean.
- Corpus files through `TestCorpus` (`TETITULZ_PDF_CORPUS` / `TETITULZ_PDF_RAW`); the corpus is not committed —
  **except the committed CC BY conformance corpora** (`tests/Tetitulz.Pdf.Plugins.Conformance.Tests/Fixtures/veraPDF`, `Fixtures/bfo`), run against
  the verdict in each name; its `known-disagreements.tsv` only shrinks.
- **Every writer's output passes an independent structure check, in both file layouts** —
  `WriterOutputIndependentCheckTests` over `IndependentPdfCheck` (its own tokenizer, cross-reference
  reader and standard-handler decryption, sharing no code with the library). **A new writer joins its
  table.** **An encoder's stream is read by a decoder this repository did not write**: the answers an
  independent decoder gave are recorded under `Fixtures/IndependentDecodes` and `Fixtures/IndependentRenders`,
  keyed by the bytes (`IndependentJpxDecode`, `IndependentImageRender`), so an encoder whose output
  changes fails until its new bytes are decoded independently and recorded; the JP2 container and the
  Ed448 CMS have in-repo validators (`IndependentJp2Check`, `IndependentEd448Cms`). **No test runs a
  program outside the repository.** A round trip through our own decoder shares whatever both halves
  believe. The public-corpus scorecard (`tools/corpus-scorecard`) counts crashes, hangs, opens and round
  trips; its "made worse" rows (input clean, our output not) must stay empty. **The structure fuzzer is the same
  harness fed a generated corpus** (`tools/corpus-scorecard --generate`, `HostileGraphs.cs`: twenty
  families, seeded, sizes by round) — run it after a reader changes and before a release; a crash, a
  hang, a memory blow-up or an exception that is not a typed refusal (the child probes some forty
  public readers and the writers that decide what to keep) is a finding; a `PdfException` or an argument
  error is not. **The same harness runs
  hostile markup through the managed HTML engine** (`--generate-html`, `--html`; `HostileHtml.cs`).
  **What the mapper hands a dependency it cannot interrupt is bounded before the hand-over, and the
  bound is measured** (`RefuseDeepMarkup`, `SelectorComplexity`, `ParseSpan`, `MaxTableCells`).
  **Coverage-guided fuzzing is `tools/fuzz`** (`run.ps1 -Target <t>`: twenty targets over the public
  doors, SharpFuzz under libFuzzer, the corpus persisting between runs) — run a target before a
  release and after its reader changes; a target swallows only the declared refusals, so every
  artifact is a finding; a timeout is a finding about **cost** (instrumented code runs about six
  times slower), measured on the shipped build by `--replay` or a probe before it is judged.
  `[CorpusFact]`/`[CorpusTheory]` carry `Category=Corpus`: **`--filter "Category!=Corpus"` halves a
  local Base run**, and `mutate.ps1` leaves them out (a corpus-only kill is a CI survivor; `-IncludeCorpus`).
- **Every README's C# compiles against the code** — `tools/readme-samples/check.ps1`, before a release
  and after changing an API a README shows; a listing is checked by reflection.
- **An RSA key in a test comes from `TestHelpers.RsaTestKeys.Next()`** — distinct within the test,
  reused across tests (fresh generation was most of the signing suites' time). A key held by a
  `static` fixture is made inside `RsaTestKeys.Shared(...)`, or a later test can be handed it.
- **Never record a test count anywhere.**

### Three banned patterns — all fail the build

1. No absolute path into a local checkout.
2. **No `if (!File.Exists(path)) return;`** — use `[CorpusFact]`/`[CorpusTheory]` (reports Skipped).
3. **No assertion that cannot fail** (`Assert.NotEmpty(png)`, `Record.Exception` + `Assert.Null`).
   Assert decoded pixels or the parsed object. There is no allow-list. A test whose only assertions
   are inside a helper that returns the value is flagged too — assert on what the helper returned.

A no-throw test is allowed only when the input is hostile: the name must carry a token from
`NoUnfalsifiableAssertionsTests.HostileInputNameTokens` (the source is the authority). The guard is a
substring match and misses `Assert.True(png.Length > 100)` — a green guard is not a clean bill.

- **A negative assertion needs a potency assertion beside it** — an empty output also "does not contain".
- **Two methods answering one question are asserted in one test**, with a matching control.
- **When behaviour changes, rename the test or restore the behaviour** — never edit the assertion and
  leave the name.
- **A refusal test must refuse for the reason in its name**; pair refusals with an acceptance control.

### Three blind spots no assertion about the *answer* can reach

1. **A parser normalises away what a raw writer can get wrong** (duplicate keys; IMPLICIT vs EXPLICIT
   tagging) — read raw bytes.
2. **An optimisation whose fast and slow paths agree** — assert its liveness (`UsesCidRangeIndex`); a
   DoS ceiling likewise — probe the work (`LastEdgesExamined`).
3. **A relative assertion cannot see a change to both sides** — pin a characterisation value, or hand
   the output to an independent implementation (the BER-vs-DER finding).

### Mutation

**Mutate the code to prove a new test can fail; a survivor is a finding about the test.** Four
explanations: fixture never reached it; assertion weaker than the claim; equivalent mutation; **the line
does nothing** (read the callee). A fifth when mutating an authoring side against a checker: **the
standard permits it** — read the clause, leave an acceptance control.

- Re-run a mutation against the test written for it, and against **every consumer's suite**.
- A guard can be redundant at one end of its input range and load-bearing at the other.
- A merge exposes the branch nothing tested — mutate the survivor.
- **Before removing a write, grep every read of what it wrote.** A new stage that also removes what an
  earlier one removes can un-kill the earlier stage's mutations — re-run them.
- A rename that breaks the build proves nothing; **report build failures as such**.
- **Quote the run's total and skip count beside every verdict.** A short total is a dead run.
- A ceiling only production scale reaches is tested as a parameter — **then mutate the forwarding**.

**Harness hygiene** (`.claude/mutate.ps1`): derive line endings **per file, by majority**; a missed
anchor is not a kill; write multi-line anchors as one double-quoted string; **baseline green in the
harness's own process first**; replace a brace-less `if` body with `;`; read *which* tests killed it;
**after killing a harness, re-run the suite** (a corrupted baseline reports false verdicts); kill the
`testhost`, not just `dotnet test`; **never two runs at once** (the lock; kill the shell by pid); **when
a failure's numbers don't match the source, rebuild**; a DoS mutation that does not return is the
strongest kill; **so is one that eats memory** (a test host over `-MaxTestMemoryGB` is killed; the
time limit alone let one freeze the machine); **after a machine reset, restore the tree from git, not
from `mutation-backup`**; a scripted edit must not change a file's encoding or BOM. **A sweep mutates
the shared checkout**: do not edit or read a source file it names while it runs, and do not run
`dotnet` beside it.

### Diagnosing a red whole-solution run

1. **`dotnet test --blame` first** — green sequentially means concurrency, not an assertion.
2. **An assertion that reads shared state asserts about the whole process** — filter to the calling
   thread; don't serialise the collection. A collector that misses an event means the publisher lost it.
3. **A count that disagrees with the assembly's total is a stale binary or an aborted run.**
4. **A DoS-guard test asserts the guard's evidence, never a wall clock.** Remedies in order: delete the
   clock; run on a dedicated `Thread` with a generous `Join`; measure the thread's CPU time.
5. **A merge whose survivor no test reaches is a deletion** — mutate the survivor.
6. **Run `git status` before calling a repository-wide guard flaky.**
7. **A test host that lingers after `Passed!` is a test still running** — `dotnet-stack report -p`
   names it; a per-item search that runs to the end of the buffer is quadratic in the item count.

### Parallel work

- Partition by assembly; **the silent auto-merge is the expensive case** — diff each parent against
  the result and account for every dropped hunk.
- **A finding or a register row is a hypothesis; the code is the authority** — verify before acting.
- "Nothing implements it" is not "nothing calls it" (`PluginHookDispatchTests`).
- Corpus smoke tests correlate against a second opinion.

---

## CI

Three jobs: `build-test` (Windows, full solution, packing gate), `linux-verify` (Ubuntu) and
`macos-verify` (macOS), the last two on `tetitulz.pdf.portable.slnf`; a `v*` tag adds `release`, which
pushes the packages to nuget.org (Trusted Publishing, no stored key; the account is the repository
variable `NUGET_USER`) and attaches them to a GitHub release once the three are green. **Six guard steps — none may
be relaxed to go green**: every `src/` project in the `.sln`; every `src/` library has a test
project (empty `KnownGaps`); every portable project in the `.slnf`; only `src/` libraries packed; no
`TODO_` metadata in a `.nuspec`; **the test run actually completed**. Plus a vulnerability audit and the coverage ratchet; the
restore-the-packed-`.nupkg`s check and the benchmarks' `-- verify` are run locally. Actions are
pinned by SHA, the token is read-only outside `release`, every job has a timeout, and no job installs a test oracle
(none is needed). The workflow file and each step's detail: `CONTRIBUTING.md` § CI. Compiler-setting
guards: never `NoWarn` to go green.
**A shipped package's dependency version is every consumer's floor**: the lowest that carries what the
library needs (an API, a fix), never raised for freshness — a consumer pinning below it cannot restore.
Dependabot skips the majors of the BCL packages the libraries ship with.

---

## Build & test

```bash
# From the repository root — never hardcode a checkout path.
dotnet build tetitulz.pdf.sln
dotnet test  tetitulz.pdf.sln
dotnet pack  tetitulz.pdf.sln -c Release -o artifacts
TETITULZ_PDF_CORPUS=/path/to/pdfs dotnet test tetitulz.pdf.sln   # corpus tests, else Skipped
dotnet test tetitulz.pdf.sln --filter "Category!=Corpus"          # the inner loop: corpus left out
```

Versions come from git tags via MinVer; an untagged commit is a prerelease.

### Gap-closure checklist

1. Implement. 2. Test, including an adversarial-input test for any DoS / recursion guard.
3. Expose via `PdfOperations` if public API. 4. Update `GAPS.md` (a closed item leaves it).

---

## Key file locations

The key-file map — with the contract each component keeps — is in `CONTRIBUTING.md` § Key file
locations. **Read a component's row there before editing it**; many rows carry rules that apply only
to that component (reflow, progressive reading, linearization hints, PDF MAC, templating).
