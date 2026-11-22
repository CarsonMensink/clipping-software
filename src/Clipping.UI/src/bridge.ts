// Messages exchanged with the C# host. Keep in sync with Clipping.Windows.Support/Shell/OverlayProtocol.cs.

export type NotificationKind = "info" | "error";

export type HostMessage = {
  type: "notify";
  /** Identifies the card; echoed back in "idle". */
  id: number;
  title: string;
  message: string;
  kind: NotificationKind;
};

export type PageMessage =
  /** The page is listening; the host may send messages. */
  | { type: "ready" }
  /** Card id has faded out and nothing is on screen, so the host can hide the window. */
  | { type: "idle"; id: number };

type WebView = {
  postMessage(message: unknown): void;
  addEventListener(type: "message", listener: (event: { data: unknown }) => void): void;
  removeEventListener(type: "message", listener: (event: { data: unknown }) => void): void;
};

declare global {
  interface Window {
    chrome?: { webview?: WebView };
  }
}

// Absent when the page is opened in a normal browser, such as from the Vite dev server.
const webview = window.chrome?.webview;

export function postToHost(message: PageMessage): void {
  webview?.postMessage(message);
}

export function onHostMessage(listener: (message: HostMessage) => void): () => void {
  const handler = (event: { data: unknown }) => listener(event.data as HostMessage);
  if (webview) {
    webview.addEventListener("message", handler);
    return () => webview.removeEventListener("message", handler);
  }

  // Outside WebView2, window.postMessage stands in for the host so the page can be tried in a browser.
  const browserHandler = (event: MessageEvent) => handler(event);
  window.addEventListener("message", browserHandler);
  return () => window.removeEventListener("message", browserHandler);
}
