import { expect, test } from "@playwright/test";
import { loadSeed, signIn } from "./support";

const seed = loadSeed();

test("an empty analytics page says there is no data", async ({ page }, info) => {
  test.skip(info.project.name !== "desktop", "runs before any import on the shared database");
  await signIn(page, seed);
  await page.goto("/analytics");
  await expect(page.getByTestId("no-metrics")).toContainText("No metrics have been imported yet");
});

test("importing the same CSV twice never double counts and missing numbers stay blank", async ({ page }, info) => {
  test.skip(info.project.name !== "desktop", "mutates shared seeded data");
  await signIn(page, seed);

  // The owner records a real post first, as they would after posting by hand.
  const url = `https://www.tiktok.com/@owner/video/${Date.now()}`;
  await page.goto(`/videos/${seed.videos.export}`);
  const panel = page.getByTestId("export-panel");
  await panel.getByRole("button", { name: "Create export package" }).click();
  await expect(page.getByTestId("export-message")).toBeVisible();
  await panel.getByLabel(/Post URL/).fill(url);
  await panel.getByRole("button", { name: "Record my post" }).click();
  await expect(page.getByTestId("publication-list")).toContainText(url);

  await page.goto("/analytics");
  const csv = Buffer.from(`post_url,views\n${url},321\nhttps://www.tiktok.com/@nobody/video/1,5\n`);
  async function importCsv() {
    await page.getByLabel(/CSV file/).setInputFiles({ name: "metrics.csv", mimeType: "text/csv", buffer: csv });
    await page.getByLabel("Source name").fill("tiktok-export");
    await page.getByLabel("Which column identifies the post?").fill("post_url");
    await page.getByLabel("As-of date for every row").fill("2026-10-20");
    await page.getByLabel("Views column").fill("views");
    await page.getByRole("button", { name: "Import" }).click();
    return page.getByTestId("import-report");
  }

  const first = await importCsv();
  await expect(first).toContainText("Added 1");
  await expect(first).toContainText("rejected 1");
  await expect(first).toContainText("METRICS_PUBLICATION_UNKNOWN");

  const source = page.getByTestId("metrics-source").first();
  await expect(source).toContainText("321");
  await expect(source).toContainText("No data");

  const second = await importCsv();
  await expect(second).toContainText("Added 0");
  await expect(second).toContainText("unchanged 1");
  await expect(page.getByTestId("metrics-source").first()).toContainText("321");
});
