# Security Policy

## The trust boundary

**This library parses untrusted PDFs and never acts on them.** It does not navigate, submit, launch
or open anything: `PdfActionRunner` raises an event and the host's handler decides. Document
JavaScript runs only when a host registers an `IJavaScriptPlugin` (`Tetitulz.Pdf.Plugins.JavaScript`
ships a Jint one), under memory, time and recursion limits; that is a resource sandbox, not a
permission model. A web page's scripts in HTML conversion likewise run only when the host registers
`JintHtmlScriptPlugin` by name; plugin discovery turns neither engine on.

Everything below follows from that. The two things most likely to bite an integrator are on this
page: what `IsValid` does **not** mean, and which decisions the library hands back rather than makes.

## Reporting a vulnerability

Report privately. Do not open a public issue for a suspected vulnerability.

Report within 14 days, with the fix if you have one, and publish neither before a fix is released or 90 days have
passed. **For `Plugins.PostScript`, `Plugins.Maintenance` and `Plugins.PageEdits` this is a licence condition**
([`LICENSE-ATTRIBUTION.txt`](LICENSE-ATTRIBUTION.txt) §3(d)), including for a derived work's copy of them; for the
Apache-licensed rest of the library it is a request.

- Use **GitHub private vulnerability reporting** on
  [github.com/tetitu/tetitulz.pdf](https://github.com/tetitu/tetitulz.pdf) (Security → Report a
  vulnerability). The maintainer is Tamir Mordo, as the package metadata and `LICENSE` name him.

- Include the affected version, a minimal PDF or code that reproduces it, and what you expected.
- Expect an acknowledgement within **5 working days** and an assessment within **15**.
- Please allow **90 days** before public disclosure, or less by agreement once a fix ships.

**Supported versions:** the latest tagged release only. A minor version may break the API.

## Asking the right question

The commonest way to ship a PDF-signature bug is a green tick beside `IsValid`.

| You want to know | Ask | Do **not** ask |
|---|---|---|
| do the bytes match the CMS | `IsValid` | |
| is this signer trusted | `IsFullyTrusted`, **with `SignatureVerificationOptions.TrustAnchors` set** | `IsValid` |
| does the signature cover what I am displaying | `CoversEntireDocument`, **and** an empty `ValidationErrors` | `IsFullyTrusted` alone |
| may I honour the `/P` permission bits | `PdfDecryptor.LastPermsValidation == "OK"` | the `/P` integer |
| did anything get silently skipped | the relevant `Last*` diagnostic, or the feature's diagnostics sink | a non-null return |

**Without `TrustAnchors` nothing can ever be `Trusted`.** A verify call with no anchors is an
integrity check, and every chain status stays `Unknown`, `SelfSigned` or `UntrustedRoot`.

**`CoversEntireDocument` is a separate question from validity, on purpose.** In a counter-signed
document every signature but the last stops short of the end, which is also what a shadow attack
looks like. The verifier records a `Warning` on any signature that does not reach the end; deciding
what the later revisions changed is the application's call.

## In scope

Report these:

- **Signature forgery or spoofing**: a document where a signature reports `IsValid` or
  `IsFullyTrusted` while the bytes a user would see were not covered by it.
- **Encryption bypass**: decrypting without the password, or defeating `/Perms` authentication.
- **XXE**: external entity or DTD resolution from any XML this library parses (XMP, XFDF, XFA
  datasets and templates, SVG, embedded e-invoices, PDF/A and PDF/UA metadata).
- **SSRF**: any document- or certificate-derived URL reaching a private, loopback, link-local or
  cloud-metadata address, HTML subresources fetched through `GuardedRemoteResourceLoader` on
  `net10.0` and redirects included.
- **Sandbox escape** from the JavaScript engines: a script reaching a CLR type, the filesystem or the
  network.
- **Path traversal** from any document-derived name, or a local file read outside the caller's
  `BasePath` by the HTML engine.
- **Resource exhaustion** that no documented cap bounds: an amplification that runs unbounded, a cap
  that does not fire, a regular expression or a parser that is quadratic in a field the file controls.
- **Any parse that faults** with an exception outside the entry point's documented set
  (`NullReferenceException`, `IndexOutOfRangeException`, `InvalidCastException`,
  `OverflowException`, `OutOfMemoryException` on bounded input, a stack overflow).

## Out of scope

Each is a deliberate decision, listed so an application can decide what it must do itself.

- **A host that acts on `UriRequested`, `LaunchRequested` or `FormActionRequested` without
  validating the target.** The library surfaces the action and does not perform it. A
  `javascript:`, `file:`, `data:` or `smb:` target is the handler's to refuse. (The WinForms viewer
  opens `http`, `https` and `mailto` links only.)
- **A host that displays `IsValid` as a trust indicator.** See the table above.
- **Action chains.** `PdfActionRunner.RunAction(PdfAction)` runs one parsed action and never its
  `/Next` chain. The four trigger entry points (`RunCatalogOpenAction`, `RunCatalogAa`,
  `RunPageAction`, `RunFieldAction`) follow the chain under four bounds: `PdfLimits.MaxActionChainDepth`
  (64), an identity-keyed visited set, `MaxActionsPerTrigger` (256) and
  `MaxJavaScriptActionsPerTrigger` (16, each JavaScript action getting its own 5 s / 32 MB engine).
  Every refusal is on `Warnings`; what ran is on `PdfActionResult.ChainedEvents`.
  `PdfActionDispatcher.ExtractActions` lists every action a document holds, stopping at 65,536 and
  saying so on `LastChainDiagnostic`. `PdfSanitizer` cleans every dictionary reachable from the
  catalog — the one a reader takes, however the trailer names it — and `/Info` in one iterative walk (one visited set, so a cyclic or hundred-thousand-deep
  graph neither hangs nor is clipped), severs a dangerous `/Next` link and neutralizes a dangerous
  inline action wherever it is reached, and then **writes only what the cleaned graph still reaches**
  — a stripped script stream, an embedded file whose name tree or `/AF` entry is gone, and the
  object-stream / cross-reference-stream containers a compressed file is built from all leave the
  output rather than riding along as orphans. Beyond JavaScript, launch, form-submit and embedded
  files, it removes (each on by default, each its own `SanitizeOptions` switch) remote `/GoToR`,
  `/GoToE`, `/ImportData` and external `/Thread` actions, non-web `/URI` actions (`file:`/UNC), and
  `/3D` annotations; an unreadable object is reported on `LastObjectWarning`, never dropped in
  silence.
- **AcroForm JavaScript field protections are recorded, not applied.** `readOnly`, `hidden` and
  `setFocus` requests are reported through `PdfJsEngine.LastUnsupportedRequest`, so a document's own
  form protections are not a security control here.
- **JavaScript budgets are per call unless a document budget is open.** The `ExecuteDocumentScripts`
  overloads open one (`PluginJavaScriptRunner.DefaultDocumentBudget`, 30 s); a host calling script by
  script gets N times the per-call budget unless it opens `BeginDocumentBudget` itself.
- **The TSA URL and EthereumSign's RPC URL are caller-configured**, so either may legitimately reach
  a private address. The guard applies to URLs derived from documents and certificates (OCSP, CRL,
  HTML subresources), not to one the caller supplied.
- **DNS rebinding through a proxy.** A fetch of an untrusted URL connects to the very address the
  guard vetted (`Core.Net.GuardedHttpFetch`): through the handler's connect callback on .NET 5 and
  later, and on .NET Framework through the library's own transport. **Through a proxy the check is
  all there is**, since the proxy resolves the host — a configured proxy, or a system proxy that
  applies to the URL — and so it is on .NET Framework for a request carrying credentials. There,
  treat outbound fetching as a network-egress decision of your own, and for HTML prefer an
  `IRemoteResourceLoader` with an allow-list of hosts (GAPS.md §17a). The browser engine connects
  through its own loopback proxy, which dials the address it vetted on every runtime.
- **HtmlToPdf remote subresources are fetched only when you set a loader.** With
  `HtmlToPdfOptions.RemoteResourceLoader` left `null` the managed engine performs no network I/O. A
  loader you implement is outside the library's guard. Local files are read only inside `BasePath`,
  and a fetched stylesheet names none, by `url()` or by `@import`.
- **The browser engine runs Chromium in its sandbox** (`HtmlToPdfOptions.ChromiumSandbox`, on by
  default). Its proxy and request route bound what an intact renderer does; a host that turns the
  sandbox off gives a renderer a page compromised its own file and network access.
- **Resource exhaustion already bounded by a documented cap.** If the cap fires and the refusal is
  reported, that is the design (GAPS.md §8). If it does not fire, report it.

  > **The caps are yours to tighten.** Every value in `Core.Constants.PdfLimits` and
  > `Plugins.PostScript.PsLimits` is a settable property with a `Default…` constant and
  > `ResetToDefaults()`. They are process-global: set them at startup, before any parsing. A service
  > taking untrusted uploads should lower the ones that matter to it rather than accept defaults sized
  > for a legitimate 400-page document. Values are not validated on assignment: the guards fail
  > closed, so a ceiling of zero refuses rather than admits. **Raising one is a security decision**;
  > each cap has an adversarial regression test. Where a ceiling differs per call, the entry point
  > takes it as an argument (`PdfXValidator.Validate`, `PdfUaChecker.Validate`,
  > `PdfRichMediaExtractor.ListRichMedia`, `Pdf3DExtractor.List3DAnnotations`, `Woff2Decoder.Decode`).
- **An appended revision never removes what it supersedes.** "Replaced" means hidden, not deleted;
  `PdfRevisions` hands the earlier bytes back by design. Only the single-revision rewrite in
  `PdfRedactionEngine.Redact` destroys content, and destroying content invalidates any signature over
  it.
- **Redaction removes the data from the pages you name, not from the file.** A Form XObject drawn by
  several pages is replaced on the named page by a redacted clone; the original stays for the other
  pages. **Check `PdfRedactionEngine.LastSharedFormWarning` after every redaction**, which names the
  forms left intact, and redact every page that draws them (GAPS.md §6).
- **Rendering is not a sandbox for the content.** A PDF that renders misleading pixels is a
  document-authoring problem.

## Threat model

**Assets:** signature verdicts, decryption keys and passwords, the host process's availability, and
the host's network position.

**Adversary:** anyone who can hand you a PDF, an HTML page, a DOCX or a font. Assume every byte,
every declared length, every count and every embedded certificate is chosen by them.

**Boundaries:**

1. *parse* against *act*: the library does the first and never the second;
2. *library* against *host handlers*: action and JavaScript decisions cross back to the host;
3. *managed* against *native*: the library's own code makes no native call (guarded by
   `NoLibraryPInvokeTests`). Native code runs in-process only inside third-party packages: SkiaSharp,
   which also decodes the file's JPEG, PNG and WebP images, and Tesseract in the optional OCR plugin.
   Those are the only places a memory-safety bug could be more than an exception. The `libopenjp2`
   P/Invoke for JPEG 2000 was removed on 2026-09-23 after it was found to overrun the heap; the
   codec is managed now.

**Non-goals:** DRM, and defeating a malicious author who controls a document you chose to trust.
Revocation is judged from the embedded `/DSS` by default; the online fallback
(`SignatureVerificationOptions.OnlineRevocation`) is opt-in, borrows the LTV embedder's guarded
fetch, and a failed fetch is a warning, never a verdict.

## How this is enforced

The claims here are pinned by tests rather than by prose.

- Hostile-input suites across every decoder, under a build-failing guard
  (`NoUnfalsifiableAssertionsTests`) that rejects an assertion that cannot fail.
- Deterministic in-suite fuzzers over the parser (`ParserFuzzTests`) and the stream filters
  (`FilterFuzzTests`), asserting the exception type and the declared output ceiling.
- Coverage-guided fuzzing outside the suite: `tools/fuzz` (SharpFuzz under libFuzzer) over twenty
  public doors: the parser, the renderer, the content parser, the tokenizer, the JPEG 2000, JBIG2,
  CCITT and LZW decoders, the TrueType parser, the Type 1 outlines, the PostScript converter, the sfnt
  directory, the dynamic-XFA layout reader (the template packet through the Builder exporter), the
  redaction engine, the sanitizer, the signature verifier with the DocMDP analysis, and the page
  elements' list, removal and replacement (the harness's `--list` names them). Each target swallows only the declared
  refusals, so every artifact is a finding. A target is run before a release and after its reader
  changes, and a slice of each every night (`.claude/nightly.ps1`), the corpus persisting so each run
  continues from the last one's coverage.
- The structure fuzzer (`tools/corpus-scorecard --generate`, `--generate-html`): seeded corpora of
  hostile object graphs and hostile markup, scored one child process per file; and the public
  corpora `tools/corpus-fetch` fetches, scored the same way. Open findings are in GAPS.md.
- Every regular expression that reads a field a file controls is unambiguous, capped on the field's
  length before it runs, and has a match timeout behind both (`XfaMeasure`, `CssColor`, the HTML
  mapper's CSS length, the auto-linker's bounded URL pattern).
- Signature attack coverage in `SignatureShadowAttackTests`, permission forgery in
  `PermsForgeryTests`, render amplification in `RenderFormXObjectBudgetTests`, each with a floor and a
  control, so a passing attack test cannot mean "there was never a defence".
- A dependency-vulnerability gate in CI that fails the build on a known-vulnerable package.

See also `README.md` (Security), `GAPS.md` §8 for the accepted limits, and `NOTICE.md` for the Jint
sandbox parameters.
