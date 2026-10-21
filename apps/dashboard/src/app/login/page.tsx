"use client";

import { useRouter } from "next/navigation";
import { useState, type FormEvent } from "react";
import { api, ApiError } from "@/lib/api";

export default function LoginPage() {
  const router = useRouter();
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  async function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const form = new FormData(event.currentTarget);
    setBusy(true);
    setError(null);
    try {
      await api.post("/api/v1/auth/login", {
        username: String(form.get("username") ?? ""),
        password: String(form.get("password") ?? ""),
      });
      router.replace("/");
    } catch (caught) {
      setError(caught instanceof ApiError ? caught.message : "Sign-in failed.");
    } finally {
      setBusy(false);
    }
  }

  return (
    <div className="card" style={{ maxWidth: 420, margin: "10vh auto" }}>
      <h1>Sign in</h1>
      <p className="muted">This dashboard only works on this computer.</p>
      <form onSubmit={submit}>
        <label className="field">
          <span>Username</span>
          <input name="username" type="text" autoComplete="username" required />
        </label>
        <label className="field">
          <span>Password</span>
          <input name="password" type="password" autoComplete="current-password" required />
        </label>
        {error ? (
          <p role="alert" className="alert bad">
            {error}
          </p>
        ) : null}
        <button type="submit" disabled={busy}>
          {busy ? "Signing in…" : "Sign in"}
        </button>
      </form>
    </div>
  );
}
