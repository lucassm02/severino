// Two small jobs: set the board's letters by hand, and point the download at the latest release.

(function board() {
  const quadro = document.querySelector(".quadro");
  if (!quadro) return;

  // A fixed sequence instead of Math.random: the board looks the same on every visit.
  let seed = 7;
  const next = () => ((seed = (seed * 9301 + 49297) % 233280) / 233280) - 0.5;

  const rows = [quadro.querySelector(".quadro-titulo"), ...quadro.querySelectorAll(".nome, .porta")];
  let delay = 0;
  let lastRow = null;
  for (const el of rows) {
    const row = el.closest("li") || el;
    if (row !== lastRow) {
      delay += 90;
      lastRow = row;
    }
    const text = el.textContent;
    el.textContent = "";
    [...text].forEach((ch, i) => {
      const span = document.createElement("span");
      span.className = "letra";
      span.textContent = ch;
      span.style.setProperty("--r", `${(next() * 2.4).toFixed(2)}deg`);
      span.style.setProperty("--y", `${(next() * 1.6).toFixed(2)}px`);
      span.style.setProperty("--d", `${delay + i * 18}ms`);
      el.append(span);
    });
  }

  if (!window.matchMedia("(prefers-reduced-motion: reduce)").matches) {
    quadro.classList.add("encaixando");
  }
})();

(async function latestRelease() {
  const repo = "lucassm02/severino";
  try {
    const response = await fetch(`https://api.github.com/repos/${repo}/releases/latest`, {
      headers: { Accept: "application/vnd.github+json" },
    });
    if (!response.ok) return;
    const release = await response.json();
    const setup = (release.assets || []).find((a) => /\.exe$/i.test(a.name));
    if (!setup) return;

    for (const link of document.querySelectorAll("[data-download]")) {
      link.href = setup.browser_download_url;
    }
    const version = (release.tag_name || "").replace(/^v/, "");
    const size = `${Math.round(setup.size / (1024 * 1024))} MB`;
    const note = document.querySelector("[data-release-note]");
    if (note) note.textContent = `Versão ${version}, ${size}. Para Windows 10 e 11, 64 bits.`;
  } catch {
    // Offline or rate-limited: the links already go to the releases page.
  }
})();
