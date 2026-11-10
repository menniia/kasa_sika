import { useNetworkState } from "expo-network";
import { createContext, ReactNode, useCallback, useContext, useEffect, useMemo, useState } from "react";
import { Platform } from "react-native";
import { api, ApiError, setToken } from "@/api/client";
import type { SystemInfo, UserView } from "@/api/types";
import { SKIP_SIGN_IN } from "@/lib/demoMode";
import { newId, storage } from "@/lib/storage";

export type EnglishEngine = "auto" | "khaya";

type Prefs = { speechRate: number; handsFree: boolean; englishEngine: EnglishEngine; curtain: boolean };

type Session = {
  ready: boolean;
  user: UserView | null;
  system: SystemInfo | null;
  systemError: boolean;
  language: string;
  prefs: Prefs;
  online: boolean;
  deviceId: string | null;
  setLanguage: (code: string) => Promise<void>;
  setPrefs: (p: Partial<Prefs>) => Promise<void>;
  demoLogin: () => Promise<void>;
  login: (phone: string, passcode: string) => Promise<void>;
  logout: () => Promise<void>;
  refreshSystem: () => Promise<void>;
};

const Ctx = createContext<Session | null>(null);

const defaults: Prefs = { speechRate: 0.9, handsFree: true, englishEngine: "auto", curtain: false };

export function SessionProvider({ children }: { children: ReactNode }) {
  const [ready, setReady] = useState(false);
  const [user, setUser] = useState<UserView | null>(null);
  const [system, setSystem] = useState<SystemInfo | null>(null);
  const [systemError, setSystemError] = useState(false);
  const [language, setLang] = useState("en");
  const [prefs, setPrefsState] = useState<Prefs>(defaults);
  const [deviceId, setDeviceId] = useState<string | null>(null);
  const network = useNetworkState();
  const online = network.isConnected !== false && network.isInternetReachable !== false;

  const device = useCallback(async () => {
    let id = await storage.get("kasa.installationId");
    if (!id) { id = newId(); await storage.set("kasa.installationId", id); }
    return { installationId: id, platform: Platform.OS, label: `Kasa Sika ${Platform.OS}` };
  }, []);

  const refreshSystem = useCallback(async () => {
    try { setSystem(await api.systemInfo()); setSystemError(false); } catch { setSystemError(true); }
  }, []);

  const startSession = useCallback(async (session: { accessToken: string; user: UserView; deviceId: string | null }) => {
    setToken(session.accessToken);
    await storage.set("kasa.token", session.accessToken);
    if (session.deviceId) { setDeviceId(session.deviceId); await storage.set("kasa.deviceId", session.deviceId); }
    setUser(session.user);
    setPrefsState((p) => ({ ...p, speechRate: session.user.preferences.speechRate, handsFree: session.user.preferences.handsFree }));
  }, []);

  useEffect(() => {
    (async () => {
      const [savedLang, savedPrefs, savedToken, savedDevice] = await Promise.all([
        storage.get("kasa.language"), storage.get("kasa.prefs"), storage.get("kasa.token"), storage.get("kasa.deviceId"),
      ]);
      if (savedLang) setLang(savedLang);
      if (savedPrefs) { try { setPrefsState({ ...defaults, ...JSON.parse(savedPrefs), curtain: false }); } catch { /* ignore */ } }
      if (savedDevice) setDeviceId(savedDevice);
      await refreshSystem();
      let signedIn = false;
      if (savedToken) {
        setToken(savedToken);
        try {
          const me = await api.me();
          setUser(me);
          signedIn = true;
          if (!savedLang) setLang(me.preferences.language);
        } catch (e) {
          if (e instanceof ApiError && e.status === 401) { setToken(null); await storage.remove("kasa.token"); }
        }
      }
      if (!signedIn && SKIP_SIGN_IN) {
        try {
          const session = await api.demoLogin(await device());
          await startSession(session);
          if (!savedLang) setLang(session.user.preferences.language);
        } catch { /* backend unreachable: the landing screen offers a retry */ }
      }
      setReady(true);
    })();
  }, [refreshSystem, device, startSession]);

  const value = useMemo<Session>(() => ({
    ready, user, system, systemError, language, prefs, online, deviceId,
    setLanguage: async (code) => {
      setLang(code);
      await storage.set("kasa.language", code);
      if (user) { try { setUser(await api.updatePreferences({ language: code })); } catch { /* offline: kept locally */ } }
    },
    setPrefs: async (p) => {
      const next = { ...prefs, ...p };
      setPrefsState(next);
      await storage.set("kasa.prefs", JSON.stringify({ ...next, curtain: false }));
      if (user && (p.speechRate !== undefined || p.handsFree !== undefined)) {
        try { await api.updatePreferences({ speechRate: next.speechRate, handsFree: next.handsFree }); } catch { /* ignore */ }
      }
    },
    demoLogin: async () => { await startSession(await api.demoLogin(await device())); },
    login: async (phone, passcode) => { await startSession(await api.login(phone, passcode, await device())); },
    logout: async () => {
      try { await api.logout(); } catch { /* revoke best-effort */ }
      setToken(null);
      await storage.remove("kasa.token");
      setUser(null);
    },
    refreshSystem,
  }), [ready, user, system, systemError, language, prefs, online, deviceId, startSession, device, refreshSystem]);

  return <Ctx.Provider value={value}>{children}</Ctx.Provider>;
}

export function useSession(): Session {
  const s = useContext(Ctx);
  if (!s) throw new Error("useSession outside SessionProvider");
  return s;
}
