import { router, useLocalSearchParams } from "expo-router";
import { useEffect, useState } from "react";
import { ActivityIndicator, ScrollView, StyleSheet, Text, View } from "react-native";
import { SafeAreaView } from "react-native-safe-area-context";
import { api } from "@/api/client";
import type { Transaction } from "@/api/types";
import { Button, EnvBanner, KenteStripe, TransactionCard } from "@/components/ui";
import { t } from "@/lib/strings";
import { useSession } from "@/state/session";
import { colors, space, type } from "@/theme";

/** One transfer, and the append-only record of every state it passed through (from PostgreSQL). */
export default function TransactionDetails() {
  const { id } = useLocalSearchParams<{ id: string }>();
  const { language } = useSession();
  const s = t(language);
  const [tx, setTx] = useState<Transaction | null>(null);
  const [error, setError] = useState(false);

  useEffect(() => {
    if (!id) return;
    api.transaction(id, language).then(setTx).catch(() => setError(true));
  }, [id, language]);

  return (
    <SafeAreaView style={styles.safe} edges={["top", "bottom"]}>
      <KenteStripe />
      <EnvBanner />
      <View style={styles.header}>
        <Button label={`‹ ${s.back}`} variant="ghost" onPress={() => router.back()} />
        <Text style={styles.title} accessibilityRole="header">{s.status}</Text>
      </View>
      {!tx && !error && <ActivityIndicator color={colors.gold} size="large" style={{ marginTop: 40 }} />}
      {error && <Text style={styles.muted}>{s.network}</Text>}
      {tx && (
        <ScrollView contentContainerStyle={styles.content}>
          <TransactionCard tx={tx} />
          <Text style={styles.section} accessibilityRole="header">{s.timeline}</Text>
          {tx.events.map((e, i) => (
            <View key={i} style={styles.event} accessible accessibilityLabel={`${new Date(e.occurredAt).toLocaleTimeString()}: ${e.reason}. State ${humanState(e.to)}.`}>
              <View style={styles.eventDot} />
              <View style={{ flex: 1 }}>
                <Text style={styles.eventState}>{humanState(e.to)}</Text>
                <Text style={styles.eventReason}>{e.reason}</Text>
                <Text style={styles.eventMeta}>{new Date(e.occurredAt).toLocaleTimeString()} · {e.actor}</Text>
              </View>
            </View>
          ))}
        </ScrollView>
      )}
    </SafeAreaView>
  );
}

function humanState(state: string): string {
  return state.toLowerCase().split("_").map((w, i) => (i === 0 ? w[0].toUpperCase() + w.slice(1) : w)).join(" ");
}

const styles = StyleSheet.create({
  safe: { flex: 1, backgroundColor: colors.background },
  header: { flexDirection: "row", alignItems: "center", gap: space.md, padding: space.md },
  title: { color: colors.text, fontSize: type.title, fontWeight: "900" },
  content: { padding: space.md, gap: space.md, maxWidth: 720, width: "100%", alignSelf: "center" },
  section: { color: colors.text, fontSize: type.heading, fontWeight: "800", marginTop: space.md },
  muted: { color: colors.muted, fontSize: type.body, textAlign: "center", marginTop: 40 },
  event: { flexDirection: "row", gap: space.md, paddingVertical: space.sm, borderLeftWidth: 2, borderLeftColor: colors.line, paddingLeft: space.md, marginLeft: space.sm },
  eventDot: { width: 12, height: 12, borderRadius: 6, backgroundColor: colors.gold, marginLeft: -23, marginTop: 6 },
  eventState: { color: colors.text, fontSize: type.body, fontWeight: "800" },
  eventReason: { color: colors.muted, fontSize: type.small, marginTop: 2 },
  eventMeta: { color: colors.muted, fontSize: 13, marginTop: 2, opacity: 0.8 },
});
