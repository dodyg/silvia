import { createDesktopClient, invoke, subscribe } from "@neoastra/client";
import { chat, evaluation, facts, inspect, library, projects, settings } from "#neoastra";

export { chat, evaluation, facts, inspect, library, projects, settings };
export type {
  AppearanceDto,
  AskUpdateDto,
  CitationDto,
  ClaimDto,
  ConversationDto,
  CoverageDto,
  DocumentCoverageDto,
  DocumentDetailDto,
  DocumentDto,
  EvalQuestionDto,
  EvalResultDto,
  EvalRunDetailDto,
  EvalRunDto,
  FactDto,
  FactVerdictDto,
  FailureDto,
  FontListDto,
  GlobalSettingsDto,
  HitDto,
  JobSummaryDto,
  MapDto,
  MapPointDto,
  MessageDto,
  PassageDto,
  ModelDto,
  ProjectDto,
  ProjectSettingsDto,
  QuestionChangeDto,
  RunComparisonDto,
  RunEstimateDto,
  SettingChangeDto,
  ChatProviderDto,
  SourceDto,
  TraceDto,
  TracedHitDto,
  VerdictDto,
} from "#neoastra";

/** Native dialogs (folder and file pickers). Calls are denied when the host has not granted them. */
export const desktop = createDesktopClient({ invoke, subscribe });

/** A short, user-facing sentence for any failed call. Details stay out of the UI on purpose. */
export function describeError(error: unknown): string {
  const message = typeof error === "object" && error !== null && "message" in error
    ? String((error as { message: unknown }).message)
    : "";
  if (message.length > 0 && message.length < 240) {
    return message;
  }

  return "Something went wrong. Try again.";
}

/** Reads an async stream item by item, until it ends or the caller stops it. */
export async function consume<T>(stream: AsyncIterable<T>, onItem: (item: T) => void): Promise<void> {
  for await (const item of stream) {
    onItem(item);
  }
}

export function formatWhen(iso: string | null | undefined): string {
  if (!iso) {
    return "";
  }

  const date = new Date(iso);
  return Number.isNaN(date.getTime())
    ? ""
    : date.toLocaleString(undefined, { dateStyle: "medium", timeStyle: "short" });
}
