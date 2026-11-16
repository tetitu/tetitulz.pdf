# docs/

The documentation set is deliberately small. Each document has one job, and none repeats what another
owns. Specification extracts live in the git-ignored `docs/iso/`, obtained by whoever needs them (the
rules for citing them are in `CONTRIBUTING.md`).

| Document | Owns |
|---|---|
| [`../README.md`](../README.md) | What the library is, how to build and call it, the platforms, the hosting checklist. |
| [`../CLAUDE.md`](../CLAUDE.md) | The rulebook digest, loaded by agents on every turn: architecture invariants, shared decision points, coding conventions, diagnostics, testing rules. One line per rule. |
| [`../CONTRIBUTING.md`](../CONTRIBUTING.md) | The rulebook's long form: the same headings, each rule with its reasoning and measurements, and the key-file map. A rule changes in both files in one commit. |
| [`../SECURITY.md`](../SECURITY.md) | Trust boundary, threat model, what is in and out of scope, how to report. |
| [`../NOTICE.md`](../NOTICE.md) | Third-party components and their licences. |

## Per-project documentation

Every shipping package has a README beside its project, with its own contract and limits.

**The core ring** (`src/tetitulz.pdf/`):
[`Core`](../src/tetitulz.pdf/Tetitulz.Pdf.Core/README.md) ·
[`Base`](../src/tetitulz.pdf/Tetitulz.Pdf.Base/README.md) ·
[`Encryption`](../src/tetitulz.pdf/Tetitulz.Pdf.Encryption/README.md) ·
[`Signing`](../src/tetitulz.pdf/Tetitulz.Pdf.Signing/README.md) ·
[`WinForms.PdfViewer`](../src/tetitulz.pdf/Tetitulz.Pdf.WinForms.PdfViewer/README.md)

**Features** (`src/features/`):
[`Builder`](../src/features/Tetitulz.Pdf.Builder/README.md) (the code-first document model every converter targets; the XFA importers) ·
[`HtmlToPdf`](../src/features/Tetitulz.Pdf.HtmlToPdf/README.md) (the managed engine against the browser one, every option) ·
[`DocxToPdf`](../src/features/Tetitulz.Pdf.DocxToPdf/README.md) (supported features and limits) ·
[`Accessibility`](../src/features/Tetitulz.Pdf.Accessibility/README.md) (PDF/UA: every operation, the full check-code table, the encrypted-document matrix)

**Plugins** (`src/plugins/`; writing your own: [`src/plugins/README.md`](../src/plugins/README.md)):
[`CertStoreSign`](../src/plugins/Tetitulz.Pdf.Plugins.CertStoreSign/README.md) ·
[`CMap`](../src/plugins/Tetitulz.Pdf.Plugins.CMap/README.md) ·
[`Conformance`](../src/plugins/Tetitulz.Pdf.Plugins.Conformance/README.md) ·
[`Docx`](../src/plugins/Tetitulz.Pdf.Plugins.Docx/README.md) ·
[`EthereumSign`](../src/plugins/Tetitulz.Pdf.Plugins.EthereumSign/README.md) ·
[`Fonts`](../src/plugins/Tetitulz.Pdf.Plugins.Fonts/README.md) ·
[`Html`](../src/plugins/Tetitulz.Pdf.Plugins.Html/README.md) ·
[`HtmlBrowser`](../src/plugins/Tetitulz.Pdf.Plugins.HtmlBrowser/README.md) ·
[`HtmlScript`](../src/plugins/Tetitulz.Pdf.Plugins.HtmlScript/README.md) (and its [`VENDORED.md`](../src/plugins/Tetitulz.Pdf.Plugins.HtmlScript/VENDORED.md)) ·
[`JavaScript`](../src/plugins/Tetitulz.Pdf.Plugins.JavaScript/README.md) ·
[`Maintenance`](../src/plugins/Tetitulz.Pdf.Plugins.Maintenance/README.md) ·
[`Ocr`](../src/plugins/Tetitulz.Pdf.Plugins.Ocr/README.md) ·
[`PageEdits`](../src/plugins/Tetitulz.Pdf.Plugins.PageEdits/README.md) ·
[`PostScript`](../src/plugins/Tetitulz.Pdf.Plugins.PostScript/README.md) (what the interpreter implements, what it reports, every bound) ·
[`Renderers`](../src/plugins/Tetitulz.Pdf.Plugins.Renderers/README.md) ·
[`SignatureVerify`](../src/plugins/Tetitulz.Pdf.Plugins.SignatureVerify/README.md) ·
[`Svg`](../src/plugins/Tetitulz.Pdf.Plugins.Svg/README.md) ·
[`Templating`](../src/plugins/Tetitulz.Pdf.Plugins.Templating/README.md) (template filling, RTL included) ·
[`Text`](../src/plugins/Tetitulz.Pdf.Plugins.Text/README.md)

**The apps** (`apps/`, never packed):
[`WpfViewer`](../apps/Tetitulz.Pdf.WpfViewer/README.md) (the desktop editor) ·
[`StampSign`](../apps/Tetitulz.Pdf.StampSign/README.md) (stamps and a smart-card signature) ·
[`Splitter`](../apps/Tetitulz.Pdf.Splitter/README.md) ·
[`SignCli`](../apps/Tetitulz.Pdf.SignCli/README.md) (sign and verify from the command line) ·
[`BatchRenderer`](../apps/Tetitulz.Pdf.BatchRenderer/README.md) ·
[`CompareViewer`](../apps/Tetitulz.Pdf.CompareViewer/README.md) (beside Acrobat Reader) ·
[`VirtualPrinter`](../apps/Tetitulz.Pdf.VirtualPrinter/README.md) and its [`PrinterTray`](../apps/Tetitulz.Pdf.PrinterTray/README.md) ·
[`WasmSign`](../apps/Tetitulz.Pdf.WasmSign/README.md) (signing in the browser) ·
[`installers`](../apps/installers/README.md) (the MSIs)

`VerifyNetzerWebPdf` lives outside this repository, where it consumes the compiled `Tetitulz.Pdf.Core` package.

**Elsewhere:**
[`tests/README.md`](../tests/README.md) (how the suite is put together, the guards that police it, the skip attributes, the API baselines)

## Getting set up on a new machine

```bash
git clone <remote> tetitulz.pdf && cd tetitulz.pdf
dotnet build tetitulz.pdf.sln          # 0 errors, every target framework
dotnet test  tetitulz.pdf.sln          # CI is the authority on the count
```

Requirements: the **.NET 10 SDK** (`global.json` pins the feature band), and Windows for the full
solution, which builds `net10.0-windows` apps and the `net462` viewer. Everything portable builds and
tests on Linux through `tetitulz.pdf.portable.slnf`, the `linux-verify` CI job.

Optional, each reported as **Skipped** when absent: a PDF corpus through `TETITULZ_PDF_CORPUS` and
`TETITULZ_PDF_RAW`; the JPEG 2000 conformance data through `TETITULZ_JPX_CORPUS`. No outside program is
needed: the writer-output and decoder checks run on in-repo checkers and recorded independent answers.

## Tools

| Tool | What it does |
|---|---|
| `tools/fuzz` | Coverage-guided fuzzing of twenty public doors (SharpFuzz under libFuzzer). `run.ps1 -Target <t> -Seconds <n>`; `-Replay` runs one artifact in-process. |
| `tools/corpus-scorecard` | Opens, renders, extracts and round-trips every file of a corpus in a child process each, scoring crashes, hangs, memory and an independent structure check; `--generate` and `--generate-html` write the hostile corpora it also runs. |
| `tools/corpus-fetch` | Fetches the public PDF corpora with their terms recorded, and runs the scorecard over them with `-Scorecard`. |
| `tools/package-consumption` | Restores the packed `.nupkg`s into a .NET Framework and a .NET consumer and runs the checks only a consumer can see. |
| `tools/readme-samples` | Compiles every README's C# blocks against the libraries, every library namespace imported, and checks the API listings (`Ret Type.Member(params);`) by reflection. `check.ps1`; exit 0 is clean. |
| `tools/audit` | Two reusable harnesses: `liveness-sweep.py` (mutate, build, run the consuming project, read the verdict and the total) and `resolve-asymmetry.py` (keys the two renderers read with different resolution styles). |
| `tools/unicode-scripts`, `tools/unicode-bidi` | Generate the Unicode script and bidi-class tables from the Character Database; never hand-edit the generated files. |
