import DOMPurify from "dompurify";
import { marked } from "marked";

// Model output is untrusted text: it is parsed as Markdown, then sanitized before it reaches the DOM.
// Raw HTML from the model is dropped, images are removed, and links are limited to web and mail
// addresses. The only in-page links are citation markers (#cite-n), which open the cited passage.

marked.setOptions({ gfm: true, breaks: true });

DOMPurify.addHook("afterSanitizeAttributes", (node) => {
  if (node.tagName !== "A") {
    return;
  }

  const href = node.getAttribute("href") ?? "";
  if (/^#cite-\d{1,2}$/.test(href)) {
    node.setAttribute("class", "cite");
    return;
  }

  if (!/^(https?:|mailto:)/i.test(href)) {
    node.removeAttribute("href");
  }

  node.setAttribute("rel", "noopener noreferrer");
  node.setAttribute("target", "_blank");
});

/**
 * Renders Markdown with each line of a paragraph as a paragraph of its own. A line break does not
 * end a paragraph for the browser's text direction, so this is what lets an Arabic line under an
 * English one read right to left.
 */
export function renderLines(source: string): string {
  const template = document.createElement("template");
  template.innerHTML = renderMarkdown(source);
  for (const paragraph of template.content.querySelectorAll("p")) {
    const lines: Node[][] = [[]];
    for (const node of [...paragraph.childNodes]) {
      if (node.nodeName === "BR") {
        lines.push([]);
      } else {
        lines[lines.length - 1].push(node);
      }
    }

    if (lines.length > 1) {
      paragraph.replaceWith(...lines.map((nodes) => {
        const line = document.createElement("p");
        line.append(...nodes);
        return line;
      }));
    }
  }

  return template.innerHTML;
}

export function renderMarkdown(source: string): string {
  const html = marked.parse(source, { async: false }) as string;
  return DOMPurify.sanitize(html, {
    USE_PROFILES: { html: true },
    FORBID_TAGS: ["style", "script", "iframe", "img", "form", "input"],
    ADD_ATTR: ["target"],
  });
}
