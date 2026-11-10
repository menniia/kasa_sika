import { Platform } from "react-native";

/**
 * The phone's (or browser's) own speech recogniser, used for ENGLISH only when available: it is instant
 * and costs nothing. It has no Twi model, so Twi always goes to Khaya. Where it is unavailable (Expo Go,
 * unsupported browsers) English also goes to Khaya's African-English model.
 */

type Recognizer = { recognize: (locale: string) => Promise<string | null>; stop: () => void };

function webRecognizer(): Recognizer | null {
  const Ctor = (globalThis as unknown as { SpeechRecognition?: new () => any; webkitSpeechRecognition?: new () => any })
    .SpeechRecognition ?? (globalThis as unknown as { webkitSpeechRecognition?: new () => any }).webkitSpeechRecognition;
  if (!Ctor) return null;
  let active: any = null;
  return {
    recognize: (locale) => new Promise((resolve, reject) => {
      const r = new Ctor();
      active = r;
      r.lang = locale;
      r.interimResults = false;
      r.maxAlternatives = 1;
      let result: string | null = null;
      r.onresult = (e: any) => { result = e.results?.[0]?.[0]?.transcript ?? null; };
      // Permission problems are errors (so hands-free mode stops); anything else is "heard nothing".
      r.onerror = (e: any) => (["not-allowed", "service-not-allowed", "audio-capture"].includes(e?.error) ? reject(new Error("MIC_DENIED")) : resolve(null));
      r.onend = () => resolve(result);
      r.start();
    }),
    stop: () => { try { active?.stop(); } catch { /* ignore */ } },
  };
}

function nativeRecognizer(): Recognizer | null {
  try {
    // Native module: present in development builds, absent in Expo Go (then this throws and we use Khaya).
    // eslint-disable-next-line @typescript-eslint/no-require-imports
    const mod = require("expo-speech-recognition");
    const Module = mod.ExpoSpeechRecognitionModule;
    if (!Module?.start) return null;
    return {
      recognize: async (locale) => {
        const permission = await Module.requestPermissionsAsync();
        if (!permission.granted) return null;
        return new Promise((resolve) => {
          let text: string | null = null;
          const subs = [
            Module.addListener("result", (e: any) => { if (e.isFinal) text = e.results?.[0]?.transcript ?? text; }),
            Module.addListener("error", () => finish()),
            Module.addListener("end", () => finish()),
          ];
          function finish() { subs.forEach((s: any) => s.remove()); resolve(text); }
          Module.start({ lang: locale, interimResults: false, continuous: false });
        });
      },
      stop: () => { try { Module.stop(); } catch { /* ignore */ } },
    };
  } catch {
    return null;
  }
}

let cached: Recognizer | null | undefined;

export function deviceRecognizer(): Recognizer | null {
  if (cached === undefined) cached = Platform.OS === "web" ? webRecognizer() : nativeRecognizer();
  return cached;
}
