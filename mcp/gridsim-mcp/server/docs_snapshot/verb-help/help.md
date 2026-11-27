## help

`gridsim help [<verb> [<mode>]]`, `gridsim <verb> --help`, `gridsim <verb> -h`

With a verb, prints that verb's section of this chapter as plain text: its usage line, every flag with its default, its
exit codes and the notes its group shares, then the general exit-code rule, and exits 0. Because the chapter is compiled
into the binary, the help is this text. Aliases work (`gridsim n-1 --help` prints the `nminus1` section), and a verb
with modes takes the mode (`gridsim bench --estimate --help`). `gridsim help` with no verb is `gridsim --help` (the
verb list). `gridsim help <word>` for a word that is not a verb exits 2; `gridsim <word> --help` for a word that is
not a verb prints the verb list. Help needs no licence and is answered before the unknown-flag check.
