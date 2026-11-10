import { hmac } from "@noble/hashes/hmac.js";
import { sha256 } from "@noble/hashes/sha2.js";
import * as Crypto from "expo-crypto";
import * as LocalAuthentication from "expo-local-authentication";
import * as SecureStore from "expo-secure-store";
import { Platform } from "react-native";
import { api } from "@/api/client";
import { toBase64 } from "@/voice/wav";

/**
 * Fingerprint / face approval for transfers.
 *
 * On enrolment the phone generates a random 256-bit key, keeps it in the OS keystore (SecureStore) and
 * registers it with the server (which requires the passcode). To approve a transfer, the OS biometric prompt
 * must succeed; then the phone signs the server's challenge - which names the exact amount, currency and
 * masked recipient - with HMAC-SHA256. A signature for one transfer cannot approve another.
 *
 * PROTOTYPE LIMITS (documented in docs/security/threat-model.md): the key is a shared secret gated by the
 * biometric prompt in app code. Production should use a hardware-bound, non-exportable asymmetric key or a
 * passkey so the server stores only a public key. Not verified on a physical device in this build.
 */
const KEY = "kasa.deviceKey";

export async function biometricsAvailable(): Promise<boolean> {
  if (Platform.OS === "web") return false;
  try {
    return (await LocalAuthentication.hasHardwareAsync()) && (await LocalAuthentication.isEnrolledAsync());
  } catch {
    return false;
  }
}

export async function isEnrolled(): Promise<boolean> {
  if (Platform.OS === "web") return false;
  return (await SecureStore.getItemAsync(KEY)) !== null;
}

export async function enroll(passcode: string): Promise<void> {
  const key = toBase64(Crypto.getRandomBytes(32));
  await api.enrollDeviceKey(passcode, key); // server verifies the passcode first
  await SecureStore.setItemAsync(KEY, key);
}

export async function unenroll(): Promise<void> {
  await SecureStore.deleteItemAsync(KEY);
}

/** Shows the OS biometric prompt; on success signs the challenge message. Null if the user cancelled or failed. */
export async function signWithBiometrics(message: string, prompt: string): Promise<string | null> {
  const result = await LocalAuthentication.authenticateAsync({ promptMessage: prompt, disableDeviceFallback: true, cancelLabel: "Use passcode" });
  if (!result.success) return null;
  const key = await SecureStore.getItemAsync(KEY);
  if (!key) return null;
  const keyBytes = Uint8Array.from(atobSafe(key), (c) => c.charCodeAt(0));
  const mac = hmac(sha256, keyBytes, new TextEncoder().encode(message));
  return toBase64(mac).replace(/=+$/, "").replace(/\+/g, "-").replace(/\//g, "_");
}

function atobSafe(b64: string): string {
  const chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
  let out = "";
  let buffer = 0;
  let bits = 0;
  for (const c of b64.replace(/=+$/, "")) {
    buffer = (buffer << 6) | chars.indexOf(c);
    bits += 6;
    if (bits >= 8) { bits -= 8; out += String.fromCharCode((buffer >> bits) & 0xff); }
  }
  return out;
}
