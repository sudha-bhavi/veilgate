import { useSyncExternalStore } from "react";
import { getSession, subscribe, type SessionState } from "../lib/session";

/** The current session; the component re-renders whenever it changes. */
export function useSession(): SessionState {
  return useSyncExternalStore(subscribe, getSession);
}
