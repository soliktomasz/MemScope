export async function copyText(text, clipboard) {
  try {
    await clipboard.writeText(text);
    return "copied";
  } catch {
    return "select";
  }
}

export function setCurrentYear(element, year) {
  element.textContent = String(year);
}

export function initReveals(elements, Observer, reduceMotion) {
  if (reduceMotion || !Observer) {
    elements.forEach((element) => element.classList.add("is-visible"));
    return null;
  }

  elements.forEach((element) => element.classList.add("is-reveal-ready"));
  const observer = new Observer(
    (entries) => {
      entries.forEach((entry) => {
        if (!entry.isIntersecting) {
          return;
        }

        entry.target.classList.add("is-visible");
        observer.unobserve(entry.target);
      });
    },
    { threshold: 0.16 },
  );
  elements.forEach((element) => observer.observe(element));
  return observer;
}

function selectCommands(source) {
  source.focus();
  const selection = window.getSelection();
  if (!selection) {
    return;
  }

  const range = document.createRange();
  range.selectNodeContents(source);
  selection.removeAllRanges();
  selection.addRange(range);
}

function initializePage() {
  const year = document.querySelector("[data-year]");
  if (year) {
    setCurrentYear(year, new Date().getFullYear());
  }

  const reduceMotion = window.matchMedia(
    "(prefers-reduced-motion: reduce)",
  ).matches;
  initReveals(
    [...document.querySelectorAll(".reveal")],
    window.IntersectionObserver,
    reduceMotion,
  );

  const button = document.querySelector("[data-copy-button]");
  const source = document.querySelector("[data-copy-source]");
  const status = document.querySelector("#copy-status");
  if (!button || !source || !status) {
    return;
  }

  const originalLabel = button.textContent.trim();
  button.addEventListener("click", async () => {
    const clipboard = navigator.clipboard ?? {
      writeText: async () => {
        throw new Error("Clipboard API unavailable");
      },
    };
    const result = await copyText(source.textContent.trim(), clipboard);

    if (result === "copied") {
      button.textContent = "Copied";
      status.textContent = "Commands copied to clipboard.";
      window.setTimeout(() => {
        button.textContent = originalLabel;
        status.textContent = "";
      }, 1800);
      return;
    }

    button.textContent = "Select and copy";
    status.textContent =
      "Clipboard access was unavailable. The commands are selected.";
    selectCommands(source);
  });
}

if (typeof document !== "undefined") {
  if (document.readyState === "loading") {
    document.addEventListener("DOMContentLoaded", initializePage, { once: true });
  } else {
    initializePage();
  }
}
