import { useEffect, useRef, useState, type FormEvent } from "react";
import { describeError, evaluation, formatWhen, settings, type ChatProviderDto, type FontListDto, type GlobalSettingsDto, type ModelDto, type ProjectSettingsDto } from "../api";
import { INTERFACE_SIZE, READING_SIZE, SYSTEM_FONT, type Appearance, type LineHeightChoice, type ReadingWidthChoice, type ThemeChoice } from "../theme";
import { renderMarkdown } from "../markdown";
import { Banner, Button, Card, Field, PageHeader, Section } from "../ui";

interface Props {
  projectId: string | null;
  appearance: Appearance;
  onAppearance: (next: Appearance) => void | Promise<void>;
  onError: (message: string | null) => void;
  /** Opens the Evaluate screen, after a run was started from here. */
  onEvaluate: () => void;
}

type Tab = "project" | "global" | "appearance";

export function SettingsScreen({ projectId, appearance, onAppearance, onError, onEvaluate }: Props) {
  const [tab, setTab] = useState<Tab>("appearance");

  return (
    <div>
      <PageHeader title="Settings" description="How the open project searches and answers, what Silvia stores, and how it looks." />
      <div className="tabs" role="tablist" aria-label="Settings">
        {(["project", "global", "appearance"] as const).map((id) => (
          <button key={id} type="button" role="tab" aria-selected={tab === id} onClick={() => setTab(id)}>
            {id === "project" ? "Project" : id === "global" ? "Global" : "Appearance"}
          </button>
        ))}
      </div>
      {tab === "project" ? (
        projectId ? <ProjectSettings projectId={projectId} onError={onError} onEvaluate={onEvaluate} /> : <Banner tone="info">Choose a project to change its settings.</Banner>
      ) : tab === "global" ? (
        <GlobalSettings onError={onError} />
      ) : (
        <AppearanceSettings appearance={appearance} onChange={onAppearance} onError={onError} />
      )}
    </div>
  );
}

/** Settings that change what an answer says, so a test set run before and after them can differ. */
const ANSWER_SETTINGS: ReadonlyArray<keyof ProjectSettingsDto> = [
  "embedModel", "rerankModel", "chatModel", "judgeModel", "cloudPolicy", "chunkTokens", "chunkOverlap", "topK", "systemPrompt", "documentOverviews",
];

/** After a save that changes answers: how many test questions there are, and when the set last ran. */
interface CompareOffer {
  questions: number;
  lastRun: string | null;
}

function ProjectSettings({ projectId, onError, onEvaluate }: { projectId: string; onError: (message: string | null) => void; onEvaluate: () => void }) {
  const [current, setCurrent] = useState<ProjectSettingsDto | null>(null);
  const [draft, setDraft] = useState<ProjectSettingsDto | null>(null);
  const [models, setModels] = useState<ModelDto[]>([]);
  const [saved, setSaved] = useState<string | null>(null);
  const [provider, setProvider] = useState<ChatProviderDto | null>(null);
  const [offer, setOffer] = useState<CompareOffer | null>(null);

  useEffect(() => {
    void (async () => {
      try {
        const [loaded, known] = await Promise.all([settings.getProject({ projectId }), settings.listModels({})]);
        setCurrent(loaded);
        setDraft(loaded);
        setModels([...known]);
      } catch (failure) {
        onError(describeError(failure));
      }
    })();
    // The provider's model list loads on its own; the form works without it.
    settings.listChatModels({}).then(setProvider).catch(() => setProvider(null));
    // The screen is keyed by project, so this effect runs once per project.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  if (!draft || !current) {
    return <p className="muted">Loading the project's settings…</p>;
  }

  const embedders = models.filter((m) => m.kind === "embedder");
  const embedderChanged = draft.embedModel !== current.embedModel;
  // With a working key the choice is limited to what DeepSeek offers; otherwise the name is typed.
  const offered = provider?.state === "ok" ? provider.models : null;
  const usesCloud = draft.cloudPolicy === "deepseek";
  const modelUnavailable = usesCloud && offered !== null && !offered.includes(draft.chatModel);

  function set<K extends keyof ProjectSettingsDto>(key: K, value: ProjectSettingsDto[K]) {
    setSaved(null);
    setOffer(null);
    setDraft((state) => (state ? { ...state, [key]: value } : state));
  }

  async function save() {
    if (!draft) {
      return;
    }

    try {
      const before = current;
      const next = await settings.updateProject({ projectId, settings: draft });
      setCurrent(next);
      setDraft(next);
      setSaved("Saved. The project reopens with these settings the next time it is used.");
      onError(null);
      if (before && ANSWER_SETTINGS.some((key) => before[key] !== next[key])) {
        void offerCompare();
      }
    } catch (failure) {
      onError(describeError(failure));
    }
  }

  async function offerCompare() {
    try {
      const [questions, runs] = await Promise.all([evaluation.listQuestions({ projectId }), evaluation.listRuns({ projectId })]);
      const count = questions.filter((q) => !q.draft).length;
      const last = runs.find((r) => r.status !== "running" && r.completed > 0);
      setOffer(count > 0 ? { questions: count, lastRun: last?.startedUtc ?? null } : null);
    } catch {
      // The offer is a convenience; the settings are saved either way.
      setOffer(null);
    }
  }

  async function runTestSet() {
    try {
      await evaluation.startRun({ projectId });
      onError(null);
      onEvaluate();
    } catch (failure) {
      onError(describeError(failure));
    }
  }

  return (
    <div>
      {offer ? (
        <Banner tone="info">
          {offer.lastRun
            ? `Your ${offer.questions} test ${offer.questions === 1 ? "question" : "questions"} last ran on ${formatWhen(offer.lastRun)}, before this change. Run them again to see what it did.`
            : `Run your ${offer.questions} test ${offer.questions === 1 ? "question" : "questions"} now, so the next change has a run to compare with.`}
          <div className="row banner-actions">
            <Button size="sm" variant="primary" onClick={() => void runTestSet()}>Run test set</Button>
            <Button size="sm" variant="quiet" onClick={onEvaluate}>Open Evaluate</Button>
          </div>
        </Banner>
      ) : null}
      {embedderChanged ? (
        <Banner tone="warn">
          Changing the embedder re-embeds every document the next time this project opens. Search keeps using the old vectors until that finishes.
        </Banner>
      ) : null}
      {saved ? <Banner tone="info">{saved}</Banner> : null}
      {modelUnavailable ? (
        <Banner tone="warn">
          DeepSeek does not offer the chat model "{draft.chatModel}" to your key, so answers may fail. Choose one of the models it offers below.
        </Banner>
      ) : null}

      <Section title="Models" description="Embedding and reranking run on this computer; only answers may use the cloud.">
        <Card>
          <div className="form-grid form-grid-wide">
            <Field label="Embedder" hint="Its vectors are only comparable within one embedder.">
              <select value={draft.embedModel} onChange={(event) => set("embedModel", event.target.value)}>
                {embedders.map((m) => (
                  <option key={m.id} value={m.id} disabled={!m.installed}>
                    {m.id} ({m.dimensions} dimensions){m.installed ? "" : " — model files not found"}
                  </option>
                ))}
              </select>
            </Field>
            <Field label="Reranker" hint="Reorders the retrieved passages with a local cross-encoder.">
              <input type="text" value={draft.rerankModel} onChange={(event) => set("rerankModel", event.target.value)} />
            </Field>
            <Field label="Cloud policy" hint="search-only never calls a model. local-only is reserved until a local model is wired.">
              <select value={draft.cloudPolicy} onChange={(event) => set("cloudPolicy", event.target.value)}>
                <option value="deepseek">deepseek: answers with DeepSeek</option>
                <option value="local-only">local-only: no cloud model</option>
                <option value="search-only">search-only: passages only</option>
              </select>
            </Field>
            <Field label="Chat model" hint={chatModelHint(provider)}>
              {offered ? (
                <select value={draft.chatModel} onChange={(event) => set("chatModel", event.target.value)}>
                  {offered.includes(draft.chatModel) ? null : <option value={draft.chatModel}>{draft.chatModel} (not offered by DeepSeek)</option>}
                  {offered.map((model) => (
                    <option key={model} value={model}>{model}</option>
                  ))}
                </select>
              ) : (
                <input type="text" value={draft.chatModel} onChange={(event) => set("chatModel", event.target.value)} />
              )}
            </Field>
            <Field label="Judge model" hint="Grades answers in Evaluate. A model other than the chat model gives a more independent grade.">
              {offered ? (
                <select value={draft.judgeModel ?? ""} onChange={(event) => set("judgeModel", event.target.value)}>
                  <option value="">Same as the chat model</option>
                  {draft.judgeModel && !offered.includes(draft.judgeModel) ? <option value={draft.judgeModel}>{draft.judgeModel} (not offered by DeepSeek)</option> : null}
                  {offered.map((model) => (
                    <option key={model} value={model}>{model}</option>
                  ))}
                </select>
              ) : (
                <input type="text" value={draft.judgeModel ?? ""} placeholder="Same as the chat model" onChange={(event) => set("judgeModel", event.target.value)} />
              )}
            </Field>
          </div>
        </Card>
      </Section>

      <Section title="Retrieval" description="How documents are split, and how much context each answer gets.">
        <Card>
          <div className="form-grid">
            <NumberField label="Chunk size (tokens)" value={draft.chunkTokens} min={50} max={2000} onChange={(v) => set("chunkTokens", v)} />
            <NumberField label="Chunk overlap (tokens)" value={draft.chunkOverlap} min={0} max={500} onChange={(v) => set("chunkOverlap", v)} />
            <NumberField label="Passages per answer" value={draft.topK} min={1} max={30} onChange={(v) => set("topK", v)} />
            <NumberField label="Earlier exchanges to recall" value={draft.memoryTopK} min={0} max={10} onChange={(v) => set("memoryTopK", v)} />
            <Field label="Recall threshold (% similarity)">
              <input type="number" step="5" min={0} max={100} value={draft.memoryMinPercent} onChange={(event) => set("memoryMinPercent", Number(event.target.value))} />
            </Field>
          </div>
        </Card>
      </Section>

      <Section title="System prompt" description="The standing instructions sent with every question.">
        <Card>
          <Field label="System prompt">
            <textarea rows={4} value={draft.systemPrompt} onChange={(event) => set("systemPrompt", event.target.value)} />
          </Field>
        </Card>
      </Section>

      <Section title="Document overviews" description="An outline of each document, so questions about a whole document, such as listing everything in it, are not limited to a few passages.">
        <Card>
          <label className="check">
            <input
              type="checkbox"
              checked={draft.documentOverviews}
              disabled={!usesCloud}
              onChange={(event) => set("documentOverviews", event.target.checked)}
            />
            <span>
              Write an overview of each document with the chat model
              <span className="field-hint">
                {usesCloud
                  ? "Each document's text is sent to DeepSeek once when it is indexed or changes, not only the passages an answer uses."
                  : "Needs the deepseek cloud policy."}
              </span>
            </span>
          </label>
        </Card>
      </Section>

      <div className="form-actions">
        <Button variant="primary" onClick={() => void save()}>Save project settings</Button>
        <Button onClick={() => setDraft(current)} disabled={JSON.stringify(draft) === JSON.stringify(current)}>Discard changes</Button>
      </div>
    </div>
  );
}

/** What the chat model field can promise, from the last word DeepSeek gave about the key. */
function chatModelHint(provider: ChatProviderDto | null): string {
  switch (provider?.state) {
    case "ok":
      return "The models DeepSeek offers to your key.";
    case "no_key":
      return "Save a DeepSeek key in Global to choose from the models it offers.";
    case "rejected":
      return "DeepSeek rejected the saved key; fix it in Global to list its models.";
    case undefined:
      return "Checking which models DeepSeek offers…";
    default:
      return "DeepSeek could not be asked which models it offers, so type the model name.";
  }
}

function NumberField({ label, value, min, max, onChange }: { label: string; value: number; min: number; max: number; onChange: (value: number) => void }) {
  return (
    <Field label={label}>
      <input type="number" min={min} max={max} value={value} onChange={(event) => onChange(Number(event.target.value))} />
    </Field>
  );
}

interface DownloadView {
  state: string;
  file: string | null;
  percent: number;
  message: string | null;
}

function GlobalSettings({ onError }: { onError: (message: string | null) => void }) {
  const [global, setGlobal] = useState<GlobalSettingsDto | null>(null);
  const [models, setModels] = useState<ModelDto[]>([]);
  const [keyDraft, setKeyDraft] = useState("");
  const [download, setDownload] = useState<DownloadView>({ state: "idle", file: null, percent: 0, message: null });
  const [notice, setNotice] = useState<string | null>(null);
  const [check, setCheck] = useState<ChatProviderDto | null>(null);
  const [checking, setChecking] = useState(false);
  const timer = useRef<number | null>(null);

  async function reload() {
    try {
      const [loaded, known] = await Promise.all([settings.getGlobal({}), settings.listModels({})]);
      setGlobal(loaded);
      setModels([...known]);
    } catch (failure) {
      onError(describeError(failure));
    }
  }

  useEffect(() => {
    void reload();
    return () => {
      if (timer.current !== null) {
        window.clearInterval(timer.current);
      }
    };
    // Loaded once when the tab opens.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  function watchDownload() {
    if (timer.current !== null) {
      return;
    }

    timer.current = window.setInterval(async () => {
      try {
        const next = await settings.modelDownloadStatus({});
        setDownload({ state: next.state, file: next.file ?? null, percent: next.percent, message: next.message ?? null });
        if (next.state !== "running" && timer.current !== null) {
          window.clearInterval(timer.current);
          timer.current = null;
          await reload();
        }
      } catch (failure) {
        onError(describeError(failure));
      }
    }, 1000);
  }

  async function startDownload() {
    onError(null);
    try {
      const next = await settings.startModelDownload({});
      setDownload({ state: next.state, file: next.file ?? null, percent: next.percent, message: next.message ?? null });
      watchDownload();
    } catch (failure) {
      onError(describeError(failure));
    }
  }

  // A new key is checked with DeepSeek before it is saved; a rejected one stays unsaved and is reported here.
  async function saveKey(event: FormEvent) {
    event.preventDefault();
    setNotice(null);
    setChecking(true);
    try {
      setCheck(await settings.setDeepSeekKey({ key: keyDraft.trim() }));
      setKeyDraft("");
      await reload();
    } catch (failure) {
      setCheck({ state: "rejected", message: describeError(failure), models: [] });
    } finally {
      setChecking(false);
    }
  }

  async function verifyKey() {
    setNotice(null);
    setChecking(true);
    try {
      setCheck(await settings.verifyDeepSeekKey({}));
    } catch (failure) {
      onError(describeError(failure));
    } finally {
      setChecking(false);
    }
  }

  async function removeKey() {
    setNotice(null);
    setCheck(null);
    try {
      await settings.removeDeepSeekKey({});
      setNotice("The key was removed.");
      await reload();
    } catch (failure) {
      onError(describeError(failure));
    }
  }

  if (!global) {
    return <p className="muted">Loading…</p>;
  }

  const missing = models.filter((m) => !m.installed);

  return (
    <div>
      <Section title="Data" description={`Up to ${global.maxOpenProjects} projects stay open at once; the others open on demand.`}>
        <Card>
          <div className="info-row">
            <span className="field-label">Data folder</span>
            <span className="path">{global.dataRoot}</span>
          </div>
          <div className="info-row">
            <span className="field-label">Models folder</span>
            <span className="path">{global.modelsRoot}</span>
          </div>
        </Card>
      </Section>

      <Section title="Models" description="About 230 MB, from Hugging Face. Files that are already present are skipped.">
        <Card>
          {missing.length === 0 ? (
            <Banner tone="info">The embedder and the reranker are installed.</Banner>
          ) : (
            <Banner tone="warn">
              {missing.map((m) => m.id).join(", ")} not installed. They are optional: without them Silvia searches by keyword and answers with DeepSeek. With them, search also finds passages by meaning and ranks them better.
            </Banner>
          )}
          {download.state === "running" ? (
            <div role="status" aria-live="polite" className="small muted">
              Downloading {download.file ?? "the models"}… {download.percent}%
              <div className="progress"><span style={{ width: `${download.percent}%` }} /></div>
            </div>
          ) : null}
          {download.state === "done" || download.state === "failed" ? (
            <Banner tone={download.state === "done" ? "info" : "error"}>{download.message}</Banner>
          ) : null}
          <Button onClick={() => void startDownload()} disabled={download.state === "running" || missing.length === 0}>
            Download the models
          </Button>
        </Card>
      </Section>

      <Section
        title="DeepSeek key"
        description={global.deepSeekKeyConfigured
          ? "A key is configured. It is stored encrypted for this Windows account and is never shown again."
          : "No key is configured. Without one, projects answer with passages only."}
        actions={<KeyBadge configured={global.deepSeekKeyConfigured} check={check} />}
      >
        {notice ? <Banner tone="info">{notice}</Banner> : null}
        <Card>
          <form className="inline-form" onSubmit={saveKey}>
            <Field label="API key">
              <input type="password" autoComplete="off" value={keyDraft} onChange={(event) => setKeyDraft(event.target.value)} placeholder="sk-…" />
            </Field>
            <Button type="submit" variant="primary" disabled={checking || keyDraft.trim().length < 8}>
              {checking && keyDraft.trim().length >= 8 ? "Checking…" : "Save key"}
            </Button>
            {global.deepSeekKeyConfigured ? (
              <>
                <Button onClick={() => void verifyKey()} disabled={checking}>
                  {checking && keyDraft.trim().length < 8 ? "Checking…" : "Verify key"}
                </Button>
                <Button variant="danger" onClick={() => void removeKey()}>Remove key</Button>
              </>
            ) : null}
          </form>
          {check ? (
            <div className="key-check">
              <Banner tone={check.state === "ok" ? "info" : check.state === "rejected" ? "error" : "warn"}>{check.message}</Banner>
              {check.state === "ok" && check.models.length > 0 ? (
                <div className="row">
                  <span className="muted small">Chat models:</span>
                  {check.models.map((model) => (
                    <span key={model} className="badge">{model}</span>
                  ))}
                </div>
              ) : null}
            </div>
          ) : null}
        </Card>
      </Section>
    </div>
  );
}

/** The key's state: verified or rejected once checked in this visit, otherwise whether one is saved. */
function KeyBadge({ configured, check }: { configured: boolean; check: ChatProviderDto | null }) {
  if (check?.state === "ok") {
    return <span className="badge badge-ready">Verified</span>;
  }

  if (check?.state === "rejected") {
    return <span className="badge badge-danger">Rejected</span>;
  }

  return configured ? <span className="badge badge-accent">Configured</span> : <span className="badge">Not set</span>;
}

function AppearanceSettings({
  appearance,
  onChange,
  onError,
}: {
  appearance: Appearance;
  onChange: (next: Appearance) => void | Promise<void>;
  onError: (message: string | null) => void;
}) {
  const [fonts, setFonts] = useState<FontListDto | null>(null);

  useEffect(() => {
    settings.listFonts({}).then(setFonts).catch((failure) => onError(describeError(failure)));
    // Font installs change rarely; the list is read when this tab opens.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  const set = <K extends keyof Appearance>(key: K, value: Appearance[K]) => void onChange({ ...appearance, [key]: value });
  const fontOptions = (current: string) => {
    const list = [SYSTEM_FONT, ...(fonts?.bundled ?? []), ...(fonts?.system ?? [])];
    return (
      <>
        {list.map((font) => (
          <option key={font} value={font}>
            {font}
          </option>
        ))}
        {list.includes(current) ? null : <option value={current}>{current} (not installed)</option>}
      </>
    );
  };

  return (
    <div>
      <Section title="Theme and text" description="Changes apply at once and are saved for every project.">
        <Card>
          <fieldset className="fieldset">
            <legend className="field-label">Theme</legend>
            <div className="segmented">
              {(["system", "light", "dark"] as ThemeChoice[]).map((theme) => (
                <label key={theme}>
                  <input type="radio" name="theme" checked={appearance.theme === theme} onChange={() => set("theme", theme)} />
                  {theme === "system" ? "Follow Windows" : theme === "light" ? "Light" : "Dark"}
                </label>
              ))}
            </div>
          </fieldset>

          <div className="form-grid">
            <Field label={`Interface text size: ${appearance.interfaceSize}px`}>
              <input type="range" min={INTERFACE_SIZE.min} max={INTERFACE_SIZE.max} step={INTERFACE_SIZE.step} value={appearance.interfaceSize} onChange={(event) => set("interfaceSize", Number(event.target.value))} />
            </Field>
            <Field label={`Reading text size: ${appearance.readingSize}px`} hint="Ctrl + and Ctrl − change this too.">
              <input type="range" min={READING_SIZE.min} max={READING_SIZE.max} step={READING_SIZE.step} value={appearance.readingSize} onChange={(event) => set("readingSize", Number(event.target.value))} />
            </Field>
          </div>

          <div className="form-grid">
            <Field label="Line height">
              <select value={appearance.lineHeight} onChange={(event) => set("lineHeight", event.target.value as LineHeightChoice)}>
                <option value="compact">Compact</option>
                <option value="comfortable">Comfortable</option>
                <option value="spacious">Spacious</option>
              </select>
            </Field>
            <Field label="Reading width">
              <select value={appearance.readingWidth} onChange={(event) => set("readingWidth", event.target.value as ReadingWidthChoice)}>
                <option value="narrow">Narrow</option>
                <option value="wide">Wide</option>
                <option value="full">Full</option>
              </select>
            </Field>
          </div>
        </Card>
      </Section>

      <Section title="Fonts" description="Bundled fonts ship with Silvia; installed fonts come from Windows.">
        <Card>
          <div className="form-grid">
            <Field label="Interface font">
              <select value={appearance.interfaceFont} onChange={(event) => set("interfaceFont", event.target.value)}>{fontOptions(appearance.interfaceFont)}</select>
            </Field>
            <Field label="Reading font">
              <select value={appearance.readingFont} onChange={(event) => set("readingFont", event.target.value)}>{fontOptions(appearance.readingFont)}</select>
            </Field>
            <Field label="Monospace font">
              <select value={appearance.monoFont} onChange={(event) => set("monoFont", event.target.value)}>{fontOptions(appearance.monoFont)}</select>
            </Field>
          </div>

          <div className="preview" aria-label="Preview">
            <p className="preview-label">Preview</p>
            <div className="sample" dangerouslySetInnerHTML={{ __html: renderMarkdown(SAMPLE) }} />
          </div>
        </Card>
      </Section>
    </div>
  );
}

const SAMPLE = `The pilot launches in **Lisbon** and **Nairobi** on 14 March. Each kit carries a 40 watt charger [1].

Run \`silvia rescan\` after you move a folder [2].

שלום — مرحبا: a line of mixed direction text.`;
