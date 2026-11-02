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
  },
  overview: {
    documentId: "C:/Documents/Lumen/handbook.md",
    chunkIndex: -1,
    fileName: "handbook.md",
    text: "The Lumen field handbook for the 2027 pilot.\nLisbon: first launch city.\nNairobi: second launch city.\nSections: launch plan, kit, support.",
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

export const library = Object.freeze({
  listSources: () => delay(sample.sources),
  addSource: (request: { path: string }) => delay({ ...sample.sources[0], id: "2", path: request.path }),
  pickFolder: () => delay({ status: "picked", path: "C:/Documents/Picked" }),
  modelStatus: () => delay({ canEmbed: true, canRerank: true }),
  removeSource: () => delay({}),
  rescan: () => delay({}),
  listDocuments: () => delay(sample.documents),
  activity: () => delay({ pending: 1, running: 0, done: 8, failed: 1, recentFailures: [{ path: "C:/Documents/Lumen/scan.pdf", error: "The PDF has no text layer.", finishedUtc: now }] }),
  openDocument: () => delay({ opened: true, message: "Opened." }),
  revealDocument: () => delay({ opened: true, message: "Revealed." }),
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
      { kind: "sources", conversationId: null, text: null, sources: [sample.overview, sample.citation] },
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
  coverage: () => delay({
    mode: "vector",
    embedModel: "bge-small-en-v1.5",
    overviewsOn: true,
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

export const settings = Object.freeze({
  getAppearance: () => delay({ ...appearance }),
  updateAppearance: (request: typeof appearance) => {
    appearance = { ...request };
    return delay({ ...appearance });
  },
  listFonts: () => delay(fonts),
  getProject: () => delay({ embedModel: "bge-small-en-v1.5", embedDims: 384, rerankModel: "ms-marco-MiniLM-L-6-v2", chatModel: "deepseek-chat", cloudPolicy: "deepseek", chunkTokens: 400, chunkOverlap: 60, topK: 6, memoryTopK: 3, memoryMinPercent: 70, systemPrompt: "Answer from the numbered context.", documentOverviews: true }),
  updateProject: (request: { settings: unknown }) => delay(request.settings),
  listModels: () => delay([
    { id: "bge-small-en-v1.5", kind: "embedder", dimensions: 384, installed: true, default: true },
    { id: "ms-marco-MiniLM-L-6-v2", kind: "reranker", dimensions: 0, installed: false, default: true },
  ]),
  getGlobal: () => delay({ dataRoot: "C:/Users/me/AppData/Local/Silvia", modelsRoot: "C:/Users/me/AppData/Local/Silvia/models", deepSeekKeyConfigured: true, maxOpenProjects: 3 }),
  setDeepSeekKey: () => delay(providerOk("Saved. DeepSeek accepted the key and offers 2 chat models.")),
  verifyDeepSeekKey: () => delay(providerOk("The key works. DeepSeek offers 2 chat models.")),
  listChatModels: () => delay(providerOk("The key works. DeepSeek offers 2 chat models.")),
  removeDeepSeekKey: () => delay({}),
});
