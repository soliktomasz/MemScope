from __future__ import annotations

import re
import sys
from html.parser import HTMLParser
from pathlib import Path
from urllib.parse import urlparse

ROOT = Path(__file__).resolve().parents[2]
DOCS = ROOT / "docs"
INDEX = DOCS / "index.html"
REPOSITORY_URL = "https://github.com/soliktomasz/MemScope"


class SiteParser(HTMLParser):
    def __init__(self) -> None:
        super().__init__(convert_charrefs=True)
        self.tags: list[str] = []
        self.ids: set[str] = set()
        self.links: list[tuple[str, str]] = []
        self.scripts: list[dict[str, str | None]] = []
        self.visible_text: list[str] = []
        self._anchor_stack: list[tuple[str, list[str]]] = []
        self._hidden_depth = 0

    def handle_starttag(
        self, tag: str, attrs: list[tuple[str, str | None]]
    ) -> None:
        values = dict(attrs)
        self.tags.append(tag)
        if values.get("id"):
            self.ids.add(values["id"] or "")
        if tag == "a" and values.get("href"):
            self._anchor_stack.append((values["href"] or "", []))
        if tag == "script":
            self.scripts.append(values)
        if tag in {"script", "style", "template"}:
            self._hidden_depth += 1

    def handle_endtag(self, tag: str) -> None:
        if tag == "a" and self._anchor_stack:
            href, parts = self._anchor_stack.pop()
            self.links.append((href, " ".join(parts).strip()))
        if tag in {"script", "style", "template"} and self._hidden_depth:
            self._hidden_depth -= 1

    def handle_data(self, data: str) -> None:
        if not self._hidden_depth and data.strip():
            self.visible_text.append(data.strip())
            if self._anchor_stack:
                self._anchor_stack[-1][1].append(data.strip())


def parse(path: Path) -> SiteParser:
    parser = SiteParser()
    parser.feed(path.read_text(encoding="utf-8"))
    return parser


def assert_relative_assets(source: str) -> None:
    asset_values = re.findall(r'(?:href|src)="([^"]+)"', source)
    local_assets = [
        value
        for value in asset_values
        if not urlparse(value).scheme
        and not value.startswith(("#", "mailto:"))
    ]
    assert local_assets, "expected local page assets"
    assert all(not value.startswith("/") for value in local_assets), local_assets


def main() -> int:
    assert INDEX.exists(), "docs/index.html is missing"
    source = INDEX.read_text(encoding="utf-8")
    parser = parse(INDEX)

    for landmark in ("header", "nav", "main", "section", "footer"):
        assert landmark in parser.tags, f"missing {landmark} landmark"
    assert {"main-content", "capabilities", "get-started"} <= parser.ids

    text = " ".join(parser.visible_text)
    assert "See what managed memory is doing." in text
    assert "—" not in text and "–" not in text
    assert "fake" not in text.lower()

    github_labels = [
        label for href, label in parser.links if href == REPOSITORY_URL
    ]
    assert len(github_labels) >= 2
    assert set(github_labels) == {"View GitHub"}, github_labels
    assert_relative_assets(source)

    print("website structure: ok")
    return 0


if __name__ == "__main__":
    sys.exit(main())
