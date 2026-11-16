# tetitulz-sign

Signs a PDF with a PFX certificate, or checks the signatures a PDF carries, from the command line.
One source, two builds: `tetitulz-sign` on .NET Framework 4.8 (the library's netstandard2.0 build
serving a .NET Framework host) and `tetitulz-sign10` on .NET 10.

```powershell
dotnet build apps/Tetitulz.Pdf.SignCli -c Release
$env:PFX_PASSWORD = '…'
tetitulz-sign10 --input in.pdf --output signed.pdf --pfx me.pfx --password-env PFX_PASSWORD --reason "Approved" --tsa http://timestamp.digicert.com
tetitulz-sign10 verify --input signed.pdf --trust roots\
```

`--help` lists every option. In short:

- **The password** is taken one way: `--password-env NAME` or `--password-stdin` (a `--password` on the
  command line is visible to other processes and kept in shell history).
- `--tsa URL` adds an RFC 3161 timestamp; `--ltv` embeds revocation data as well; `--pades B-B|B-T|B-LT`
  applies a PAdES baseline, and a combination that contradicts it is refused rather than narrowed.
- `--image` (with `--page`, `--rect x,y,w,h` in points) makes the signature visible: a PNG keeps its
  transparency. `--sanitize` strips scripts, actions and embedded files first.
- **A signature a verifier would refuse** (an expired certificate, a weak digest, a certificate for
  another purpose) is still written, with a `warning:` line on standard error.
- The output is written beside the target and moved over it only when signing succeeded.
- **`verify`** prints each signature's verdict, signer, claimed time, timestamp, what it covers, its
  chain and revocation status, errors and warnings. `--trust` names root certificates (a `.cer`,
  `.crt`, `.pem` or `.der` file, or a folder of them); without it the chain's trust is not checked.
  `--pdf-password-env` opens an encrypted document.

| Exit code | |
|---|---|
| 0 | Signed; or every signature verifies (and is trusted, with `--trust`) |
| 2 | Bad arguments |
| 3 | A file could not be read or written |
| 4 | Signing failed |
| 5 | `--sanitize` refused the input |
| 6 | A signature does not verify, or is not trusted |
| 7 | The document is not signed |

`test\` holds a sample document and a test certificate (password `test`, not for production).
Tests: `tests/Tetitulz.Pdf.SignCli.Tests`.
