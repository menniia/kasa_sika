import { requestRecordingPermissionsAsync, setAudioModeAsync, useAudioStream } from "expo-audio";
import { useCallback, useRef, useState } from "react";
import { encodeWav, rms, toBase64, toMono16k } from "./wav";

export type Recording = { base64: string; contentType: "audio/wav"; durationMs: number; heardSpeech: boolean };

const MAX_MS = 10000;          // one request never needs more
const NO_SPEECH_MS = 6000;     // nobody spoke: stop and say so
const END_SILENCE_MS = 1300;   // pause after speech = finished

/**
 * Records the microphone as 16 kHz mono PCM on every platform (expo-audio's PCM stream) and encodes WAV
 * locally, because Khaya accepts WAV/MP3/FLAC/OGG and phone recorders produce AAC/3GP. Stops by itself
 * when the speaker pauses, so a blind user does not have to find a "stop" button.
 */
export function useRecorder() {
  const chunks = useRef<Int16Array[]>([]);
  const rate = useRef(16000);
  const startedAt = useRef(0);
  const lastVoiceAt = useRef(0);
  const heardSpeech = useRef(false);
  const noiseFloor = useRef(0.01);
  const finish = useRef<((r: Recording | null) => void) | null>(null);
  const timer = useRef<ReturnType<typeof setInterval> | null>(null);
  const [level, setLevel] = useState(0);
  const [recording, setRecording] = useState(false);

  const { stream } = useAudioStream({
    sampleRate: 16000,
    channels: 1,
    encoding: "int16",
    onBuffer: (buffer) => {
      const { samples, rate: r } = toMono16k(buffer.data, buffer.sampleRate, buffer.channels);
      rate.current = r;
      chunks.current.push(samples);
      const level = rms(samples);
      setLevel(level);
      const elapsed = Date.now() - startedAt.current;
      // The first 300 ms calibrate the room's noise; speech is anything clearly above it.
      if (elapsed < 300) noiseFloor.current = Math.max(noiseFloor.current, level);
      else if (level > Math.max(0.035, noiseFloor.current * 2.5)) {
        heardSpeech.current = true;
        lastVoiceAt.current = Date.now();
      }
    },
  });

  const stop = useCallback((): void => {
    if (!finish.current) return;
    const done = finish.current;
    finish.current = null;
    if (timer.current) clearInterval(timer.current);
    try { stream.stop(); } catch { /* already stopped */ }
    setRecording(false);
    setLevel(0);
    void setAudioModeAsync({ allowsRecording: false, playsInSilentMode: true }).catch(() => undefined);

    const total = chunks.current.reduce((n, c) => n + c.length, 0);
    const all = new Int16Array(total);
    let offset = 0;
    for (const c of chunks.current) { all.set(c, offset); offset += c.length; }
    chunks.current = [];
    if (total === 0) { done(null); return; }
    done({
      base64: toBase64(encodeWav(all, rate.current)),
      contentType: "audio/wav",
      durationMs: Math.round((total / rate.current) * 1000),
      heardSpeech: heardSpeech.current,
    });
  }, [stream]);

  /** Resolves with the recording when the user stops talking (or taps stop), or null on failure. */
  const record = useCallback(async (): Promise<Recording | null> => {
    const permission = await requestRecordingPermissionsAsync();
    if (!permission.granted) throw new Error("MIC_DENIED");
    await setAudioModeAsync({ allowsRecording: true, playsInSilentMode: true });
    chunks.current = [];
    heardSpeech.current = false;
    noiseFloor.current = 0.01;
    startedAt.current = Date.now();
    lastVoiceAt.current = Date.now();
    const result = new Promise<Recording | null>((resolve) => { finish.current = resolve; });
    await stream.start();
    setRecording(true);
    timer.current = setInterval(() => {
      const now = Date.now();
      const elapsed = now - startedAt.current;
      if (elapsed > MAX_MS) stop();
      else if (!heardSpeech.current && elapsed > NO_SPEECH_MS) stop();
      else if (heardSpeech.current && now - lastVoiceAt.current > END_SILENCE_MS) stop();
    }, 100);
    return result;
  }, [stream, stop]);

  return { record, stop, level, recording };
}
