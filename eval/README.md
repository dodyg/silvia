# Retrieval evaluation

Two question sets run against `corpus/`, a small fictional document set that exists so the baseline can be
reproduced anywhere. It is not your data.

- `golden.json`: plain lookups. Each question names the file its answer comes from and an exact phrase the answer
  must contain.
- `hard.json`: the questions plain lookups don't exercise, each tagged with a `kind`: `paraphrase`, `stale`,
  `long-doc`, `format`, `multi-hop`, `follow-up`, `aggregation` and `unanswerable`. Each lists its `evidence`: the
  passages a full answer needs, as a file and terms that must all appear in one passage. A `follow-up` question
  also has the earlier turns in `history`, alternating user and assistant and starting with the user; an
  `unanswerable` one has no evidence.

Chat rewrites a follow-up into a question that stands on its own before it searches, and so does the evaluation.
The rewrites are kept in `results/rewrites.json`, so a rerun searches the same text and needs no API key. A new or
changed follow-up is rewritten by the chat model when `DEEPSEEK_API_KEY` is set, and searched as asked otherwise;
the run says which.

Chat also reads the passages it found for a question and plans up to two bridge searches for what they point to but
do not hold, such as what the person they name did elsewhere; each adds up to two passages. The evaluation plans
them the same way from the passages of the default setting, which runs first, and keeps them in
`results/bridges.json` by query, so every setting and every rerun searches the same text. Without a kept plan and
without `DEEPSEEK_API_KEY`, a question is searched once; the run says how many were.

The PDF, Word, Excel and PowerPoint documents are not checked in. `Silvia.Eval/Fixtures.cs` writes them from text
at the start of every run, next to a temporary copy of `corpus/`.

Run from the repository root, after `scripts/fetch-models.ps1`. A full run takes about ten minutes or more.

```bash
dotnet run --project eval/Silvia.Eval                 # print the grid of settings
dotnet run --project eval/Silvia.Eval -- --record     # write results/baseline.json and baseline.md
dotnet run --project eval/Silvia.Eval -- --check      # exit 1 when the default setting drops more than 2 points
```

The grid varies chunk size (200, 400, 800 tokens), passages per answer (3, 6) and the reranker (off, on). The
report gives the hit rate per kind, evidence recall, and how well a threshold on the top score separates
unanswerable questions. The run stops if any corpus file was not indexed, so a parser that silently skips a format
shows up as an error, not as missed questions. The regression check compares the overall hit rate of the default
setting only (400 tokens, 6 passages, reranker on), and lists any kind that dropped.

To evaluate your own documents, put them in a folder, point `corpus` at it, and replace the two question files
with questions you know the answers to. Record a new baseline before you change the defaults in `ProjectSettings`.
