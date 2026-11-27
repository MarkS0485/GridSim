### cgmes-shacl

`gridsim cgmes-shacl <bundle.zip|folder> --shapes <folder|file> [--shapes <folder|file>] [--rdfs <folder|file>] [--profile-filter eq,tp,ssh,sv,dl,gl,sc,dy] [--report <file.csv|file.json>]`

`gridsim cgmes-shacl --census <folder|file> [<folder|file> ...]`

Runs SHACL shapes on a CGMES bundle with GridSim's own SHACL Core checker (no third-party library). The shapes are the
user's: for example the ENTSO-E CGMES 3.0 files of https://github.com/entsoe/CGMES-AP (`*-AP-Con-Simple-SHACL.ttl` and
the others) or the CGMES 2.4 sets of https://github.com/entsoe/QoCDC-AP; none ship with GridSim. Shapes and RDFS files
are Turtle (`.ttl`) or RDF/XML (`.rdf`, `.xml`); a folder is read for those files. The checker covers targets by class
(with the rdfs:subClassOf closure of the `--rdfs` files; without them the exact rdf:type only, said in the output), by
node, by subject and by object; paths that are a property, an inverse or a sequence; and the constraints sh:class,
sh:datatype, sh:nodeKind, sh:minCount, sh:maxCount, the four range bounds, sh:minLength, sh:maxLength, sh:pattern,
sh:in, sh:hasValue, sh:node, sh:property, sh:and, sh:or, sh:not, sh:xone, sh:equals and sh:disjoint, with severity,
message and deactivation. Every sh:sparql constraint, SPARQL-based target and other constraint is listed as NOT RUN by
name (many ENTSO-E constraints are SHACL-SPARQL). The verdict is one of three. "conforms" means every constraint that
applies was run and none gave a result. "does not conform" means a constraint that was run gave a result. "not
determined" means no result was found, but a constraint that applies was not run. A constraint that holds a part not run
(for example sh:node or sh:not of a shape with sh:closed) is decided only when that part cannot change the answer;
otherwise it is listed as not run too. Even "conforms" covers the SHACL Core subset only and is not a conformity
certificate. CGMES documents carry no datatypes, so an untyped value satisfies sh:datatype when its text is a
valid value of that type. Files are read in path order and the results are sorted, so the report does not depend on the
order the files are named in. `--report` writes every result and every constraint not run as CSV or JSON, never over an
existing file. The exit codes are those of `cgmes-conform`: 0 conforms; 1 the shapes, the RDFS or the bundle cannot be
read, or the report cannot be written; 2 usage; 3 no pass (the bundle does not conform, or the verdict is not
determined).

Result files: `--json <file.json>`, `--csv <file.csv>` or `--out <file.json|file.csv>` write the result to a file
([Result files](#result-files)): the summary (`conforms`, `verdict`, the counts) and the tables `results` (severity, component, focus, path, value,
shape, message), `not_run` (what, shape, reason) and `notes`.

| Flag | Default | Meaning |
|---|---|---|
| `--shapes` | required | A shapes file or folder; may be given more than once. |
| `--rdfs` | none | The profile RDFS (file or folder) for the rdfs:subClassOf closure. |
| `--profile-filter` | all | Only the bundle's documents of these profiles (eq, tp, ssh, sv, dl, gl, sc, dy, eqbd, tpbd). |
| `--report` | none | Write the report to this new `.csv` or `.json` file. |
| `--census` | none | Read only the shapes named after it and list every SHACL feature they use: the count of each, whether the checker runs it, is inert (labels, targets, the path) or does not run it, the number of `sh:sparql` constraints and SPARQL targets, and the path forms. Exit 0 read; 1 unreadable; 2 usage. The result files hold the summary and the table `features`. |
