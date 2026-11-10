import { createAudioPlayer } from "expo-audio";
import * as Haptics from "expo-haptics";
import { Platform } from "react-native";

/**
 * Non-speech cues, so a user who cannot see the screen always knows when the microphone is open.
 * Rising tone = listening, falling tone = stopped, arpeggio = success, double beep = your action needed.
 * Paired with a vibration on phones.
 */
const sources = {
  listenStart: require("../../assets/sounds/listen-start.wav"),
  listenStop: require("../../assets/sounds/listen-stop.wav"),
  success: require("../../assets/sounds/success.wav"),
  attention: require("../../assets/sounds/attention.wav"),
} as const;

export type Earcon = keyof typeof sources;

export function playEarcon(name: Earcon): Promise<void> {
  if (Platform.OS !== "web") {
    const style = name === "attention" ? Haptics.NotificationFeedbackType.Warning
      : name === "success" ? Haptics.NotificationFeedbackType.Success : null;
    if (style) void Haptics.notificationAsync(style).catch(() => undefined);
    else void Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Medium).catch(() => undefined);
  }
  return new Promise((resolve) => {
    try {
      const player = createAudioPlayer(sources[name]);
      const fallback = setTimeout(() => { try { player.remove(); } catch { /* ignore */ } resolve(); }, 700);
      const sub = player.addListener("playbackStatusUpdate", (s) => {
        if (s.didJustFinish) {
          clearTimeout(fallback);
          sub.remove();
          try { player.remove(); } catch { /* ignore */ }
          resolve();
        }
      });
      player.play();
    } catch {
      resolve();
    }
  });
}
