import { expect, test } from "@playwright/test";
import { expectNoHorizontalScroll, loadSeed, signIn } from "./support";

const seed = loadSeed();

test.beforeEach(async ({ page }) => {
  await signIn(page, seed);
});

test("preview, originals, claims and the cost are on the review page", async ({ page }) => {
  await page.goto(`/videos/${seed.videos.review}`);
  await expect(page.getByTestId("preview-video")).toBeVisible();
  await expect(page.getByRole("img", { name: /Original product image/ }).first()).toBeVisible();
  await expect(page.getByRole("heading", { name: /Claims used/ })).toBeVisible();
  await expect(page.getByTestId("video-cost")).toContainText("Reserved");
  await expect(page.getByTestId("qa-result")).toContainText("Passed");
  await expectNoHorizontalScroll(page);
});

// These change the seeded data, so they run once (desktop); the read-only checks run at both widths.
test("ApproveNeedsEveryChecklistItemAndIsKeyboardOperable", async ({ page }, info) => {
  test.skip(info.project.name !== "desktop", "mutates shared seeded data");
  await page.goto(`/videos/${seed.videos.review}`);
  const approve = page.getByRole("button", { name: "Approve this exact version" });
  await expect(approve).toBeDisabled();

  const boxes = page.getByRole("checkbox");
  const count = await boxes.count();
  expect(count).toBe(7);
  for (let i = 0; i < count - 1; i++) {
    await boxes.nth(i).focus();
    await page.keyboard.press("Space");
  }
  await expect(approve).toBeDisabled();

  await boxes.nth(count - 1).focus();
  await page.keyboard.press("Space");
  await expect(approve).toBeEnabled();
  await approve.focus();
  await page.keyboard.press("Enter");

  await expect(page.getByTestId("decision")).toContainText("Approved");
  await expect(page.getByTestId("review-message")).toContainText("Approved this exact version");
  await expect(page.getByRole("button", { name: "Approve this exact version" })).toHaveCount(0);
});

test("RejectNeedsAComment", async ({ page }) => {
  await page.goto(`/videos/${seed.videos.captionEdit}`);
  const reject = page.getByRole("button", { name: "Reject" });
  await expect(reject).toBeDisabled();
  await page.getByLabel(/Comment/).fill("The logo looks different.");
  await expect(reject).toBeEnabled();
});

test("EditedCaptionCreatesANewVersionThatNeedsReviewAndHasNoExport", async ({ page }, info) => {
  test.skip(info.project.name !== "desktop", "mutates shared seeded data");
  // Approve the export candidate's sibling flow: edit a caption on an approved video.
  await page.goto(`/videos/${seed.videos.export}`);
  await expect(page.getByTestId("export-panel")).toBeVisible();

  const firstCaption = page.getByLabel(/Caption for/).first();
  await firstCaption.fill("ข้อความใหม่หลังรีวิว");
  await page.getByRole("button", { name: "Save as new version" }).first().click();
  await expect(page.getByTestId("review-message")).toContainText("Approval does not carry over");

  // The worker rebuilds it; the new version appears in the queue and is NOT exportable.
  await page.goto("/videos?status=");
  await page.getByLabel("Show").selectOption("awaiting_review");
  await expect(async () => {
    await page.reload();
    await page.getByLabel("Show").selectOption("awaiting_review");
    await expect(page.getByRole("link", { name: "Open" }).first()).toBeVisible();
    expect(await page.getByRole("row").filter({ hasText: "E2E export product" }).count()).toBeGreaterThan(0);
  }).toPass({ timeout: 80_000, intervals: [2_000] });

  await page.getByRole("row").filter({ hasText: "E2E export product" }).getByRole("link", { name: "Open" }).click();
  await expect(page.getByText("Awaiting review").first()).toBeVisible();
  await expect(page.getByTestId("export-panel")).toHaveCount(0);
  await expect(page.getByRole("button", { name: "Approve this exact version" })).toBeDisabled();
});
