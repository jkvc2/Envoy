export interface FileAttachment {
  uploadId: string;
  name: string;
  contentType: string;
  length: number;
  sha256: string;
  expiresAt: string;
}

export interface ChatMessage {
  id: string;
  sender: string;
  sentAt: string;
  text: string | null;
  file: FileAttachment | null;
}

export interface UploadJob {
  id: string;
  chunkSize: number;
  chunkCount: number;
}
