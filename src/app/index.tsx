import { createFromVoiceIntent, Transaction, transition } from "@/api";
import { SupportedLanguage, t } from "@/i18n";
import { useVoiceIntent } from "@/useVoiceIntent";
import * as Speech from "expo-speech";
import { useEffect, useState } from "react";
import { AccessibilityInfo, Text, View } from "react-native";
import { ScrollView } from "react-native-reanimated/lib/typescript/Animated";
import { SafeAreaView } from "react-native-safe-area-context";

type Screen =
  | "HOME"
  | "CONFIRM"
  | "AUTH"
  | "AUTHORIZE"
  | "PROCESSING"
  | "RESULT";

export default function HomeScreen() {
  const [language, setLanguage] = useState<SupportedLanguage>("en");
  const [screen, setScreen] = useState<Screen>("HOME");
  const [transaction, setTransaction] = useState<Transaction | null>(null);
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState(
    "Welcome to Kasa Sika. Demo mode, No real money moved",
  );
  const copy = t(language);
  const speak = (value: string): void => {
    setMessage(value);
    AccessibilityInfo.announceForAccessibility(value);
    Speech.stop();
    Speech.speak(value, { rate: 0.85, language: "en-GH" });
  };
  const fail = (error: unknown): void =>
    speak(
      `I could not complete that safely. ${error instanceof Error ? error.message : "Please try again"}`,
    );
  const onTranscript = async (utterance: string): Promise<void> => {
    try {
      setBusy(true);
      speak(`I heard: ${utterance}. Checking your request`);
      const created = await createFromVoiceIntent(utterance);
      setTransaction(created);
      setScreen("CONFIRM");
      speak(
        `${created.spokenSummary} Say confirm or use the confirm transfer button to continue`,
      );
    } catch (error) {
      fail(error);
    } finally {
      setBusy(false);
    }
  };
  const voice = useVoiceIntent((value) => {
    void onTranscript(value);
  });
  const advance = async (
    action: "confirm" | "authenticate" | "authorize" | "submit" | "status",
    next: Screen,
    spoken: string,
  ): Promise<void> => {
    if (transaction === null) return;
    try {
      setBusy(true);
      const updated = await transition(transaction.id, action);
      setTransaction(updated);
      setScreen(next);
      speak(spoken);
    } catch (error) {
      fail(error);
    } finally {
      setBusy(false);
    }
  };
  useEffect(() => {
    speak(message);
  }, []);
  useEffect(() => {
    if (
      transaction?.state === "SUCCESS" ||
      transaction?.state === "FAILED" ||
      transaction?.state === "RECONCILIATION_REQUIRED"
    ) {
      setScreen("RESULT");
      speak(
        transaction.state === "SUCCESS"
          ? "Demo provider confirmed success =. No real money moved."
          : transaction.state == "RECONCILIATION_REQUIRED"
            ? "I could not confirm the final status. It has not marked as failed"
            : "Demo provider reported a failure",
      );
    }
  }, [transaction?.state]);
  const action =
    screen === "HOME"
      ? {
          label: copy.start,
          run: () => {
            void voice.start();
          },
        }
      : screen === "CONFIRM"
        ? {
            label: copy.confirm,
            run: () => {
              void advance(
                "confirm",
                "AUTH",
                "Transaction confirmed. Verify your identity",
              );
            },
          }
        : screen === "AUTH"
          ? {
              label: copy.authenticate,
              run: () => {
                void advance(
                  "authenticate",
                  "AUTHORIZE",
                  "Demo identity verification completed. This is not live biometric verification.",
                );
              },
            }
          : screen === "AUTHORIZE"
            ? {
                label: copy.authorize,
                run: () => {
                  void advance(
                    "authorize",
                    "PROCESSING",
                    "Transaction authorization context created.",
                  );
                },
              }
            : screen === "PROCESSING"
              ? {
                  label:
                    transaction?.state === "PENDING" ? copy.check : copy.send,
                  run: () => {
                    void advance(
                      transaction?.state === "PENDING" ? "status" : "submit",
                      "PROCESSING",
                      transaction?.state === "PENDING"
                        ? "Checking provider status. The money is not confirmed until the provider confirms it."
                        : "Demo transaction submitted. No real money moved. The provider result is pending.",
                    );
                  },
                }
              : {
                  label: copy.start,
                  run: () => {
                    setTransaction(null);
                    setScreen("HOME");
                    speak("Ready for another demo transaction.");
                  },
                };
  return (
    <SafeAreaView accessibilityLabel="Kasa Sika accessible mobile money demo">
      <ScrollView>
        <View>
          <Text>{copy.title}</Text>
          <Text>{copy.demo}</Text>
        </View>
        <View accessibilityLiveRegion="polite">
          <Text>Current guidance</Text>
          <Text>{message}</Text>
        </View>
        {transaction !== null && (
          <View
            accessibilityLabel={`Transaction state ${transaction.state}`}
          ></View>
        )}
      </ScrollView>
    </SafeAreaView>
  );
}
