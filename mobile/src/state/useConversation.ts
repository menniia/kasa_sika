import { useCallback, useEffect, useRef, useState } from "react";
import { AccessibilityInfo } from "react-native";
import { api, ApiError } from "@/api/client";
import type { ConversationReply } from "@/api/types";
import { newId } from "@/lib/storage";
import { t } from "@/lib/strings";
import { useSession } from "@/state/session";
import { deviceRecognizer } from "@/voice/deviceStt";
import { playEarcon } from "@/voice/earcons";
import { speak, speakLocal, stopSpeaking } from "@/voice/speaker";
import { useRecorder } from "@/voice/useRecorder";

/** One retry for transient failures. Safe because the retried request has the same idempotent turn id. */
async function withRetry<T>(call: () => Promise<T>): Promise<T> {
  try { return await call(); }
  catch (e) {
    if (e instanceof ApiError && (e.code === "NETWORK" || e.code === "CONCURRENT_UPDATE")) {
      await new Promise((r) => setTimeout(r, 400));
      return call();
    }
    throw e;
  }
}

export type Phase = "idle" | "listening" | "thinking" | "speaking";

export type Line = { id: string; role: "user" | "assistant"; text: string; by?: string | null };

/**
 * Drives one voice conversation: listen → send → speak the reply → (hands-free) listen again.
 * The server decides what happens; this hook only moves audio and text, and never decides anything
 * about money.
 */
export function useConversation() {
  const session = useSession();
  const { language, prefs, online } = session;
  const recorder = useRecorder();
  const [reply, setReply] = useState<ConversationReply | null>(null);
  const [lines, setLines] = useState<Line[]>([]);
  const [phase, setPhase] = useState<Phase>("idle");
  const [notice, setNotice] = useState<string | null>(null);
  const conversationId = useRef<string | undefined>(undefined);
  const pollTimer = useRef<ReturnType<typeof setTimeout> | null>(null);
  const alive = useRef(true);
  const silentTurns = useRef(0);
  // One turn at a time: a voice result and a tap must never race each other on the server.
  const queue = useRef<Promise<unknown>>(Promise.resolve());
  const micBroken = useRef(false);
  const enqueue = <T,>(job: () => Promise<T>): Promise<T> => {
    const next = queue.current.then(job, job);
    queue.current = next.catch(() => undefined);
    return next;
  };
  // Called from the memoized reply handler, so always read through refs to get the current version.
  const listenRef = useRef<() => Promise<void>>(async () => undefined);
  const pollRef = useRef<(attempt: number) => Promise<void>>(async () => undefined);
  const latest = useRef({ language, prefs, online });
  latest.current = { language, prefs, online };

  useEffect(() => () => {
    alive.current = false;
    if (pollTimer.current) clearTimeout(pollTimer.current);
    stopSpeaking();
    recorder.stop();
  }, []); // eslint-disable-line react-hooks/exhaustive-deps

  const addLine = (role: Line["role"], text: string, by?: string | null) =>
    setLines((l) => [...l.slice(-11), { id: newId(), role, text, by }]);

  const sayLocal = useCallback(async (text: string) => {
    setNotice(text);
    addLine("assistant", text);
    AccessibilityInfo.announceForAccessibility(text);
    setPhase("speaking");
    await speakLocal(text, latest.current.language, latest.current.prefs.speechRate);
    if (alive.current) setPhase("idle");
  }, []);

  const handle = useCallback(async (r: ConversationReply, opts: { autoListen?: boolean } = {}) => {
    conversationId.current = r.conversationId;
    setReply(r);
    setNotice(null);
    if (r.heard !== null && r.heard !== undefined) addLine("user", r.heard || "…", r.heardBy);
    if (r.speech) {
      addLine("assistant", r.speech.text);
      setPhase("speaking");
      if (r.authentication) void playEarcon("attention");
      else if (r.transaction?.state === "SUCCESS" && r.stage === "COMPLETED") void playEarcon("success");
      await speak(r.speech, latest.current.prefs.speechRate);
    }
    if (!alive.current) return;
    setPhase("idle");

    // Two silences in a row: stop re-opening the microphone and wait for a tap (no endless loop in a quiet room).
    silentTurns.current = (r.replyKeys ?? []).includes("NO_SPEECH") ? silentTurns.current + 1 : 0;
    if (r.pollAfterMs) {
      if (pollTimer.current) clearTimeout(pollTimer.current);
      pollTimer.current = setTimeout(() => void pollRef.current(0), r.pollAfterMs);
    } else if ((opts.autoListen ?? true) && latest.current.prefs.handsFree && r.listenNext && !r.authentication && silentTurns.current < 2 && !micBroken.current) {
      void listenRef.current();
    }
  }, []); // eslint-disable-line react-hooks/exhaustive-deps

  const failed = useCallback(async (e: unknown) => {
    const s = t(latest.current.language);
    if (e instanceof ApiError && e.code === "NETWORK") return sayLocal(s.network);
    if (e instanceof ApiError && e.status === 401) { await session.logout(); return; }
    return sayLocal(e instanceof ApiError ? e.message : s.network);
  }, [sayLocal, session]);

  const poll = useCallback(async (attempt: number) => {
    if (!conversationId.current || !alive.current) return;
    try {
      const r = await api.status(conversationId.current);
      if (r.speech || !r.pollAfterMs || attempt >= 40) return handle(r, { autoListen: false });
      setReply(r);
      pollTimer.current = setTimeout(() => void pollRef.current(attempt + 1), r.pollAfterMs);
    } catch {
      if (attempt < 40) pollTimer.current = setTimeout(() => void pollRef.current(attempt + 1), 3000);
    }
  }, [handle]);
  pollRef.current = poll;

  const start = useCallback(async () => {
    setPhase("thinking");
    try { await handle(await api.welcome(latest.current.language)); } catch (e) { setPhase("idle"); await failed(e); }
  }, [handle, failed]);

  /**
   * Text from typing, a button, or the device recogniser. Turns are queued, and a turn that hit a network
   * drop or a concurrent update is retried once with the SAME turn id (the server answers it at most once).
   */
  const sendText = useCallback((text: string, inputMode: "text" | "voice" | "button", recognizedBy?: string) => enqueue(async () => {
    if (!latest.current.online) return sayLocal(t(latest.current.language).offline);
    const turnId = newId();
    setPhase("thinking");
    const body = { conversationId: conversationId.current, turnId, language: latest.current.language, text, inputMode, recognizedBy };
    try {
      await handle(await withRetry(() => api.sayText(body)));
    } catch (e) {
      setPhase("idle");
      await failed(e);
    }
  }), [handle, failed, sayLocal]); // eslint-disable-line react-hooks/exhaustive-deps

  const listen = useCallback(async () => {
    const { language: lang, online: isOnline, prefs: p } = latest.current;
    const s = t(lang);
    if (!isOnline) return sayLocal(s.offline);
    stopSpeaking();
    if (pollTimer.current) clearTimeout(pollTimer.current);

    // English: the phone's recogniser when available (instant, free); Twi: always Khaya on the server.
    const device = lang === "en" && p.englishEngine === "auto" ? deviceRecognizer() : null;
    await playEarcon("listenStart");
    setPhase("listening");
    try {
      if (device) {
        const text = await device.recognize("en-GH");
        void playEarcon("listenStop");
        if (!alive.current) return;
        if (!text) { setPhase("idle"); return sendText("", "voice", "device"); }
        return sendText(text, "voice", "device");
      }
      const recording = await recorder.record();
      void playEarcon("listenStop");
      if (!alive.current) return;
      if (!recording || !recording.heardSpeech) { setPhase("idle"); return sendText("", "voice", "khaya"); }
      setPhase("thinking");
      const turnId = newId();
      const body = { conversationId: conversationId.current, turnId, language: lang, audioBase64: recording.base64, audioContentType: recording.contentType };
      await enqueue(async () => handle(await withRetry(() => api.sayAudio(body))));
    } catch (e) {
      setPhase("idle");
      // No microphone: say so once and stop re-opening it automatically. Typing and buttons still work.
      if (e instanceof Error && e.message === "MIC_DENIED") { micBroken.current = true; return sayLocal(s.micDenied); }
      if (e instanceof ApiError) return failed(e);
      return sayLocal(s.noMic);
    }
  }, [recorder, sendText, handle, failed, sayLocal]);
  listenRef.current = listen;

  /** The big button: start listening, stop listening, or interrupt speech. */
  const talk = useCallback(() => {
    if (phase === "listening") { recorder.stop(); deviceRecognizer()?.stop(); return; }
    if (phase === "speaking") { stopSpeaking(); setPhase("idle"); return; }
    if (phase === "thinking") return;
    silentTurns.current = 0;
    micBroken.current = false; // an explicit tap is worth another try
    void listen();
  }, [phase, recorder, listen]);

  const authenticate = useCallback(async (proof: { method: "PASSCODE"; passcode: string } | { method: "DEVICE_BIOMETRIC"; signature: string }) => {
    if (!reply?.authentication || !conversationId.current) return;
    if (!latest.current.online) return sayLocal(t(latest.current.language).offline);
    setPhase("thinking");
    try {
      await handle(await api.authenticate(conversationId.current, { challengeId: reply.authentication.challengeId, ...proof }));
    } catch (e) {
      setPhase("idle");
      await failed(e);
    }
  }, [reply, handle, failed, sayLocal]);

  const resetConversation = useCallback(async () => {
    stopSpeaking();
    if (!conversationId.current) return start();
    setPhase("thinking");
    try { await handle(await api.reset(conversationId.current)); } catch (e) { setPhase("idle"); await failed(e); }
  }, [start, handle, failed]);

  const repeat = useCallback(() => {
    if (reply?.speech) { setPhase("speaking"); void speak(reply.speech, latest.current.prefs.speechRate).then(() => alive.current && setPhase("idle")); }
  }, [reply]);

  return { reply, lines, phase, notice, level: recorder.level, start, talk, listen, sendText, authenticate, resetConversation, repeat, started: reply !== null };
}
