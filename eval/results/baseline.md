# Retrieval baseline

90 questions over the synthetic corpus in `eval/corpus` plus the PDF and Office documents that
`Fixtures.cs` writes at the start of each run (basic 34, paraphrase 9, stale 6, long-doc 7, format 9, multi-hop 7, follow-up 8, aggregation 3, unanswerable 7). Scores come from the real ONNX embedder
(bge-small-en-v1.5, 384 dimensions) and, where marked, the ms-marco MiniLM reranker. The only chat model calls are the rewrite of each
follow-up and the bridge searches: further searches for what the first passages point to, which add up to two
passages each, as chat does. Both are kept in `results/`, so a rerun needs no API key; without a kept plan or a
key, a question is searched once.

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
(0.5 means the score says nothing). With the reranker on, that score is its relevance from 0 to 1, which
the app's `WeakMatchBelow` setting is chosen from; with it off, it is a fused rank score that says little
about relevance. *Abstain + terms* is the same, except that a question also counts as unanswerable when a name,
number or acronym in it appears in none of its passages, which chat tells the model too. MRR uses the final order
of the hits, which the lost-in-the-middle reordering changes, so read it as a rough signal only.

Searches run as chat runs them: a question that asks for every item of a kind ("List everything…", "Which
rules…") gets twice the passages.

Recorded on the default settings of the app: 400-token chunks, 6 passages, reranker on. The regression
check compares the *All* column of that row.

| Chunk | K | Rerank | All | basic | paraphrase | stale | long-doc | format | multi-hop | follow-up | aggregation | Evidence recall | MRR | Abstain | Abstain + terms |
|---:|---:|:---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| 200 | 3 | off | **94 %** | 100 % | 89 % | 83 % | 100 % | 100 % | 86 % | 88 % | 67 % | 93 % | 0.775 | 0.80 | 0.95 |
| 200 | 3 | on | **96 %** | 100 % | 100 % | 100 % | 100 % | 100 % | 86 % | 88 % | 67 % | 97 % | 0.802 | 0.84 | 0.93 |
| 200 | 6 | off | **98 %** | 100 % | 100 % | 100 % | 100 % | 100 % | 86 % | 100 % | 67 % | 98 % | 0.751 | 0.80 | 0.95 |
| 200 | 6 | on | **100 %** | 100 % | 100 % | 100 % | 100 % | 100 % | 100 % | 100 % | 100 % | 100 % | 0.778 | 0.84 | 0.93 |
| 400 | 3 | off | **94 %** | 100 % | 89 % | 83 % | 100 % | 100 % | 86 % | 88 % | 67 % | 93 % | 0.777 | 0.79 | 0.93 |
| 400 | 3 | on | **96 %** | 100 % | 100 % | 100 % | 100 % | 100 % | 86 % | 88 % | 67 % | 97 % | 0.818 | 0.83 | 0.93 |
| 400 | 6 | off | **98 %** | 100 % | 100 % | 100 % | 100 % | 100 % | 86 % | 100 % | 67 % | 98 % | 0.755 | 0.79 | 0.93 |
| 400 | 6 | on | **100 %** | 100 % | 100 % | 100 % | 100 % | 100 % | 100 % | 100 % | 100 % | 100 % | 0.798 | 0.83 | 0.93 |
| 800 | 3 | off | **94 %** | 100 % | 89 % | 83 % | 100 % | 100 % | 86 % | 88 % | 67 % | 93 % | 0.777 | 0.79 | 0.93 |
| 800 | 3 | on | **96 %** | 100 % | 100 % | 100 % | 100 % | 100 % | 86 % | 88 % | 67 % | 97 % | 0.818 | 0.83 | 0.93 |
| 800 | 6 | off | **98 %** | 100 % | 100 % | 100 % | 100 % | 100 % | 86 % | 100 % | 67 % | 98 % | 0.755 | 0.79 | 0.93 |
| 800 | 6 | on | **100 %** | 100 % | 100 % | 100 % | 100 % | 100 % | 100 % | 100 % | 100 % | 100 % | 0.798 | 0.83 | 0.93 |

Missed at the default setting (400 tokens, top 6, reranker on):

- **unanswerable**: best-hit score 0.000 to 1.000; answerable questions 0.000 to 1.000 (median 0.996); best threshold 0.706
- **unanswerable, best-hit score each**: none-health-insurance (0.000), none-wifi (0.001), none-resignation (0.003), none-remote-work (0.008), none-radio-battery (0.042), none-enterprise-price (0.576), none-accra-launch (1.000)
- **unmentioned terms**: 3 of 7 unanswerable, 0 of 83 answerable ()
- **with unmentioned terms**: balanced accuracy 0.93 at threshold 0.043; at the app's weak-match cut of 0.01, 6 of 7 unanswerable and 9 of 83 answerable are flagged
- **below 0.01**: 4 of 7 unanswerable, 9 of 83 answerable (security-password, para-password, para-holiday-carryover, para-doctor-note, para-big-claim, para-unlabelled, para-closed-account, fu-fuse-cause, agg-working-days)
- **below 0.05**: 5 of 7 unanswerable, 13 of 83 answerable (security-password, para-password, para-page-escalation, para-holiday-carryover, para-doctor-note, para-big-claim, para-unlabelled, para-closed-account, pptx-churn, hop-contractor-payroll, hop-carvalho-manager, fu-fuse-cause, agg-working-days)
- **below 0.10**: 5 of 7 unanswerable, 15 of 83 answerable (security-password, para-password, para-page-escalation, para-holiday-carryover, para-doctor-note, para-big-claim, para-unlabelled, para-closed-account, long-firmware, pdf-unit-price, pptx-churn, hop-contractor-payroll, hop-carvalho-manager, fu-fuse-cause, agg-working-days)
- **below 0.20**: 5 of 7 unanswerable, 17 of 83 answerable (security-password, para-password, para-page-escalation, para-holiday-carryover, para-doctor-note, para-big-claim, para-unlabelled, para-stale-library, para-closed-account, long-firmware, pdf-liability, pdf-unit-price, pptx-churn, hop-contractor-payroll, hop-carvalho-manager, fu-fuse-cause, agg-working-days)

The corpus is fictional. Replace it with your own documents, add your own questions to `eval/golden.json`
and `eval/hard.json`, and record a new baseline before you change the defaults.