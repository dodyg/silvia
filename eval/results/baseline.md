# Retrieval baseline

90 questions over the synthetic corpus in `eval/corpus` plus the PDF and Office documents that
`Fixtures.cs` writes at the start of each run (basic 34, paraphrase 9, stale 6, long-doc 7, format 9, multi-hop 7, follow-up 8, aggregation 3, unanswerable 7). Scores come from the real ONNX embedder
(bge-small-en-v1.5, 384 dimensions) and, where marked, the ms-marco MiniLM reranker. The only chat model call is the rewrite of each follow-up.

A question is a hit when the top-K passages hold every piece of its evidence: a passage from the expected
file that contains all the expected terms. Kinds:

- **basic**: `golden.json`, one fact per question, worded much like the document.
- **paraphrase**: the same kind of fact, asked in other words than the document uses.
- **stale**: the current answer, when an older or more general document says something else.
- **long-doc**: a fact buried in the middle of a long document.
- **format**: facts from the generated PDF, Word, Excel and PowerPoint files.
- **multi-hop**: an answer that needs passages from two or three files.
- **follow-up**: a question that only makes sense after the turns in `history`. Retrieval searches its
  standalone rewrite, as chat does, kept in `results/rewrites.json`.
- **aggregation**: a question whose full answer is spread across three or four files.
- **unanswerable**: nothing in the corpus answers it. Not part of the hit rate.

*All* is the hit rate over every answerable question. *Evidence recall* counts the pieces of evidence
found, so a multi-hop question with one of two pieces counts half. *Abstain* is the balanced accuracy
of the best single threshold on the top hit's score at telling unanswerable questions from the rest
(0.5 means the score says nothing). MRR uses the final order of the hits, which Rag.NET's lost-in-the-middle
reordering changes, so read it as a rough signal only.

Recorded on the default settings of the app: 400-token chunks, 6 passages, reranker on. The regression
check compares the *All* column of that row.

| Chunk | K | Rerank | All | basic | paraphrase | stale | long-doc | format | multi-hop | follow-up | aggregation | Evidence recall | MRR | Abstain |
|---:|---:|:---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| 200 | 3 | off | **90 %** | 97 % | 100 % | 67 % | 100 % | 100 % | 71 % | 75 % | 67 % | 90 % | 0.783 | 0.80 |
| 200 | 3 | on | **89 %** | 94 % | 100 % | 83 % | 100 % | 100 % | 57 % | 75 % | 67 % | 89 % | 0.781 | 0.80 |
| 200 | 6 | off | **89 %** | 94 % | 89 % | 67 % | 100 % | 100 % | 71 % | 88 % | 67 % | 89 % | 0.737 | 0.80 |
| 200 | 6 | on | **89 %** | 94 % | 89 % | 83 % | 100 % | 100 % | 57 % | 88 % | 67 % | 89 % | 0.751 | 0.80 |
| 400 | 3 | off | **90 %** | 97 % | 100 % | 67 % | 100 % | 100 % | 71 % | 75 % | 67 % | 89 % | 0.777 | 0.79 |
| 400 | 3 | on | **90 %** | 97 % | 100 % | 83 % | 100 % | 100 % | 57 % | 75 % | 67 % | 90 % | 0.795 | 0.78 |
| 400 | 6 | off | **90 %** | 94 % | 100 % | 67 % | 100 % | 100 % | 71 % | 88 % | 67 % | 90 % | 0.743 | 0.79 |
| 400 | 6 | on | **90 %** | 94 % | 100 % | 83 % | 100 % | 100 % | 57 % | 88 % | 67 % | 90 % | 0.769 | 0.79 |
| 800 | 3 | off | **90 %** | 97 % | 100 % | 67 % | 100 % | 100 % | 71 % | 75 % | 67 % | 89 % | 0.777 | 0.79 |
| 800 | 3 | on | **90 %** | 97 % | 100 % | 83 % | 100 % | 100 % | 57 % | 75 % | 67 % | 90 % | 0.795 | 0.78 |
| 800 | 6 | off | **90 %** | 94 % | 100 % | 67 % | 100 % | 100 % | 71 % | 88 % | 67 % | 90 % | 0.743 | 0.79 |
| 800 | 6 | on | **90 %** | 94 % | 100 % | 83 % | 100 % | 100 % | 57 % | 88 % | 67 % | 90 % | 0.769 | 0.79 |

Missed at the default setting (400 tokens, top 6, reranker on):

- **basic**: security-password, travel-meals-abroad
- **stale**: stale-hotel-cap
- **multi-hop**: hop-berg-keys (2/3), hop-zurich-difference (1/2), hop-carvalho-manager (0/2)
- **follow-up**: fu-meals-abroad
- **aggregation**: agg-working-days (2/4)
- **unanswerable**: best-hit score 0.015 to 0.016; answerable questions 0.015 to 0.016 (median 0.016)

The corpus is fictional. Replace it with your own documents, add your own questions to `eval/golden.json`
and `eval/hard.json`, and record a new baseline before you change the defaults.