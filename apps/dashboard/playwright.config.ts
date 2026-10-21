import { defineConfig, devices } from "@playwright/test";

// The servers are started by scripts/run-e2e.mjs (API, worker, dashboard on loopback only).
const baseURL = process.env.E2E_BASE_URL ?? "http://127.0.0.1:3100";

export default defineConfig({
  testDir: "./e2e",
  timeout: 90_000,
  expect: { timeout: 15_000 },
  fullyParallel: false,
  workers: 1,
  retries: 0,
  reporter: [["list"]],
  use: { baseURL, trace: "retain-on-failure" },
  projects: [
    { name: "desktop", use: { ...devices["Desktop Chrome"], viewport: { width: 1280, height: 900 } } },
    { name: "narrow-390", use: { ...devices["Desktop Chrome"], viewport: { width: 390, height: 844 } } },
  ],
});
