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
      { kind: "delta", conversationId: null, text: "in Lisbon and Nairobi [1].", sources: null },
      { kind: "sources", conversationId: null, text: null, sources: [sample.citation] },
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

export const settings = Object.freeze({
  getAppearance: () => delay({ ...appearance }),
  updateAppearance: (request: typeof appearance) => {
    appearance = { ...request };
    return delay({ ...appearance });
  },
  listFonts: () => delay(fonts),
  getProject: () => delay({ embedModel: "bge-small-en-v1.5", embedDims: 384, rerankModel: "ms-marco-MiniLM-L-6-v2", chatModel: "deepseek-chat", cloudPolicy: "deepseek", chunkTokens: 400, chunkOverlap: 60, topK: 6, memoryTopK: 3, memoryMinPercent: 70, systemPrompt: "Answer from the numbered context." }),
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
