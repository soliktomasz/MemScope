import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";

const scriptPath = new URL("../../docs/script.js", import.meta.url);
const scriptSource = await readFile(scriptPath, "utf8");
const scriptModule = await import(
  `data:text/javascript;base64,${Buffer.from(scriptSource).toString("base64")}`
);
const { copyText, initReveals, setCurrentYear } = scriptModule;

const cases = [];

function test(name, run) {
  cases.push({ name, run });
}

test("copyText writes the supplied commands", async () => {
  let copied = "";
  const result = await copyText("dotnet run", {
    writeText: async (value) => {
      copied = value;
    },
  });

  assert.equal(result, "copied");
  assert.equal(copied, "dotnet run");
});

test("copyText returns a selectable fallback when clipboard access fails", async () => {
  const result = await copyText("dotnet run", {
    writeText: async () => {
      throw new Error("denied");
    },
  });

  assert.equal(result, "select");
});

test("setCurrentYear writes a stable numeric year", () => {
  const element = { textContent: "" };

  setCurrentYear(element, 2026);

  assert.equal(element.textContent, "2026");
});

test("initReveals renders immediately when motion is reduced", () => {
  const element = {
    classList: {
      added: [],
      add(value) {
        this.added.push(value);
      },
    },
  };

  initReveals([element], class {}, true);

  assert.deepEqual(element.classList.added, ["is-visible"]);
});

let failures = 0;
for (const { name, run } of cases) {
  try {
    await run();
    console.log(`ok - ${name}`);
  } catch (error) {
    failures += 1;
    console.error(`not ok - ${name}`);
    console.error(error);
  }
}

if (failures) {
  process.exitCode = 1;
} else {
  console.log(`${cases.length} tests passed`);
}
