import { Redirect, router } from "expo-router";
import { useState } from "react";
import { ScrollView, StyleSheet, Text, TextInput, View } from "react-native";
import { SafeAreaView } from "react-native-safe-area-context";
import { ApiError } from "@/api/client";
import { Button, EnvBanner, KenteStripe } from "@/components/ui";
import { SKIP_SIGN_IN } from "@/lib/demoMode";
import { t } from "@/lib/strings";
import { useSession } from "@/state/session";
import { colors, space, touch, type } from "@/theme";

export default function SignIn() {
  const session = useSession();
  const s = t(session.language);
  const [phone, setPhone] = useState("");
  const [passcode, setPasscode] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  if (SKIP_SIGN_IN) return <Redirect href="/" />;

  const submit = async () => {
    setBusy(true);
    setError(null);
    try { await session.login(phone, passcode); router.replace("/talk"); }
    catch (e) { setError(e instanceof ApiError ? e.message : s.network); }
    finally { setBusy(false); }
  };

  return (
    <SafeAreaView style={styles.safe} edges={["top", "bottom"]}>
      <KenteStripe />
      <EnvBanner />
      <ScrollView contentContainerStyle={styles.content}>
        <Button label={`‹ ${s.back}`} variant="ghost" onPress={() => router.back()} style={{ alignSelf: "flex-start" }} />
        <Text style={styles.title} accessibilityRole="header">{s.signIn}</Text>
        <View style={{ gap: space.sm }}>
          <Text style={styles.label} nativeID="phoneLabel">{s.phone}</Text>
          <TextInput value={phone} onChangeText={setPhone} keyboardType="phone-pad" autoComplete="tel" placeholder="024 000 0001"
            placeholderTextColor={colors.muted} style={styles.input} accessibilityLabelledBy="phoneLabel" accessibilityLabel={s.phone} />
          <Text style={styles.label} nativeID="passLabel">{s.passcode}</Text>
          <TextInput value={passcode} onChangeText={(v) => setPasscode(v.replace(/\D/g, "").slice(0, 6))} keyboardType="number-pad" secureTextEntry
            style={styles.input} accessibilityLabelledBy="passLabel" accessibilityLabel={s.passcode} onSubmitEditing={() => void submit()} />
          <Text style={styles.hint}>{s.verifyHint}</Text>
        </View>
        {error && <Text style={styles.error} accessibilityRole="alert">{error}</Text>}
        <Button label={s.signIn} big onPress={() => void submit()} disabled={busy || passcode.length !== 6 || phone.length < 9} />
      </ScrollView>
    </SafeAreaView>
  );
}

const styles = StyleSheet.create({
  safe: { flex: 1, backgroundColor: colors.background },
  content: { padding: space.lg, gap: space.md, maxWidth: 560, width: "100%", alignSelf: "center" },
  title: { color: colors.text, fontSize: type.title, fontWeight: "900" },
  label: { color: colors.text, fontSize: type.body, fontWeight: "700" },
  input: { minHeight: touch, borderRadius: 16, borderWidth: 2, borderColor: colors.line, color: colors.text, fontSize: 24, paddingHorizontal: space.md, backgroundColor: colors.surface },
  hint: { color: colors.muted, fontSize: type.small },
  error: { color: colors.danger, fontSize: type.small, fontWeight: "700" },
});
