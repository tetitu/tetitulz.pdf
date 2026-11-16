# Tetitulz.Pdf.WasmSign — sign a PDF in the browser

A Blazor WebAssembly page that signs a PDF entirely inside the browser with `Tetitulz.Pdf.Core` +
`Tetitulz.Pdf.Signing`, optionally with an RFC 3161 timestamp. The document and the private key stay
in the page; only the hash of the signature value leaves it, to the host's `/tsa` relay.

## Run

```bash
dotnet run --project apps/Tetitulz.Pdf.WasmSign.Host
```

Open the URL it prints, pick a PDF and, optionally, a `.pfx`/`.p12` and its password. With no key a
throwaway self-signed demo key is used — the signature is intact but trusted by no one.

The relay's upstream is `Tsa:Url` in the host's configuration (default `http://timestamp.digicert.com`).

## Why it is built this way

- **Core + Signing only.** Both are fully managed. `Base` is left out: SkiaSharp has no browser build here.
- **Always the deferred signer** (`PdfDeferredSigner.Prepare` → sign the attributes →
  `CmsDeferredSignatureBuilder.AssembleDetachedSignature` → `Complete`). `PdfSigningOperations.Sign` with
  `TimestampAuthorityUrl` fetches its token synchronously, which browser-wasm refuses with
  `PlatformNotSupportedException`; the deferred route lets the page fetch the token with `await`.
  LTV (`EmbedLtv`, OCSP/CRL fetching) has the same synchronous network path and is not attempted here.
- **Keys through BouncyCastle.** `.pfx` files are read with `Pkcs12StoreBuilder`; browser-wasm has no
  `X509Certificate2` private-key support.
- **A relay, not a direct call.** No public TSA answers a CORS preflight (DigiCert, Sectigo, SSL.com,
  Certum, FreeTSA and Apple, checked 2026-10-14), and most are plain `http`, which an `https` page may not
  call. The host forwards only `application/timestamp-query` bodies of at most 8 KB, and only to the
  configured URL — a caller cannot choose where it connects.
- **The token is checked before it is embedded**: `TimeStampResponse.Validate` against the request
  (status, imprint = SHA-256 of the signature value per RFC 3161 Appendix A, nonce).

## Self-test

`/?selftest` signs a built-in one-page PDF with a demo key and a timestamp, with no clicks, and writes
the result into `<pre id="out">`. Headless:

```powershell
dotnet publish apps/Tetitulz.Pdf.WasmSign.Host -c Release -o out
dotnet out/Tetitulz.Pdf.WasmSign.Host.dll --urls http://localhost:8767   # in out/
msedge --headless=new --virtual-time-budget=120000 --dump-dom "http://localhost:8767/?selftest"
```

Expect `SIGNED`, `TIMESTAMP … DigiCert …`, `VERIFY … integrity=True wholeDocument=True`, `SELFTEST-DONE`.
