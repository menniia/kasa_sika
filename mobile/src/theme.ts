/**
 * Kasa Sika visual language: a warm, high-contrast dark theme (easier on low vision, readable in
 * sunlight at full brightness), gold as the single accent ("sika" = money), and a thin kente-inspired
 * stripe as the only decoration. State is never shown by colour alone: every status also has an icon
 * and words.
 */
export const colors = {
  background: "#0E1712",
  surface: "#17241D",
  raised: "#1F3027",
  line: "#2E4537",
  text: "#F7F4EA",
  muted: "#BAC6BC",
  gold: "#F2C14E",
  goldDark: "#1A1405",
  success: "#5EE08F",
  danger: "#FF8A80",
  warning: "#FFD166",
  info: "#8AD8FF",
  kente: ["#F2C14E", "#1B7F4B", "#C8372D", "#F2C14E", "#111111"],
} as const;

export const space = { xs: 4, sm: 8, md: 16, lg: 24, xl: 32 } as const;

export const type = {
  hero: 40,
  title: 28,
  heading: 22,
  body: 20,
  small: 16,
} as const;

/** Minimum touch target. WCAG asks for 44pt; we use more because many users have tremors or low vision. */
export const touch = 64;

export type Tone = "success" | "pending" | "failed" | "unknown" | "neutral";

export const toneStyle: Record<Tone, { color: string; icon: string }> = {
  success: { color: colors.success, icon: "✓" },
  pending: { color: colors.warning, icon: "⏳" },
  failed: { color: colors.danger, icon: "✕" },
  unknown: { color: colors.info, icon: "?" },
  neutral: { color: colors.muted, icon: "•" },
};
