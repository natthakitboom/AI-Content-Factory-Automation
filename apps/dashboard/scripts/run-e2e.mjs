// Starts a scratch PostgreSQL database, the API, the worker and the dashboard on loopback,
// runs the Playwright specs, then stops everything and drops the scratch database.
// Mock providers only; nothing here can reach a paid API or an external host.
import { spawn, spawnSync } from "node:child_process";
import { existsSync, mkdtempSync, readFileSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const dashboard = resolve(fileURLToPath(new URL("..", import.meta.url)));
const repo = resolve(dashboard, "..", "..");
const API_PORT = process.env.E2E_API_PORT ?? "5081";
const WEB_PORT = process.env.E2E_WEB_PORT ?? "3100";
const api = `http://127.0.0.1:${API_PORT}`;
const web = `http://127.0.0.1:${WEB_PORT}`;

const env = { ...process.env };
env.PATH = [
  join(process.env.HOME ?? "", ".dotnet"),
  "/opt/homebrew/opt/ffmpeg-full/bin",
  env.PATH,
].join(":");

if (!env.CF_TEST_POSTGRES) {
  const file = join(repo, ".env.test.local");
  if (existsSync(file)) {
    env.CF_TEST_POSTGRES = readFileSync(file, "utf8").trim().replace(/^CF_TEST_POSTGRES=/, "");
  }
}
if (!env.CF_TEST_POSTGRES) {
  console.error("CF_TEST_POSTGRES is not set; the end-to-end run is BLOCKED (not passed).");
  process.exit(2);
}
for (const tool of ["ffmpeg", "ffprobe"]) {
  if (spawnSync(tool, ["-version"], { env, stdio: "ignore" }).status !== 0) {
    console.error(`${tool} is not installed; the end-to-end run is BLOCKED (not passed).`);
    process.exit(2);
  }
}

const font = [process.env.CAPTION_FONT_FILE, "/System/Library/Fonts/Thonburi.ttc", "/System/Library/Fonts/Supplemental/Thonburi.ttc", "/usr/share/fonts/truetype/tlwg/Loma.ttf"].filter(Boolean).find(existsSync);
if (!font) {
  console.error("No Thai caption font found for CAPTION_FONT_FILE; the end-to-end run is BLOCKED (not passed).");
  process.exit(2);
}

const root = mkdtempSync(join(tmpdir(), "cf-e2e-"));
const seedFile = join(root, "seed.json");
const children = [];
let database = null;
let exitCode = 1;

function run(command, args, options = {}) {
  return spawnSync(command, args, { env, cwd: repo, encoding: "utf8", ...options });
}

function start(name, command, args, extraEnv, cwd = repo) {
  const child = spawn(command, args, {
    env: { ...env, ...extraEnv }, cwd, stdio: ["ignore", "pipe", "pipe"],
  });
  let output = "";
  child.stdout.on("data", (chunk) => { output += chunk; });
  child.stderr.on("data", (chunk) => { output += chunk; });
  child.on("exit", (code) => { if (code && code !== 143) console.error(`[${name}] exited with ${code}\n${output.slice(-1500)}`); });
  children.push(child);
  return child;
}

async function waitFor(url, label, ms = 120_000) {
  const end = Date.now() + ms;
  while (Date.now() < end) {
    try {
      const response = await fetch(url);
      if (response.ok) return;
    } catch { /* not up yet */ }
    await new Promise((r) => setTimeout(r, 500));
  }
  throw new Error(`${label} did not become ready at ${url}`);
}

async function cleanup() {
  for (const child of children) child.kill("SIGTERM");
  await new Promise((r) => setTimeout(r, 1000));
  for (const child of children) if (child.exitCode === null) child.kill("SIGKILL");
  if (database) run("dotnet", ["run", "--project", "tests/ContentFactory.E2eSeed", "--no-launch-profile", "--", "drop", database]);
  rmSync(root, { recursive: true, force: true });
}

process.on("SIGINT", () => { void cleanup().then(() => process.exit(130)); });

try {
  console.log("Seeding a scratch database with real mock-pipeline output…");
  const dataRoot = join(root, "data");
  const seed = run("dotnet", ["run", "--project", "tests/ContentFactory.E2eSeed", "--no-launch-profile", "--", "seed", root, seedFile]);
  process.stdout.write(seed.stdout ?? "");
  if (seed.status !== 0) throw new Error(`Seeding failed:\n${seed.stdout}\n${seed.stderr}`);
  const seeded = JSON.parse(readFileSync(seedFile, "utf8"));
  database = seeded.database;

  const common = {
    "ConnectionStrings__App": seeded.connection,
    DATA_ROOT: dataRoot,
    TEXT_PROVIDER: "mock", VIDEO_PROVIDER: "mock", VOICE_PROVIDER: "mock",
    ALLOW_PAID_APIS: "false", AUTO_PUBLISH: "false", PUBLISHING_MODE: "manual_export",
    FAL_KEY: "", CAPTION_FONT_FILE: font,
  };
  start("api", "dotnet", ["run", "--project", "src/ContentFactory.Api", "--no-launch-profile", "--no-build"],
    { ...common, ASPNETCORE_URLS: api, ASPNETCORE_ENVIRONMENT: "Development" });
  start("worker", "dotnet", ["run", "--project", "src/ContentFactory.Worker", "--no-launch-profile", "--no-build"],
    { ...common, DOTNET_ENVIRONMENT: "Development" });
  await waitFor(`${api}/health/ready`, "API");

  if (!process.env.E2E_SKIP_BUILD) {
    console.log("Building the dashboard against the e2e API…");
    const build = spawnSync("npm", ["run", "build"], { env: { ...env, API_ORIGIN: api }, cwd: dashboard, encoding: "utf8" });
    if (build.status !== 0) throw new Error(`Dashboard build failed:\n${build.stdout}\n${build.stderr}`);
  }
  start("web", "npx", ["next", "start", "--hostname", "127.0.0.1", "--port", WEB_PORT], { API_ORIGIN: api }, dashboard);
  await waitFor(`${web}/login`, "dashboard");

  const forwarded = process.argv.slice(2).filter((arg) => arg !== "--");
  const test = spawnSync("npx", ["playwright", "test", ...forwarded], {
    env: { ...env, E2E_BASE_URL: web, E2E_SEED_FILE: seedFile },
    cwd: dashboard, stdio: "inherit",
  });
  exitCode = test.status ?? 1;
} catch (error) {
  console.error(String(error));
  exitCode = 1;
} finally {
  await cleanup();
}
process.exit(exitCode);
