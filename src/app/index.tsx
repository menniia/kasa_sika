import "@/global.css";

import { createFromVoiceIntent, Transaction, transition } from "@/api";
import { SupportedLanguage, t } from "@/i18n";
import { useVoiceIntent } from "@/useVoiceIntent";
import * as Speech from "expo-speech";
import { useEffect, useState } from "react";
import {
  AccessibilityInfo,
  ActivityIndicator,
  Pressable,
  ScrollView,
  Text,
  View,
} from "react-native";
import { SafeAreaView } from "react-native-safe-area-context";

type Screen =
  "HOME" | "CONFIRM" | "AUTH" | "AUTHORIZE" | "PROCESSING" | "RESULT";

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
    <SafeAreaView
      className="flex-1 bg-[#061827]"
      accessibilityLabel="Kasa Sika accessible mobile money demo"
    >
      <ScrollView contentContainerClassName="p-6 gap-5">
        {/* header */}
        <View className="gap-2">
          <Text className="text-[#FFFFFF] text-[36px] font-extrabold">
            {copy.title}
          </Text>

          <Text className="text-[#071E2E] bg-[#FBBF24] p-3 rounded-[10px] font-extrabold text-base">
            {copy.demo}
          </Text>
        </View>

        {/* current guidance  */}
        <View
          accessibilityLiveRegion="polite"
          className="bg-[#FFFFFF] rounded-2xl p-5 gap-2.5"
        >
          <Text className="text-[#334155] text-base font-bold">
            Current guidance
          </Text>

          <Text className="text-[#0F172A] text-[21px] leading-[30px]">
            {message}
          </Text>
        </View>

        {/* transaction status */}
        {transaction !== null && (
          <View
            className="bg-[#FFFFFF] rounded-2xl p-5 gap-2.5"
            accessibilityLabel={`Transaction state ${transaction.state}`}
          >
            <Text className="text-[#334155] text-base font-bold">
              Transaction status
            </Text>

            <Text className="text-[#075985] text-[26px] font-extrabold">
              {transaction.state}
            </Text>

            <Text className="text-[#0F172A] text-[21px] leading-[30px]">
              {transaction.spokenSummary}
            </Text>
          </View>
        )}

        {/* listening */}
        {voice.isListening && (
          <View className="bg-[#FFFFFF] rounded-2xl p-5 gap-2.5">
            <Text className="text-[#0F172A] text-[21px] leading-[30px]">
              {copy.listening}
            </Text>

            <ActivityIndicator size="large" color="#062033" />
          </View>
        )}

        {/* error */}
        {voice.error !== null && (
          <Text
            accessibilityRole="alert"
            className="text-[#FDE68A] text-lg leading-[26px]"
          >
            {voice.error}. {copy.fallback}.
          </Text>
        )}

        {/* demo fallback */}
        {screen === "HOME" && (
          <Pressable
            accessibilityRole="button"
            accessibilityLabel="Use demo voice request: Send fifty cedis to Ama."
            accessibilityHint="Runs the same backend intent flow without needing microphone permission"
            className="min-h-[72px] justify-center items-center rounded-2xl border-2 border-[#BAE6FD] p-4"
            onPress={() => void onTranscript("Send fifty cedis to Ama")}
          >
            <Text className="text-white text-xl font-bold text-center">
              {copy.fallback}
            </Text>
          </Pressable>
        )}

        {/* primary action */}
        <Pressable
          accessibilityRole="button"
          accessibilityLabel={action.label}
          accessibilityHint="Double tap to continue the secure demo transaction"
          disabled={busy}
          className={`min-h-[88px] justify-center items-center rounded-2xl bg-[#38BDF8] p-4 ${
            busy ? "opacity-[0.55]" : ""
          }`}
          onPress={action.run}
        >
          <Text className="text-[#062033] text-2xl font-extrabold text-center">
            {busy ? "Please wait" : action.label}
          </Text>
        </Pressable>

        {/* language */}
        <Pressable
          accessibilityRole="button"
          accessibilityLabel="Switch language"
          className="min-h-[56px] items-center justify-center"
          onPress={() => setLanguage(language === "en" ? "tw" : "en")}
        >
          <Text className="text-[#BAE6FD] text-lg font-bold">
            {language === "en" ? "Twi" : "English"}
          </Text>
        </Pressable>
      </ScrollView>
    </SafeAreaView>
  );
}
