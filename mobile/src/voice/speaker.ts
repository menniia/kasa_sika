import { AudioPlayer, createAudioPlayer, setAudioModeAsync } from "expo-audio";
import * as Speech from "expo-speech";
import { apiBaseUrl } from "@/api/client";
import type { SpeechOutput } from "@/api/types";

let current: AudioPlayer | null = null;
let cancelCurrent: (() => void) | null = null;

/** Stops whatever Kasa Sika is saying (the user tapped to interrupt). */
export function stopSpeaking(): void {
  Speech.stop();
  if (current) {
    try { current.pause(); current.remove(); } catch { /* already released */ }
    current = null;
  }
  cancelCurrent?.();
  cancelCurrent = null;
}

function speakOnDevice(text: string, locale: string | null, rate: number): Promise<void> {
  return new Promise((resolve) => {
    // Some browsers never fire "end": never let the conversation hang on it.
    const guard = setTimeout(resolve, Math.max(4000, (text.length * 95) / Math.max(rate, 0.5)));
    const done = () => { clearTimeout(guard); resolve(); };
    Speech.speak(text, {
      language: locale ?? "en-GH",
      rate,
      onDone: done,
      onStopped: done,
      onError: done,
    });
  });
}

function playUrl(url: string): Promise<boolean> {
  return new Promise((resolve) => {
    let settled = false;
    const done = (ok: boolean) => {
      if (settled) return;
      settled = true;
      clearTimeout(startTimeout);
      sub?.remove();
      resolve(ok);
    };
    const player = createAudioPlayer({ uri: url });
    current = player;
    cancelCurrent = () => done(true);
    // If the clip has not started within 20 s (network/synthesis failure), fall back to the device voice.
    const startTimeout = setTimeout(() => done(false), 20000);
    let started = false;
    const sub = player.addListener("playbackStatusUpdate", (status) => {
      if (status.playing && !started) { started = true; clearTimeout(startTimeout); }
      if (status.didJustFinish) done(true);
    });
    try { player.play(); } catch { done(false); }
  });
}

/**
 * Speaks one reply. Server engine = Khaya's Twi voice, fetched as a cached clip. If that clip cannot be
 * played, the phone says so in English and reads the English version - it never reads Twi text with an
 * English voice, and it never stays silent about a financial result.
 */
export async function speak(output: SpeechOutput, rate: number): Promise<void> {
  stopSpeaking();
  await setAudioModeAsync({ playsInSilentMode: true, allowsRecording: false }).catch(() => undefined);
  if (output.engine === "server" && output.audioUrl) {
    const ok = await playUrl(apiBaseUrl() + output.audioUrl);
    try { current?.remove(); } catch { /* ignore */ }
    current = null;
    if (ok) return;
    if (output.fallbackText) await speakOnDevice(output.fallbackText, output.deviceLocale ?? "en-GH", rate);
    return;
  }
  await speakOnDevice(output.fallbackText ?? output.text, output.deviceLocale, rate);
}

/** Speaks a short local phrase (offline, permission) without the server. */
export function speakLocal(text: string, language: string, rate: number): Promise<void> {
  stopSpeaking();
  // The phone has no Twi voice; local Twi phrases are shown on screen and spoken in English.
  return speakOnDevice(text, language === "tw" ? "en-GH" : "en-GH", rate);
}
