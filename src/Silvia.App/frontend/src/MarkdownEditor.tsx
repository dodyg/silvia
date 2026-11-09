import { useEffect, useRef } from "react";
import OverType, { toolbarButtons, type OverType as Editor, type Theme } from "overtype";
import "virtual:overtype.css";

// The stylesheet comes from the import above (see vite.config.ts): the app's content security
// policy blocks the <style> element OverType would otherwise add.
OverType.stylesInjected = true;

// OverType draws styled Markdown over a real textarea, so the value is exactly the text typed. Its
// colors come from the app's tokens through the --md-* aliases in app.css: OverType sets its own
// --text, --border and --selection on the editor, which would refer to themselves if they named
// the app's variables directly.
const theme: Theme = {
  name: "silvia",
  colors: {
    bgPrimary: "var(--surface)",
    bgSecondary: "var(--surface)",
    text: "var(--md-text)",
    textPrimary: "var(--md-text)",
    textSecondary: "var(--text-muted)",
    h1: "var(--md-text)",
    h2: "var(--md-text)",
    h3: "var(--md-text)",
    strong: "var(--md-text)",
    em: "var(--md-text)",
    del: "var(--text-muted)",
    link: "var(--citation)",
    code: "var(--md-text)",
    codeBg: "var(--md-code-bg)",
    blockquote: "var(--text-muted)",
    hr: "var(--text-muted)",
    syntaxMarker: "var(--text-muted)",
    syntax: "var(--text-muted)",
    listMarker: "var(--text-muted)",
    rawLine: "var(--text-muted)",
    cursor: "var(--focus)",
    selection: "var(--md-selection)",
    placeholder: "var(--text-muted)",
    primary: "var(--focus)",
    border: "var(--md-border)",
    hoverBg: "var(--surface-2)",
    toolbarBg: "var(--surface-2)",
    toolbarBorder: "var(--md-border)",
    toolbarIcon: "var(--text-muted)",
    toolbarHover: "var(--accent-soft)",
    toolbarActive: "var(--accent-soft)",
  },
};

const buttons = [
  toolbarButtons.bold,
  toolbarButtons.italic,
  toolbarButtons.code,
  toolbarButtons.separator,
  toolbarButtons.link,
  toolbarButtons.bulletList,
  toolbarButtons.orderedList,
];

interface Props {
  value: string;
  onChange: (value: string) => void;
  /** Ctrl+Enter (Cmd+Enter on a Mac). */
  onSubmit?: () => void;
  /** Escape. */
  onCancel?: () => void;
  placeholder?: string;
  maxLength?: number;
  label?: string;
  autoFocus?: boolean;
}

/** A Markdown editor for short text. The value stays plain Markdown; nothing is converted on save. */
export function MarkdownEditor({ value, onChange, onSubmit, onCancel, placeholder, maxLength, label, autoFocus }: Props) {
  const host = useRef<HTMLDivElement>(null);
  const editor = useRef<Editor | null>(null);
  // The editor is created once, so it reads the latest callbacks through this ref.
  const handlers = useRef({ onChange, onSubmit, onCancel });
  handlers.current = { onChange, onSubmit, onCancel };

  useEffect(() => {
    const [instance] = new OverType(host.current!, {
      value,
      placeholder,
      autofocus: autoFocus,
      autoResize: true,
      // OverType reads these with parseInt, so they must be in pixels.
      minHeight: "72px",
      maxHeight: "320px",
      fontFamily: "var(--font-mono)",
      fontSize: "var(--font-size-ui)",
      lineHeight: "var(--line-height)",
      padding: "0.5rem 0.65rem",
      spellcheck: true,
      toolbar: true,
      toolbarButtons: buttons,
      // A fact is a statement, not a document: headings, rules and task boxes stay plain text.
      disable: ["headings", "hr", "tasks"],
      theme,
      textareaProps: { maxLength, "aria-label": label },
      onChange: (next) => handlers.current.onChange(next),
      onKeydown: (event) => {
        if (event.key === "Enter" && (event.ctrlKey || event.metaKey) && handlers.current.onSubmit) {
          event.preventDefault();
          handlers.current.onSubmit();
        } else if (event.key === "Escape" && handlers.current.onCancel) {
          event.preventDefault();
          handlers.current.onCancel();
        }
      },
    });
    editor.current = instance;
    return () => {
      instance.destroy();
      editor.current = null;
    };
    // Options are read once; the value is kept in step by the effect below.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  useEffect(() => {
    if (editor.current && editor.current.getValue() !== value) {
      editor.current.setValue(value);
    }
  }, [value]);

  return <div ref={host} className="markdown-editor" />;
}
