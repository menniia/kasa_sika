// Mirrors of the backend contracts (backend/src/KasaSika.Application). Enums arrive as SCREAMING_SNAKE.

export type TransactionState =
  | "DRAFT" | "PARSED" | "VALIDATING" | "READY_FOR_CONFIRMATION" | "CONFIRMED" | "AUTHENTICATION_PENDING"
  | "AUTHENTICATED" | "AUTHORIZATION_PENDING" | "AUTHORIZED" | "SUBMITTING" | "SUBMITTED" | "PENDING"
  | "SUCCESS" | "FAILED" | "CANCELLED" | "EXPIRED" | "UNKNOWN" | "REVERSED" | "RECONCILIATION_REQUIRED";

export type ProviderMode = "DEMO_SIMULATED" | "SANDBOX" | "LIVE";

export type Stage =
  | "IDLE" | "AWAITING_AMOUNT" | "AWAITING_RECIPIENT" | "CHOOSING_RECIPIENT" | "AWAITING_CONFIRMATION"
  | "AWAITING_AUTHENTICATION" | "PROCESSING" | "COMPLETED";

export type AuthMethod = "PASSCODE" | "DEVICE_BIOMETRIC" | "DEMO_SIMULATED" | "LOGIN";

export type TransactionEvent = { from: TransactionState | null; to: TransactionState; actor: string; reason: string; occurredAt: string };

export type Transaction = {
  id: string;
  state: TransactionState;
  statusLabel: string;
  statusTone: "success" | "pending" | "failed" | "unknown" | "neutral";
  isFinal: boolean;
  mode: ProviderMode;
  notice: string;
  amountMinor: number;
  currency: string;
  amountText: string;
  spokenSummary: string;
  spokenStatus: string;
  recipient: { displayName: string; maskedIdentifier: string };
  failureReason: string | null;
  reference: string | null;
  authenticationMethod: string | null;
  trust: { requested: string; authorized: string | null; providerReport: string; current: string };
  createdAt: string;
  updatedAt: string;
  expiresAt: string;
  events: TransactionEvent[];
};

export type SpeechOutput = {
  text: string;
  language: string;
  engine: "server" | "device";
  audioUrl: string | null;
  deviceLocale: string | null;
  fallbackText: string | null;
};

export type ConversationReply = {
  conversationId: string;
  stage: Stage;
  language: string;
  heard: string | null;
  heardBy: string | null;
  understood: { intent: string; amountMinor: number | null; amountText: string | null; recipientName: string | null; recipientMasked: string | null } | null;
  speech: SpeechOutput | null;
  replyKey: string | null;
  replyKeys: string[];
  expecting: string[];
  authentication: { challengeId: string; transactionId: string; expiresAt: string; methods: AuthMethod[]; signingMessage: string } | null;
  transaction: Transaction | null;
  candidates: { number: number; name: string; maskedPhone: string }[] | null;
  listenNext: boolean;
  pollAfterMs: number | null;
  transcript?: { role: "user" | "assistant"; text: string; language: string; inputMode: string; at: string }[] | null;
};

export type Language = { code: string; name: string; nativeName: string; deviceLocale: string | null; serverTts: boolean; serverAsr: boolean };

export type SystemInfo = {
  environmentLabel: string;
  mode: ProviderMode;
  provider: string;
  capabilities: { debitsUserWallet: boolean; userWalletBalance: boolean; fundingSource: string };
  languages: Language[];
  khayaConfigured: boolean;
  demoEnabled: boolean;
  demoLogin: boolean;
  stepUpMethods: string[];
  maxTransactionMinor: number;
};

export type UserView = {
  id: string;
  displayName: string;
  maskedPhoneNumber: string;
  isDemo: boolean;
  preferences: { language: string; speechRate: number; handsFree: boolean };
};

export type SessionResult = { accessToken: string; expiresAt: string; user: UserView; deviceId: string | null; stepUpMethods: AuthMethod[] };

export type DemoState = { enabled: boolean; activeOverride: string | null; scenarios: string[]; contacts: { name: string; maskedPhone: string; scenario: string }[] };
