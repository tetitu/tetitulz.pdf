# Tetitulz.Pdf.Plugins.CertStoreSign

Signs a PDF with a certificate from the **Windows certificate store** — including one whose private
key lives on a smart card, a USB token or an HSM, where the key cannot be exported and the signing
operation has to happen inside the provider.

It plugs into `Tetitulz.Pdf.Signing` through `IExternalSigner`: the signer computes the signature,
the signing code never sees a private key.

## Signing with a store certificate

```csharp
X509Certificate2 cert = CertStoreLookup.FindByThumbprint("A1B2…", StoreLocation.CurrentUser);

using var signer = new CertStoreExternalSigner(cert);
using var input  = File.OpenRead("contract.pdf");
using var output = File.Create("contract-signed.pdf");

new PdfSigner().Sign(input, output, new SignatureParameters { ExternalSigner = signer });
```

`CertStoreLookup.FindUsableSigningCertificates(location, asOf)` lists the certificates that are
actually usable for signing at a given instant — valid dates, a private key present, and a key usage
and extended key usage that permit a document signature (`PermitsDocumentSigning`, the verifier's
own rule: a certificate issued for a web server signs nothing) — so a picker does not offer the user
a certificate that will fail at the last step.

## Token prompts

`CertStoreExternalSigner.Silent` controls whether the provider may show its own PIN or consent
dialog. Leave it `false` for an interactive desktop application; set it `true` for a service, and
expect the signature to fail rather than block if the provider needs a human.

`IncludeOption` decides how much of the chain travels in the signature.

## Handing the PIN over

A host that asks for the PIN in its own window — or keeps it for a while — hands it to the key's
provider through `KeyPin`, and the provider's dialog does not appear:

```csharp
X509Certificate2 cert = CertStoreLookup.FindByThumbprint("A1B2…", StoreLocation.CurrentUser);
string pin = "…";   // asked for in the host's own window

using var signer = new CertStoreExternalSigner(cert) { Silent = true, KeyPin = pin };
```

- **Ask the key first.** A key that takes no PIN refuses one handed to it, so sign once with
  `Silent = true` and no `KeyPin`; a `CryptographicException` there is the key asking for its PIN.
- **A wrong PIN is the provider's `CryptographicException`**, raised before anything is signed where
  the provider checks the PIN as it is handed over, and it counts against the device's tries — a
  host that keeps a PIN forgets it at the first refusal.
- **Windows, and the `net10.0` build.** The `netstandard2.0` build has no `KeyPin`: the CMS signer
  there cannot be handed the key the PIN was given to.

## This is the signing side

Opening a *public-key-encrypted* document with a key that never leaves the device is the encryption
package's job, not this plugin's: `Tetitulz.Pdf.Encryption.IExternalDecryptor` is the decrypt-side
mirror of `IExternalSigner`, and `PdfPublicKeyDecryptor.Decrypt(input, output, decryptor)` takes
one. The seam is the CMS envelope, so an implementation over a store certificate is a few lines of
`EnvelopedCms` — the Encryption README shows it. The certificate match happens before the device is
asked, so a card is prompted once, for the one envelope addressed to it.

## Targets

`net10.0` and `netstandard2.0`. Uses `System.Security.Cryptography.Pkcs`; the certificate store is a
Windows facility, so this package is only useful on Windows.

## Licence and provenance

Apache License 2.0. Copyright (c) 2026 Tamir Mordo. Credit it as "Uses Tetitulz.Pdf by Tamir Mordo" (the package's NOTICE file).

See the repository for the full guide, the open-gap register and the third-party notices.
