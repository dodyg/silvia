import { useCallback, useEffect, useState } from "react";
import { describeError, projects, settings, type ProjectDto } from "./api";
import { applyAppearance, stepReadingSize, type Appearance } from "./theme";
import { Banner, Button, Empty, Icon, Spinner, type IconName } from "./ui";
import { ActivityScreen } from "./screens/ActivityScreen";
import { ChatScreen } from "./screens/ChatScreen";
import { LibraryScreen } from "./screens/LibraryScreen";
import { ProjectsScreen } from "./screens/ProjectsScreen";
import { SearchScreen } from "./screens/SearchScreen";
import { SettingsScreen } from "./screens/SettingsScreen";

type Screen = "chat" | "search" | "library" | "activity" | "projects" | "settings";

// Project screens need an open project; the app screens below them work without one.
const PROJECT_SCREENS: Array<{ id: Screen; label: string; icon: IconName }> = [
  { id: "chat", label: "Chat", icon: "chat" },
  { id: "search", label: "Search", icon: "search" },
  { id: "library", label: "Library", icon: "library" },
  { id: "activity", label: "Activity", icon: "activity" },
];

const APP_SCREENS: Array<{ id: Screen; label: string; icon: IconName }> = [
  { id: "projects", label: "Projects", icon: "projects" },
  { id: "settings", label: "Settings", icon: "settings" },
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
      if (event.key === "Escape") {
        setSwitcherOpen(false);
        return;
      }

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
      <div className="loading">
        <p className="row muted">
          <Spinner label="Starting" /> Starting Silvia…
        </p>
      </div>
    );
  }

  const chooseProject = (id: string) => {
    setProjectId(id);
    setSwitcherOpen(false);
    setScreen("chat");
  };

  const navButton = (item: { id: Screen; label: string; icon: IconName }) => (
    <button
      key={item.id}
      type="button"
      aria-current={screen === item.id ? "page" : undefined}
      onClick={() => setScreen(item.id)}
      disabled={!project && PROJECT_SCREENS.includes(item)}
    >
      <Icon name={item.icon} />
      {item.label}
    </button>
  );

  // Chat fills the window edge to edge; the other screens sit in a centered column.
  const flush = screen === "chat" && project !== null;

  return (
    <div className="shell">
      <aside className="sidebar">
        <div className="brand">
          <span className="brand-mark" aria-hidden="true">S</span>
          <span className="brand-name">Silvia</span>
        </div>

        <div className="project-switch">
          <button
            type="button"
            className="project-button"
            onClick={() => setSwitcherOpen((open) => !open)}
            aria-haspopup="listbox"
            aria-expanded={switcherOpen}
          >
            <span className="grow">
              <span className="label">Project</span>
              <span className="name">{project ? project.name : "No project"}</span>
            </span>
            <Icon name="chevron" />
            <span className="visually-hidden">(Ctrl+P)</span>
          </button>
          {switcherOpen ? (
            <div className="popover" role="listbox" aria-label="Projects">
              {projectList.length === 0 ? <span className="popover-empty">No projects yet.</span> : null}
              {projectList.map((p) => (
                <button key={p.id} type="button" role="option" aria-selected={p.id === projectId} aria-current={p.id === projectId} onClick={() => chooseProject(p.id)}>
                  {p.name}
                </button>
              ))}
              <hr />
              <button type="button" onClick={() => { setSwitcherOpen(false); setScreen("projects"); }}>
                <Icon name="projects" /> Manage projects…
              </button>
            </div>
          ) : null}
        </div>

        <nav className="nav" aria-label="Project">
          {PROJECT_SCREENS.map(navButton)}
        </nav>
        <nav className="nav nav-end" aria-label="App">
          {APP_SCREENS.map(navButton)}
        </nav>
      </aside>

      <main className={flush ? "main main-flush" : "main"}>
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
            <Empty title="Create a project to start" icon="projects">
              Each project keeps its own folders, index and conversations.
              <br />
              <Button variant="primary" onClick={() => setScreen("projects")}>
                <Icon name="plus" /> Create a project
              </Button>
            </Empty>
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
