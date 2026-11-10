import { Redirect, router } from "expo-router";
import { useEffect, useState } from "react";
import { ActivityIndicator, Pressable, ScrollView, StyleSheet, Text, View } from "react-native";
import { SafeAreaView } from "react-native-safe-area-context";
import { Button, EnvBanner, KenteStripe } from "@/components/ui";
import { SKIP_SIGN_IN } from "@/lib/demoMode";
import { t } from "@/lib/strings";
import { useSession } from "@/state/session";
import { colors, space, touch, type } from "@/theme";

/**
 * First screen. Within seconds a judge should know: who it is for, what it does, and how to try it.
 * Language is the first choice, before anything else.
 */
export default function Landing() {
  const session = useSession();
  const { ready, user, language, system, systemError } = session;
  const s = t(language);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  if (!ready) return <View style={styles.center}><ActivityIndicator size="large" color={colors.gold} /></View>;
  if (user) return <Redirect href="/talk" />;
  if (SKIP_SIGN_IN) return <Connecting />;

  const demo = async () => {
    setBusy(true);
    setError(null);
    try { await session.demoLogin(); router.replace("/talk"); }
    catch { setError(s.network); }
    finally { setBusy(false); }
  };

  const languages = system?.languages ?? [{ code: "en", name: "English", nativeName: "English" }, { code: "tw", name: "Twi", nativeName: "Twi" }];

  return (
    <SafeAreaView style={styles.safe} edges={["top", "bottom"]}>
      <KenteStripe />
      <EnvBanner />
      <ScrollView contentContainerStyle={styles.content}>
        <Text style={styles.brand} accessibilityRole="header">Kasa Sika</Text>
        <Text style={styles.meaning}>{s.meaning}</Text>

        <View style={styles.tagline} accessible accessibilityLabel={`${s.tagline1} ${s.tagline2} ${s.tagline3}`}>
          <Text style={styles.tag}>{s.tagline1}</Text>
          <Text style={styles.tag}>{s.tagline2}</Text>
          <Text style={[styles.tag, { color: colors.gold }]}>{s.tagline3}</Text>
        </View>

        <Text style={styles.section} accessibilityRole="header">{s.chooseLanguage}</Text>
        <View style={styles.langRow} accessibilityRole="radiogroup">
          {languages.map((l) => {
            const selected = language === l.code;
            return (
              <Pressable
                key={l.code}
                accessibilityRole="radio"
                accessibilityState={{ selected }}
                accessibilityLabel={l.name === l.nativeName ? l.name : `${l.name}, ${l.nativeName}`}
                onPress={() => void session.setLanguage(l.code)}
                style={[styles.lang, selected && styles.langSelected]}
              >
                <Text style={[styles.langText, selected && styles.langTextSelected]}>{selected ? "● " : "○ "}{l.name}</Text>
              </Pressable>
            );
          })}
        </View>

        <View style={styles.try} accessible accessibilityLabel={`${s.trySaying}: ${language === "tw" ? s.exampleTw : s.example}`}>
          <Text style={styles.tryLabel}>{s.trySaying}</Text>
          <Text style={styles.tryText}>{language === "tw" ? s.exampleTw : s.example}</Text>
        </View>

        {system?.demoLogin && (
          <Button label={s.continueDemo} big onPress={demo} disabled={busy} hint="Signs in to the demonstration account. Payments are simulated." />
        )}
        <Button label={s.signIn} variant="ghost" onPress={() => router.push("/sign-in")} style={{ marginTop: space.md }} />
        {(error || systemError) && (
          <Text style={styles.error} accessibilityRole="alert">{error ?? `${s.network} (${"API: check the backend is running"})`}</Text>
        )}

        <View style={styles.why}>
          <Text style={styles.section} accessibilityRole="header">{s.whyTitle}</Text>
          <Text style={styles.body}>{s.why}</Text>
          <Text style={[styles.body, { marginTop: space.md, color: colors.gold, fontWeight: "800" }]}>
            Kasa Sika authenticates the person. MTN authorises the money.
          </Text>
        </View>
      </ScrollView>
    </SafeAreaView>
  );
}

/** Demo mode only: the silent sign-in did not succeed (usually the backend is not running). Retries on open and on tap. */
function Connecting() {
  const session = useSession();
  const s = t(session.language);
  const [busy, setBusy] = useState(true);
  const [failed, setFailed] = useState(false);

  const connect = async () => {
    setBusy(true);
    setFailed(false);
    try { await session.refreshSystem(); await session.demoLogin(); router.replace("/talk"); }
    catch { setFailed(true); }
    finally { setBusy(false); }
  };

  useEffect(() => { void connect(); }, []); // eslint-disable-line react-hooks/exhaustive-deps

  return (
    <SafeAreaView style={styles.safe} edges={["top", "bottom"]}>
      <KenteStripe />
      <View style={[styles.content, { flex: 1, justifyContent: "center" }]}>
        <Text style={styles.brand} accessibilityRole="header">Kasa Sika</Text>
        {busy && <ActivityIndicator size="large" color={colors.gold} />}
        {failed && (
          <>
            <Text style={styles.error} accessibilityRole="alert">{`${s.network} (API: check the backend is running)`}</Text>
            <Button label={s.startAgain} big onPress={() => void connect()} />
          </>
        )}
      </View>
    </SafeAreaView>
  );
}

const styles = StyleSheet.create({
  safe: { flex: 1, backgroundColor: colors.background },
  center: { flex: 1, backgroundColor: colors.background, alignItems: "center", justifyContent: "center" },
  content: { padding: space.lg, gap: space.md, maxWidth: 640, width: "100%", alignSelf: "center" },
  brand: { color: colors.text, fontSize: 52, fontWeight: "900", letterSpacing: -1, marginTop: space.md },
  meaning: { color: colors.muted, fontSize: type.small, marginTop: -space.sm },
  tagline: { marginVertical: space.sm },
  tag: { color: colors.text, fontSize: 34, fontWeight: "800", lineHeight: 42 },
  section: { color: colors.text, fontSize: type.heading, fontWeight: "800", marginTop: space.sm },
  langRow: { flexDirection: "row", gap: space.md },
  lang: { flex: 1, minHeight: touch + 8, borderRadius: 16, borderWidth: 2, borderColor: colors.line, backgroundColor: colors.surface, alignItems: "center", justifyContent: "center" },
  langSelected: { borderColor: colors.gold, backgroundColor: colors.raised },
  langText: { color: colors.muted, fontSize: 24, fontWeight: "800" },
  langTextSelected: { color: colors.gold },
  try: { backgroundColor: colors.surface, borderRadius: 20, padding: space.lg, borderLeftWidth: 6, borderLeftColor: colors.gold },
  tryLabel: { color: colors.muted, fontSize: type.small, fontWeight: "700", textTransform: "uppercase", letterSpacing: 1 },
  tryText: { color: colors.text, fontSize: 28, fontWeight: "800", marginTop: space.xs },
  error: { color: colors.danger, fontSize: type.small, fontWeight: "700" },
  why: { marginTop: space.lg, paddingTop: space.md, borderTopWidth: 1, borderTopColor: colors.line },
  body: { color: colors.muted, fontSize: type.body, lineHeight: 30 },
});
