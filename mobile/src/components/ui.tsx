import * as Haptics from "expo-haptics";
import { ReactNode } from "react";
import { Platform, Pressable, StyleSheet, Text, View, ViewStyle } from "react-native";
import type { Transaction } from "@/api/types";
import { t } from "@/lib/strings";
import { useSession } from "@/state/session";
import { colors, space, touch, toneStyle, type } from "@/theme";

/** Thin kente-inspired stripe: the only decoration, and a quiet signal that this is a Ghanaian product. */
export function KenteStripe() {
  return (
    <View style={styles.kente} accessible={false} importantForAccessibility="no-hide-descendants">
      {colors.kente.map((c, i) => <View key={i} style={{ flex: 1, backgroundColor: c }} />)}
    </View>
  );
}

/**
 * Always-visible environment indicator. Real money is never implied: the label comes from the server
 * ("DEMO — MOCK PAYMENTS" / "MTN SANDBOX — TEST MONEY"), and "LIVE" does not exist in this prototype.
 */
export function EnvBanner() {
  const { system, online, language, systemError } = useSession();
  const s = t(language);
  return (
    <View>
      <View style={styles.env} accessibilityRole="header" accessibilityLabel={`${system?.environmentLabel ?? "Not connected"}. No real money moves.`}>
        <Text style={styles.envText}>{system?.environmentLabel ?? (systemError ? "NOT CONNECTED TO KASA SIKA" : "CONNECTING…")}</Text>
        <Text style={styles.envSub}>No real money moves</Text>
      </View>
      {!online && (
        <View style={styles.offline} accessibilityRole="alert" accessibilityLiveRegion="assertive">
          <Text style={styles.offlineText}>⚠ {s.offlineBanner}</Text>
        </View>
      )}
    </View>
  );
}

type ButtonProps = {
  label: string;
  onPress: () => void;
  variant?: "primary" | "secondary" | "ghost" | "danger";
  hint?: string;
  disabled?: boolean;
  style?: ViewStyle;
  big?: boolean;
  icon?: string;
};

export function Button({ label, onPress, variant = "primary", hint, disabled, style, big, icon }: ButtonProps) {
  const v = variantStyles[variant];
  return (
    <Pressable
      accessibilityRole="button"
      accessibilityLabel={label}
      accessibilityHint={hint}
      accessibilityState={{ disabled: !!disabled }}
      disabled={disabled}
      onPress={onPress}
      style={({ pressed }) => [styles.button, big && styles.buttonBig, v.box, pressed && { opacity: 0.8 }, disabled && { opacity: 0.45 }, style]}
    >
      <Text style={[styles.buttonText, big && styles.buttonTextBig, v.text]} maxFontSizeMultiplier={1.6}>
        {icon ? `${icon}  ` : ""}{label}
      </Text>
    </Pressable>
  );
}

const variantStyles = {
  primary: { box: { backgroundColor: colors.gold }, text: { color: colors.goldDark } },
  secondary: { box: { backgroundColor: colors.raised, borderWidth: 2, borderColor: colors.line }, text: { color: colors.text } },
  ghost: { box: { backgroundColor: "transparent", borderWidth: 2, borderColor: colors.line }, text: { color: colors.text } },
  danger: { box: { backgroundColor: "transparent", borderWidth: 2, borderColor: colors.danger }, text: { color: colors.danger } },
};

/** Status with icon + words; colour is reinforcement only. */
export function StatusBadge({ tone, label }: { tone: Transaction["statusTone"]; label: string }) {
  const s = toneStyle[tone];
  return (
    <View style={[styles.badge, { borderColor: s.color }]} accessible accessibilityLabel={`Status: ${label}`}>
      <Text style={[styles.badgeIcon, { color: s.color }]}>{s.icon}</Text>
      <Text style={[styles.badgeText, { color: s.color }]}>{label}</Text>
    </View>
  );
}

export function Card({ children, style, label }: { children: ReactNode; style?: ViewStyle; label?: string }) {
  return (
    <View style={[styles.card, style]} accessible={!!label} accessibilityLabel={label}>
      {children}
    </View>
  );
}

/** The transaction and the four things a user needs to trust it: asked, authorised, provider says, now. */
export function TransactionCard({ tx, onDetails }: { tx: Transaction; onDetails?: () => void }) {
  const { language } = useSession();
  const s = t(language);
  const summary = `${tx.statusLabel}. ${tx.amountText} to ${tx.recipient.displayName}, ${tx.recipient.maskedIdentifier}. ${tx.trust.providerReport}.${tx.reference ? ` Reference ${tx.reference}.` : ""}`;
  return (
    <Card label={summary}>
      <View style={styles.rowBetween}>
        <StatusBadge tone={tx.statusTone} label={tx.statusLabel} />
        <Text style={styles.small}>{new Date(tx.createdAt).toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" })}</Text>
      </View>
      <Text style={styles.amount}>{tx.amountText}</Text>
      <Text style={styles.recipient}>{tx.recipient.displayName}</Text>
      <Text style={styles.muted}>{tx.recipient.maskedIdentifier}</Text>
      <View style={styles.trust}>
        <TrustRow label={s.requested} value={tx.trust.requested} />
        {tx.trust.authorized && <TrustRow label={s.authorized} value={tx.trust.authorized} />}
        <TrustRow label={s.providerReports} value={tx.trust.providerReport} />
        {tx.reference && <TrustRow label={s.reference} value={tx.reference} />}
      </View>
      <Text style={styles.notice}>{tx.notice}</Text>
      {onDetails && <Button label={s.timeline} variant="ghost" onPress={onDetails} style={{ marginTop: space.sm }} />}
    </Card>
  );
}

function TrustRow({ label, value }: { label: string; value: string }) {
  return (
    <View style={{ marginTop: space.sm }}>
      <Text style={styles.trustLabel}>{label}</Text>
      <Text style={styles.trustValue}>{value}</Text>
    </View>
  );
}

/**
 * Numeric keypad for the Kasa Sika passcode. Standard phone layout (1-2-3 on top) so it can be used by
 * position; each press vibrates and is announced as a COUNT, never as the digit, so a bystander or a
 * screen reader in a crowded place does not reveal it.
 */
export function Keypad({ value, onChange, disabled }: { value: string; onChange: (v: string) => void; disabled?: boolean }) {
  const { language } = useSession();
  const s = t(language);
  const press = (k: string) => {
    if (disabled) return;
    if (Platform.OS !== "web") void Haptics.selectionAsync().catch(() => undefined);
    if (k === "del") onChange(value.slice(0, -1));
    else if (value.length < 6) onChange(value + k);
  };
  // Explicit rows: always the standard phone layout, whatever the screen width, so it works by position.
  const rows = [["1", "2", "3"], ["4", "5", "6"], ["7", "8", "9"], ["", "0", "del"]];
  return (
    <View>
      <View style={styles.dots} accessible accessibilityLiveRegion="polite" accessibilityLabel={s.digitsEntered(value.length)}>
        {Array.from({ length: 6 }, (_, i) => <View key={i} style={[styles.dot, i < value.length && styles.dotFilled]} />)}
      </View>
      <View style={styles.keypad}>
        {rows.map((row, r) => (
          <View key={r} style={styles.keyRow}>
            {row.map((k, i) => k === "" ? <View key={i} style={styles.key} /> : (
              <Pressable
                key={i}
                accessibilityRole="button"
                accessibilityLabel={k === "del" ? s.delete : k}
                onPress={() => press(k)}
                style={({ pressed }) => [styles.key, styles.keyActive, pressed && { backgroundColor: colors.line }]}
              >
                <Text style={styles.keyText}>{k === "del" ? "⌫" : k}</Text>
              </Pressable>
            ))}
          </View>
        ))}
      </View>
    </View>
  );
}

export const styles = StyleSheet.create({
  kente: { flexDirection: "row", height: 6 },
  env: { backgroundColor: colors.gold, paddingVertical: 6, paddingHorizontal: space.md, flexDirection: "row", justifyContent: "space-between", alignItems: "center", flexWrap: "wrap" },
  envText: { color: colors.goldDark, fontWeight: "900", fontSize: type.small, letterSpacing: 0.5 },
  envSub: { color: colors.goldDark, fontWeight: "700", fontSize: 14 },
  offline: { backgroundColor: colors.danger, paddingVertical: 8, paddingHorizontal: space.md },
  offlineText: { color: "#2B0A08", fontWeight: "800", fontSize: type.small },
  button: { minHeight: touch, borderRadius: 16, paddingHorizontal: space.lg, paddingVertical: space.sm, alignItems: "center", justifyContent: "center" },
  buttonBig: { minHeight: 76 },
  buttonText: { fontSize: type.body, fontWeight: "800", textAlign: "center" },
  buttonTextBig: { fontSize: 24 },
  badge: { flexDirection: "row", alignItems: "center", borderWidth: 2, borderRadius: 999, paddingHorizontal: 12, paddingVertical: 4, alignSelf: "flex-start" },
  badgeIcon: { fontSize: 18, fontWeight: "900", marginRight: 6 },
  badgeText: { fontSize: type.small, fontWeight: "800" },
  card: { backgroundColor: colors.surface, borderRadius: 20, padding: space.lg, borderWidth: 1, borderColor: colors.line },
  rowBetween: { flexDirection: "row", justifyContent: "space-between", alignItems: "center" },
  amount: { color: colors.text, fontSize: 40, fontWeight: "900", marginTop: space.md, fontVariant: ["tabular-nums"] },
  recipient: { color: colors.text, fontSize: type.heading, fontWeight: "700", marginTop: space.xs },
  muted: { color: colors.muted, fontSize: type.small, marginTop: 2 },
  small: { color: colors.muted, fontSize: 14 },
  trust: { marginTop: space.md, borderTopWidth: 1, borderTopColor: colors.line, paddingTop: space.xs },
  trustLabel: { color: colors.muted, fontSize: 14, fontWeight: "700", textTransform: "uppercase", letterSpacing: 0.5 },
  trustValue: { color: colors.text, fontSize: type.small, marginTop: 2 },
  notice: { color: colors.warning, fontSize: 14, marginTop: space.md, fontWeight: "600" },
  dots: { flexDirection: "row", justifyContent: "center", gap: 14, marginVertical: space.md },
  dot: { width: 18, height: 18, borderRadius: 9, borderWidth: 2, borderColor: colors.muted },
  dotFilled: { backgroundColor: colors.gold, borderColor: colors.gold },
  keypad: { width: "100%", maxWidth: 360, alignSelf: "center", gap: 10 },
  keyRow: { flexDirection: "row", gap: 10 },
  key: { flex: 1, height: 68, borderRadius: 16, alignItems: "center", justifyContent: "center" },
  keyActive: { backgroundColor: colors.raised, borderWidth: 1, borderColor: colors.line },
  keyText: { color: colors.text, fontSize: 30, fontWeight: "800" },
});
