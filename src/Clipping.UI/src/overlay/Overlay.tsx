import { useEffect, useState } from "react";
import { onHostMessage, postToHost, type NotificationKind } from "../bridge";

const VisibleMs = 3000;

type Card = {
  id: number;
  title: string;
  message: string;
  kind: NotificationKind;
  leaving: boolean;
};

export function Overlay() {
  const [card, setCard] = useState<Card | null>(null);

  useEffect(() => {
    const stop = onHostMessage((message) => {
      if (message.type === "notify") {
        setCard({ id: message.id, title: message.title, message: message.message, kind: message.kind, leaving: false });
      }
    });
    postToHost({ type: "ready" });
    return stop;
  }, []);

  useEffect(() => {
    if (!card || card.leaving) {
      return;
    }

    const timer = setTimeout(() => setCard({ ...card, leaving: true }), VisibleMs);
    return () => clearTimeout(timer);
  }, [card]);

  if (!card) {
    return null;
  }

  return (
    <div
      key={card.id}
      className={`card card-${card.kind}${card.leaving ? " card-leaving" : ""}`}
      role="status"
      onAnimationEnd={() => {
        if (card.leaving) {
          setCard(null);
          postToHost({ type: "idle", id: card.id });
        }
      }}
    >
      <div className="card-title">{card.title}</div>
      <div className="card-message">{card.message}</div>
    </div>
  );
}
