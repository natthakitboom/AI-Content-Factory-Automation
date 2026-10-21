import { expect, type Page } from "@playwright/test";
import { readFileSync } from "node:fs";

export interface Seed {
  username: string;
  password: string;
  videos: { review: string; captionEdit: string; export: string };
  unknownJob: string;
}

export function loadSeed(): Seed {
  const file = process.env.E2E_SEED_FILE;
  if (!file) {
    throw new Error("E2E_SEED_FILE is not set; run these specs through `npm run test:e2e`.");
  }
  return JSON.parse(readFileSync(file, "utf8")) as Seed;
}

export async function signIn(page: Page, seed: Seed): Promise<void> {
  await page.goto("/login");
  await page.getByLabel("Username").fill(seed.username);
  await page.getByLabel("Password").fill(seed.password);
  await page.getByRole("button", { name: "Sign in" }).click();
  await expect(page.getByTestId("mode-banner")).toBeVisible();
}

/** The page must never need horizontal scrolling at phone width. */
export async function expectNoHorizontalScroll(page: Page): Promise<void> {
  const overflow = await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
  expect(overflow).toBeLessThanOrEqual(1);
}
