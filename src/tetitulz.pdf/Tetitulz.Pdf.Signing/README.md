# Tetitulz.Pdf.Signing

Digital signatures for Tetitulz.Pdf: CMS and PAdES signing and verification, deferred (two-phase)
signing for remote signers and HSMs, RFC 3161 timestamps, DSS/LTV evidence and DocMDP.

It references `Tetitulz.Pdf.Core` only — not `Base` — so signing does not pull a rendering stack
into your graph. Visible signature appearances live in Core for the same reason.

## Signing

```csharp
using var input  = File.OpenRead("contract.pdf");
using var output = File.Create("contract-signed.pdf");

new PdfSigner().Sign(input, output, new SignatureParameters
{
    CertificatePath     = "signer.pfx",
    CertificatePassword = "…",
    SignatureFieldName  = "Signature1",
    Reason              = "Approved",
});
```

`PdfSigningOperations.GetUnsignedSignatureFieldNames(stream)` lists the names you can pass as
`SignatureFieldName` to sign into a field the document already has. Two things the document's
author may have attached to such a field are honoured when it is signed:

- a **`/Lock`** (ISO 32000-2 Table 236) becomes a FieldMDP reference in the signature, and its `/P`
  makes the signature a certification at that level — the verifier then reports any locked field
  whose value changed afterwards;
- a **seed value dictionary** (`/SV`, Tables 237–238) constrains the signature — sub-filter, digest,
  reason, timestamping server, certification level, and which certificate may sign. A required
  constraint this signer cannot meet (`/Filter` other than `Adobe.PPKLite`, a legal attestation,
  revocation data inside the CMS, a named appearance, a credential to fetch) throws
  `PdfSigningException` naming the entry; an optional one is a recommendation. **The seed's
  timestamping server is the document author's, not yours**: it is asked only when
  `UseDocumentTimestampAuthority` is set and `TimestampAuthorityUrl` is not, and then under the
  revocation fetches' guard; without it a seed that requires a timestamp is refused unless you name
  a server. A SHA-1 the seed offers first is passed over, and a required list of SHA-1 alone refused.

**After signing, read `PdfSigner.LastSignerWarning`.** It is `null`, or the reason this library's
verifier refuses the signature just made under its default options: a certificate outside its
validity period, a key usage that does not permit signing, an extended key usage naming no purpose a
document is signed under (a TLS or timestamping certificate), a critical extension the chain walk
does not process, a SHA-1 digest, a key below the default floors. The signature is written all the
same — a verifier configured otherwise accepts it — but the first report is then yours rather than
the relying party's. Each reason is the verifier's own text, from the function it asks. Trust is not
judged: the signer has no anchors. The slot is per thread and belongs to the last `Sign` or
`Complete`; a deferred or counter signature, whose certificate arrives inside a finished CMS, is not
judged here.

## Verifying — ask the right question

```csharp
using var doc = PdfDocument.Open(stream);
var result = PdfSignatureVerifier.VerifyAll(doc, new SignatureVerificationOptions
{
    TrustAnchors = myRoots,          // without these, nothing can ever be Trusted
});
```

| You want to know | Ask | Do **not** ask |
|---|---|---|
| do the bytes match the CMS | `IsValid` | — |
| is this signer trusted | `IsFullyTrusted`, **with `TrustAnchors` set** | `IsValid` |
| does the signature cover what I display | `CoversEntireDocument` **and** an empty `ValidationErrors` | `IsFullyTrusted` alone |

**`IsValid` is integrity plus structure, not trust.** Putting a green tick next to it is the most
common way to ship a PDF-signature bug. `CoversEntireDocument` being false is normal in a
counter-signed document — every signature but the last stops short of EOF — which is also what a
shadow attack looks like, so the verifier asks what the later revisions **changed**: for every
signature that stops short, the revisions after it are judged by DocMDP's rules at level 3
(ISO 32000-2 Table 257), where form filling, annotating, further signing, timestamps and `/DSS`
evidence are permitted and a rewritten page or a restructured catalog is not. A disallowed change
lands in that signature's `ValidationErrors` (prefixed `Changed after signing:`) and
`MdpViolations`, and `IsValid` goes false — whoever signed the later revision, so a self-signed
decoy that reaches EOF cannot launder it. A certification signature is judged by its own `/P`, and a
FieldMDP lock by the fields it named.

**What a chain is held to beyond signatures and dates.** An issuer is sought among the
certificates the signature carries and then among `TrustAnchors`, so a CMS that leaves its root out
still anchors. A signer certificate whose extended key usage names purposes, none of them one a
document is signed under (every purpose, e-mail protection, code signing, document signing, Adobe's
and Microsoft's document purposes), is `Invalid` — a certificate issued for a web server does not
sign a document under the same root; `RequireSignerKeyPurpose = false` turns that off for a private
PKI. A certificate marking critical an extension the walk does not process is refused (RFC 5280
§4.2), and a CA's name constraints are applied to every certificate below it.

**A timestamp moves the assessment time only while its authority can be relied on.** The TSA
certificate must not be revoked (the document's `/DSS`, and online when asked), and its chain must
be valid at the current time as well as at the token's own — or a later document timestamp from an
authority that can be relied on must cover the token, which is the renewal PAdES-LTA is made of. A
token's time is the TSA's own claim, so an authority judged only at that time vouches for itself:
the key of a TSA certificate that has since expired signs a token with any time in its old validity
period. `RequireCurrentTimestampAuthority = false` accepts such a token for an archive whose
timestamps were never renewed.

**Revocation is judged from the document's own `/DSS` evidence by default**, which keeps verification
deterministic and offline. `SignatureVerificationOptions.OnlineRevocation` opts into fetching an OCSP
response or a CRL for a certificate the document says nothing about: the fetch is the LTV embedder's
(the private-address guard, redirects re-checked per hop, a capped body, the revocation budget,
host-scoped credentials only), the judgement is the verifier's (the same scope and subject rules as
embedded data), the answer is about *now* and says so in a warning, nothing fetched is written back,
and a failed fetch is a warning, never a verdict.

## Signing with a key this process does not hold

`IExternalSigner` is called *inside* `Sign`, so it cannot reach a signer behind a request boundary.
For a remote signing service, a smart card in another process or a CSC/eIDAS flow, use the
two-phase API: what crosses the wire is **data**, not a callback.

```csharp
PreparedSignature prepared = PdfDeferredSigner.Prepare(input, staged, chainDer, parameters);
// … send prepared.SignedBytes to the signer, get the raw signature back …
PdfDeferredSigner.Complete(staged, prepared, signatureValue, output);
```

`Complete` overwrites the reserved hex digits and nothing else, so the byte ranges the signature
covers are exactly the ones `Prepare` measured.

## LTV

`PdfLtvRefresher` appends `/DSS` evidence to an already-signed document, and
`PdfSigningOperations.GetDssInventory` reports what a document carries and whether it is still
current. Check `DssInventory.VriEvidence` as well as `IsCurrent`: evidence scoped to one signature
under a `/VRI` key does not count toward the document-wide judgement, so a perfectly covered
document can report `IsCurrent == false`.

## Network policy

`SigningNetworkOptions` carries the credentials, proxy and timeout for every request signing makes:
the timestamp request and each OCSP and CRL fetch for `/DSS` evidence. It does not widen what a URL
may be — the scheme restriction, the private-address guard on document-derived URLs and the
no-redirect rule are applied first.

`Timeout` means one thing wherever it is read. Null keeps each transport's built-in (30 s for the
TSA, 15 s for revocation data — revocation data is best-effort LTV material, a TSA round trip is the
signature); zero or negative disables the timeout on both; a value beyond `int.MaxValue` milliseconds
is clamped rather than refused. A CRL fetch's redirect chain runs under one budget of that figure.

## Targets

`net10.0` and `netstandard2.0`. Cryptography is BouncyCastle.

## Licence and provenance

Apache License 2.0. Copyright (c) 2026 Tamir Mordo. Credit it as "Uses Tetitulz.Pdf by Tamir Mordo" (the package's NOTICE file).

See the repository for the full guide, the open-gap register and the third-party notices.
