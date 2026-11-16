# Tetitulz.Pdf.Plugins.Fonts

Fonts for hosts that have none. `Tetitulz.Pdf` ships no fonts in its core packages and maps the
standard 14 to Liberation first, so on a Linux container or a locked-down service with no fonts
installed nothing renders predictably. This package carries, unmodified and under the SIL Open Font
License 1.1:

| Family | Files | Stands in for |
|---|---|---|
| Liberation Sans 2.1.5 | Regular, Bold, Italic, Bold Italic | Helvetica / Arial |
| Liberation Serif 2.1.5 | Regular, Bold, Italic, Bold Italic | Times |
| Liberation Mono 2.1.5 | Regular, Bold, Italic, Bold Italic | Courier |
| Noto Sans Hebrew 3.001 | Regular, Bold | Hebrew text with its points |
| Noto Sans Arabic 2.013 | Regular, Bold | Arabic text, joined |

## Registering it

```csharp
BundledFonts.Register();                       // writes the fonts under the account's data folder and registers it
// or, for a directory you control:
BundledFonts.Register("/var/lib/myservice/fonts");
// or through the plugin host:
PdfPluginHost.Instance.Register(new BundledFontsPlugin());
```

The *mechanism* is in `Tetitulz.Pdf.Core`: `FontSearchPath.Add` puts a directory ahead of the
operating system's and of `TETITULZ_PDF_FONTDIR`'s, so the bundled Liberation Sans is the one found
when the host also has one, and two hosts render a page the same whatever they have installed. This
package only carries the files and calls that door. `Extract(directory)` writes the files without
registering; `Unregister()` takes the directory back out of the search path.

Files are written once per package version (each unless it already holds the bundled bytes), whole
to a sibling and moved into place, so concurrent starts cannot read a half-written font.

**The directory is searched first, so it must be one only your account writes** — a font found
there is the one rendered and embedded. `Register()` uses the account's application-data folder
(`%LOCALAPPDATA%\tetitulz-pdf-fonts\<version>`, `~/.local/share/tetitulz-pdf-fonts/<version>`). An
account with no such folder it can write gets a directory this process made under the temporary
directory, by a name no one could have made first, removed when the process ends — never a
well-known folder in a temporary directory other accounts share. A directory you pass to
`Register(string)` is yours to guard.

## Why a separate package

**Mechanism in Core, payload in the plugin.** Sixteen TrueType files are about four and a half
megabytes that most hosts — the ones with fonts — never need. Like `Tetitulz.Pdf.Plugins.CMap`, this package
isolates a cost and nothing in Core depends on it.

## Targets

`net10.0` and `netstandard2.0`. No third-party dependency.

## Licence and provenance

Apache License 2.0. Copyright (c) 2026 Tamir Mordo. Credit it as "Uses Tetitulz.Pdf by Tamir Mordo" (the package's NOTICE file).

Every font is redistributed unmodified under the SIL Open Font License 1.1 — Liberation Fonts
copyright 2012 Red Hat, Inc. (and Ascender Corporation for the original Liberation Fonts); Noto Sans
Hebrew and Noto Sans Arabic copyright 2022 The Noto Project Authors. The licence texts, with each
family's Reserved Font Names, are packed beside this package as `LICENSE-Liberation-OFL.txt`,
`LICENSE-NotoSansHebrew-OFL.txt` and `LICENSE-NotoSansArabic-OFL.txt` and written beside the fonts.
The OFL permits bundling the fonts with software and forbids selling them by themselves. Sources:
github.com/liberationfonts/liberation-fonts (release 2.1.5), github.com/notofonts/hebrew
(NotoSansHebrew-v3.001), github.com/notofonts/arabic (NotoSansArabic-v2.013); the unhinted builds of
the Noto families, since a PDF producer uses outlines, not hints. The package's own code is MIT, as
the rest of the library is.
