<script lang="ts">
  import { onMount, tick } from "svelte";
  import { loadMessages, sendText, uploadFile } from "./lib/api";
  import { connectSocket } from "./lib/socket";
  import type { ChatMessage } from "./lib/types";

  let messages = $state<ChatMessage[]>([]);
  let deviceName = $state(localStorage.getItem("envoy-name") ?? "");
  let draftName = $state(localStorage.getItem("envoy-name") ?? "");
  let text = $state("");
  let uploadProgress = $state<{ name: string; percentage: number } | null>(null);
  let error = $state("");
  let messageList = $state<HTMLElement | undefined>(undefined);
  let lightboxUrl = $state<string | null>(null);
  let lightboxName = $state("");
  let brokenImages = $state<Record<string, boolean>>({});

  function scrollToBottom() {
    tick().then(() => messageList?.lastElementChild?.scrollIntoView({ behavior: "smooth", block: "end" }));
  }

  function addMessage(message: ChatMessage) {
    if (!messages.some((existing) => existing.id === message.id)) {
      messages = [...messages, message];
      scrollToBottom();
    }
  }

  async function loadHistory() {
    try {
      (await loadMessages()).forEach(addMessage);
    } catch (reason) {
      error = reason instanceof Error ? reason.message : "Unable to load messages.";
    }
  }

  function saveName() {
    const name = draftName.trim();
    if (!name) {
      return;
    }

    deviceName = name;
    localStorage.setItem("envoy-name", name);
  }

  async function submitText() {
    const message = text.trim();
    if (!message || !deviceName) {
      return;
    }

    text = "";
    try {
      await sendText(deviceName, message);
      scrollToBottom();
    } catch (reason) {
      text = message;
      error = reason instanceof Error ? reason.message : "Unable to send message.";
    }
  }

  async function sendFiles(files: FileList | File[]) {
    for (const file of files) {
      try {
        uploadProgress = { name: file.name, percentage: 0 };
        await uploadFile(deviceName, file, (percentage) => {
          uploadProgress = { name: file.name, percentage };
        });
        scrollToBottom();
      } catch (reason) {
        error = reason instanceof Error ? reason.message : `Unable to upload ${file.name}.`;
      } finally {
        uploadProgress = null;
      }
    }
  }

  function fileSelected(event: Event) {
    const input = event.currentTarget as HTMLInputElement;
    if (input.files) {
      sendFiles(input.files);
    }
    input.value = "";
  }

  function dropped(event: DragEvent) {
    event.preventDefault();
    if (event.dataTransfer?.files.length) {
      sendFiles(event.dataTransfer.files);
    }
  }

  function pasted(event: ClipboardEvent) {
    const items = event.clipboardData?.items;
    if (!items || !deviceName) return;

    const files: File[] = [];
    for (let i = 0; i < items.length; i++) {
      const item = items[i];
      // Handle pasted files (desktop: Ctrl+V on file in explorer; mobile: paste from clipboard)
      if (item.kind === "file") {
        const file = item.getAsFile();
        if (file) files.push(file);
      }
      // Handle pasted images (screenshots, copied images from web pages, etc.)
      else if (item.kind === "string" && item.type === "text/html") {
        // HTML content may contain <img> tags — handled by the next branch
        continue;
      }
    }

    // Blob-based image paste fallback: some browsers/clipboard managers expose
    // images as blob items with a MIME type instead of kind="file".
    if (files.length === 0) {
      for (let i = 0; i < items.length; i++) {
        const item = items[i];
        if (item.type.startsWith("image/")) {
          const blob = item.getAsFile();
          if (blob) {
            files.push(new File([blob], `pasted-${Date.now()}.png`, { type: item.type }));
          }
        }
      }
    }

    if (files.length > 0) {
      event.preventDefault();
      sendFiles(files);
    }
  }

  function fileUrl(uploadId: string) {
    return `/api/files/${uploadId}`;
  }

  function openLightbox(url: string, name: string) {
    lightboxUrl = url;
    lightboxName = name;
  }

  function closeLightbox() {
    lightboxUrl = null;
    lightboxName = "";
  }

  function handleKeydown(event: KeyboardEvent) {
    if (event.key === "Escape") {
      closeLightbox();
    }
  }

  function handleBackdropClick(event: MouseEvent) {
    // Close only when clicking the backdrop itself, not its children.
    if (event.target === event.currentTarget) {
      closeLightbox();
    }
  }

  function handleBackdropKeydown(event: KeyboardEvent) {
    if (event.key === "Enter" || event.key === " ") {
      event.preventDefault();
      closeLightbox();
    }
  }

  onMount(() => {
    loadHistory();
    return connectSocket(addMessage);
  });
</script>

<svelte:head>
  <title>Envoy</title>
</svelte:head>

<svelte:window onkeydown={handleKeydown} />

{#if !deviceName}
  <div class="modal-backdrop">
    <form class="identity-card" onsubmit={(event) => { event.preventDefault(); saveName(); }}>
      <div class="brand-mark">E</div>
      <h1>Welcome to Envoy</h1>
      <p>Choose a name for this device. It is shown on every message you send.</p>
      <input bind:value={draftName} maxlength="40" placeholder="My iPhone" />
      <button type="submit">Join chat</button>
    </form>
  </div>
{:else}
  <main class="app-shell" ondragover={(event) => event.preventDefault()} ondrop={dropped} onpaste={pasted}>
    <header class="topbar">
      <div class="brand-mark">E</div>
      <div class="title-group">
        <strong>Envoy</strong>
        <span>Local network chat</span>
      </div>
      <button class="name-button" type="button" onclick={() => { deviceName = ""; draftName = localStorage.getItem("envoy-name") ?? ""; }}>
        {deviceName}
      </button>
    </header>

    <section class="notice">Connected through your local network. Do not use on public Wi-Fi.</section>

    <section class="chat-list" bind:this={messageList}>
      {#each messages as message (message.id)}
        <article class:mine={message.sender === deviceName} class="message-row">
          <div class="bubble">
            <div class="message-meta">{message.sender} · {new Date(message.sentAt).toLocaleString()}</div>
            {#if message.text}
              <div class="message-text">{message.text}</div>
            {/if}
            {#if message.file}
              {#if message.file.contentType.startsWith("image/")}
                {#if brokenImages[message.file.uploadId]}
                  <div class="broken-placeholder">
                    <span class="broken-icon">🖼</span>
                    <small>Image not available</small>
                  </div>
                {:else}
                  <button
                    class="image-preview-btn"
                    type="button"
                    onclick={() => openLightbox(fileUrl(message.file!.uploadId), message.file!.name)}
                    title="Click to preview"
                  >
                    <img
                      class="image-preview"
                      src={fileUrl(message.file.uploadId)}
                      alt={message.file.name}
                      loading="lazy"
                      onerror={() => brokenImages[message.file!.uploadId] = true}
                    />
                  </button>
                {/if}
              {:else if message.file.contentType.startsWith("video/")}
                <video class="media-preview" controls preload="metadata">
                  <source src={fileUrl(message.file.uploadId)} type={message.file.contentType} />
                  Your browser cannot play this video.
                </video>
              {:else if message.file.contentType.startsWith("audio/")}
                <audio class="audio-preview" controls preload="metadata">
                  <source src={fileUrl(message.file.uploadId)} type={message.file.contentType} />
                  Your browser cannot play this audio file.
                </audio>
              {:else}
                <div class="generic-file-icon">
                  <span>📄</span>
                </div>
              {/if}
              <!-- Always show the download card for files -->
              <a class="file-card" href={fileUrl(message.file.uploadId)} download={message.file.name}>
                <span class="file-icon">⇩</span>
                <span><strong>{message.file.name}</strong><small>{Math.ceil(message.file.length / 1024)} KB</small></span>
              </a>
            {/if}
          </div>
        </article>
      {/each}
    </section>

    {#if error}
      <div class="error-toast" role="alert">{error}<button type="button" onclick={() => error = ""}>×</button></div>
    {/if}

    {#if uploadProgress}
      <div class="upload-status"><span>Uploading {uploadProgress.name}</span><strong>{uploadProgress.percentage}%</strong><div><i style={`width:${uploadProgress.percentage}%`}></i></div></div>
    {/if}

    <form class="composer" onsubmit={(event) => { event.preventDefault(); submitText(); }}>
      <label class="attachment-button" title="Send files">
        <input type="file" multiple accept="image/*,video/*,audio/*,.zip,.rar,.7z" onchange={fileSelected} />
        <span>+</span>
      </label>
      <input class="message-input" bind:value={text} maxlength="4000" placeholder="Message" />
      <button class="send-button" type="submit" aria-label="Send message">↑</button>
    </form>
  </main>
{/if}

<!-- Lightbox overlay for image preview -->
{#if lightboxUrl}
  <div class="lightbox-backdrop" onclick={handleBackdropClick} onkeydown={handleBackdropKeydown} role="dialog" aria-label="Image preview" tabindex="-1">
    <button class="lightbox-close" type="button" onclick={closeLightbox} aria-label="Close preview">×</button>
    <div class="lightbox-header">
      <span>{lightboxName}</span>
      <a class="lightbox-download" href={lightboxUrl} download={lightboxName}>⇩ Download</a>
    </div>
    <img class="lightbox-image" src={lightboxUrl} alt={lightboxName} />
  </div>
{/if}
