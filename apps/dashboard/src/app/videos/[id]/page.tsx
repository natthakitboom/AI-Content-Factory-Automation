"use client";

import { use } from "react";
import { VideoReview } from "@/components/VideoReview";

export default function VideoPage({ params }: { params: Promise<{ id: string }> }) {
  const { id } = use(params);
  return <VideoReview id={id} />;
}
