import { router, useFocusEffect } from "expo-router";
import { useCallback, useState } from "react";
import { ActivityIndicator, FlatList, Pressable, StyleSheet, Text, View } from "react-native";
import { SafeAreaView } from "react-native-safe-area-context";
import { api } from "@/api/client";
import type { Transaction } from "@/api/types";
import { Button, EnvBanner, KenteStripe, StatusBadge } from "@/components/ui";
import { t } from "@/lib/strings";
import { useSession } from "@/state/session";
import { colors, space, type } from "@/theme";

/** Accessible history: every row reads as one sentence ("Successful. 50 Ghana cedis to Ama Owusu, today at 21:04"). */
export default function History() {
  const { language } = useSession();
  const s = t(language);
  const [items, setItems] = useState<Transaction[] | null>(null);
  const [error, setError] = useState(false);

  useFocusEffect(useCallback(() => {
    let live = true;
    api.history(language).then((x) => live && setItems(x)).catch(() => live && setError(true));
    return () => { live = false; };
  }, [language]));

  return (
    <SafeAreaView style={styles.safe} edges={["top", "bottom"]}>
      <KenteStripe />
      <EnvBanner />
      <View style={styles.header}>
        <Button label={`‹ ${s.back}`} variant="ghost" onPress={() => router.back()} />
        <Text style={styles.title} accessibilityRole="header">{s.history}</Text>
      </View>
      {!items && !error && <ActivityIndicator color={colors.gold} size="large" style={{ marginTop: 40 }} />}
      {error && <Text style={styles.empty}>{s.network}</Text>}
      {items && (
        <FlatList
          data={items}
          keyExtractor={(x) => x.id}
          contentContainerStyle={styles.list}
          ListEmptyComponent={<Text style={styles.empty}>{s.noHistory}</Text>}
          renderItem={({ item }) => {
            const when = new Date(item.createdAt);
            const whenText = `${when.toLocaleDateString([], { day: "numeric", month: "short" })} ${when.toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" })}`;
            return (
              <Pressable
                accessibilityRole="button"
                accessibilityLabel={`${item.statusLabel}. ${item.amountText} to ${item.recipient.displayName}, ${whenText}.${item.reference ? ` Reference ${item.reference}.` : ""}`}
                accessibilityHint="Opens the details and the step-by-step record."
                onPress={() => router.push(`/transaction/${item.id}`)}
                style={({ pressed }) => [styles.row, pressed && { backgroundColor: colors.raised }]}
              >
                <View style={{ flex: 1, gap: 4 }}>
                  <Text style={styles.name}>{item.recipient.displayName}</Text>
                  <Text style={styles.meta}>{whenText} · {item.recipient.maskedIdentifier}</Text>
                  <StatusBadge tone={item.statusTone} label={item.statusLabel} />
                </View>
                <Text style={styles.amount}>{item.amountText}</Text>
              </Pressable>
            );
          }}
        />
      )}
    </SafeAreaView>
  );
}

const styles = StyleSheet.create({
  safe: { flex: 1, backgroundColor: colors.background },
  header: { flexDirection: "row", alignItems: "center", gap: space.md, padding: space.md },
  title: { color: colors.text, fontSize: type.title, fontWeight: "900" },
  list: { padding: space.md, gap: space.sm, maxWidth: 720, width: "100%", alignSelf: "center" },
  row: { flexDirection: "row", alignItems: "center", gap: space.md, backgroundColor: colors.surface, borderRadius: 18, padding: space.md, borderWidth: 1, borderColor: colors.line, minHeight: 96 },
  name: { color: colors.text, fontSize: type.heading, fontWeight: "800" },
  meta: { color: colors.muted, fontSize: 15 },
  amount: { color: colors.text, fontSize: 22, fontWeight: "900", fontVariant: ["tabular-nums"] },
  empty: { color: colors.muted, fontSize: type.body, textAlign: "center", marginTop: 40 },
});
