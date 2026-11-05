/**
 * The help topics: what each screen and setting does, and how to get good answers. Written in Markdown and
 * rendered by the Help screen. Keep it in step with the settings and their defaults in ProjectSettings.cs.
 */
export interface HelpTopic {
  id: HelpTopicId;
  title: string;
  /** One line under the title in the topic list. */
  summary: string;
  body: string;
}

export type HelpTopicId =
  | "start"
  | "how"
  | "asking"
  | "screens"
  | "project-settings"
  | "app-settings"
  | "privacy"
  | "quality"
  | "troubleshooting"
  | "shortcuts";

export const HELP_TOPICS: readonly HelpTopic[] = [
  {
    id: "start",
    title: "Getting started",
    summary: "From an empty app to a first answer.",
    body: `
Silvia answers questions from your own documents. It reads the folders you give it, keeps them indexed as files change, and answers with citations to the passages it used.

### 1. Install the local models (recommended)

Open **Settings → Global → Models** and choose **Download the models** (about 230 MB). They run on this computer:

- The **embedder** lets search find passages by meaning, not only by matching words.
- The **reranker** puts the best passages first and tells Silvia when nothing matches well.

Without them Silvia still works: it searches by keyword and uses the chat model to widen the search.

### 2. Add a DeepSeek key (for written answers)

In **Settings → Global → DeepSeek key**, paste your API key and choose **Save key**. Silvia checks it with DeepSeek first and stores it encrypted for your Windows account.

Without a key, Silvia still finds the passages for a question but writes no answer.

### 3. Create a project

A project is a separate knowledge base, with its own folders, index, conversations and facts. Open **Projects**, give the project a name and create it. Use one project per subject (a client, a product, your HR policies) so answers do not mix unrelated documents.

### 4. Add folders

In **Library**, choose **Add folder**. Silvia indexes the supported files under it and keeps the index in step as files are added, changed or removed. It also rescans every hour.

Supported files: Markdown (\`.md\`), text (\`.txt\`), PDF, Word (\`.docx\`), Excel (\`.xlsx\`), PowerPoint (\`.pptx\`), HTML, CSV and JSON. Scanned PDFs without a text layer cannot be read yet.

**Activity** shows indexing progress and any file that failed.

### 5. Add web pages (optional)

In **Library → Web pages**, paste a web address, or a list with one per line, and choose **Add pages**. Silvia downloads each page, keeps its main text (without menus, footers or cookie banners) as a document of the project, and from then on searches and cites it like a file. Answers name a page by its title and site, and **Open page** opens it in your browser.

- Web pages, Markdown and plain text, PDF and Office files can be added. Silvia follows links only to reach the page itself (up to five redirects); it does not crawl a site.
- Silvia downloads each page again once its copy is older than a day (see *Project settings → Web pages*), and you can choose **Refresh** on a page, or **Refresh all**, at any time. A page that has not changed is left as it is and costs nothing.
- Pages on your company's intranet work too, as long as they open without a sign-in.

### 6. Ask

Open **Chat** and ask a question. Numbers like **[1]** in the answer are citations: select one to see the passage and open its file.
`,
  },
  {
    id: "how",
    title: "How Silvia answers",
    summary: "What happens between your question and the answer.",
    body: `
Knowing the steps helps you understand an answer, and fix it when it is wrong.

### When a file is indexed

1. **Parsing.** The file's text is extracted, with its headings where the format has them.
2. **Chunking.** The text is split into **passages** of about *Chunk size* tokens, which overlap a little so that a sentence on a boundary is not lost. Each passage remembers its document title and section.
3. **Embedding.** With the local embedder, each passage gets a vector, a set of numbers that captures its meaning. A keyword index is kept too.
4. **Optional extras** with the chat model: an **overview** of each document, and the **knowledge graph**.

### When you ask

1. **Follow-up rewrite.** In a conversation, a question like *"and the next one up?"* is rewritten into one that stands on its own before searching. The answer still sees your words.
2. **Search.** Silvia searches by meaning and by keyword, and combines the two (*hybrid search*). Without the embedder, the chat model first turns your question into search terms.
3. **Reranking.** The reranker reads at least the top 30 candidates next to the question and orders them by how well they answer it.
4. **Spreading.** No document takes more than half the places while other documents also match, so one long file cannot crowd out the rest. A question that asks for every item of a kind (*"List everything…"*, *"Which rules…"*) gets twice the passages, because its answer is usually spread over several documents.
5. **Bridge searches.** If the passages point to something they do not contain (*"the person who signs off access reviews"*), the chat model writes up to two more searches, each adding up to two passages.
6. **Context.** The answer is given:
   - the passages, each with its file, section and modification date (for a web page: its title, site and the date it was fetched)
   - the **overviews** of up to three cited documents
   - the matching part of the **knowledge graph**
   - a similar exchange from an earlier conversation
   - your project's **facts**
   - the recent turns of this conversation
7. **Budget.** If all of that is larger than the *Context budget*, the least useful parts are dropped first. See *Project settings*.
8. **Answer.** The chat model answers from that context only, and cites it.

### Built-in safeguards

- **Weak matches.** When the reranker finds no passage that matches closely, the answer is told to say the documents do not contain the answer rather than piece one together.
- **Names nothing mentions.** When a name, number or acronym in your question appears in none of the passages (*"the pilot in Accra"* when the documents only describe pilots elsewhere), the answer is told so, and to say the documents do not contain the answer unless a passage states it in other words.
- **Instructions inside documents.** Text in a document or web page that tries to instruct the model is treated as data. See *Privacy*.
- **Old and new versions.** When passages disagree, the answer is told to follow the current document (by what the documents say about themselves, then by date) and to say which one it followed.
- **Stale memory.** An earlier exchange is not recalled if a document it cited has changed or been deleted since.
- **Documents are data.** Text in your documents is never treated as instructions to the model.
`,
  },
  {
    id: "asking",
    title: "Asking good questions",
    summary: "Habits that get better answers.",
    body: `
- **Name things.** *"What is the hotel cap in the 2025 travel policy?"* finds more than *"how much for hotels?"*. Names, product codes and dates are strong search terms.
- **One question at a time** works best. A question that joins two facts often works too, thanks to bridge searches. If it does not, ask the two parts in turn.
- **Follow-ups are fine.** *"And for Cairo?"* after a question about hotel caps is rewritten into a full question before searching.
- **Questions about a whole document** (*"list every deadline in the onboarding guide"*) use its overview. Keep **Document overviews** on for these.
- **"Across all documents" questions** (*"which policies mention contractors?"*) are the hardest for any RAG system, because only a limited number of passages fit in one answer. Raise *Passages per answer*, or ask about one area at a time.
- **Check the citations.** Select **[n]** to read the passage. An answer is only as good as the passages it was given.
- **"The documents do not say."** This is often correct. If you know the answer is there, see *Troubleshooting*.

### Teach Silvia what the documents leave out

Under an answer, **Correct this** saves a correction as a project **fact**. Facts reach every later answer in the project, and answers prefer them over the documents when the two disagree. Use facts for things that are true but not written down, or written ambiguously.
`,
  },
  {
    id: "screens",
    title: "The screens",
    summary: "What each part of the app is for.",
    body: `
| Screen | What it is for |
|---|---|
| **Chat** | Ask questions and get cited answers. Conversations are kept per project. Under each answer you can **Check this answer**, **Correct this** (save a fact) and **Save as test question**. |
| **Search** | Find passages without calling any cloud model, so it works offline. It also shows the *close calls*: passages that were near the question but did not make the list. |
| **Library** | The folders and web pages this project indexes, and its documents. Add a web address or a pasted list under **Web pages**; each page shows when it was fetched and whether it is indexed, with **Open**, **Refresh** and **Remove**. Open a document to see its overview and the exact passages Silvia extracted from it. |
| **Map** | Every passage placed by meaning. Passages on one topic cluster together. A dot far from its document's cluster is a passage unlike the rest of that document. |
| **Graph** | The people, organisations, products and systems in the project, and how they relate. Only with the knowledge graph turned on. |
| **Facts** | Things you know to be true that the documents leave out or only imply. Every answer in the project is given them. |
| **Evaluate** | A test set of questions you know the answers to. A run asks each one the way chat does, and the judge model grades every answer. |
| **Activity** | Indexing work: what is queued, running, done, or failed, and why. |
| **Projects** | Create, open, export, import and delete projects. An export (\`.silvia\` file) keeps folders as paths; it does not copy your files. |
| **Settings** | How the open project searches and answers (**Project**), the models and key (**Global**), and how the app looks (**Appearance**). |
`,
  },
  {
    id: "project-settings",
    title: "Project settings",
    summary: "Every setting under Settings → Project, and when to change it.",
    body: `
Project settings apply to the open project only. Saving reopens the project with them. When a change affects answers and the project has a test set, Silvia offers to run it, so you can compare before and after.

### Models

| Setting | What it does | When to change it |
|---|---|---|
| **Embedder** | The local model that turns passages and questions into vectors for search by meaning. | Rarely. Changing it re-embeds every document, because vectors from different embedders cannot be compared. |
| **Reranker** | A local model that reorders the retrieved passages by how well each answers the question. It is also what detects weak matches. | Keep it on. Turn it off only to compare, or on a very slow computer. |
| **Cloud policy** | Where answers may come from. **deepseek**: answers are written by DeepSeek. **search-only**: no model is ever called, and you get passages only. **local-only**: reserved for a local chat model, not available yet. | Use **search-only** for documents that must not leave this computer. See *Privacy*. |
| **Chat model** | The DeepSeek model that writes answers, overviews, rewrites and bridge searches. | A larger model reasons better over many passages; a smaller one is faster and cheaper. |
| **Judge model** | The model that grades answers in **Evaluate** and in **Check this answer**. Empty means the chat model. | Choose a different model from the chat model for a more independent grade. |

### Retrieval

| Setting | Default | What it does |
|---|---|---|
| **Chunk size (tokens)** | 400 | The length of each passage. A token is about four characters, or three quarters of a word. Smaller passages are more precise but carry less context; larger ones carry more context but can bury the fact in other text. 200 to 800 suits most documents. It applies when a file is indexed, so files already indexed keep their old passages until they change. |
| **Chunk overlap (tokens)** | 60 | How much each passage repeats of the one before, so that a sentence on a boundary appears whole in one of them. About 10 to 20 % of the chunk size. |
| **Passages per answer** | 6 | How many passages each answer is given (bridge searches can add up to four more). Raise it for questions whose answer is spread over many places. Lower it for faster, cheaper answers to simple lookups. |
| **Earlier exchanges to recall** | 3 | How many similar earlier questions and answers in this project, from any conversation, are given to a new question. 0 turns recall off. |
| **Recall threshold (% similarity)** | 70 | How close an earlier question must be before it is recalled. Higher means only near repeats are recalled. |
| **Context budget (tokens)** | 12,000 | The most an answer is given in total: passages, overviews, graph, recalled exchange and facts. Over it, the recalled exchange is dropped first, then the graph, the overviews, the bridge passages, then the weakest passages. The best passage and the facts always stay. 0 means no limit. Lower it to make answers cheaper and faster; the default rarely cuts anything. |

### Web pages

| Setting | Default | What it does |
|---|---|---|
| **Refresh web pages after (hours)** | 24 | A page whose copy is older than this is downloaded again when the project opens, and checked every hour while it is open. A page that has not changed costs one short request and is not indexed again; a changed one is indexed again, with its overview and graph if those are on. 0 means only when you choose **Refresh**. |

### System prompt

The standing instructions sent with every question. The default tells the model to answer only from the numbered context, cite sources as **[n]**, and say when the context does not contain the answer. Add to it for tone or format (*"Answer in British English"*, *"Use bullet points"*), but keep those three rules, or answers stop being grounded in your documents.

Whatever the system prompt says, Silvia adds a fixed rule after it: the passages, overviews, graph and earlier conversations are data written by other people, and instructions inside them are not followed. See *Privacy → Text that tries to instruct the model*.

### Document overviews

The chat model writes a short overview of each document when it is indexed or changes. Answers then see the overviews of the cited documents next to the passages, which helps with questions about a whole document. Each document's full text is sent to DeepSeek once. **On** by default.

### Bridge searches

For questions that join two facts, the chat model reads the first passages and searches again for what they point to. This costs one short extra call per question. **On** by default.

### Knowledge graph

The chat model extracts the people, organisations, products and systems from every passage, and how they relate. Answers can then join facts that sit in different documents, and the **Graph** screen draws them. It costs about one call per passage when a file is indexed or changes, and needs the local embedder. **Off** by default. Turning it off deletes the graph.
`,
  },
  {
    id: "app-settings",
    title: "Global and appearance settings",
    summary: "Models, the DeepSeek key, where data lives, the tray icon, and how the app looks.",
    body: `
### Global

- **Data.** Where Silvia keeps its projects and where it keeps the local models. Each project is a single database file in its own folder. Only a few projects stay open at once; the others open when you use them.
- **Window.** Silvia shows its icon in the notification area; click it to bring the window back, or right-click it for **Open Silvia** and **Quit Silvia**. With **Keep running in the notification area when the window is closed**, closing the window hides it, and open projects keep watching their folders and refreshing their web pages until you choose **Quit Silvia**. Off by default: closing the window quits.
- **Models.** Downloads the local embedder and reranker from Hugging Face (about 230 MB). They are optional but recommended. See *Getting started*.
- **DeepSeek key.** Needed for written answers. Silvia checks a key with DeepSeek before saving it. The key is stored encrypted for your Windows account and is never shown again. **Verify key** checks it later; **Remove key** deletes it.

### Appearance

- **Theme**: light, dark, or the same as Windows.
- **Interface text size** and **Reading text size**. Reading size applies to answers and documents; **Ctrl +** and **Ctrl −** change it too.
- **Line height** and **Reading width**, for comfortable reading of long answers.
- **Fonts** for the interface, for reading and for code. Bundled fonts ship with Silvia; installed fonts come from Windows.

Appearance applies at once, and to every project.
`,
  },
  {
    id: "privacy",
    title: "Privacy: what leaves this computer",
    summary: "What each cloud policy sends to DeepSeek.",
    body: `
Indexing, search, embedding and reranking always run on this computer. Only the chat model is in the cloud.

| | **deepseek** | **search-only** |
|---|---|---|
| Your question, and the recent turns of the conversation | Sent | Never |
| The passages found for it, the facts, and any recalled exchange | Sent | Never |
| Each document's full text, once, to write its overview | Sent if *Document overviews* is on | Never |
| Every passage, once, to build the knowledge graph | Sent if *Knowledge graph* is on | Never |
| Search and the Search screen | Local | Local |

Choose **search-only** for a project whose documents must not leave this computer. You still get the matching passages for every question, with links to the files.

### Web pages

Adding or refreshing a web page is a request from this computer to that page's site, the same as opening it in a browser. It sends nothing from your project: no documents, questions or keys, and no cookies. The site sees your computer's address and that Silvia asked for the page. Once downloaded, a page is a document like any other, and the table above applies to it. Pages are downloaded again by themselves once their copy is older than *Refresh web pages after* (a day by default); set it to 0 to refresh only on demand.

### Text that tries to instruct the model

Documents and web pages are written by other people, and a page can hide text meant for an AI (*"ignore your instructions and…"*), sometimes in characters no reader sees. Before passages reach the chat model, Silvia:

- removes invisible characters (zero-width and direction marks, Unicode tag characters) from them;
- defuses text that imitates the structure of the prompt, such as chat-model control tokens or a line that looks like another numbered passage;
- labels a passage that addresses an AI assistant, so the model and you can see it;
- adds a fixed rule after your system prompt: the context is data, and instructions in it are not followed.

The passages you see under an answer are shown exactly as the document has them. No defence is perfect: treat an answer that seems to follow a document's orders rather than your question as a warning sign, and check its sources.

Your API key is stored encrypted for your Windows account. Projects are kept apart: no answer in one project sees the documents, facts or conversations of another.
`,
  },
  {
    id: "quality",
    title: "Checking answer quality",
    summary: "Check answers, build a test set, and compare settings.",
    body: `
### Check one answer

Under an answer, **Check this answer** asks the judge model to split the answer into statements and test each one against the cited passages. Statements no passage backs are highlighted. It also says whether the answer addresses the question at all.

### Build a test set

A test set is a list of questions you know the answers to, with the facts each answer must state.

- Under a good answer, choose **Save as test question**.
- Or add questions in **Evaluate**. Silvia can also draft questions from your documents for you to review.
- Include a few questions the documents do **not** answer, to check that Silvia says so instead of inventing an answer.

### Run it

In **Evaluate**, start a run. Each question is asked the way chat asks it, and the judge grades:

- **Questions passed**
- **Expected facts stated**: did the answer include what it should?
- **Statements backed**: is everything it said supported by the cited passages?

Run the set before and after you change a setting. The **Trend** chart shows each run, so you can see whether a change helped. Before a run starts, Silvia tells you how many DeepSeek calls it will make. Drafted questions stay out of runs until you accept them.
`,
  },
  {
    id: "troubleshooting",
    title: "Troubleshooting",
    summary: "When an answer is wrong, missing, or slow.",
    body: `
### "The documents do not contain the answer", but they do

1. Search for the fact on the **Search** screen. If it is not in the results, look at the *close calls*: the passage may be near but outranked.
2. Open the document in **Library** and look at its **passages**. If the fact is not in any passage, the parser did not extract it. This happens with scanned PDFs, text in images, and some complex layouts.
3. Rephrase the question with the words the document uses, or name the document.
4. Raise **Passages per answer**, or lower **Chunk size** for documents dense with facts.
5. If it is something the documents only imply, add it as a **fact**.

### The answer uses an old version of a document

Make sure the old file is really gone or renamed, or say in the question which edition you mean. When two versions disagree, Silvia prefers the one that says it is current, then the newer one. If a fact keeps being wrong, add the correct one as a project **fact**.

### A file is not found at all

Open **Activity**: failed files are listed with the reason. Check that the type is supported (see *Getting started*) and that the file is not open with exclusive access in another program. **Rescan** in Library indexes the folder again.

### A web page shows "Needs a browser" or "Failed"

- **Needs a browser**: the page showed almost no text when downloaded. It is probably built by script in the browser, or shows its content only after a sign-in. Silvia cannot read such pages. Save the page from your browser (**Save as**, or **Print → Save as PDF**) into a folder of the project instead.
- **Failed** with *"Silvia cannot read this type of page"*: the address leads to something other than a web page, text, PDF or Office file, such as an image or a video.
- **Failed** with an answer from the site (*404 Not Found*, *403 Forbidden*): the address is wrong, the page has moved, or the site refuses automated downloads. Check it in your browser.
- **Failed** with *"did not answer"* or *"could not be reached"*: the site is down, or this computer is offline. Silvia tries twice more by itself; choose **Refresh** later.

A page that fails on a refresh keeps its last good copy in the index. **Activity** lists failed pages by their address.

### Answers are slow or costly

- Lower **Passages per answer** or the **Context budget**.
- Turn off **Bridge searches** if your questions rarely join two facts.
- The **knowledge graph** costs the most to build; turn it on only for projects that need it.
- A smaller chat model is faster.

### No written answer, only passages

The project uses the **search-only** policy, or no DeepSeek key is saved, or DeepSeek cannot be reached. Check **Settings → Global → DeepSeek key → Verify key**.
`,
  },
  {
    id: "shortcuts",
    title: "Keyboard shortcuts",
    summary: "Faster ways around the app.",
    body: `
| Keys | Action |
|---|---|
| **F1** | Open this help |
| **Ctrl + P** | Switch project |
| **Ctrl +** / **Ctrl −** | Larger or smaller reading text |
| **Ctrl + 0** | Reading text back to its default size |
| **Ctrl + mouse wheel** over a conversation | Larger or smaller reading text |
| **Esc** | Close the project switcher |
`,
  },
];

/** The topics whose title, summary or text contain every word of the query. */
export function filterTopics(query: string): HelpTopic[] {
  const words = query.toLowerCase().split(/\s+/).filter((word) => word.length > 0);
  if (words.length === 0) {
    return [...HELP_TOPICS];
  }

  return HELP_TOPICS.filter((topic) => {
    const text = `${topic.title} ${topic.summary} ${topic.body}`.toLowerCase();
    return words.every((word) => text.includes(word));
  });
}
