### cgmes-conform

`gridsim cgmes-conform <bundle.zip|folder>`

Checks a CGMES bundle against GridSim's own, hand-coded subset of the published profile rules (RC4 R4-11 part B). The
bundle is a ZIP (read in sorted entry order; path traversal, repeated entries, duplicate profiles and a DTD are refused) or
a folder of `.xml` documents. The checks, each named in the result with the rule it follows: C01 well-formed XML without a
DTD; C02 a recognised `md:Model.profile` in every document; C03 one CIM namespace (CIM16 for CGMES 2.4.15, CIM100 for
CGMES 3.0); C04 a coherent profile set; C05 `md:Model.DependentOn` resolves; C06 identifiers unique and in the edition's
form; C07 every reference resolves; C08 mandatory attributes of the classes GridSim writes and reads; C09 attributes sit in
their owning profile; C10 numeric and boolean values well formed; C11 terminal counts per class. The ENTSO-E SHACL
constraint files and the RDFS / OCL sets are NOT applied and the result lists them as `notrun` (S01, S02): a pass here is
not a conformity certificate, and conformity against a vendor tool or the ENTSO-E conformity test models is
comparator-unverified. Exit 0 when no check fails; 1 when the bundle cannot be read; 3 when a check fails (the verdict);
2 usage.

Result files: `--json <file.json>`, `--csv <file.csv>` or `--out <file.json|file.csv>` write the result to a file
([Result files](#result-files)): the summary and the table `checks` (id, name, basis, status, findings).
