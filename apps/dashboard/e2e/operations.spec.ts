import { expect, test } from "@playwright/test";
import { expectNoHorizontalScroll, loadSeed, signIn } from "./support";

const seed = loadSeed();

test.beforeEach(async ({ page }) => {
  await signIn(page, seed);
});

test("mode banner says MOCK and names the providers", async ({ page }) => {
  await expect(page.getByTestId("mode-banner")).toContainText("MOCK");
  await expect(page.getByTestId("mode-banner")).toContainText("video: mock");
});

test("ReservedAndSpentAreDistinct", async ({ page }) => {
  await page.goto("/costs");
  await expect(page.getByTestId("reserved-cost")).toBeVisible();
  await expect(page.getByTestId("settled-cost")).toBeVisible();
  await expect(page.getByTestId("reserved-cost")).toContainText("0.12");
  await expect(page.getByTestId("settled-cost")).toContainText("0.10");
  await expect(page.getByTestId("stale-price")).toBeVisible();
  await expect(page.getByTestId("remaining-cost")).toBeVisible();
});

test("UnknownJobHasNoBlindRetryButton", async ({ page }) => {
  await page.goto("/jobs");
  const row = page.locator(`[data-job-id="${seed.unknownJob}"]`);
  await expect(row).toContainText("Needs reconciliation");
  await expect(row).toContainText("never retried automatically");
  await expect(page.getByTestId("unknown-job-retry")).toHaveCount(0);
  await expect(page.getByRole("button", { name: /retry/i })).toHaveCount(0);
});

test("job details show a timeline section", async ({ page }) => {
  await page.goto("/jobs");
  await page.locator(`[data-job-id="${seed.unknownJob}"]`).getByRole("button", { name: "Details" }).click();
  await expect(page.getByTestId("job-detail")).toContainText("Timeline");
});

test("PausedStatePersistsAfterRefresh", async ({ page }) => {
  await page.goto("/settings");
  await page.getByRole("button", { name: "Pause production" }).click();
  await expect(page.getByTestId("pause-state")).toContainText("Paused");
  await page.reload();
  await expect(page.getByTestId("pause-state")).toContainText("Paused");
  await page.goto("/");
  await expect(page.getByTestId("paused-notice")).toBeVisible();
  await page.goto("/settings");
  await page.getByRole("button", { name: "Resume production" }).click();
  await expect(page.getByTestId("pause-state")).toContainText("Running");
});

test("KeysAreNeverInPagePayload", async ({ page }) => {
  const bodies: string[] = [];
  page.on("response", async (response) => {
    if (response.url().includes("/api/")) {
      bodies.push(await response.text().catch(() => ""));
    }
  });
  await page.goto("/settings");
  await expect(page.getByTestId("diagnostics")).toContainText("fal key: no");
  const html = await page.content();
  for (const text of [...bodies, html]) {
    expect(text).not.toMatch(/FAL_KEY\s*=|sk-[A-Za-z0-9]{10,}|Key [A-Za-z0-9]{20,}/);
    expect(text).not.toContain(seed.password);
  }
});

test("diagnostics report what could not be measured", async ({ page }) => {
  await page.goto("/settings");
  await expect(page.getByTestId("diagnostics")).toContainText("Temperature");
  await expect(page.getByTestId("diagnostics")).toContainText("unavailable");
  await expect(page.getByTestId("diagnostics")).toContainText("subtitles filter: yes");
});

test("navigation works with the keyboard and fits a phone width", async ({ page }) => {
  await page.goto("/");
  await page.keyboard.press("Tab");
  await expect(page.locator(":focus")).toBeVisible();
  await expectNoHorizontalScroll(page);
  for (const path of ["/videos", "/jobs", "/costs", "/analytics", "/settings"]) {
    await page.goto(path);
    await expectNoHorizontalScroll(page);
  }
});

test("status is shown with text, not colour alone", async ({ page }) => {
  await page.goto("/videos");
  await expect(page.locator(".badge").first()).not.toBeEmpty();
  await expect(page.locator("[data-status='awaiting_review']").first()).toContainText("Awaiting review");
});
