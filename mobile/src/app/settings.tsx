import { router } from "expo-router";
import { useEffect, useState } from "react";
import { Pressable, ScrollView, StyleSheet, Switch, Text, TextInput, View } from "react-native";
import { SafeAreaView } from "react-native-safe-area-context";
import { api, ApiError } from "@/api/client";
import type { DemoState } from "@/api/types";
import { biometricsAvailable, enroll, isEnrolled } from "@/auth/deviceKey";
import { Button, Card, EnvBanner, KenteStripe } from "@/components/ui";
import { SKIP_SIGN_IN } from "@/lib/demoMode";
import { t } from "@/lib/strings";
import { useSession } from "@/state/session";
import { colors, space, touch, type } from "@/theme";

export default function Settings() {
  const session = useSession();
  const { language, prefs, user, system } = session;
  const s = t(language);
  const [bioAvailable, setBioAvailable] = useState(false);
  const [bioOn, setBioOn] = useState(false);
  const [bioPasscode, setBioPasscode] = useState("");
  const [bioMessage, setBioMessage] = useState<string | null>(null);
  const [demo, setDemo] = useState<DemoState | null>(null);
  const [demoMessage, setDemoMessage] = useState<string | null>(null);

  useEffect(() => {
    void (async () => { setBioAvailable(await biometricsAvailable()); setBioOn(await isEnrolled()); })();
    if (user?.isDemo && system?.demoEnabled) api.demoState().then(setDemo).catch(() => undefined);
  }, [user, system]);

  const enrollBio = async () => {
    setBioMessage(null);
    try { await enroll(bioPasscode); setBioOn(true); setBioPasscode(""); setBioMessage(s.enrolled); }
    catch (e) { setBioMessage(e instanceof ApiError ? e.message : s.network); }
  };

  const demoAction = async (fn: () => Promise<unknown>, done: string) => {
    setDemoMessage("…");
    try { await fn(); setDemoMessage(done); setDemo(await api.demoState()); } catch (e) { setDemoMessage(e instanceof ApiError ? e.message : s.network); }
  };

  return (
    <SafeAreaView style={styles.safe} edges={["top", "bottom"]}>
      <KenteStripe />
      <EnvBanner />
      <View style={styles.header}>
        <Button label={`‹ ${s.back}`} variant="ghost" onPress={() => router.back()} />
        <Text style={styles.title} accessibilityRole="header">{s.settings}</Text>
      </View>
      <ScrollView contentContainerStyle={styles.content}>
        <Card style={{ gap: space.sm }}>
          <Text style={styles.label} accessibilityRole="header">{s.language}</Text>
          <View style={styles.row} accessibilityRole="radiogroup">
            {(system?.languages ?? []).map((l) => (
              <Pressable key={l.code} accessibilityRole="radio" accessibilityState={{ selected: language === l.code }} accessibilityLabel={l.name}
                onPress={() => void session.setLanguage(l.code)} style={[styles.choice, language === l.code && styles.choiceOn]}>
                <Text style={[styles.choiceText, language === l.code && { color: colors.gold }]}>{language === l.code ? "● " : "○ "}{l.name}</Text>
              </Pressable>
            ))}
          </View>
        </Card>

        <Card style={{ gap: space.md }}>
          <Toggle label={s.handsFree} value={prefs.handsFree} onChange={(v) => void session.setPrefs({ handsFree: v })} />
          <Toggle label={s.curtain} hint={s.curtainHint} value={prefs.curtain} onChange={(v) => { void session.setPrefs({ curtain: v }); if (v) router.back(); }} />
          <View>
            <Text style={styles.label}>{s.speechRate}: {prefs.speechRate.toFixed(2)}</Text>
            <View style={styles.row}>
              <Button label={`– ${s.slower}`} variant="secondary" style={{ flex: 1 }} onPress={() => void session.setPrefs({ speechRate: Math.max(0.5, +(prefs.speechRate - 0.1).toFixed(2)) })} />
              <Button label={`+ ${s.faster}`} variant="secondary" style={{ flex: 1 }} onPress={() => void session.setPrefs({ speechRate: Math.min(1.5, +(prefs.speechRate + 0.1).toFixed(2)) })} />
            </View>
          </View>
          <Toggle label="English: use this phone's recogniser when available (otherwise Khaya AI)" value={prefs.englishEngine === "auto"}
            onChange={(v) => void session.setPrefs({ englishEngine: v ? "auto" : "khaya" })} />
        </Card>

        {bioAvailable && (
          <Card style={{ gap: space.sm }}>
            <Text style={styles.label} accessibilityRole="header">{s.biometrics}</Text>
            {bioOn ? <Text style={styles.ok}>✓ {s.enrolled}</Text> : (
              <>
                <TextInput value={bioPasscode} onChangeText={(v) => setBioPasscode(v.replace(/\D/g, "").slice(0, 6))} secureTextEntry keyboardType="number-pad"
                  placeholder={s.passcode} placeholderTextColor={colors.muted} style={styles.input} accessibilityLabel={s.passcode} />
                <Button label={s.enroll} onPress={() => void enrollBio()} disabled={bioPasscode.length !== 6} />
              </>
            )}
            {bioMessage && <Text style={styles.message} accessibilityLiveRegion="polite">{bioMessage}</Text>}
          </Card>
        )}

        {demo && (
          <Card style={{ gap: space.sm, borderColor: colors.warning }}>
            <Text style={styles.label} accessibilityRole="header">{s.presenter}</Text>
            <Text style={styles.hint}>{s.scenario}</Text>
            <View style={styles.wrap}>
              {[null, ...demo.scenarios].map((sc) => (
                <Pressable key={sc ?? "default"} accessibilityRole="radio" accessibilityState={{ selected: demo.activeOverride === sc }}
                  onPress={() => void demoAction(() => api.demoScenario(sc), `Scenario: ${sc ?? s.perRecipient}`)}
                  style={[styles.chip, demo.activeOverride === sc && styles.chipOn]}>
                  <Text style={[styles.chipText, demo.activeOverride === sc && { color: colors.goldDark }]}>{sc ?? s.perRecipient}</Text>
                </Pressable>
              ))}
            </View>
            <View style={{ gap: 2 }}>
              {demo.contacts.map((c) => <Text key={c.name} style={styles.hint}>• {c.name} ({c.maskedPhone}) → {c.scenario}</Text>)}
            </View>
            <Button label={s.resetDemo} variant="danger" onPress={() => void demoAction(api.demoReset, "Demo reset. History and conversations cleared.")} />
            <Button label={s.prewarm} variant="secondary" onPress={() => void demoAction(api.demoPrewarm, "Twi demo voice ready.")} />
            {demoMessage && <Text style={styles.message} accessibilityLiveRegion="polite">{demoMessage}</Text>}
          </Card>
        )}

        <Card style={{ gap: space.sm }}>
          <Text style={styles.hint}>{user?.displayName} · {user?.maskedPhoneNumber}</Text>
          <Text style={styles.hint}>{system?.capabilities.fundingSource}</Text>
          {!SKIP_SIGN_IN && <Button label={s.signOut} variant="ghost" onPress={() => void session.logout().then(() => router.replace("/"))} />}
        </Card>
      </ScrollView>
    </SafeAreaView>
  );
}

function Toggle({ label, value, onChange, hint }: { label: string; value: boolean; onChange: (v: boolean) => void; hint?: string }) {
  return (
    <Pressable accessibilityRole="switch" accessibilityState={{ checked: value }} accessibilityLabel={label} accessibilityHint={hint}
      onPress={() => onChange(!value)} style={styles.toggle}>
      <View style={{ flex: 1 }}>
        <Text style={styles.toggleText}>{label}</Text>
        {hint && <Text style={styles.hint}>{hint}</Text>}
      </View>
      <Switch value={value} onValueChange={onChange} trackColor={{ true: colors.gold, false: colors.line }} thumbColor={colors.text}
        accessibilityElementsHidden importantForAccessibility="no" />
    </Pressable>
  );
}

const styles = StyleSheet.create({
  safe: { flex: 1, backgroundColor: colors.background },
  header: { flexDirection: "row", alignItems: "center", gap: space.md, padding: space.md },
  title: { color: colors.text, fontSize: type.title, fontWeight: "900" },
  content: { padding: space.md, gap: space.md, maxWidth: 720, width: "100%", alignSelf: "center" },
  label: { color: colors.text, fontSize: type.heading, fontWeight: "800" },
  hint: { color: colors.muted, fontSize: type.small, lineHeight: 22 },
  ok: { color: colors.success, fontSize: type.body, fontWeight: "800" },
  message: { color: colors.warning, fontSize: type.small, fontWeight: "700" },
  row: { flexDirection: "row", gap: space.sm },
  wrap: { flexDirection: "row", flexWrap: "wrap", gap: space.sm },
  choice: { flex: 1, minHeight: touch, borderRadius: 14, borderWidth: 2, borderColor: colors.line, alignItems: "center", justifyContent: "center" },
  choiceOn: { borderColor: colors.gold },
  choiceText: { color: colors.muted, fontSize: type.body, fontWeight: "800" },
  toggle: { flexDirection: "row", alignItems: "center", gap: space.md, minHeight: touch },
  toggleText: { color: colors.text, fontSize: type.body, fontWeight: "700" },
  input: { minHeight: touch, borderRadius: 14, borderWidth: 2, borderColor: colors.line, color: colors.text, fontSize: type.body, paddingHorizontal: space.md, backgroundColor: colors.raised },
  chip: { minHeight: 48, paddingHorizontal: 14, borderRadius: 999, borderWidth: 2, borderColor: colors.line, justifyContent: "center" },
  chipOn: { backgroundColor: colors.gold, borderColor: colors.gold },
  chipText: { color: colors.text, fontWeight: "800", fontSize: 15 },
});
