# Tetitulz.Pdf.Plugins.Maintenance

Document maintenance for Tetitulz.Pdf: **size optimisation**, image recompression, **linearisation**
(fast web view) and **sanitising**.

## Optimising

```csharp
using var input  = File.OpenRead("big.pdf");
using var output = File.Create("small.pdf");

OptimizationResult result = PdfOptimizer.Optimize(input, output, OptimizationOptions.Lossless);
Console.WriteLine($"{result.InputBytes} → {result.OutputBytes} ({result.BytesSaved} saved)");
```

`OptimizationOptions.Lossless` re-compresses streams, drops the images and forms nothing draws,
merges duplicate objects and image strips, and removes redundancy without touching image quality.
Image recompression is opt-in through `ImageOptimizationOptions` (`ImageFormat.Jpeg`, `Png`, `Gif`,
`WebP` — stored as `DCTDecode` — `Jpeg2000` through the in-house encoder, or `Original` to leave a
stream alone), because re-encoding an image is a quality decision only you can make. A bilevel image
can go to JBIG2 through the in-house encoder; a JPEG is re-encoded only where its colours survive
(never a CMYK or non-device-colour one); a JPEG 2000 re-encode can fold the image's soft mask into
its codestream. Every image the pass leaves alone says why in the result. Measured against another optimizer
on a public test corpus, with every output render-checked: `GAPS.md` §19h.

`RemoveAnnotations` is destructive and no preset sets it: it removes every annotation and the form
(`/AcroForm`), and scrubs what each annotation and field node said — value, appearance, words, author,
file, action, destination — where another route, such as the structure tree, still reaches it.

> On `netstandard2.0` the lossless path uses deflate **Optimal** rather than `SmallestSize`, which
> exists only on .NET 6+. Output is slightly larger; it is lossless either way.

## Linearising

```csharp
PdfLinearizer.Linearize(input, output);
```

Reorders the file so a viewer can render page 1 before the rest has arrived, and writes all eleven
of ISO 32000-1 Annex F's parts — including the two that make the reordering worth anything: the
first-page cross-reference table at the front of the file, and the primary hint stream carrying the
page offset and shared object hint tables (plus an outline hint table when the document has one).
Without those a reader must still fetch the end of the file before it can resolve a single object.

> Encrypted input fails closed. Linearising renumbers every object and a per-object encryption key is
> derived from the object number, so the output would not be decryptable; decrypt first with
> `Tetitulz.Pdf.Encryption.PdfDecryptor`, linearise, then re-encrypt.

> Verified against **an independent structure checker**, which re-derives a file's linearization data and
> compares it with what is written.

## Sanitising

The sanitizer itself lives in `Tetitulz.Pdf.Core` (`Tetitulz.Pdf.Core.Document.PdfSanitizer`), so
the Conformance plugin reaches it without a plugin-to-plugin reference, which this library bans, and
a host that only signs reaches it without SkiaSharp; this package exposes it as a registrable
plugin. It strips embedded JavaScript, launch and URI actions and embedded files, and it walks
`/Next` action chains to sever them at the first dangerous link. The catalog it cleans is the one a
reader takes, however the trailer names it (inline, or through a reference to a reference).

## Encrypted input

These are whole-file rewrites, so they fail closed on an encrypted document rather than emitting a
clear-text file where the reader expects ciphertext. Route encrypted input through
`Tetitulz.Pdf.Encryption` — its incremental update preserves the encryption and any signature, its
round trip preserves neither.

The optimizer renumbers objects, so it takes the round trip: decrypt, optimise, re-encrypt, in one
call. The output keeps the input's file layout, object streams included.

```csharp
RoundTripResult result = PdfEncryptedRoundTrip.Run(input, output,
    new EncryptedRoundTripOptions { OpenPassword = "secret" },
    (plain, produced) => PdfOptimizer.Optimize(plain, produced, OptimizationOptions.Lossless));

if (!result.Succeeded) throw new InvalidOperationException(result.FailureReason);
// result.OwnerPasswordChanged: only the open password was in hand, so the owner password could not be kept.
```

There is no `Optimize(stream, stream, password)` overload, by design: it would need this package to
reference `Tetitulz.Pdf.Encryption`, which brings BouncyCastle into every optimizer user's package
graph. The delegate keeps that dependency with the caller who needs it.

## Targets

`net10.0` and `netstandard2.0`.

## Licence and provenance

Tetitulz.Pdf Attribution License 1.0 (LICENSE.txt in the package): the MIT License plus conditions on every use of this plugin, of the whole or any part — credit "Uses Tetitulz.Pdf by Tamir Mordo", report the use on https://github.com/tetitu/tetitulz.pdf, and report any security problem privately with the fix if there is one. Copyright (c) 2026 Tamir Mordo.

See the repository for the full guide, the open-gap register and the third-party notices.
