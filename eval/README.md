# Retrieval evaluation

`golden.json` holds questions with the file each answer comes from and an exact phrase the answer must contain.
`corpus/` is a small fictional document set that the questions run against. It exists so the baseline can be
reproduced anywhere. It is not your data.

Run from the repository root, after `scripts/fetch-models.ps1`:

```bash
dotnet run --project eval/Silvia.Eval                 # print the grid of settings
dotnet run --project eval/Silvia.Eval -- --record     # write results/baseline.json and baseline.md
dotnet run --project eval/Silvia.Eval -- --check      # exit 1 when the default setting drops more than 2 points
```

The grid varies chunk size (200, 400, 800 tokens), passages per answer (3, 6) and the reranker (off, on). A
question is a hit when a passage from the expected file, within the top K, contains the phrase. The regression
check compares the default setting only (400 tokens, 6 passages, reranker on).

To evaluate your own documents, put them in a folder, point `corpus` at it, and replace `golden.json` with
questions you know the answers to. Record a new baseline before you change the defaults in `ProjectSettings`.
