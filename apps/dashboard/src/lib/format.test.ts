import { describe, expect, it } from "vitest";
import { formatCount, formatMoney, formatPercent, shortHash, NO_DATA } from "./format";

describe("formatting never turns missing data into zero", () => {
  it("shows an explicit label for null", () => {
    expect(formatCount(null)).toBe(NO_DATA);
    expect(formatMoney(null, "USD")).toBe(NO_DATA);
    expect(formatPercent(null)).toBe(NO_DATA);
  });

  it("keeps a real zero as zero", () => {
    expect(formatCount(0)).toBe("0");
    expect(formatPercent(0)).toBe("0.0%");
    expect(formatMoney(0, "USD")).toBe("0.00 USD");
  });

  it("formats values predictably", () => {
    expect(formatCount(1234567)).toBe("1,234,567");
    expect(formatPercent(0.16667)).toBe("16.7%");
    expect(formatMoney(12.5, "THB")).toBe("12.50 THB");
    expect(shortHash("abcdef0123456789")).toBe("abcdef012345");
    expect(shortHash(null)).toBe(NO_DATA);
  });
});
