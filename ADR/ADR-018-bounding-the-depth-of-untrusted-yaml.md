# ADR-018: bound the nesting of untrusted YAML before it is parsed

Status: Accepted (found by a robustness test written for ADR-017; reproduced and fixed; the unprotected code crashed the test host and both API tests with a stack overflow)

Context: YamlDotNet builds its node tree by recursion (`YamlNode.ParseNode` → `YamlSequenceNode.Load` → …) and has no depth limit of its own. About 24,000 nested sequences (a 50 KB document such as
`steps: [[[[…]]]]`) overflow the stack of the thread that parses them. A stack overflow cannot be caught: it ends the process. The product parses YAML it does not control in six places, all of which accepted
documents far larger than that: the pipeline from the source repository (task inventory, runner labels, Azure triggers), the workflow a model wrote (the structure gate, a limit of 256 KB) and the approved text the
trigger editor rewrites. A model that fills a long answer with brackets (a few tokens per hundred characters) or a repository that contains such a file could therefore stop the API. On a thread with a smaller
stack than the test host's, the same document crashes at a depth of thousands, not tens of thousands.

Decision:
- **`SafeYaml.Load` replaces every `YamlStream.Load` of untrusted text.** It first reads the parser's *event* stream, which is a loop and not a recursion, counts the depth of sequences and mappings, and refuses
  a document deeper than 100 (`YamlNestingException`, an `InvalidOperationException`, so every existing catch already handles it) before any node is built. Real pipelines and workflows nest a dozen levels; 100 is
  far above that and far below any danger. A document within the limit loads exactly as before (compared node for node on the whole Orion corpus); a syntax error is still a `YamlException`.
- **The structure gate says so.** `YAML_TOO_DEEP` (Blocker): the workflow is not read, the message names the limit and never the document. The readers of the source (analyser, labels, triggers) report the YAML as
  unreadable, which the preview shows as `SourceYamlAnalysisFailed` and deriving triggers as `SourceYamlUnreadable`.
- **Tested the way that matters.** A test host that loads such a document dies, so the tests are their own detector: every site was reverted to the raw loader in turn and each made the run crash; at API level a
  model answer 140 KB deep and a source pipeline 60 KB deep are answered while the process keeps running (and both crash it with the check removed).

Consequences:
- Any new code that parses untrusted YAML must go through `SafeYaml`; the sites are few and listed by `grep YamlStream`.
- The depth is checked on the parser's events, so it also counts nesting the node tree would hide (anchors and aliases do not nest and are not counted).
- Other recursion over untrusted structures is bounded elsewhere (`System.Text.Json` has a default depth of 64; the analyser's own walks have a depth limit of 24).
- Not covered by this record: the external tools. actionlint and zizmor are separate processes, so a document that breaks one of them breaks that run, not the API.

Alternatives considered: running the parser on a thread with a bigger stack (rejected: it moves the threshold, it does not remove it); a size limit lower than 50 KB (rejected: legitimate workflows approach it, and a
compact document can still nest deeply); scanning the text for brackets (rejected: quoting, block scalars and comments make a text scan wrong in both directions, while the event stream is exact).
