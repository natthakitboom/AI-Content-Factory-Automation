export class ApiError extends Error {
  constructor(
    readonly status: number,
    readonly code: string,
    message: string,
  ) {
    super(message);
    this.name = "ApiError";
  }

  get unauthorized(): boolean {
    return this.status === 401;
  }
}

type FetchLike = typeof fetch;

interface Problem {
  code?: string;
  title?: string;
  detail?: string;
}

/**
 * Same-origin client for the proxied API. Reads need no token; every mutation sends the
 * double-submit CSRF token, fetched once and refreshed once if the server rejects it.
 */
export function createApi(fetchImpl: FetchLike = (...args) => fetch(...args)) {
  let csrf: string | null = null;

  async function token(refresh = false): Promise<string> {
    if (csrf === null || refresh) {
      const response = await fetchImpl("/api/v1/auth/csrf", { credentials: "same-origin" });
      if (!response.ok) {
        throw await toError(response);
      }
      csrf = ((await response.json()) as { token: string }).token;
    }
    return csrf;
  }

  async function toError(response: Response): Promise<ApiError> {
    let problem: Problem = {};
    try {
      problem = (await response.json()) as Problem;
    } catch {
      // A response without a JSON body still becomes a typed error.
    }
    return new ApiError(
      response.status,
      problem.code ?? `HTTP_${response.status}`,
      problem.detail ?? problem.title ?? `Request failed with status ${response.status}.`,
    );
  }

  async function parse<T>(response: Response): Promise<T> {
    if (!response.ok) {
      throw await toError(response);
    }
    if (response.status === 204 || response.status === 202 && !response.headers.get("content-type")) {
      return undefined as T;
    }
    const text = await response.text();
    return (text ? JSON.parse(text) : undefined) as T;
  }

  async function send<T>(method: string, url: string, body?: unknown, form?: FormData): Promise<T> {
    for (let attempt = 0; ; attempt++) {
      const headers = new Headers();
      headers.set("X-CSRF-TOKEN", await token(attempt > 0));
      let payload: BodyInit | undefined;
      if (form) {
        payload = form;
      } else if (body !== undefined) {
        headers.set("Content-Type", "application/json");
        payload = JSON.stringify(body);
      }
      const response = await fetchImpl(url, { method, headers, body: payload, credentials: "same-origin" });
      if (response.status === 400 && attempt === 0) {
        const problem = (await response.clone().json().catch(() => ({}))) as Problem;
        if (problem.code === "CSRF_INVALID") {
          continue;
        }
      }
      return parse<T>(response);
    }
  }

  return {
    get: async <T>(url: string): Promise<T> =>
      parse<T>(await fetchImpl(url, { credentials: "same-origin", headers: new Headers() })),
    post: <T = void>(url: string, body?: unknown) => send<T>("POST", url, body),
    put: <T = void>(url: string, body?: unknown) => send<T>("PUT", url, body),
    postForm: <T>(url: string, form: FormData) => send<T>("POST", url, undefined, form),
  };
}

export const api = createApi();
