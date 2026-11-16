# Tetitulz.Pdf.Encryption

Decryption and encryption for Tetitulz.Pdf: the standard security handler (RC4 and AES, revisions 2
through 6), `/Perms` validation, and the two supported ways to edit a document without losing its
encryption.

## Referencing this package decrypts nothing

`PdfEncryptionOperations.Register()` is the only writer of the ambient decryptor holder, and an
application must call it **once at startup**:

```csharp
PdfEncryptionOperations.Register();     // do this before opening anything encrypted
```

Without it the failure is silent rather than loud: the catalog never loads, `GetPageCount` answers
`0`, and a password prompt built on that rejects the correct password forever. Read-only Base
operations that take a `password` parameter go through the registered holder, which is why they need
no reference to this assembly themselves.

## Reading an encrypted document

```csharp
PdfEncryptionOperations.Register();

using var input = File.OpenRead("locked.pdf");
string text = PdfOperations.ExtractText(input, password: "s3cret");
```

## Writing one

Whole-file rewrites (sanitize, optimize, linearize, redact, template-fill) fail closed on an
encrypted document, because a decrypting rewrite would emit clear text where a reader expects
per-object ciphertext. Two routes:

| Route | Preserves | Cost |
|---|---|---|
| `PdfEncryptedIncrementalUpdate` (via each writer's `Plan`) | the signature **and** the owner password | one appended revision per step |
| `PdfEncryptedRoundTrip.Run` | neither | rewrites every byte |

```csharp
using var input  = File.OpenRead("locked.pdf");
using var output = File.Create("locked-edited.pdf");

RoundTripResult result = PdfEncryptedRoundTrip.Run(
    input, output,
    new EncryptedRoundTripOptions { OpenPassword = "s3cret" },
    (decrypted, plain) => PdfSanitizer.Sanitize(decrypted, plain));
```

The delegate is handed a decrypted stream and writes plain output; `Run` re-encrypts it. The options
decide *how*: `Policy` chooses between reusing the document's own parameters and
`ExplicitParameters`, and `AllowSignatureInvalidation` has to be set deliberately, because rewriting
every byte invalidates any signature over them.

## Encrypting

```csharp
PdfEncryptor.Encrypt(input, output, new EncryptionParameters
{
    UserPassword  = "open-me",
    OwnerPassword = "owner-only",
    Algorithm     = EncryptionAlgorithm.AES256,
    Permissions   = PdfPermissions.Print | PdfPermissions.Copy,
});
```

## Certificates instead of passwords — `/Adobe.PubSec`

A document can be encrypted to one or more X.509 recipients, each of whom opens it with their own
private key and no shared secret.

```csharp
PdfPublicKeyEncryptor.Encrypt(input, output, new[] { aliceCertDer, bobCertDer },
                              PdfPermissions.All, EncryptionAlgorithm.AES256);

PdfPublicKeyDecryptor.Decrypt(input, output, pkcs12Stream, "pfx-password");
```

`PdfPublicKeyDecryptor.IsPublicKeyEncrypted` says which of the two decryptors a given file needs.

### A key that never leaves the device

The PKCS#12 route needs the private key as bytes, which is the one thing a smart card, an HSM or a
non-exportable CNG key will not give you. `IExternalDecryptor` is the seam for those — the
decrypt-side mirror of `IExternalSigner`:

```csharp
sealed class CardDecryptor : IExternalDecryptor
{
    private readonly X509Certificate2 _cert;   // from the OS store; its key stays on the card

    public byte[] GetRecipientCertificate() => _cert.RawData;

    public byte[] UnwrapEnvelope(byte[] cms)
    {
        var enveloped = new EnvelopedCms();
        enveloped.Decode(cms);
        enveloped.Decrypt(new X509Certificate2Collection(_cert));
        return enveloped.ContentInfo.Content;
    }
}

PdfPublicKeyDecryptor.Decrypt(input, output, new CardDecryptor(cert));
```

The seam is the CMS envelope rather than the RSA primitive, so an implementation never reimplements
content decryption — and only the seed crosses it. The file key is derived here, from every
`/Recipients` entry in file order.

**`UnwrapEnvelope` opens only the envelope addressed to the device, once each time the key is
recovered.** `/Recipients` is attacker-controlled, so the certificate match happens first, against
public data; a card is never asked to open another envelope. Handing it the others would prompt for a
PIN each time, and enough refusals lock the card. An operation that recovers the key twice — an
appended encrypted revision, then its PDF MAC seal — asks the device twice, for the same envelope.

## PDF MAC (ISO/TS 32004)

A document protected with a MAC token carries proof that its bytes were not changed by someone who
did not know the password. `PdfMacVerifier.Verify(input, password)` — or the PKCS#12 and
`IExternalDecryptor` overloads for a public-key document — returns a `PdfMacVerification`:
`IsProtected`, `IsValid`, `Location` (the `/MACLocation` value, `Standalone` or `AttachedToSig`)
and `Error` — three states, since a document with no token is unprotected rather than invalid. A
token whose `/ByteRange` stops short of the end of the file is invalid, and so is one whose range
leaves out more than the token itself — its hex form alone for a standalone token, the signature's
`/Contents` alone for one attached to a signature — since bytes no digest covers could stand there.

**A token that was removed is invalid, not "unprotected".** The trailer and the encryption
dictionary are plain text, so deleting `/AuthCode` costs a tamperer nothing. A document that
requires a token and has none reports `IsProtected` and not `IsValid`:

- Under AES-256 with a password the requirement is permission bit 13, and `/Perms` authenticates
  the `/P` that holds it. A `/P` that `/Perms` does not confirm — edited, or `/Perms` removed with
  it — is reported as well, since a flipped bit 13 is what removing a token unseen needs.
- Under AES-256 with recipients the bit is read from the permissions inside the envelope that
  opened, which is hashed into the file key; what the dictionary's `/P` and `/KDFSalt` say is not
  asked.
- Under RC4 and AES-128 nothing authenticates the bit and old producers cleared it freely, so a
  token is held required only where the encryption dictionary also carries a `/KDFSalt`. Someone
  who removes the token **and** the salt from such a document is not detected.

**So a token is written only with AES-256 or AES-256-GCM.** ISO/TS 32004 allows one only where
`/V` is at least 5 (Table 5), and both encryptors refuse `EnablePdfMac` below it with an
`ArgumentException` that names the algorithm — AES-128, the default algorithm, included. A document
another producer wrote with a token below `/V 5` is still read: its token verifies, an appended
revision reseals it, and `ReEncryptionPolicy.KeepOriginal` keeps it; a re-encryption that would
have to write such a token again is refused with the reason, and one that would drop it is not
offered.

The encryptors
write the token, the incremental routes above carry it across an appended revision, and signing a
protected document attaches the token to the signature as §6.5.2 requires; the comparison is
fixed-time.

## Signing an encrypted document

`PdfSigner.Sign` refuses encrypted input, and that is correct: it would write `/Reason`, `/M` and
the field name in the clear. `PdfEncryptionOperations.Sign` is the route — the graph from a decrypted
copy, the revision appended encrypted — and `PdfEncryptionOperations.VerifyMdp(stream, password)`
verifies one. Verification takes the graph from the decrypted document and every byte, offset and
length from the original file; never the decrypted copy as both.

## The `/P` bits are a claim until they are validated

Ask `PdfDecryptor.LastPermsValidation` before honouring the permission bits. The `/P` integer alone
is unauthenticated; the `/Perms` entry is what ties it to the file key, and a document can claim
anything.

## Targets

`net10.0` and `netstandard2.0`.

## Licence and provenance

Apache License 2.0. Copyright (c) 2026 Tamir Mordo. Credit it as "Uses Tetitulz.Pdf by Tamir Mordo" (the package's NOTICE file).

See the repository for the full guide, the open-gap register and the third-party notices.
