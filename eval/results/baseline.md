# Retrieval baseline

Golden set: 34 questions over the synthetic corpus in `eval/corpus`. A question is a hit when a
top-K passage from the expected file contains the expected phrase. Scores come from the real ONNX embedder
(bge-small-en-v1.5, 384 dimensions) and, where marked, the ms-marco MiniLM reranker. No chat model is involved.

Recorded on the default settings of the app: 400-token chunks, 6 passages, reranker on. The regression check
compares that row only.

| Chunk (tokens) | Passages (top K) | Reranker | hit@K | MRR | Missed |
|---:|---:|:---:|---:|---:|---|
| 200 | 3 | off | 100.0 % | 0.946 | none |
| 200 | 3 | on | 97.1 % | 0.971 | security-password |
| 200 | 6 | off | 97.1 % | 0.922 | security-password |
| 200 | 6 | on | 97.1 % | 0.971 | security-password |
| 400 | 3 | off | 100.0 % | 0.946 | none |
| 400 | 3 | on | 97.1 % | 0.971 | security-password |
| 400 | 6 | off | 97.1 % | 0.922 | security-password |
| 400 | 6 | on | 97.1 % | 0.971 | security-password |
| 800 | 3 | off | 100.0 % | 0.946 | none |
| 800 | 3 | on | 97.1 % | 0.971 | security-password |
| 800 | 6 | off | 97.1 % | 0.922 | security-password |
| 800 | 6 | on | 97.1 % | 0.971 | security-password |

The corpus is fictional. Replace it with your own documents, add your own questions to `eval/golden.json`,
and record a new baseline before you change the defaults.