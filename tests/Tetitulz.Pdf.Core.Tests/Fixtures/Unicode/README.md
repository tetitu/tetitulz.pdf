# Unicode conformance data

`BidiCharacterTest-18.0.0.txt.gz` and `BidiTest-18.0.0.txt.gz` are the Unicode Character Database 18.0.0's two
conformance files for UAX #9, the Unicode Bidirectional Algorithm — character strings, and sequences of bidi
classes — from `https://www.unicode.org/Public/18.0.0/ucd/`, gzipped and otherwise unmodified, their copyright
headers kept. `BidiCharacterConformanceTests` runs every case of both through `Core.Text.UnicodeBidi`.

It is test data, distributed in no package, under the Unicode License v3 ([`LICENSE-Unicode.txt`](LICENSE-Unicode.txt)).
A new Character Database version means three regenerations together: `tools/unicode-bidi/Generate-BidiClasses.ps1`,
`tools/unicode-bidi/Generate-BidiBrackets.ps1`, and these files — the test checks they agree.
