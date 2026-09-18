export type Transaction = {
  id: string;
  state:
    | "READY_FOR_CONFIRMATION"
    | "CONFIRMED"
    | "AUTHENTICATED"
    | "AUTHORIZED"
    | "PENDING"
    | "SUCCESS"
    | "FAILED"
    | "RECONCILIATION_REQUIRED";
  mode: "DEMO_SIMULATED" | "LIVE";
  notice: string;
  spokenSummary: string;
  recipient: { displayName: string; maskedIdentifier: string };
};

const baseUrl = process.env.EXPO_PUBLIC_API_BASE_URL ?? "http://10.0.2.2:3000";

async function call<T>(path: string, options: RequestInit = {}): Promise<T> {
  const response = await fetch(`${baseUrl}${path}`, {
    ...options,
    headers: { "content-type": "application/json", ...options.headers },
  });
  const body: unknown = await response.json();
  if (!response.ok) {
    const code =
      typeof body === "object" && body !== null && "error" in body
        ? String(
            (body as { error: { code?: unknown } }).error.code ??
              "REQUEST_FAILED",
          )
        : "REQUEST_FAILED";
    throw new Error(code);
  }
  return body as T;
}

export async function createFromVoiceIntent(
  utterance: string,
): Promise<Transaction> {
  const result = await call<{ transaction: Transaction }>(
    "/api/v1/transactions/from-intent",
    {
      method: "POST",
      headers: { "idempotency-key": crypto.randomUUID() },
      body: JSON.stringify({ userId: "demo-user", utterance }),
    },
  );
  return result.transaction;
}

export async function transition(
  id: string,
  action: "confirm" | "authenticate" | "authorize" | "submit" | "status",
): Promise<Transaction> {
  const result = await call<{ transaction: Transaction }>(
    `/api/v1/transactions/${id}/${action}`,
    { method: "POST", body: "{}" },
  );
  return result.transaction;
}
