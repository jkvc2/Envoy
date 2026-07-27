import type { ChatMessage } from "./types";

export function connectSocket(onMessage: (message: ChatMessage) => void): () => void {
  let stopped = false;
  let socket: WebSocket | undefined;

  const connect = () => {
    const protocol = location.protocol === "https:" ? "wss" : "ws";
    socket = new WebSocket(`${protocol}://${location.host}/ws`);
    socket.onmessage = (event) => onMessage(JSON.parse(event.data) as ChatMessage);
    socket.onclose = () => {
      if (!stopped) {
        window.setTimeout(connect, 1000);
      }
    };
  };

  connect();
  return () => {
    stopped = true;
    socket?.close();
  };
}
