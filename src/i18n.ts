export type SupportedLanguage = "en" | "tw";

const messages = {
  en: {
    title: "Kasa Sika",
    demo: "Demo mode. No real money moved.",
    start: "Start voice transfer",
    listening: "Listening. Say: Send fifty cedis to Ama.",
    fallback: "Use demo voice request",
    confirm: "Confirm transfer",
    authenticate: "Verify my identity",
    authorize: "Authorize demo transfer",
    send: "Send securely",
    check: "Check final status",
  },
  tw: {
    title: "Kasa Sika",
    demo: "Demo mode. Sika ankɔ biara.",
    start: "Fi ase kasa ma sika nkɔ",
    listening: "Matie wo. Ka sɛ: Send fifty cedis to Ama.",
    fallback: "Fa demo voice request di dwuma",
    confirm: "Si transfer no so dua",
    authenticate: "Gye me nipadua no to mu",
    authorize: "Ma demo transfer no ho kwan",
    send: "Fa bɔ komam",
    check: "Hwɛ nea ewiee no",
  },
} as const;

export function t(language: SupportedLanguage) {
  return messages[language];
}
