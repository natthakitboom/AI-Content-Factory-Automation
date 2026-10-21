"use client";

import Link from "next/link";
import { usePathname, useRouter } from "next/navigation";
import type { ReactNode } from "react";
import { api } from "@/lib/api";
import { useLoad } from "@/lib/useLoad";
import type { ModeInfo } from "@/lib/types";

const LINKS = [
  { href: "/", label: "Overview" },
  { href: "/videos", label: "Review queue" },
  { href: "/jobs", label: "Jobs" },
  { href: "/costs", label: "Costs" },
  { href: "/analytics", label: "Analytics" },
  { href: "/settings", label: "Settings" },
];

function ModeBanner({ mode }: { mode: ModeInfo }) {
  return (
    <div className={`banner ${mode.mock ? "mock" : "live"}`} data-testid="mode-banner" role="status">
      {mode.banner}
      <span className="muted" style={{ fontWeight: 400, marginLeft: ".75rem", color: "inherit" }}>
        text: {mode.text} · video: {mode.video} · voice: {mode.voice}
        {mode.mock ? " — mock output is not real cloud output" : " — paid providers may be used"}
      </span>
    </div>
  );
}

export function AppShell({ children }: { children: ReactNode }) {
  const pathname = usePathname();
  const router = useRouter();
  const onLogin = pathname === "/login";
  const mode = useLoad(() => (onLogin ? Promise.resolve(null) : api.get<ModeInfo>("/api/v1/system/mode")), [onLogin]);

  if (onLogin) {
    return <main className="main">{children}</main>;
  }

  async function signOut() {
    await api.post("/api/v1/auth/logout").catch(() => undefined);
    router.replace("/login");
  }

  return (
    <div className="shell">
      <nav className="nav" aria-label="Main">
        <div className="brand">Content Factory</div>
        {LINKS.map((link) => (
          <Link
            key={link.href}
            href={link.href}
            aria-current={link.href === "/" ? (pathname === "/" ? "page" : undefined) : pathname.startsWith(link.href) ? "page" : undefined}
          >
            {link.label}
          </Link>
        ))}
        <button type="button" className="link" onClick={signOut}>
          Sign out
        </button>
      </nav>
      <main className="main">
        {mode.data ? <ModeBanner mode={mode.data} /> : null}
        {children}
      </main>
    </div>
  );
}
