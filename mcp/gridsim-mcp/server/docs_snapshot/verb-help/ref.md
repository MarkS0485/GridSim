### ref

`gridsim ref encode <value> [--words N] [--check]`
`gridsim ref decode <reference> [--words N] [--check]`
`gridsim ref scatter <number|reference> --seed S [--words N] [--check]`
`gridsim ref from-id <id> [--words N] [--check]`

Electrical references: a number written as electrical words that can be read aloud and typed back, one word per byte
from a fixed list of 256 (units, pioneers, materials, physics, signals and more; no switching, safety, phase, colour,
asset, fuel or alarm words), most significant byte first, joined by dots: `gridsim ref encode 0x3C042C` prints
`copper.tesla.kron`. Three words give 16,777,216 references, four give 4,294,967,296.

- `encode` writes a value (decimal, or hexadecimal after `0x`) as words; a value too large for the word count is
  refused.
- `decode` reads a reference back and prints its value in decimal and hexadecimal. It is strict: lower case only, dots
  between words and nowhere else, exactly the expected number of words. An unknown word is refused with the nearest
  valid words by edit distance.
- `scatter` maps sequential asset numbers to unrelated-looking references with no two ever the same (a keyed
  permutation; the same seed gives the same references on every machine). Given a number it prints the reference;
  given a reference it prints the number back.
- `from-id` prints the reference that follows from an existing id (the leading bytes of the SHA-256 of its UTF-8
  text). Different ids can share a reference; the verb does not check, so compare references across a set of ids
  before relying on them.

| Flag | Default | Meaning |
|---|---|---|
| `--words` | 3 | Data words (1 to 8; 2 to 4 for `scatter`). |
| `--check` | off | Append (on decode: expect and verify) a check word, the CRC-8/SAE-J1850 of the data bytes, which catches any single wrong word and any swap of two neighbouring words. |
| `--seed` | none | The key of the `scatter` arrangement (decimal, or hexadecimal after `0x`); required by `scatter`. |

Needs no case, no licence and no network. Exit 1 when a reference is refused (the reason is printed); exit 2 on a
usage error (unknown subcommand or flag, missing or malformed value, value too large for the word count).
