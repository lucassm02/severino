// Four small jobs: set the board's letters by hand, turn the Rotas/Serviços/DNS panels into
// tabs, play the intro while it is on screen, and point the download at the latest release.

// The page is English at the root and Portuguese under /pt-BR/; the few words written here follow it.
const english = document.documentElement.lang === "en";
const words = english
  ? { pause: "Pause the animation", resume: "Resume the animation", release: (v, size) => `Version ${v}, ${size}. Free and open source. For Windows 10 and 11, 64-bit.` }
  : { pause: "Pausar a animação", resume: "Continuar a animação", release: (v, size) => `Versão ${v}, ${size}. Grátis e com código aberto. Para Windows 10 e 11, 64 bits.` };

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

// Without this, the three panels show one after the other, which reads fine too.
(function tabs() {
  const root = document.querySelector("[data-abas]");
  if (!root) return;
  const list = root.querySelector("[role=tablist]");
  const buttons = [...list.querySelectorAll("[role=tab]")];
  const panels = buttons.map((b) => document.getElementById(b.getAttribute("aria-controls")));

  const select = (index, focus) => {
    buttons.forEach((button, i) => {
      const on = i === index;
      button.setAttribute("aria-selected", String(on));
      button.tabIndex = on ? 0 : -1;
      panels[i].hidden = !on;
    });
    if (focus) buttons[index].focus();
  };

  list.hidden = false;
  root.classList.add("ativa");
  select(0, false);
  buttons.forEach((button, i) => {
    button.addEventListener("click", () => select(i, false));
    button.addEventListener("keydown", (e) => {
      const last = buttons.length - 1;
      const to = { ArrowRight: i === last ? 0 : i + 1, ArrowLeft: i === 0 ? last : i - 1, Home: 0, End: last }[e.key];
      if (to === undefined) return;
      e.preventDefault();
      select(to, true);
    });
  });
})();

// The intro plays on its own, muted and in a loop, but only while on screen: the 7 MB wait
// until someone scrolls there. Reduced motion keeps the still frame until they press play.
(function screen() {
  const video = document.querySelector("[data-tela]");
  const button = document.querySelector("[data-tela-pausa]");
  if (!video || !button) return;
  const label = button.querySelector(".sr");

  let paused = window.matchMedia("(prefers-reduced-motion: reduce)").matches;
  let visible = false;
  const update = () => {
    button.setAttribute("aria-pressed", String(paused));
    label.textContent = paused ? words.resume : words.pause;
    if (visible && !paused) video.play().catch(() => {});
    else video.pause();
  };

  button.hidden = false;
  button.addEventListener("click", () => {
    paused = !paused;
    update();
  });
  new IntersectionObserver(([entry]) => {
    visible = entry.isIntersecting;
    update();
  }, { threshold: 0.35 }).observe(video);
  update();
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
    if (note) note.textContent = words.release(version, size);
  } catch {
    // Offline or rate-limited: the links already go to the releases page.
  }
})();
