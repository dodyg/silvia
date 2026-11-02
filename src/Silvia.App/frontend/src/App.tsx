import { useCallback, useEffect, useState } from "react";
import { describeError, projects, settings, type ProjectDto } from "./api";
import { applyAppearance, stepReadingSize, type Appearance } from "./theme";
import { Banner, Button } from "./ui";
import { ActivityScreen } from "./screens/ActivityScreen";
import { ChatScreen } from "./screens/ChatScreen";
import { LibraryScreen } from "./screens/LibraryScreen";
import { ProjectsScreen } from "./screens/ProjectsScreen";
import { SearchScreen } from "./screens/SearchScreen";
import { SettingsScreen } from "./screens/SettingsScreen";

type Screen = "chat" | "search" | "library" | "activity" | "projects" | "settings";

const SCREENS: Array<{ id: Screen; label: string }> = [
  { id: "chat", label: "Chat" },
  { id: "search", label: "Search" },
  { id: "library", label: "Library" },
  { id: "activity", label: "Activity" },
  { id: "projects", label: "Projects" },
  { id: "settings", label: "Settings" },
];

const PROJECT_KEY = "silvia.project.v1";

export function App() {
  const [appearance, setAppearance] = useState<Appearance | null>(null);
  const [projectList, setProjectList] = useState<ProjectDto[] | null>(null);
  const [projectId, setProjectId] = useState<string | null>(null);
  const [screen, setScreen] = useState<Screen>("chat");
  const [switcherOpen, setSwitcherOpen] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const refreshProjects = useCallback(async () => {
    const list = [...(await projects.list({}))];
    setProjectList(list);
    setProjectId((current) => {
      if (current && list.some((p) => p.id === current)) {
        return current;
      }

      const remembered = readRemembered();
      const next = list.find((p) => p.id === remembered) ?? list[0] ?? null;
      return next?.id ?? null;
    });
    return list;
  }, []);

  useEffect(() => {
    let cancelled = false;
    (async () => {
      try {
        const [saved] = await Promise.all([settings.getAppearance({}), refreshProjects()]);
        if (!cancelled) {
          setAppearance(saved as Appearance);
          applyAppearance(saved as Appearance);
        }
      } catch (failure) {
        if (!cancelled) {
          setError(describeError(failure));
          setProjectList([]);
        }
      }
    })();
    return () => {
      cancelled = true;
    };
  }, [refreshProjects]);

  useEffect(() => {
    if (projectId) {
      writeRemembered(projectId);
    }
  }, [projectId]);

  const changeAppearance = useCallback(async (next: Appearance) => {
    setAppearance(next);
    applyAppearance(next);
    try {
      const saved = await settings.updateAppearance(next);
      setAppearance(saved as Appearance);
      applyAppearance(saved as Appearance);
    } catch (failure) {
      setError(describeError(failure));
    }
  }, []);

  useEffect(() => {
    const onKey = (event: KeyboardEvent) => {
      if (!(event.ctrlKey || event.metaKey)) {
        return;
      }

      if (event.key.toLowerCase() === "p") {
        event.preventDefault();
        setSwitcherOpen((open) => !open);
        return;
      }

      // Zoom changes the answer text, not the window: the built-in page zoom stays off.
      const direction = event.key === "=" || event.key === "+" ? 1 : event.key === "-" ? -1 : event.key === "0" ? 0 : null;
      if (direction !== null && appearance) {
        event.preventDefault();
        void changeAppearance({ ...appearance, readingSize: stepReadingSize(appearance.readingSize, direction) });
      }
    };

    const onWheel = (event: WheelEvent) => {
      if ((event.ctrlKey || event.metaKey) && appearance && event.target instanceof Element && event.target.closest(".chat")) {
        event.preventDefault();
        const direction = event.deltaY < 0 ? 1 : -1;
        void changeAppearance({ ...appearance, readingSize: stepReadingSize(appearance.readingSize, direction) });
      }
    };

    window.addEventListener("keydown", onKey);
    window.addEventListener("wheel", onWheel, { passive: false });
    return () => {
      window.removeEventListener("keydown", onKey);
      window.removeEventListener("wheel", onWheel);
    };
  }, [appearance, changeAppearance]);

  const project = projectList?.find((p) => p.id === projectId) ?? null;

  if (projectList === null || appearance === null) {
    return (
      <div className="shell">
        <div className="main">
          <p className="muted" role="status">Starting Silvia…</p>
        </div>
      </div>
    );
  }

  const chooseProject = (id: string) => {
    setProjectId(id);
    setSwitcherOpen(false);
    setScreen("chat");
  };

  return (
    <div className="shell">
      <header className="topbar">
        <span className="brand">Silvia</span>
        <nav className="nav" aria-label="Sections">
          {SCREENS.map((item) => (
            <button
              key={item.id}
              type="button"
              aria-current={screen === item.id ? "page" : undefined}
              onClick={() => setScreen(item.id)}
              disabled={!project && item.id !== "projects" && item.id !== "settings"}
            >
              {item.label}
            </button>
          ))}
        </nav>
        <div className="project-switch">
          <Button onClick={() => setSwitcherOpen((open) => !open)} aria-haspopup="listbox" aria-expanded={switcherOpen}>
            {project ? project.name : "No project"} <span aria-hidden="true">▾</span>
            <span className="visually-hidden">(Ctrl+P)</span>
          </Button>
          {switcherOpen ? (
            <div className="popover" role="listbox" aria-label="Projects">
              {projectList.length === 0 ? <span className="muted">No projects yet.</span> : null}
              {projectList.map((p) => (
                <button key={p.id} type="button" role="option" aria-selected={p.id === projectId} aria-current={p.id === projectId} onClick={() => chooseProject(p.id)}>
                  {p.name}
                </button>
              ))}
              <button type="button" onClick={() => { setSwitcherOpen(false); setScreen("projects"); }}>Manage projects…</button>
            </div>
          ) : null}
        </div>
      </header>

      <main className="main">
        <div className="page">
          {error ? <Banner tone="error">{error}</Banner> : null}
          {screen === "projects" ? (
            <ProjectsScreen
              projects={projectList}
              selectedId={projectId}
              onChanged={refreshProjects}
              onOpen={chooseProject}
            />
          ) : screen === "settings" ? (
            <SettingsScreen
              projectId={projectId}
              appearance={appearance}
              onAppearance={changeAppearance}
              onError={setError}
            />
          ) : !project ? (
            <Banner tone="info">
              Create a project to start. Each project keeps its own folders, index and conversations.{" "}
              <Button variant="primary" onClick={() => setScreen("projects")}>Create a project</Button>
            </Banner>
          ) : screen === "chat" ? (
            <ChatScreen key={project.id} projectId={project.id} onError={setError} />
          ) : screen === "search" ? (
            <SearchScreen key={project.id} projectId={project.id} onError={setError} />
          ) : screen === "library" ? (
            <LibraryScreen key={project.id} projectId={project.id} onError={setError} />
          ) : (
            <ActivityScreen key={project.id} projectId={project.id} onError={setError} />
          )}
        </div>
      </main>
    </div>
  );
}

function readRemembered(): string | null {
  try {
    return localStorage.getItem(PROJECT_KEY);
  } catch {
    return null;
  }
}

function writeRemembered(id: string): void {
  try {
    localStorage.setItem(PROJECT_KEY, id);
  } catch {
    // The selection still works for this session without storage.
  }
}
