// Renders assets/branding/motion/intro.html to an MP4, frame by frame, with headless Edge and ffmpeg.
//   node scripts/render-motion.mjs [--fps 60] [--from 0] [--to 30] [--out path.mp4] [--frames dir]
// The page draws each frame from the time alone (window.seek), so nothing depends on how fast
// this machine is. Needs Edge, ffmpeg on the PATH and the internet for the fonts.
import { spawn } from "node:child_process";
import { existsSync, mkdirSync, mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";

const root = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const arg = (name, fallback) => {
  const i = process.argv.indexOf(`--${name}`);
  return i > 0 ? process.argv[i + 1] : fallback;
};
const fps = Number(arg("fps", 60));
const from = Number(arg("from", 0));
const out = resolve(arg("out", join(root, "site/assets/severino-intro.mp4")));
const framesDir = arg("frames"); // also keep PNGs of each frame, for checking
const page = pathToFileURL(join(root, "assets/branding/motion/intro.html")).href + "?render";

const edge = [
  `${process.env["ProgramFiles(x86)"]}\\Microsoft\\Edge\\Application\\msedge.exe`,
  `${process.env.ProgramFiles}\\Microsoft\\Edge\\Application\\msedge.exe`,
].find((p) => existsSync(p));
if (!edge) throw new Error("Microsoft Edge não encontrado.");

const port = 9300 + Math.floor(Math.random() * 500);
const profile = mkdtempSync(join(tmpdir(), "severino-motion-"));
const browser = spawn(edge, [
  "--headless=new", "--disable-gpu", "--hide-scrollbars", "--mute-audio",
  `--remote-debugging-port=${port}`, `--user-data-dir=${profile}`,
  "--window-size=1920,1080", "--force-device-scale-factor=1", "about:blank",
], { stdio: "ignore" });

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

async function pageSocket() {
  for (let i = 0; i < 50; i++) {
    try {
      const targets = await (await fetch(`http://127.0.0.1:${port}/json/list`)).json();
      const target = targets.find((t) => t.type === "page");
      if (target) return target.webSocketDebuggerUrl;
    } catch { /* not up yet */ }
    await sleep(200);
  }
  throw new Error("O Edge não abriu a porta de depuração.");
}

// A minimal Chrome DevTools Protocol client over Node's own WebSocket.
const ws = new WebSocket(await pageSocket());
await new Promise((r, j) => { ws.onopen = r; ws.onerror = j; });
let nextId = 1;
const pending = new Map();
ws.onmessage = (event) => {
  const message = JSON.parse(event.data);
  if (message.id && pending.has(message.id)) {
    const { resolve: ok, reject } = pending.get(message.id);
    pending.delete(message.id);
    if (message.error) reject(new Error(message.error.message));
    else ok(message.result);
  }
};
const send = (method, params = {}) => new Promise((ok, reject) => {
  const id = nextId++;
  pending.set(id, { resolve: ok, reject });
  ws.send(JSON.stringify({ id, method, params }));
});
const evaluate = async (expression) => {
  const result = await send("Runtime.evaluate", { expression, awaitPromise: true, returnByValue: true });
  if (result.exceptionDetails) throw new Error(result.exceptionDetails.exception?.description ?? "Erro no script da página.");
  return result.result.value;
};

try {
  await send("Page.enable");
  await send("Emulation.setDeviceMetricsOverride", { width: 1920, height: 1080, deviceScaleFactor: 1, mobile: false });
  await send("Page.navigate", { url: page });
  for (let i = 0; i < 100 && !(await evaluate("typeof window.ready !== 'undefined'").catch(() => false)); i++) await sleep(100);
  await evaluate("window.ready");
  const duration = Number(arg("to", await evaluate("window.DURATION")));
  const total = Math.round((duration - from) * fps);
  if (framesDir) mkdirSync(framesDir, { recursive: true });

  const ffmpeg = spawn("ffmpeg", [
    "-y", "-loglevel", "error", "-f", "image2pipe", "-framerate", String(fps), "-i", "-",
    "-c:v", "libx264", "-preset", "slow", "-crf", "17", "-pix_fmt", "yuv420p", "-movflags", "+faststart", out,
  ], { stdio: ["pipe", "inherit", "inherit"] });
  const done = new Promise((ok, reject) => ffmpeg.on("close", (code) => (code === 0 ? ok() : reject(new Error(`ffmpeg saiu com ${code}`)))));

  const started = Date.now();
  for (let i = 0; i < total; i++) {
    const t = from + i / fps;
    // Draw the frame, then wait for it to be painted before taking the picture.
    await evaluate(`seek(${t}); new Promise((r) => requestAnimationFrame(() => requestAnimationFrame(r)))`);
    const { data } = await send("Page.captureScreenshot", { format: "png", optimizeForSpeed: true });
    const png = Buffer.from(data, "base64");
    if (framesDir) writeFileSync(join(framesDir, `${String(i).padStart(5, "0")}.png`), png);
    if (!ffmpeg.stdin.write(png)) await new Promise((r) => ffmpeg.stdin.once("drain", r));
    if (i % fps === 0) process.stdout.write(`\r${(t).toFixed(0).padStart(2)} s de ${duration} s`);
  }
  ffmpeg.stdin.end();
  await done;
  console.log(`\r${out} (${total} quadros, ${fps} fps, em ${Math.round((Date.now() - started) / 1000)} s)`);
} finally {
  ws.close();
  browser.kill();
  await sleep(500);
  rmSync(profile, { recursive: true, force: true });
}
