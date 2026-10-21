"use client";

import { useCallback, useEffect, useRef, useState } from "react";
import { useRouter } from "next/navigation";
import { ApiError } from "./api";

export interface Loaded<T> {
  data: T | null;
  error: ApiError | null;
  loading: boolean;
  reload: () => Promise<void>;
}

/** Loads persisted data; an expired session sends the owner to the sign-in page. */
export function useLoad<T>(loader: () => Promise<T>, deps: unknown[] = []): Loaded<T> {
  const router = useRouter();
  const [data, setData] = useState<T | null>(null);
  const [error, setError] = useState<ApiError | null>(null);
  const [loading, setLoading] = useState(true);
  const loaderRef = useRef(loader);
  loaderRef.current = loader;

  const reload = useCallback(async () => {
    try {
      setData(await loaderRef.current());
      setError(null);
    } catch (caught) {
      const apiError = caught instanceof ApiError ? caught : new ApiError(0, "NETWORK", "The API is not reachable.");
      if (apiError.unauthorized) {
        router.replace("/login");
      }
      setError(apiError);
    } finally {
      setLoading(false);
    }
  }, [router]);

  useEffect(() => {
    setLoading(true);
    void reload();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [reload, ...deps]);

  return { data, error, loading, reload };
}
