// Stand-in for the generated RPC binding, used only by `vite --mode mock` to preview the screens in a
// plain browser. It mirrors the shapes of the generated module and keeps its data in memory.

export const neoRpcContractHash = "mock";

const now = new Date("2026-11-02T10:00:00Z").toISOString();
type ProjectRow = { id: string; name: string; description: string | null; createdUtc: string; lastOpenedUtc: string | null; status: string };
const projectsState: ProjectRow[] = [
  { id: "01MOCKLUMEN0000000000000A", name: "Lumen field notes", description: "Pilot launch material", createdUtc: now, lastOpenedUtc: now, status: "ready" },
  { id: "01MOCKSECURITY00000000000B", name: "Security policy", description: null, createdUtc: now, lastOpenedUtc: null, status: "ready" },
];

const sample = {
  sources: [{ id: "1", path: "C:/Documents/Lumen", includeGlobs: "**/*", excludeGlobs: "", enabled: true }],
  documents: [
    { documentId: "C:/Documents/Lumen/handbook.md", fileName: "handbook.md", folder: "C:/Documents/Lumen", chunks: 6 },
    { documentId: "C:/Documents/Lumen/travel.md", fileName: "travel.md", folder: "C:/Documents/Lumen", chunks: 2 },
  ],
  citation: {
    documentId: "C:/Documents/Lumen/handbook.md",
    chunkIndex: 0,
    fileName: "handbook.md",
    text: "The Lumen pilot launches on 14 March 2027 in two cities: Lisbon and Nairobi.",
    url: null,
  },
  webCitation: {
    documentId: "C:/Users/me/AppData/Local/Silvia/projects/01MOCKLUMEN0000000000000A/web/lumen-example-com-pilot-9f2c41ab.md",
    chunkIndex: 2,
    fileName: "Lumen pilot — public announcement",
    text: "Lumen's pilot opens to the public in Lisbon on 14 March 2027, with Nairobi following in April.",
    url: "https://lumen.example.com/pilot",
  },
  overview: {
    documentId: "C:/Documents/Lumen/handbook.md",
    chunkIndex: -1,
    fileName: "handbook.md",
    text: "The Lumen field handbook for the 2027 pilot.\nLisbon: first launch city.\nNairobi: second launch city.\nSections: launch plan, kit, support.",
    url: null,
  },
};

const conversations: Array<{ id: string; title: string; createdUtc: string }> = [
  { id: "01MOCKCHAT0000000000000001", title: "When does the Lumen pilot launch, and who owns the launch?", createdUtc: now },
  { id: "01MOCKCHAT0000000000000002", title: "Battery pack capacity", createdUtc: now },
];

function delay<T>(value: T): Promise<T> {
  return new Promise((resolve) => setTimeout(() => resolve(value), 60));
}

const fonts = { bundled: ["Inter", "Source Serif 4", "JetBrains Mono", "Noto Sans Arabic", "Noto Naskh Arabic"], system: ["Segoe UI", "Cascadia Mono", "Georgia"] };
let appearance = {
  theme: "system", interfaceSize: 14, readingSize: 16, lineHeight: "comfortable", readingWidth: "wide",
  interfaceFont: "System default", readingFont: "System default", monoFont: "System default",
};

function providerOk(message: string) {
  return { state: "ok", message, models: ["deepseek-flash", "deepseek-v4-pro"] };
}

export const projects = Object.freeze({
  list: () => delay(projectsState.slice()),
  create: (request: { name: string; description?: string }) => {
    const created = { id: `01MOCK${request.name.length}${projectsState.length}`, name: request.name, description: request.description ?? null, createdUtc: now, lastOpenedUtc: null, status: "ready" };
    projectsState.push(created);
    return delay(created);
  },
  rename: (request: { id: string; name: string }) => delay({ ...projectsState.find((p) => p.id === request.id)!, name: request.name }),
  delete: () => delay({}),
  duplicate: (request: { id: string; name: string }) => delay({ ...projectsState[0], id: `${request.id}-copy`, name: request.name }),
  export: () => delay({ path: "C:/Users/me/AppData/Local/Temp/export.silvia" }),
  import: (request: { name: string }) => delay({ ...projectsState[0], id: "01MOCKIMPORT", name: request.name }),
  open: (request: { id: string }) => delay(projectsState.find((p) => p.id === request.id)!),
});

let failures = [
  { id: "2", path: "C:/Documents/Lumen/scan.pdf", error: "The PDF has no text layer.", finishedUtc: now },
  { id: "1", path: "C:/Documents/Lumen/budget.xlsx", error: "Knowledge graph extraction of 'budget.xlsx' failed: No such host is known. (api.deepseek.com:443)", finishedUtc: now },
];

type MockWebPage = { id: string; url: string; finalUrl: string | null; title: string | null; documentId: string | null; fetchedUtc: string | null; status: string; error: string | null };
let webPages: MockWebPage[] = [
  { id: "1", url: "https://lumen.example.com/pilot", finalUrl: "https://lumen.example.com/pilot", title: "Lumen pilot — public announcement", documentId: sample.webCitation.documentId, fetchedUtc: now, status: "ok", error: null },
  { id: "2", url: "https://lumen.example.com/app", finalUrl: null, title: "Lumen dashboard", documentId: null, fetchedUtc: now, status: "needs-browser", error: "The page shows almost no text without a browser: it is probably built by script or behind a sign-in. It was not indexed." },
  { id: "3", url: "https://lumen.example.com/old-faq", finalUrl: null, title: null, documentId: null, fetchedUtc: now, status: "failed", error: "The site answered 404 Not Found." },
];

export const library = Object.freeze({
  listSources: () => delay(sample.sources),
  addSource: (request: { path: string }) => delay({ ...sample.sources[0], id: "2", path: request.path }),
  pickFolder: () => delay({ status: "picked", path: "C:/Documents/Picked" }),
  modelStatus: () => delay({ canEmbed: true, canRerank: true }),
  removeSource: () => delay({}),
  rescan: () => delay({}),
  resume: () => delay({}),
  listDocuments: () => delay([...sample.documents, { documentId: sample.webCitation.documentId, fileName: "lumen-example-com-pilot-9f2c41ab.md", folder: "C:/Users/me/AppData/Local/Silvia/projects/01MOCKLUMEN0000000000000A/web", chunks: 3 }]),
  activity: () => delay({ pending: 1, running: 0, done: 8, failed: failures.length, recentFailures: failures.slice() }),
  clearFailure: (request: { id: string }) => {
    failures = failures.filter((f) => f.id !== request.id);
    return delay({});
  },
  clearFailures: () => {
    failures = [];
    return delay({});
  },
  openDocument: () => delay({ opened: true, message: "Opened." }),
  revealDocument: () => delay({ opened: true, message: "Revealed." }),
  listWebPages: () => delay(webPages.slice()),
  addWebPages: (request: { urls: string }) => {
    const texts = request.urls.split(/\s+/).filter((t) => t.length > 0);
    const valid = texts.filter((t) => /^https?:\/\/\S+$/.test(t));
    const added = valid.map((url, i) => ({ id: String(webPages.length + i + 10), url, finalUrl: null, title: null, documentId: null, fetchedUtc: null, status: "pending", error: null }));
    webPages = [...webPages, ...added];
    return delay({ added, rejected: texts.filter((t) => !valid.includes(t)) });
  },
  refreshWebPage: () => delay({}),
  refreshWebPages: () => delay({}),
  removeWebPage: (request: { id: string }) => {
    webPages = webPages.filter((p) => p.id !== request.id);
    return delay({});
  },
  openUrl: () => delay({ opened: true, message: "Opened." }),
});

export const chat = Object.freeze({
  listConversations: () => delay(conversations.slice()),
  messages: () => delay([
    { role: "user", content: "When does the pilot launch?", createdUtc: now, sources: [] },
    { role: "assistant", content: "The pilot launches on 14 March 2027 in Lisbon and Nairobi [1].", createdUtc: now, sources: [sample.citation] },
  ]),
  deleteConversation: (request: { conversationId: string }) => {
    const index = conversations.findIndex((c) => c.id === request.conversationId);
    if (index >= 0) conversations.splice(index, 1);
    return delay({});
  },
  search: () => delay([{ documentId: sample.citation.documentId, chunkIndex: 0, fileName: "handbook.md", text: sample.citation.text, relevance: 810 }]),
  ask: async () => {
    const steps = [
      { kind: "conversation", conversationId: "01MOCKCONVERSATION", text: null, sources: null },
      { kind: "delta", conversationId: null, text: "The pilot launches on 14 March 2027 ", sources: null },
      { kind: "delta", conversationId: null, text: "in Lisbon and Nairobi [2].", sources: null },
      { kind: "sources", conversationId: null, text: null, sources: [sample.overview, sample.citation, sample.webCitation] },
      { kind: "done", conversationId: "01MOCKCONVERSATION", text: null, sources: null },
    ];
    return (async function* () {
      for (const step of steps) {
        await new Promise((resolve) => setTimeout(resolve, 180));
        yield step;
      }
    })();
  },
});

const passageTexts = [
  "The Lumen pilot launches on 14 March 2027 in two cities: Lisbon and Nairobi.",
  "Each field kit holds a solar panel, a 20 Ah battery pack, two lamps and a phone charger. Kits ship from the Porto warehouse six weeks before launch and are checked against the packing list on arrival.",
  "Support runs from 08:00 to 20:00 local time. Escalations go to the on-call engineer listed in the runbook.",
];

// A seeded spread of dots in three loose clusters, so the map looks the same on every load.
function mockPoints() {
  const files = ["handbook.md", "travel.md", "kit-list.md", "support.md", "budget.xlsx"];
  const centers = [[2500, 6500], [7000, 7500], [5200, 2500], [3000, 3000], [7500, 3500]];
  let seed = 7;
  const random = () => (seed = (seed * 16807) % 2147483647) / 2147483647;
  return files.flatMap((file, f) =>
    Array.from({ length: 6 + f * 4 }, (_, i) => ({
      documentId: `C:/Documents/Lumen/${file}`,
      fileName: file,
      chunkIndex: i,
      x: Math.round(Math.min(10000, Math.max(0, centers[f][0] + (random() - 0.5) * 3200))),
      y: Math.round(Math.min(10000, Math.max(0, centers[f][1] + (random() - 0.5) * 3200))),
      preview: passageTexts[i % passageTexts.length],
    })));
}

// A small fixed graph in two communities joined by one person, laid out by hand.
function mockGraph() {
  const documents = ["handbook.md", "kit-list.md", "support.md"].map((fileName) => ({ documentId: `C:/Documents/Lumen/${fileName}`, fileName }));
  const raw: Array<[string, string, string, number, number, number[], number[]]> = [
    ["Lumen pilot", "Project", "A solar lighting pilot launching in Lisbon and Nairobi in March 2027.", 3000, 5600, [0, 1], [0]],
    ["Ana Ribeiro", "Person", "Leads the Lumen pilot and signs off the launch plan.", 4300, 6900, [0], [0]],
    ["Lisbon", "Location", "One of the two pilot cities.", 1500, 7600, [0], [0]],
    ["Nairobi", "Location", "One of the two pilot cities.", 1200, 4300, [0], [0]],
    ["Porto warehouse", "Location", "Ships the field kits six weeks before launch.", 3400, 2800, [1], [0]],
    ["Field kit", "Product", "A solar panel, a battery pack, two lamps and a phone charger.", 1900, 1700, [1], [0]],
    ["Support desk", "Team", "Answers pilot users from 08:00 to 20:00 local time.", 7000, 6200, [2], [1]],
    ["On-call engineer", "Role", "Takes escalations from the support desk.", 8600, 7800, [2], [1]],
    ["Runbook", "Document", "Lists the on-call engineer and the escalation steps.", 8800, 4600, [2], [1]],
    ["Tomas Berg", "Person", "Runs the support desk and writes the runbook.", 6200, 3900, [0, 2], [1]],
  ];
  const ranks = [1000, 760, 420, 410, 380, 520, 690, 340, 450, 610];
  const nodes = raw.map(([name, type, description, x, y, docs, communities], i) => ({ name, type, description, rank: ranks[i], degree: 0, documents: docs, communities, x, y }));
  const edges = [
    [1, 0, "leads"], [0, 2, "runs in"], [0, 3, "runs in"], [4, 5, "ships"], [5, 0, "is used by"],
    [9, 6, "runs"], [6, 7, "escalates to"], [8, 7, "names"], [9, 8, "writes"], [9, 1, "reports to"],
  ].map(([source, target, description]) => ({ source: source as number, target: target as number, description: description as string }));
  edges.forEach((e) => { nodes[e.source].degree += 1; nodes[e.target].degree += 1; });
  const communities = [
    { id: 1, level: 0, report: "The Lumen pilot, led by Ana Ribeiro, brings solar field kits from the Porto warehouse to Lisbon and Nairobi.", members: 6 },
    { id: 2, level: 0, report: "Tomas Berg's support desk answers pilot users and escalates to the on-call engineer named in the runbook.", members: 4 },
  ];
  return { available: true, totalEntities: nodes.length, totalRelationships: edges.length, nodes, edges, communities, documents };
}

export const inspect = Object.freeze({
  document: (request: { documentId: string }) => delay({
    documentId: request.documentId,
    fileName: request.documentId.split("/").pop()!,
    folder: "C:/Documents/Lumen",
    overview: sample.overview.text,
    indexedUtc: now,
    passages: [...passageTexts, passageTexts[1].repeat(4)].map((text, chunkIndex) => ({
      chunkIndex,
      text,
      metadata: [{ key: "page", value: String(chunkIndex + 1) }, { key: "file_name", value: "handbook.md" }],
    })),
  }),
  trace: () => delay({
    mode: "vector",
    reranked: true,
    hits: [
      { documentId: sample.citation.documentId, chunkIndex: 0, fileName: "handbook.md", text: passageTexts[0], score: 912, position: 1, similarity: 781, vectorRank: 1 },
      { documentId: "C:/Documents/Lumen/kit-list.md", chunkIndex: 3, fileName: "kit-list.md", text: passageTexts[1], score: 604, position: 3, similarity: 512, vectorRank: 14 },
      { documentId: "C:/Documents/Lumen/support.md", chunkIndex: 1, fileName: "support.md", text: passageTexts[2], score: 455, position: 2, similarity: 301, vectorRank: null },
    ],
    closeCalls: [
      { documentId: "C:/Documents/Lumen/travel.md", chunkIndex: 0, fileName: "travel.md", text: passageTexts[2], score: 700, position: 0, similarity: 700, vectorRank: 2 },
    ],
  }),
  map: () => {
    const points = mockPoints();
    return delay({ available: true, total: points.length, firstShare: 214, secondShare: 131, points });
  },
  graph: () => delay(mockGraph()),
  coverage: () => delay({
    mode: "vector",
    embedModel: "bge-small-en-v1.5",
    overviewsOn: true,
    graph: { documents: 3, entities: 42, relationships: 51, communities: 7 },
    documents: [
      { documentId: "C:/Documents/Lumen/handbook.md", fileName: "handbook.md", passages: 6, characters: 9120, hasOverview: true, indexedUtc: now },
      { documentId: "C:/Documents/Lumen/travel.md", fileName: "travel.md", passages: 2, characters: 2410, hasOverview: true, indexedUtc: now },
      { documentId: "C:/Documents/Lumen/scan.pdf", fileName: "scan.pdf", passages: 1, characters: 42, hasOverview: false, indexedUtc: null },
    ],
  }),
});

const factRows:Array<{ id: string; text: string; createdUtc: string }> = [
  { id: "1", text: "The Lumen pilot is funded until the end of 2027.", createdUtc: now },
];

export const facts = Object.freeze({
  list: () => delay(factRows.slice()),
  add: (request: { text: string }) => {
    const fact = { id: String(factRows.length + 1), text: request.text.trim(), createdUtc: now };
    factRows.push(fact);
    return delay(fact);
  },
  update: (request: { id: string; text: string }) => {
    const fact = factRows.find((f) => f.id === request.id)!;
    fact.text = request.text.trim();
    return delay({ ...fact });
  },
  delete: (request: { id: string }) => {
    const index = factRows.findIndex((f) => f.id === request.id);
    if (index >= 0) factRows.splice(index, 1);
    return delay({});
  },
});

let mockCloseToTray = false;

export const settings = Object.freeze({
  getAppearance: () => delay({ ...appearance }),
  updateAppearance: (request: typeof appearance) => {
    appearance = { ...request };
    return delay({ ...appearance });
  },
  listFonts: () => delay(fonts),
  getProject: () => delay({ embedModel: "bge-small-en-v1.5", embedDims: 384, rerankModel: "ms-marco-MiniLM-L-6-v2", chatModel: "deepseek-chat", cloudPolicy: "deepseek", chunkTokens: 400, chunkOverlap: 60, topK: 6, memoryTopK: 3, memoryMinPercent: 70, systemPrompt: "Answer from the numbered context.", documentOverviews: true, graphRag: false, judgeModel: "", bridgeSearch: true, contextTokens: 12000, webRefreshHours: 24 }),
  updateProject: (request: { settings: unknown }) => delay(request.settings),
  listModels: () => delay([
    { id: "bge-small-en-v1.5", kind: "embedder", dimensions: 384, installed: true, default: true },
    { id: "ms-marco-MiniLM-L-6-v2", kind: "reranker", dimensions: 0, installed: false, default: true },
  ]),
  getGlobal: () => delay({ dataRoot: "C:/Users/me/AppData/Local/Silvia", modelsRoot: "C:/Users/me/AppData/Local/Silvia/models", deepSeekKeyConfigured: true, maxOpenProjects: 3, closeToTray: mockCloseToTray, trayAvailable: true }),
  setCloseToTray: ({ enabled }: { enabled: boolean }) => {
    mockCloseToTray = enabled;
    return delay({ dataRoot: "C:/Users/me/AppData/Local/Silvia", modelsRoot: "C:/Users/me/AppData/Local/Silvia/models", deepSeekKeyConfigured: true, maxOpenProjects: 3, closeToTray: mockCloseToTray, trayAvailable: true });
  },
  setDeepSeekKey: () => delay(providerOk("Saved. DeepSeek accepted the key and offers 2 chat models.")),
  verifyDeepSeekKey: () => delay(providerOk("The key works. DeepSeek offers 2 chat models.")),
  listChatModels: () => delay(providerOk("The key works. DeepSeek offers 2 chat models.")),
  removeDeepSeekKey: () => delay({}),
});

type MockQuestion = { id: string; question: string; facts: string[]; documentId: string | null; fileName: string | null; expectNoAnswer: boolean; origin: string; draft: boolean; createdUtc: string };
const evalQuestions: MockQuestion[] = [
  { id: "1", question: "When does the Lumen pilot launch?", facts: ["The pilot launches on 14 March 2027."], documentId: sample.citation.documentId, fileName: "handbook.md", expectNoAnswer: false, origin: "chat", draft: false, createdUtc: now },
  { id: "2", question: "Which cities does the pilot start in?", facts: ["Lisbon", "Nairobi"], documentId: sample.citation.documentId, fileName: "handbook.md", expectNoAnswer: false, origin: "manual", draft: false, createdUtc: now },
  { id: "3", question: "What is the pilot's marketing budget?", facts: [], documentId: null, fileName: null, expectNoAnswer: true, origin: "manual", draft: false, createdUtc: now },
];

const runSettings = (chunk: number) => [
  { name: "Chat model", value: "deepseek-flash" },
  { name: "Judge model", value: "deepseek-v4-pro" },
  { name: "Search", value: "Vectors and keywords (bge-small-en-v1.5)" },
  { name: "Reranker", value: "ms-marco-MiniLM-L-6-v2" },
  { name: "Passage size", value: `${chunk} tokens, 60 overlap` },
  { name: "Passages per answer", value: "6" },
];

const goodVerdict = {
  claims: [
    { text: "The pilot launches on 14 March 2027", supported: true, sources: [2], reason: "Passage 2 gives this date." },
    { text: "Kits ship from the Porto warehouse", supported: false, sources: [], reason: "No passage names a warehouse." },
  ],
  facts: [{ fact: "The pilot launches on 14 March 2027.", stated: true, reason: "The date is stated." }],
  answersQuestion: true,
  declines: false,
  summary: "Gives the right date, but adds a shipping detail the passages do not mention.",
};

function mockResult(runId: string, q: MockQuestion, passed: boolean) {
  return {
    questionId: q.id,
    question: q.question,
    facts: q.facts,
    expectNoAnswer: q.expectNoAnswer,
    answer: q.expectNoAnswer ? "The documents do not say what the marketing budget is." : "The pilot launches on 14 March 2027 [2]. Kits ship from the Porto warehouse.",
    sources: [sample.overview, sample.citation],
    verdict: q.expectNoAnswer
      ? { claims: [], facts: [], answersQuestion: true, declines: true, summary: "Says the documents do not hold the budget." }
      : { ...goodVerdict, facts: q.facts.map((fact) => ({ fact, stated: passed, reason: passed ? "Stated." : "Not mentioned." })) },
    foundDocument: q.documentId === null ? null : true,
    passed: q.expectNoAnswer || passed,
    error: null,
  };
}

type MockRun = { id: string; status: string; startedUtc: string; finishedUtc: string | null; total: number; settings: Array<{ name: string; value: string }>; error: string | null; results: ReturnType<typeof mockResult>[] };
const evalRuns: MockRun[] = [
  { id: "2", status: "done", startedUtc: "2026-11-02T09:00:00Z", finishedUtc: now, total: 3, settings: runSettings(400), error: null, results: evalQuestions.map((q) => mockResult("2", q, q.id !== "2")) },
  { id: "1", status: "done", startedUtc: "2026-11-01T09:00:00Z", finishedUtc: now, total: 3, settings: runSettings(200), error: null, results: evalQuestions.map((q) => mockResult("1", q, false)) },
];

function runDto(run: MockRun) {
  const verdicts = run.results.map((r) => r.verdict);
  return {
    id: run.id, status: run.status, startedUtc: run.startedUtc, finishedUtc: run.finishedUtc, total: run.total,
    completed: run.results.length,
    passed: run.results.filter((r) => r.passed).length,
    factsStated: verdicts.reduce((n, v) => n + v.facts.filter((f) => f.stated).length, 0),
    factsTotal: run.results.reduce((n, r) => n + r.facts.length, 0),
    claimsSupported: verdicts.reduce((n, v) => n + v.claims.filter((c) => c.supported).length, 0),
    claimsTotal: verdicts.reduce((n, v) => n + v.claims.length, 0),
    documentsFound: run.results.filter((r) => r.foundDocument === true).length,
    documentsExpected: run.results.filter((r) => r.foundDocument !== null).length,
    settings: run.settings, error: run.error,
  };
}

export const evaluation = Object.freeze({
  listQuestions: () => delay(evalQuestions.slice()),
  addQuestion: (request: { question: string; facts: string[]; documentId: string | null; expectNoAnswer: boolean; origin: string }) => {
    const q = { id: String(evalQuestions.length + 10), question: request.question.trim(), facts: request.facts, documentId: request.documentId, fileName: request.documentId?.split("/").pop() ?? null, expectNoAnswer: request.expectNoAnswer, origin: request.origin, draft: false, createdUtc: now };
    evalQuestions.push(q);
    return delay(q);
  },
  updateQuestion: (request: { id: string; question: string; facts: string[]; expectNoAnswer: boolean }) => {
    const q = evalQuestions.find((x) => x.id === request.id)!;
    Object.assign(q, { question: request.question, facts: request.facts, expectNoAnswer: request.expectNoAnswer });
    return delay({ ...q });
  },
  acceptQuestion: (request: { id: string }) => {
    const q = evalQuestions.find((x) => x.id === request.id)!;
    q.draft = false;
    return delay({ ...q });
  },
  deleteQuestion: (request: { id: string }) => {
    const index = evalQuestions.findIndex((x) => x.id === request.id);
    if (index >= 0) evalQuestions.splice(index, 1);
    return delay({});
  },
  draftQuestions: () => {
    const drafted = [
      { id: String(evalQuestions.length + 20), question: "How many lamps are in a field kit?", facts: ["Two lamps"], documentId: "C:/Documents/Lumen/kit-list.md", fileName: "kit-list.md", expectNoAnswer: false, origin: "drafted", draft: true, createdUtc: now },
    ];
    evalQuestions.push(...drafted);
    return delay(drafted);
  },
  checkAnswer: () => delay(goodVerdict),
  estimate: () => delay({ questions: evalQuestions.filter((q) => !q.draft).length, drafts: evalQuestions.filter((q) => q.draft).length, calls: evalQuestions.filter((q) => !q.draft).length * 2, chatModel: "deepseek-flash", judgeModel: "deepseek-v4-pro", mode: "vector", blocked: null }),
  startRun: () => {
    const questions = evalQuestions.filter((q) => !q.draft);
    const run: MockRun = { id: String(evalRuns.length + 1), status: "running", startedUtc: new Date().toISOString(), finishedUtc: null, total: questions.length, settings: runSettings(400), error: null, results: [] };
    evalRuns.unshift(run);
    questions.forEach((q, i) => setTimeout(() => {
      run.results.push(mockResult(run.id, q, true));
      if (run.results.length === questions.length) {
        run.status = "done";
        run.finishedUtc = new Date().toISOString();
      }
    }, 1200 * (i + 1)));
    return delay(runDto(run));
  },
  cancelRun: () => {
    const run = evalRuns.find((r) => r.status === "running");
    if (run) { run.status = "canceled"; run.error = "Stopped before every question was asked."; }
    return delay({});
  },
  listRuns: () => delay(evalRuns.map(runDto)),
  run: (request: { runId: string }) => {
    const run = evalRuns.find((r) => r.id === request.runId)!;
    return delay({ run: runDto(run), results: run.results.slice() });
  },
  deleteRun: (request: { runId: string }) => {
    const index = evalRuns.findIndex((r) => r.id === request.runId);
    if (index >= 0) evalRuns.splice(index, 1);
    return delay({});
  },
  compare: (request: { beforeId: string; afterId: string }) => {
    const before = evalRuns.find((r) => r.id === request.beforeId)!;
    const after = evalRuns.find((r) => r.id === request.afterId)!;
    const settingsChanged = after.settings
      .map((s) => ({ name: s.name, before: before.settings.find((b) => b.name === s.name)?.value ?? null, after: s.value }))
      .filter((c) => c.before !== c.after);
    const questions = after.results.map((r) => {
      const earlier = before.results.find((b) => b.questionId === r.questionId);
      const change = !earlier ? "added" : earlier.passed === r.passed ? "same" : r.passed ? "improved" : "regressed";
      return { questionId: r.questionId, question: r.question, before: earlier?.passed ?? null, after: r.passed, change };
    });
    return delay({ before: runDto(before), after: runDto(after), settings: settingsChanged, questions });
  },
});
