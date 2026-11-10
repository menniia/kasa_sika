// End-to-end smoke test of the voice conversation through the real API (mock payments).
// Usage: node scripts/smoke-conversation.mjs [baseUrl] [language] ["utterance" ...]
// Default: English, "Send 50 cedis to Ama" → "yes" → passcode → poll until final.
import { randomUUID } from "node:crypto";

const base = process.argv[2] ?? "http://localhost:5126";
const language = process.argv[3] ?? "en";
const utterances = process.argv.slice(4).length > 0 ? process.argv.slice(4) : ["Send 50 cedis to Ama", "yes"];
const passcode = process.env.DEMO_PASSCODE ?? "246810";

async function call(path, { method = "POST", body, token } = {}) {
  const res = await fetch(base + path, {
    method,
    headers: { "content-type": "application/json", ...(token ? { authorization: `Bearer ${token}` } : {}) },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  const text = await res.text();
  const json = text ? JSON.parse(text) : null;
  if (!res.ok) throw new Error(`${method} ${path} → ${res.status} ${text}`);
  return json;
}

const show = (label, r) => {
  const tx = r.transaction;
  console.log(`\n[${label}] stage=${r.stage} listenNext=${r.listenNext}${r.heard ? ` heard="${r.heard}"` : ""}`);
  if (r.speech) console.log(`  says (${r.speech.engine}${r.speech.audioUrl ? " " + r.speech.audioUrl : ""}): ${r.speech.text}`);
  if (tx) console.log(`  tx: ${tx.state} (${tx.statusLabel}) ${tx.amountText} → ${tx.recipient.displayName} ${tx.recipient.maskedIdentifier} ref=${tx.reference ?? "-"}`);
  if (r.candidates) console.log(`  candidates: ${r.candidates.map((c) => `${c.number}:${c.name}`).join(", ")}`);
};

const session = await call("/api/v1/auth/demo-login", { body: { device: { installationId: "smoke-test-device-01", platform: "cli", label: "smoke" } } });
const token = session.accessToken;
let reply = await call(`/api/v1/conversation?language=${language}`, { method: "GET", token });
show("welcome", reply);

for (const text of utterances) {
  reply = await call("/api/v1/conversation/turns", {
    token,
    body: { conversationId: reply.conversationId, turnId: randomUUID(), language, text, inputMode: "text" },
  });
  show(`"${text}"`, reply);
}

if (reply.authentication) {
  reply = await call(`/api/v1/conversation/${reply.conversationId}/authenticate`, {
    token,
    body: { challengeId: reply.authentication.challengeId, method: "PASSCODE", passcode },
  });
  show("authenticate", reply);
  for (let i = 0; i < 12 && reply.pollAfterMs; i++) {
    await new Promise((r) => setTimeout(r, reply.pollAfterMs));
    reply = await call(`/api/v1/conversation/${reply.conversationId}/status`, { token });
    if (reply.speech || !reply.pollAfterMs) show("status", reply);
  }
}
