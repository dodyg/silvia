import { test } from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { filterTopics, HELP_TOPICS } from "./help";

test("every topic has its own id", () => {
  const ids = HELP_TOPICS.map((topic) => topic.id);
  assert.equal(new Set(ids).size, ids.length);
});

test("a search keeps the topics that contain every word, in any case", () => {
  assert.deepEqual(filterTopics("   ").map((t) => t.id), HELP_TOPICS.map((t) => t.id));
  assert.ok(filterTopics("CONTEXT budget").some((t) => t.id === "project-settings"));
  assert.ok(filterTopics("ctrl wheel").every((t) => t.body.toLowerCase().includes("wheel")));
  assert.deepEqual(filterTopics("xylophone"), []);
});

test("every project setting on the Settings screen is explained in help", () => {
  // The labels and section titles of the Project tab, between its component and the next function.
  const source = readFileSync(fileURLToPath(new URL("./screens/SettingsScreen.tsx", import.meta.url)), "utf8");
  const start = source.indexOf("function ProjectSettings(");
  const end = source.indexOf("function chatModelHint(");
  assert.ok(start > 0 && end > start, "The Project tab of SettingsScreen.tsx was not found.");
  const names = [...source.slice(start, end).matchAll(/(?:label|title)="([^"]+)"/g)].map((match) => match[1]);
  assert.ok(names.length >= 10, `Only ${names.length} settings were found.`);

  const help = HELP_TOPICS.find((topic) => topic.id === "project-settings")!.body;
  const missing = names.filter((name) => !help.includes(`**${name}**`) && !help.includes(`### ${name}`));
  assert.deepEqual(missing, [], "Add these settings to the Project settings topic in help.ts.");
});
