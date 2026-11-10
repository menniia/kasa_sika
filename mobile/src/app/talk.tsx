import { Redirect, router } from "expo-router";
import { useEffect, useRef, useState } from "react";
import { AccessibilityInfo, Modal, Pressable, ScrollView, StyleSheet, Text, TextInput, View } from "react-native";
import { SafeAreaView } from "react-native-safe-area-context";
import { Button, Card, EnvBanner, Keypad, KenteStripe, TransactionCard } from "@/components/ui";
import { biometricsAvailable, isEnrolled, signWithBiometrics } from "@/auth/deviceKey";
import { t } from "@/lib/strings";
import { useSession } from "@/state/session";
import { Phase, useConversation } from "@/state/useConversation";
import { colors, space, touch, type } from "@/theme";

export default function Talk() {
  const session = useSession();
  const { user, language, prefs } = session;
  const s = t(language);
  const c = useConversation();
  const [typing, setTyping] = useState(false);
  const [typed, setTyped] = useState("");
  const scroll = useRef<ScrollView>(null);

  useEffect(() => { scroll.current?.scrollToEnd({ animated: true }); }, [c.lines.length, c.reply?.stage]);

  if (!user) return <Redirect href="/" />;

  const r = c.reply;
  const tx = r?.transaction ?? null;
  const confirming = r?.stage === "AWAITING_CONFIRMATION" && tx;
  const needsAuth = r?.stage === "AWAITING_AUTHENTICATION" && !!r.authentication;
  const showTx = tx && (r?.stage === "PROCESSING" || r?.stage === "COMPLETED" || tx.isFinal);

  const submitTyped = () => {
    const text = typed.trim();
    if (!text) return;
    setTyped("");
    void c.sendText(text, "text");
  };

  return (
    <SafeAreaView style={styles.safe} edges={["top", "bottom"]}>
      <KenteStripe />
      <EnvBanner />

      <View style={styles.header}>
        <Text style={styles.title} accessibilityRole="header">Kasa Sika</Text>
        <View style={styles.headerButtons}>
          <LanguageToggle />
          <HeaderButton label={s.history} icon="☰" onPress={() => router.push("/history")} />
          <HeaderButton label={s.settings} icon="⚙" onPress={() => router.push("/settings")} />
        </View>
      </View>

      <ScrollView ref={scroll} style={{ flex: 1 }} contentContainerStyle={styles.scroll}>
        {!c.started && (
          <Card style={{ gap: space.md }}>
            <Text style={styles.welcome}>{`${s.tagline1} ${s.tagline2} ${s.tagline3}`}</Text>
            <Text style={styles.hint}>{s.trySaying}: <Text style={{ color: colors.text, fontWeight: "800" }}>{language === "tw" ? s.exampleTw : s.example}</Text></Text>
            <Button label={s.start} big icon="▶" onPress={() => void c.start()} hint={s.startHint} />
          </Card>
        )}

        {c.lines.map((line) => (
          <View key={line.id} style={[styles.bubble, line.role === "user" ? styles.userBubble : styles.botBubble]}
            accessible accessibilityLabel={`${line.role === "user" ? s.youSaid : s.kasaSika}: ${line.text}`}>
            <Text style={styles.bubbleWho}>
              {line.role === "user" ? `${s.youSaid}${line.by ? ` · ${heardByLabel(line.by)}` : ""}` : s.kasaSika}
            </Text>
            <Text style={styles.bubbleText}>{line.text}</Text>
          </View>
        ))}

        {r?.understood && !confirming && !showTx && (r.stage === "AWAITING_AMOUNT" || r.stage === "AWAITING_RECIPIENT") && (
          <Card label={`${s.understood}: ${r.understood.amountText ?? "amount not yet given"}, ${r.understood.recipientName ?? "recipient not yet given"}`}>
            <Text style={styles.cardLabel}>{s.understood}</Text>
            <Text style={styles.field}>{s.amount}: <Text style={styles.fieldValue}>{r.understood.amountText ?? "—"}</Text></Text>
            <Text style={styles.field}>{s.recipient}: <Text style={styles.fieldValue}>{r.understood.recipientName ?? "—"}</Text></Text>
          </Card>
        )}

        {r?.stage === "CHOOSING_RECIPIENT" && r.candidates && (
          <Card style={{ gap: space.sm }}>
            <Text style={styles.cardLabel}>{s.recipient}?</Text>
            {r.candidates.map((cand) => (
              <Button key={cand.number} variant="secondary" label={`${cand.number}. ${cand.name} · ${cand.maskedPhone}`}
                onPress={() => void c.sendText(String(cand.number), "button")} />
            ))}
          </Card>
        )}

        {confirming && tx && (
          <Card style={styles.confirm} label={`${s.understood}. ${tx.amountText} to ${tx.recipient.displayName}, ${tx.recipient.maskedIdentifier}. ${tx.notice}`}>
            <Text style={styles.cardLabel}>{s.understood}</Text>
            <Text style={styles.confirmAmount}>{tx.amountText}</Text>
            <Text style={styles.confirmName}>→ {tx.recipient.displayName}</Text>
            <Text style={styles.hint}>{tx.recipient.maskedIdentifier}</Text>
            <Text style={styles.notice}>{tx.notice}</Text>
            <View style={{ gap: space.sm, marginTop: space.md }}>
              <Button label={s.confirm} big icon="✓" onPress={() => void c.sendText(language === "tw" ? "aane" : "yes", "button")} />
              <Button label={s.cancel} variant="danger" icon="✕" onPress={() => void c.sendText(language === "tw" ? "gyae" : "cancel", "button")} />
            </View>
          </Card>
        )}

        {showTx && tx && <TransactionCard tx={tx} onDetails={() => router.push(`/transaction/${tx.id}`)} />}

        {c.notice && <Text style={styles.notice} accessibilityRole="alert">{c.notice}</Text>}
      </ScrollView>

      {typing && (
        <View style={styles.typeRow}>
          <TextInput
            value={typed}
            onChangeText={setTyped}
            onSubmitEditing={submitTyped}
            placeholder={s.typePlaceholder}
            placeholderTextColor={colors.muted}
            style={styles.input}
            accessibilityLabel={s.typeInstead}
            returnKeyType="send"
            autoFocus
          />
          <Button label={s.send} onPress={submitTyped} style={{ paddingHorizontal: space.md }} />
        </View>
      )}

      <View style={styles.dock}>
        {c.started && <TalkButton phase={c.phase} level={c.level} onPress={c.talk} language={language} />}
        <View style={styles.dockRow}>
          <Button label={s.repeat} variant="secondary" onPress={c.repeat} style={styles.dockButton} disabled={!r?.speech} />
          <Button label={s.help} variant="secondary" onPress={() => void c.sendText(language === "tw" ? "boa me" : "help", "button")} style={styles.dockButton} disabled={!c.started} />
          <Button label={s.typeInstead} variant="secondary" onPress={() => setTyping((v) => !v)} style={styles.dockButton} />
          <Button label={s.startAgain} variant="secondary" onPress={() => void c.resetConversation()} style={styles.dockButton} disabled={!c.started} />
        </View>
      </View>

      <AuthSheet visible={needsAuth} reply={r} onPasscode={(p) => void c.authenticate({ method: "PASSCODE", passcode: p })}
        onSignature={(sig) => void c.authenticate({ method: "DEVICE_BIOMETRIC", signature: sig })}
        onCancel={() => void c.sendText(language === "tw" ? "gyae" : "cancel", "button")} busy={c.phase === "thinking"} />

      {prefs.curtain && (
        <Curtain phase={c.phase} onTalk={c.started ? c.talk : () => void c.start()} onExit={() => void session.setPrefs({ curtain: false })}
          needsAuth={needsAuth} onPasscode={(p) => void c.authenticate({ method: "PASSCODE", passcode: p })} />
      )}
    </SafeAreaView>
  );
}

function heardByLabel(by: string): string {
  if (by.startsWith("khaya:")) return `Khaya AI (${by.slice(6) === "twi" ? "Twi" : "English"})`;
  if (by === "device") return "this device";
  return by;
}

function HeaderButton({ label, icon, onPress }: { label: string; icon: string; onPress: () => void }) {
  return (
    <Pressable accessibilityRole="button" accessibilityLabel={label} onPress={onPress} style={styles.headerButton}>
      <Text style={styles.headerIcon}>{icon}</Text>
      <Text style={styles.headerLabel}>{label}</Text>
    </Pressable>
  );
}

function LanguageToggle() {
  const { language, setLanguage } = useSession();
  const next = language === "tw" ? "en" : "tw";
  return (
    <Pressable accessibilityRole="button" accessibilityLabel={`Language: ${language === "tw" ? "Twi" : "English"}. Double tap to switch to ${next === "tw" ? "Twi" : "English"}.`}
      onPress={() => void setLanguage(next)} style={styles.langChip}>
      <Text style={[styles.langChipText, language === "en" && styles.langOn]}>EN</Text>
      <Text style={styles.langSep}>|</Text>
      <Text style={[styles.langChipText, language === "tw" && styles.langOn]}>TWI</Text>
    </Pressable>
  );
}

/** The largest control on screen. Its label always says what will happen, for screen readers and eyes. */
function TalkButton({ phase, level, onPress, language }: { phase: Phase; level: number; onPress: () => void; language: string }) {
  const s = t(language);
  const label = phase === "listening" ? s.listening : phase === "thinking" ? s.thinking : phase === "speaking" ? s.speaking : s.tapToSpeak;
  const bars = 12;
  const lit = Math.min(bars, Math.round(Math.sqrt(level) * bars * 2.2));
  return (
    <Pressable
      accessibilityRole="button"
      accessibilityLabel={label}
      accessibilityHint={phase === "idle" ? "Speak your request after the rising tone." : undefined}
      accessibilityState={{ busy: phase === "thinking" }}
      onPress={onPress}
      style={({ pressed }) => [styles.talk, phase === "listening" && styles.talkListening, phase === "thinking" && styles.talkThinking, pressed && { opacity: 0.85 }]}
    >
      <Text style={[styles.talkIcon, phase === "listening" && { color: colors.text }]}>{phase === "listening" ? "●" : phase === "speaking" ? "◼" : phase === "thinking" ? "…" : "🎙"}</Text>
      <Text style={[styles.talkText, phase === "listening" && { color: colors.text }]}>{label}</Text>
      {phase === "listening" && (
        <View style={styles.meter} accessible={false}>
          {Array.from({ length: bars }, (_, i) => <View key={i} style={[styles.meterBar, i < lit && styles.meterLit]} />)}
        </View>
      )}
    </Pressable>
  );
}

function AuthSheet({ visible, reply, onPasscode, onSignature, onCancel, busy }: {
  visible: boolean; reply: ReturnType<typeof useConversation>["reply"]; busy: boolean;
  onPasscode: (p: string) => void; onSignature: (s: string) => void; onCancel: () => void;
}) {
  const { language } = useSession();
  const s = t(language);
  const [code, setCode] = useState("");
  const [bio, setBio] = useState(false);
  const tx = reply?.transaction;

  useEffect(() => {
    setCode("");
    if (!visible) return;
    AccessibilityInfo.announceForAccessibility(s.verifyTitle);
    void (async () => setBio((await biometricsAvailable()) && (await isEnrolled())))();
  }, [visible, reply?.authentication?.challengeId]); // eslint-disable-line react-hooks/exhaustive-deps

  useEffect(() => { if (code.length === 6) onPasscode(code); }, [code]); // eslint-disable-line react-hooks/exhaustive-deps

  const fingerprint = async () => {
    if (!reply?.authentication) return;
    const sig = await signWithBiometrics(reply.authentication.signingMessage, s.verifyTitle);
    if (sig) onSignature(sig);
  };

  return (
    <Modal visible={visible} animationType="slide" transparent onRequestClose={onCancel}>
      <View style={styles.sheetBackdrop}>
        <View style={styles.sheet} accessibilityViewIsModal>
          <Text style={styles.sheetTitle} accessibilityRole="header">🔒 {s.verifyTitle}</Text>
          {tx && <Text style={styles.sheetWhat}>{tx.amountText} → {tx.recipient.displayName} ({tx.recipient.maskedIdentifier})</Text>}
          <Text style={styles.hint}>{s.verifyHint}</Text>
          {bio && <Button label={s.useFingerprint} icon="☝" big onPress={() => void fingerprint()} style={{ marginTop: space.md }} disabled={busy} />}
          <Keypad value={code} onChange={setCode} disabled={busy} />
          <Button label={s.cancel} variant="danger" onPress={onCancel} disabled={busy} />
        </View>
      </View>
    </Modal>
  );
}

/**
 * Eyes-free mode for the "no sight required" demonstration: the screen goes black and the WHOLE screen
 * becomes the talk button. Triple-tap the top-left corner to lift it. When verification is needed the
 * keypad appears, dimmed, in the standard phone layout so it can be used by position.
 */
function Curtain({ phase, onTalk, onExit, needsAuth, onPasscode }: { phase: Phase; onTalk: () => void; onExit: () => void; needsAuth: boolean; onPasscode: (p: string) => void }) {
  const { language } = useSession();
  const s = t(language);
  const taps = useRef<number[]>([]);
  const [code, setCode] = useState("");
  useEffect(() => { if (code.length === 6) { onPasscode(code); setCode(""); } }, [code]); // eslint-disable-line react-hooks/exhaustive-deps
  useEffect(() => { if (!needsAuth) setCode(""); }, [needsAuth]);
  const corner = () => {
    const now = Date.now();
    taps.current = [...taps.current.filter((x) => now - x < 1200), now];
    if (taps.current.length >= 3) onExit();
  };
  return (
    <View style={styles.curtain}>
      <Pressable style={styles.curtainCorner} onPress={corner} accessibilityRole="button" accessibilityLabel="Exit eyes-free mode (triple tap)" />
      {needsAuth ? (
        <View style={{ opacity: 0.55, marginTop: 80 }}>
          <Keypad value={code} onChange={setCode} />
        </View>
      ) : (
        <Pressable style={StyleSheet.absoluteFill} onPress={onTalk} accessibilityRole="button"
          accessibilityLabel={`Eyes-free mode. ${phase === "listening" ? s.listening : s.tapToSpeak}. Double tap anywhere.`}>
          <Text style={styles.curtainText}>{phase === "listening" ? "●" : ""}</Text>
        </Pressable>
      )}
    </View>
  );
}

const styles = StyleSheet.create({
  safe: { flex: 1, backgroundColor: colors.background },
  header: { flexDirection: "row", alignItems: "center", justifyContent: "space-between", paddingHorizontal: space.md, paddingVertical: space.sm, flexWrap: "wrap", gap: space.sm },
  title: { color: colors.text, fontSize: type.title, fontWeight: "900" },
  headerButtons: { flexDirection: "row", gap: space.sm, alignItems: "center" },
  headerButton: { minHeight: 52, minWidth: 64, paddingHorizontal: 10, borderRadius: 14, backgroundColor: colors.surface, alignItems: "center", justifyContent: "center", borderWidth: 1, borderColor: colors.line },
  headerIcon: { color: colors.text, fontSize: 18 },
  headerLabel: { color: colors.muted, fontSize: 12, fontWeight: "700" },
  langChip: { minHeight: 52, flexDirection: "row", alignItems: "center", paddingHorizontal: 12, borderRadius: 14, borderWidth: 2, borderColor: colors.gold, gap: 6 },
  langChipText: { color: colors.muted, fontWeight: "800", fontSize: 16 },
  langOn: { color: colors.gold },
  langSep: { color: colors.line, fontSize: 16 },
  scroll: { padding: space.md, gap: space.md, maxWidth: 720, width: "100%", alignSelf: "center" },
  welcome: { color: colors.text, fontSize: 30, fontWeight: "900", lineHeight: 38 },
  hint: { color: colors.muted, fontSize: type.small, lineHeight: 24 },
  bubble: { borderRadius: 18, padding: space.md, maxWidth: "92%" },
  userBubble: { alignSelf: "flex-end", backgroundColor: colors.raised, borderWidth: 1, borderColor: colors.gold },
  botBubble: { alignSelf: "flex-start", backgroundColor: colors.surface, borderWidth: 1, borderColor: colors.line },
  bubbleWho: { color: colors.muted, fontSize: 13, fontWeight: "800", textTransform: "uppercase", letterSpacing: 0.5, marginBottom: 4 },
  bubbleText: { color: colors.text, fontSize: type.body, lineHeight: 29 },
  cardLabel: { color: colors.muted, fontSize: 14, fontWeight: "800", textTransform: "uppercase", letterSpacing: 1 },
  field: { color: colors.muted, fontSize: type.body, marginTop: space.sm },
  fieldValue: { color: colors.text, fontWeight: "800" },
  confirm: { borderColor: colors.gold, borderWidth: 2 },
  confirmAmount: { color: colors.text, fontSize: 48, fontWeight: "900", marginTop: space.sm, fontVariant: ["tabular-nums"] },
  confirmName: { color: colors.text, fontSize: 28, fontWeight: "800" },
  notice: { color: colors.warning, fontSize: type.small, fontWeight: "700", marginTop: space.sm },
  typeRow: { flexDirection: "row", gap: space.sm, paddingHorizontal: space.md, paddingBottom: space.sm, maxWidth: 720, width: "100%", alignSelf: "center" },
  input: { flex: 1, minHeight: touch, borderRadius: 16, borderWidth: 2, borderColor: colors.line, backgroundColor: colors.surface, color: colors.text, fontSize: type.body, paddingHorizontal: space.md },
  dock: { padding: space.md, gap: space.sm, borderTopWidth: 1, borderTopColor: colors.line, backgroundColor: colors.background, maxWidth: 720, width: "100%", alignSelf: "center" },
  dockRow: { flexDirection: "row", gap: space.sm, flexWrap: "wrap" },
  dockButton: { flexGrow: 1, flexBasis: 140, minHeight: 56, paddingHorizontal: space.sm },
  talk: { minHeight: 132, borderRadius: 28, backgroundColor: colors.gold, alignItems: "center", justifyContent: "center", paddingVertical: space.md },
  talkListening: { backgroundColor: "#8A2E24", borderWidth: 4, borderColor: colors.danger },
  talkThinking: { backgroundColor: colors.raised },
  talkIcon: { fontSize: 36, color: colors.goldDark },
  talkText: { color: colors.goldDark, fontSize: 24, fontWeight: "900", marginTop: 4, textAlign: "center" },
  meter: { flexDirection: "row", gap: 4, marginTop: space.sm, height: 18, alignItems: "flex-end" },
  meterBar: { width: 10, height: 18, borderRadius: 3, backgroundColor: "rgba(255,255,255,0.2)" },
  meterLit: { backgroundColor: colors.text },
  sheetBackdrop: { flex: 1, backgroundColor: "rgba(0,0,0,0.7)", justifyContent: "flex-end" },
  sheet: { backgroundColor: colors.surface, borderTopLeftRadius: 28, borderTopRightRadius: 28, padding: space.lg, gap: space.sm, maxWidth: 720, width: "100%", alignSelf: "center" },
  sheetTitle: { color: colors.text, fontSize: type.title, fontWeight: "900" },
  sheetWhat: { color: colors.gold, fontSize: type.heading, fontWeight: "800" },
  curtain: { position: "absolute", top: 0, left: 0, right: 0, bottom: 0, backgroundColor: "#000", alignItems: "center" },
  curtainCorner: { position: "absolute", top: 0, left: 0, width: 90, height: 90, zIndex: 2 },
  curtainText: { color: "#300", fontSize: 40, textAlign: "center", marginTop: "60%" },
});
