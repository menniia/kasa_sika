import Constants from "expo-constants";
import { Platform } from "react-native";
import type { ConversationReply, DemoState, SessionResult, SystemInfo, Transaction, UserView } from "./types";

/**
 * The API base URL. EXPO_PUBLIC_API_BASE_URL wins; otherwise web uses localhost and a phone running the
 * app from the Expo dev server uses that server's LAN address - so a judge's phone on the same Wi-Fi
 * works without configuration.
 */
export function apiBaseUrl(): string {
  const configured = process.env.EXPO_PUBLIC_API_BASE_URL;
  if (configured) return configured.replace(/\/$/, "");
  if (Platform.OS === "web") return `http://${globalThis.location?.hostname ?? "localhost"}:5126`;
  const hostUri = Constants.expoConfig?.hostUri ?? "";
  const host = hostUri.split(":")[0];
  return `http://${host || "10.0.2.2"}:5126`;
}

/** A failure with a normalized code the UI turns into plain language. Never shown raw to the user. */
export class ApiError extends Error {
  constructor(public code: string, message: string, public status: number, public details?: Record<string, unknown> | null) {
    super(message);
  }
}

let token: string | null = null;
export function setToken(value: string | null) { token = value; }

async function call<T>(path: string, init: { method?: string; body?: unknown; timeoutMs?: number } = {}): Promise<T> {
  const controller = new AbortController();
  const timer = setTimeout(() => controller.abort(), init.timeoutMs ?? 30000);
  let response: Response;
  try {
    response = await fetch(apiBaseUrl() + path, {
      method: init.method ?? (init.body === undefined ? "GET" : "POST"),
      headers: {
        "content-type": "application/json",
        ...(token ? { authorization: `Bearer ${token}` } : {}),
      },
      body: init.body === undefined ? undefined : JSON.stringify(init.body),
      signal: controller.signal,
    });
  } catch {
    // Network down, server unreachable, or timeout. We do not know more, and we say so.
    throw new ApiError("NETWORK", "Could not reach Kasa Sika.", 0);
  } finally {
    clearTimeout(timer);
  }
  const text = await response.text();
  let body: unknown = null;
  try { body = text ? JSON.parse(text) : null; } catch { body = null; }
  if (!response.ok) {
    const error = (body as { error?: { code?: string; message?: string; details?: Record<string, unknown> } } | null)?.error;
    throw new ApiError(error?.code ?? (response.status === 401 ? "UNAUTHENTICATED" : "REQUEST_FAILED"), error?.message ?? "Request failed.", response.status, error?.details);
  }
  return body as T;
}

type Device = { installationId: string; platform: string; label: string };

export const api = {
  systemInfo: () => call<SystemInfo>("/api/v1/system/info", { timeoutMs: 8000 }),
  demoLogin: (device: Device) => call<SessionResult>("/api/v1/auth/demo-login", { body: { device } }),
  login: (phoneNumber: string, passcode: string, device: Device) => call<SessionResult>("/api/v1/auth/login", { body: { phoneNumber, passcode, device } }),
  logout: () => call<void>("/api/v1/auth/logout", { body: {} }),
  me: () => call<UserView>("/api/v1/auth/me"),
  updatePreferences: (p: { language?: string; speechRate?: number; handsFree?: boolean }) =>
    call<UserView>("/api/v1/auth/me/preferences", { method: "PUT", body: p }),
  enrollDeviceKey: (passcode: string, deviceKey: string) => call<void>("/api/v1/auth/device-key", { body: { passcode, deviceKey } }),

  welcome: (language: string) => call<ConversationReply>(`/api/v1/conversation?language=${encodeURIComponent(language)}`),
  sayText: (p: { conversationId?: string; turnId: string; language: string; text: string; inputMode: "text" | "voice" | "button"; recognizedBy?: string }) =>
    call<ConversationReply>("/api/v1/conversation/turns", { body: p }),
  sayAudio: (p: { conversationId?: string; turnId: string; language: string; audioBase64: string; audioContentType: string }) =>
    call<ConversationReply>("/api/v1/conversation/turns", { body: { ...p, inputMode: "voice" }, timeoutMs: 45000 }),
  authenticate: (conversationId: string, p: { challengeId: string; method: "PASSCODE" | "DEVICE_BIOMETRIC"; passcode?: string; signature?: string }) =>
    call<ConversationReply>(`/api/v1/conversation/${conversationId}/authenticate`, { body: p, timeoutMs: 45000 }),
  status: (conversationId: string) => call<ConversationReply>(`/api/v1/conversation/${conversationId}/status`, { body: {} }),
  reset: (conversationId: string) => call<ConversationReply>(`/api/v1/conversation/${conversationId}/reset`, { body: {} }),

  history: (language: string) => call<Transaction[]>(`/api/v1/transactions?take=30&language=${language}`),
  transaction: (id: string, language: string) => call<Transaction>(`/api/v1/transactions/${id}?language=${language}`),

  demoState: () => call<DemoState>("/api/v1/demo"),
  demoScenario: (scenario: string | null) => call<DemoState>("/api/v1/demo/scenario", { method: "PUT", body: { scenario } }),
  demoReset: () => call<{ cleared: number }>("/api/v1/demo/reset", { body: {} }),
  demoPrewarm: () => call<{ generated: number }>("/api/v1/demo/prewarm-speech?language=tw", { body: {}, timeoutMs: 180000 }),
};
