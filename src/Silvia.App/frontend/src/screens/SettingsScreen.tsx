import { useEffect, useState } from "react";
import { describeError, settings, type FontListDto, type GlobalSettingsDto, type ModelDto, type ProjectSettingsDto } from "../api";
import { INTERFACE_SIZE, READING_SIZE, SYSTEM_FONT, type Appearance, type LineHeightChoice, type ReadingWidthChoice, type ThemeChoice } from "../theme";
import { renderMarkdown } from "../markdown";
import { Banner, Button, Field, Section } from "../ui";

interface Props {
  projectId: string | null;
  appearance: Appearance;
  onAppearance: (next: Appearance) => void | Promise<void>;
  onError: (message: string | null) => void;
}

type Tab = "project" | "global" | "appearance";

export function SettingsScreen({ projectId, appearance, onAppearance, onError }: Props) {
  const [tab, setTab] = useState<Tab>("appearance");

  return (
    <div>
      <div className="tabs" role="tablist" aria-label="Settings">
        {(["project", "global", "appearance"] as const).map((id) => (
          <button key={id} type="button" role="tab" aria-selected={tab === id} onClick={() => setTab(id)}>
            {id === "project" ? "Project" : id === "global" ? "Global" : "Appearance"}
          </button>
        ))}
      </div>
      {tab === "project" ? (
        projectId ? <ProjectSettings projectId={projectId} onError={onError} /> : <Banner tone="info">Choose a project to change its settings.</Banner>
      ) : tab === "global" ? (
        <GlobalSettings onError={onError} />
      ) : (
        <AppearanceSettings appearance={appearance} onChange={onAppearance} onError={onError} />
      )}
    </div>
  );
}

function ProjectSettings({ projectId, onError }: { projectId: string; onError: (message: string | null) => void }) {
  const [current, setCurrent] = useState<ProjectSettingsDto | null>(null);
  const [draft, setDraft] = useState<ProjectSettingsDto | null>(null);
  const [models, setModels] = useState<ModelDto[]>([]);
  const [saved, setSaved] = useState<string | null>(null);

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
    // The screen is keyed by project, so this effect runs once per project.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  if (!draft || !current) {
    return <p className="muted">Loading the project's settings…</p>;
  }

  const embedders = models.filter((m) => m.kind === "embedder");
  const embedderChanged = draft.embedModel !== current.embedModel;

  function set<K extends keyof ProjectSettingsDto>(key: K, value: ProjectSettingsDto[K]) {
    setSaved(null);
    setDraft((state) => (state ? { ...state, [key]: value } : state));
  }

  async function save() {
    if (!draft) {
      return;
    }

    try {
      const next = await settings.updateProject({ projectId, settings: draft });
      setCurrent(next);
      setDraft(next);
      setSaved("Saved. The project reopens with these settings the next time it is used.");
      onError(null);
    } catch (failure) {
      onError(describeError(failure));
    }
  }

  return (
    <Section title="Project settings">
      {embedderChanged ? (
        <Banner tone="warn">
          Changing the embedder re-embeds every document the next time this project opens. Search keeps using the old vectors until that finishes.
        </Banner>
      ) : null}
      {saved ? <Banner tone="info">{saved}</Banner> : null}

      <Field label="Embedder" hint="Runs locally. Its vectors are only comparable within one embedder.">
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
      <Field label="Chat model" hint="Sent to DeepSeek when the cloud policy allows it.">
        <input type="text" value={draft.chatModel} onChange={(event) => set("chatModel", event.target.value)} />
      </Field>
      <Field label="Cloud policy" hint="search-only never calls a model. local-only is reserved until a local model is wired.">
        <select value={draft.cloudPolicy} onChange={(event) => set("cloudPolicy", event.target.value)}>
          <option value="deepseek">deepseek: answers with DeepSeek</option>
          <option value="local-only">local-only: no cloud model</option>
          <option value="search-only">search-only: passages only</option>
        </select>
      </Field>
      <div className="row">
        <NumberField label="Chunk size (tokens)" value={draft.chunkTokens} min={50} max={2000} onChange={(v) => set("chunkTokens", v)} />
        <NumberField label="Chunk overlap (tokens)" value={draft.chunkOverlap} min={0} max={500} onChange={(v) => set("chunkOverlap", v)} />
        <NumberField label="Passages per answer" value={draft.topK} min={1} max={30} onChange={(v) => set("topK", v)} />
      </div>
      <div className="row">
        <NumberField label="Earlier exchanges to recall" value={draft.memoryTopK} min={0} max={10} onChange={(v) => set("memoryTopK", v)} />
        <Field label="Recall threshold (percent of similarity)">
          <input type="number" step="5" min={0} max={100} value={draft.memoryMinPercent} onChange={(event) => set("memoryMinPercent", Number(event.target.value))} />
        </Field>
      </div>
      <Field label="System prompt">
        <textarea rows={4} value={draft.systemPrompt} onChange={(event) => set("systemPrompt", event.target.value)} />
      </Field>
      <div className="row">
        <Button variant="primary" onClick={() => void save()}>Save project settings</Button>
        <Button onClick={() => setDraft(current)} disabled={JSON.stringify(draft) === JSON.stringify(current)}>Discard changes</Button>
      </div>
    </Section>
  );
}

function NumberField({ label, value, min, max, onChange }: { label: string; value: number; min: number; max: number; onChange: (value: number) => void }) {
  return (
    <Field label={label}>
      <input type="number" min={min} max={max} value={value} onChange={(event) => onChange(Number(event.target.value))} />
    </Field>
  );
}

function GlobalSettings({ onError }: { onError: (message: string | null) => void }) {
  const [global, setGlobal] = useState<GlobalSettingsDto | null>(null);

  useEffect(() => {
    settings.getGlobal({}).then(setGlobal).catch((failure) => onError(describeError(failure)));
    // Loaded once when the tab opens.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  if (!global) {
    return <p className="muted">Loading…</p>;
  }

  return (
    <Section title="Global settings">
      <p>
        <strong>Data folder</strong>
        <br />
        <span className="path">{global.dataRoot}</span>
      </p>
      <p>
        <strong>Models folder</strong>
        <br />
        <span className="path">{global.modelsRoot}</span>
      </p>
      <p>
        <strong>DeepSeek key</strong>
        <br />
        {global.deepSeekKeyConfigured
          ? "Configured through the DEEPSEEK_API_KEY environment variable."
          : "Not configured. Set DEEPSEEK_API_KEY and restart Silvia to enable answers from DeepSeek."}
      </p>
      <p className="muted">Up to {global.maxOpenProjects} projects stay open at once; the others open on demand.</p>
    </Section>
  );
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
    const known = new Set([SYSTEM_FONT, ...(fonts?.bundled ?? []), ...(fonts?.system ?? [])]);
    const list = [SYSTEM_FONT, ...(fonts?.bundled ?? []), ...(fonts?.system ?? [])];
    return (
      <>
        {list.map((font) => (
          <option key={font} value={font} style={{ fontFamily: font === SYSTEM_FONT ? undefined : `"${font}"` }}>
            {font}
          </option>
        ))}
        {known.has(current) ? null : <option value={current}>{current} (not installed)</option>}
      </>
    );
  };

  return (
    <Section title="Appearance">
      <fieldset style={{ border: "none", padding: 0, margin: "0.5rem 0" }}>
        <legend className="field-label">Theme</legend>
        <div className="row">
          {(["system", "light", "dark"] as ThemeChoice[]).map((theme) => (
            <label key={theme} className="choice">
              <input type="radio" name="theme" checked={appearance.theme === theme} onChange={() => set("theme", theme)} />
              {theme === "system" ? "Follow Windows" : theme === "light" ? "Light" : "Dark"}
            </label>
          ))}
        </div>
      </fieldset>

      <div className="row">
        <Field label={`Interface text size: ${appearance.interfaceSize}px`}>
          <input type="range" min={INTERFACE_SIZE.min} max={INTERFACE_SIZE.max} step={INTERFACE_SIZE.step} value={appearance.interfaceSize} onChange={(event) => set("interfaceSize", Number(event.target.value))} />
        </Field>
        <Field label={`Reading text size: ${appearance.readingSize}px`} hint="Ctrl + and Ctrl − change this too.">
          <input type="range" min={READING_SIZE.min} max={READING_SIZE.max} step={READING_SIZE.step} value={appearance.readingSize} onChange={(event) => set("readingSize", Number(event.target.value))} />
        </Field>
      </div>

      <div className="row">
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

      <div className="row">
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
        <div className="sample" dangerouslySetInnerHTML={{ __html: renderMarkdown(SAMPLE) }} />
      </div>
    </Section>
  );
}

const SAMPLE = `The pilot launches in **Lisbon** and **Nairobi** on 14 March. Each kit carries a 40 watt charger [1].

Run \`silvia rescan\` after you move a folder [2].

שלום — مرحبا: a line of mixed direction text.`;
