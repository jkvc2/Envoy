import type { ChatMessage, UploadJob } from "./types";

async function request<T>(url: string, init?: RequestInit): Promise<T> {
  const response = await fetch(url, init);
  if (!response.ok) {
    throw new Error(await response.text() || "Request failed.");
  }

  return response.json() as Promise<T>;
}

export function loadMessages(): Promise<ChatMessage[]> {
  return request<ChatMessage[]>("/api/messages", { cache: "no-store" });
}

export function sendText(sender: string, text: string): Promise<ChatMessage> {
  return request<ChatMessage>("/api/messages", {
    method: "POST",
    headers: { "content-type": "application/json" },
    body: JSON.stringify({ sender, text })
  });
}

export async function uploadFile(
  sender: string,
  file: File,
  onProgress: (percentage: number) => void
): Promise<ChatMessage> {
  const job = await request<UploadJob>("/api/uploads", {
    method: "POST",
    headers: { "content-type": "application/json" },
    body: JSON.stringify({
      name: file.name,
      contentType: file.type || "application/octet-stream",
      length: file.size,
      sender,
      // HTTP pages in Safari cannot use Web Crypto; the server calculates the final SHA-256.
      sha256: null
    })
  });

  for (let index = 0; index < job.chunkCount; index += 1) {
    const start = index * job.chunkSize;
    const end = Math.min(file.size, start + job.chunkSize);
    const response = await fetch(`/api/uploads/${job.id}/chunks/${index}`, {
      method: "PUT",
      body: file.slice(start, end)
    });
    if (!response.ok) {
      throw new Error(await response.text() || "Unable to upload file chunk.");
    }

    onProgress(Math.round(((index + 1) / job.chunkCount) * 100));
  }

  return request<ChatMessage>(`/api/uploads/${job.id}/complete`, { method: "POST" });
}
