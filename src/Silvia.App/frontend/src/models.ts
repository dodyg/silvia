import { useEffect, useState } from "react";
import { library } from "./api";

export interface ModelStatus {
  canEmbed: boolean;
  canRerank: boolean;
}

/**
 * Which local models a project can use. Both are optional, so a failed read counts as "available":
 * the screen then behaves as before rather than showing a notice it cannot back up.
 */
export function useModelStatus(projectId: string): ModelStatus {
  const [status, setStatus] = useState<ModelStatus>({ canEmbed: true, canRerank: true });

  useEffect(() => {
    let active = true;
    library
      .modelStatus({ projectId })
      .then((next) => {
        if (active) {
          setStatus({ canEmbed: next.canEmbed, canRerank: next.canRerank });
        }
      })
      .catch(() => {
        // Leave the default; any call that needs a model reports its own state.
      });
    return () => {
      active = false;
    };
  }, [projectId]);

  return status;
}
