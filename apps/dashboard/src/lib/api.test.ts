import { afterEach, describe, expect, it, vi } from "vitest";
import { ApiError, createApi } from "./api";

function response(status: number, body: unknown): Response {
  return new Response(body === undefined ? null : JSON.stringify(body), {
    status,
    headers: { "content-type": "application/json" },
  });
}

afterEach(() => vi.restoreAllMocks());

describe("api client", () => {
  it("reads without a CSRF token and sends cookies", async () => {
    const fetchMock = vi.fn().mockResolvedValue(response(200, { ok: true }));
    const api = createApi(fetchMock as unknown as typeof fetch);

    expect(await api.get<{ ok: boolean }>("/api/v1/system/status")).toEqual({ ok: true });

    const [, init] = fetchMock.mock.calls[0]!;
    expect(init.credentials).toBe("same-origin");
    expect(new Headers(init.headers).has("X-CSRF-TOKEN")).toBe(false);
  });

  it("fetches a CSRF token once and sends it on every mutation", async () => {
    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(response(200, { token: "tok-1" }))
      .mockResolvedValue(response(204, undefined));
    const api = createApi(fetchMock as unknown as typeof fetch);

    await api.post("/api/v1/system/pause");
    await api.post("/api/v1/system/resume");

    const urls = fetchMock.mock.calls.map((call) => call[0]);
    expect(urls).toEqual(["/api/v1/auth/csrf", "/api/v1/system/pause", "/api/v1/system/resume"]);
    expect(new Headers(fetchMock.mock.calls[1]![1].headers).get("X-CSRF-TOKEN")).toBe("tok-1");
    expect(new Headers(fetchMock.mock.calls[2]![1].headers).get("X-CSRF-TOKEN")).toBe("tok-1");
  });

  it("turns a problem response into an ApiError with the stable code", async () => {
    const fetchMock = vi
      .fn()
      .mockResolvedValue(response(409, { code: "REVIEW_ALREADY_DECIDED", title: "Already decided" }));
    const api = createApi(fetchMock as unknown as typeof fetch);

    const error = await api.get("/api/v1/videos/x").catch((e: unknown) => e);

    expect(error).toBeInstanceOf(ApiError);
    expect((error as ApiError).status).toBe(409);
    expect((error as ApiError).code).toBe("REVIEW_ALREADY_DECIDED");
    expect((error as ApiError).unauthorized).toBe(false);
  });

  it("flags an unauthenticated response", async () => {
    const fetchMock = vi.fn().mockResolvedValue(response(401, undefined));
    const api = createApi(fetchMock as unknown as typeof fetch);

    const error = (await api.get("/api/v1/system/status").catch((e: unknown) => e)) as ApiError;

    expect(error.unauthorized).toBe(true);
  });

  it("refreshes the CSRF token once after a CSRF rejection", async () => {
    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(response(200, { token: "old" }))
      .mockResolvedValueOnce(response(400, { code: "CSRF_INVALID" }))
      .mockResolvedValueOnce(response(200, { token: "new" }))
      .mockResolvedValueOnce(response(204, undefined));
    const api = createApi(fetchMock as unknown as typeof fetch);

    await api.post("/api/v1/system/pause");

    expect(new Headers(fetchMock.mock.calls[3]![1].headers).get("X-CSRF-TOKEN")).toBe("new");
  });
});
