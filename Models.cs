namespace Envoy;

public sealed record ChatMessage(Guid Id, string Sender, DateTimeOffset SentAt, string? Text, FileAttachment? File);
public sealed record FileAttachment(Guid UploadId, string Name, string ContentType, long Length, string Sha256, DateTimeOffset ExpiresAt);
public sealed record UploadRequest(string Name, string ContentType, long Length, string Sender, string? Sha256);
public sealed record UploadState(Guid Id, string Name, string ContentType, long Length, string Sender, string? Sha256, int ChunkSize, int ChunkCount, bool[] Received, DateTimeOffset CreatedAt);
public sealed record CreateUploadResponse(Guid Id, int ChunkSize, int ChunkCount);
