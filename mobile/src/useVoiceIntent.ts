import {
    ExpoSpeechRecognitionModule,
    useSpeechRecognitionEvent,
} from "expo-speech-recognition";
import { useEffect, useState } from "react";

export function useVoiceIntent(onTranscript: (value: string) => void) {
  const [isListening, setListening] = useState(false);
  const [error, setError] = useState<string | null>(null);
  useSpeechRecognitionEvent("result", (event) => {
    const transcript = event.results[0]?.transcript;
    if (event.isFinal && transcript !== undefined) onTranscript(transcript);
  });
  useEffect(() => () => ExpoSpeechRecognitionModule.abort(), []);
  const start = async (): Promise<void> => {
    setError(null);
    const permission =
      await ExpoSpeechRecognitionModule.requestPermissionsAsync();
    if (!permission.granted) {
      setError("MICROPHONE_OR-SPEECH_PERMISSION_DENIED");
      return;
    }
    setListening(true);
    ExpoSpeechRecognitionModule.start({
      lang: "en-GH",
      interimResults: true,
      continuous: false,
    });
  };
  return { isListening, error, start };
}
