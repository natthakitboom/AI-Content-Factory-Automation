import { expect, test } from "@playwright/test";
import { createHash } from "node:crypto";
import { readFileSync } from "node:fs";
import { execFileSync } from "node:child_process";
import { mkdtempSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { loadSeed, signIn } from "./support";

const seed = loadSeed();

test("an approved version exports a ZIP whose checksums match, and replay makes no duplicate", async ({ page }) => {
  await signIn(page, seed);
  await page.goto(`/videos/${seed.videos.export}`);
  const panel = page.getByTestId("export-panel");
  await expect(panel).toContainText("Not published");

  await panel.getByRole("button", { name: "Create export package" }).click();
  await expect(page.getByTestId("export-message")).toContainText("Package created");
  await panel.getByRole("button", { name: "Create export package" }).click();
  await expect(page.getByTestId("export-list").locator("li")).toHaveCount(1);

  const download = page.waitForEvent("download");
  await page.getByRole("link", { name: "Download ZIP" }).click();
  const file = await (await download).path();
  const dir = mkdtempSync(join(tmpdir(), "cf-zip-"));
  execFileSync("unzip", ["-q", file, "-d", dir]);

  const names = execFileSync("ls", [dir]).toString().trim().split("\n").sort();
  expect(names).toEqual([
    "captions.srt", "disclosure-checklist.md", "manifest.json", "post-caption.txt",
    "product-reference.json", "sha256sums.txt", "thumbnail.jpg", "video.mp4",
  ]);
  for (const line of readFileSync(join(dir, "sha256sums.txt"), "utf8").trim().split("\n")) {
    const [hash, name] = line.split("  ");
    expect(createHash("sha256").update(readFileSync(join(dir, name!))).digest("hex")).toBe(hash);
  }
  const probe = execFileSync("ffprobe", ["-v", "error", "-show_entries", "stream=codec_name", "-of", "csv=p=0", join(dir, "video.mp4")]).toString();
  expect(probe).toContain("h264");
});

test("recording a post is the owner's own action and the link stays unknown without evidence", async ({ page }) => {
  await signIn(page, seed);
  await page.goto(`/videos/${seed.videos.export}`);
  const panel = page.getByTestId("export-panel");
  await panel.getByRole("button", { name: "Create export package" }).click();
  await expect(page.getByTestId("export-message")).toBeVisible();

  await panel.getByLabel(/Post URL/).fill("https://www.tiktok.com/@owner/video/777");
  await panel.getByRole("button", { name: "Record my post" }).click();
  await expect(page.getByTestId("publication-list")).toContainText("link: unknown");

  await panel.getByLabel(/Post URL/).fill("https://www.tiktok.com/@owner/video/778");
  await panel.getByLabel("Product link status").selectOption("verified");
  await panel.getByRole("button", { name: "Record my post" }).click();
  await expect(page.getByTestId("export-message")).toContainText("PUBLICATION_EVIDENCE_REQUIRED");
});
